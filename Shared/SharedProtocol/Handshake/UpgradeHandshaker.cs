﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Org.Mentalis.Security.Ssl;
using SharedProtocol.Exceptions;
using SharedProtocol.Framing;
using SharedProtocol.Utils;

namespace SharedProtocol.Handshake
{
    /// <summary>
    /// Class is used to upgrade handshake
    /// </summary>
    public class UpgradeHandshaker
    {
        private const int HandshakeResponseSizeLimit = 4096;
        private static readonly byte[] CRLFCRLF = new[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
        private const int Timeout = 60000;


        private Thread _readThread;
        private readonly ConnectionEnd _end;
        private readonly Dictionary<string, string> _headers;
        private readonly ManualResetEvent _responseReceivedRaised;
        private bool _wasResponseReceived;
        private Exception _error;
        private readonly IDictionary<string, object> _handshakeResult;

        public SecureSocket InternalSocket { get; private set; }

        public UpgradeHandshaker(IDictionary<string, object> handshakeEnvironment)
        {
            InternalSocket = (SecureSocket)handshakeEnvironment["secureSocket"];
            _end = (ConnectionEnd)handshakeEnvironment["end"];
            _responseReceivedRaised = new ManualResetEvent(false);
            _handshakeResult = new Dictionary<string, object>();

            if (_end == ConnectionEnd.Client)
            {
                if (handshakeEnvironment.ContainsKey(":host") || (handshakeEnvironment[":host"] is string)
                    || handshakeEnvironment.ContainsKey(":version") || (handshakeEnvironment[":version"] is string))
                {
                    _headers = new Dictionary<string, string>
                        {
                            {":path", (string) handshakeEnvironment[":path"]},
                            {":host", (string) handshakeEnvironment[":host"]},
                            {":version", (string) handshakeEnvironment[":version"]}
                        };
                }
                else
                {
                    throw new InvalidConstraintException("Incorrect header for upgrade handshake");
                }
            }
        }

        public IDictionary<string, object> Handshake()
        {
            var response = new HandshakeResponse();
            _readThread = new Thread((object state) =>
                {
                    var handle = state as EventWaitHandle;

                    try
                    {
                        response = Read11Headers();
                        _wasResponseReceived = true;
                        if (handle != null) handle.Set();
                    }
                    catch (Exception ex)
                    {
                        Http2Logger.LogError(ex.Message);

                        // singal that there is an error
                        if (handle != null) handle.Set();
                        _error = ex;
                        throw;
                    }
                });

            _readThread.IsBackground = true;
            _readThread.Start(_responseReceivedRaised);

            if (_end == ConnectionEnd.Client)
            {
                // Build the request
                var builder = new StringBuilder();
                builder.AppendFormat("{0} {1} {2}\r\n", "GET", _headers[":path"], "HTTP/1.1");
                //TODO pass here requested filename
                builder.AppendFormat("Host: {0}\r\n", _headers[":host"]);
                builder.Append("Connection: Upgrade, Http2-Settings\r\n");
                builder.Append("Upgrade: HTTP-DRAFT-04/2.0\r\n");
                builder.Append("Http2-Settings: ");
                //TODO check out how to send window size and max_conc_streams

                if (_headers != null)
                {
                    var http2Settings = new StringBuilder();
                    foreach (var key in _headers.Keys)
                    {
                        if (!string.Equals(":path", key, StringComparison.OrdinalIgnoreCase))
                            http2Settings.AppendFormat("{0}: {1}\r\n", key, _headers[key]);
                    }
                    byte[] settingsBytes = Encoding.UTF8.GetBytes(http2Settings.ToString());
                    builder.Append(Convert.ToBase64String(settingsBytes));
                }
                builder.Append("\r\n\r\n");
                byte[] requestBytes = Encoding.UTF8.GetBytes(builder.ToString());
                InternalSocket.Send(requestBytes, 0, requestBytes.Length, SocketFlags.None);

                _responseReceivedRaised.WaitOne(Timeout);
                _responseReceivedRaised.Dispose();
            }
            else
            {
                _responseReceivedRaised.WaitOne(Timeout);
                _responseReceivedRaised.Dispose();

                if (response.Result == HandshakeResult.Upgrade)
                {
                    const string status = "101";
                    const string protocol = "HTTP/1.1";
                    const string postfix = "Switching Protocols";

                    var builder = new StringBuilder();
                    builder.AppendFormat("{0} {1} {2}\r\n", protocol, status, postfix);
                    builder.Append("Connection: Upgrade\r\n");
                    builder.Append("Upgrade: HTTP-draft-04/2.0\r\n");
                    builder.Append("\r\n");

                    byte[] requestBytes = Encoding.ASCII.GetBytes(builder.ToString());
                    InternalSocket.Send(requestBytes, 0, requestBytes.Length, SocketFlags.None);
                }
            }

            if (!_wasResponseReceived)
            {
                if (_readThread.IsAlive)
                {
                    _readThread.Abort();
                    _readThread.Join();
                }
                throw new Http2HandshakeFailed(HandshakeFailureReason.Timeout);
            }

            if (_error != null)
            {
                throw _error;
            }

            if (response.Result != HandshakeResult.Upgrade)
            {
                throw new Http2HandshakeFailed(HandshakeFailureReason.InternalError);
            }

            if (_readThread.IsAlive)
            {
                _readThread.Abort();
            }
            _readThread.Join();

            return _handshakeResult;
        }

        private HandshakeResponse Read11Headers()
        {
            byte[] buffer = new byte[HandshakeResponseSizeLimit];
            int readOffset = 0;
            do
            {
                int read;
                try
                {
                    read = InternalSocket.Receive(buffer, readOffset, buffer.Length - readOffset, SocketFlags.None);
                }
                catch (IOException)
                {
                    return new HandshakeResponse { Result = HandshakeResult.UnexpectedConnectionClose };
                }

                if (read <= 0)
                {
                    return new HandshakeResponse { Result = HandshakeResult.UnexpectedConnectionClose };
                }

                readOffset += read;
                int lastInspectionOffset = Math.Max(0, readOffset - CRLFCRLF.Length);
                int matchIndex;
                if (TryFindRangeMatch(buffer, lastInspectionOffset, readOffset, CRLFCRLF, out matchIndex))
                {
                    return InspectHanshake(buffer, matchIndex + CRLFCRLF.Length, readOffset);
                }

            } while (readOffset < HandshakeResponseSizeLimit);

            throw new InvalidOperationException("Handshake response size limit exceeded");
        }

        private bool TryFindRangeMatch(byte[] buffer, int offset, int limit, byte[] matchSequence, out int matchIndex)
        {
            matchIndex = 0;
            for (int master = offset; master < limit && master + matchSequence.Length <= limit; master++)
            {
                if (TryRangeMatch(buffer, master, limit, matchSequence))
                {
                    matchIndex = master;
                    return true;
                }
            }
            return false;
        }

        private bool TryRangeMatch(byte[] buffer, int offset, int limit, byte[] matchSequence)
        {
            bool matched = (limit - offset) >= matchSequence.Length;
            for (int sequence = 0; sequence < matchSequence.Length && matched; sequence++)
            {
                matched = (buffer[offset + sequence] == matchSequence[sequence]);
            }
            if (matched)
            {
                return true;
            }
            return false;
        }

        // We've found a CRLFCRLF sequence.  Confirm the status code is 101 for upgrade.
        private HandshakeResponse InspectHanshake(byte[] buffer, int split, int limit)
        {
            var handshake = new HandshakeResponse
                {
                    ResponseBytes = new ArraySegment<byte>(buffer, 0, split),
                    ExtraData = new ArraySegment<byte>(buffer, split, limit),
                };
            // Must be at least "HTTP/1.1 101\r\nConnection: Upgrade\r\nUpgrade: HTTP/2.0\r\n\r\n"
            string response = FrameHelpers.GetAsciiAt(buffer, 0, split);
            if (_end == ConnectionEnd.Client)
            {
                if (response.StartsWith("HTTP/1.1 101 SWITCHING PROTOCOLS", StringComparison.OrdinalIgnoreCase)
                    && response.IndexOf("\r\nCONNECTION: UPGRADE\r\n", StringComparison.OrdinalIgnoreCase) >= 0
                    && response.IndexOf(string.Format("\r\nUPGRADE: {0}\r\n", Protocols.Http2), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    handshake.Result = HandshakeResult.Upgrade;
                }
                else
                {
                    handshake.Result = HandshakeResult.NonUpgrade;
                }
            }
            else
            {
                if (response.IndexOf("\r\nCONNECTION: UPGRADE, HTTP2-SETTINGS\r\n", StringComparison.OrdinalIgnoreCase) >= 0
                    && response.IndexOf(string.Format("\r\nUPGRADE: {0}\r\n", Protocols.Http2), StringComparison.OrdinalIgnoreCase) >= 0
                    && response.IndexOf("\r\nHTTP2-SETTINGS:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    GetHeaders(response);
                    handshake.Result = HandshakeResult.Upgrade;
                }
                else
                {
                    handshake.Result = HandshakeResult.NonUpgrade;
                }
            }

            return handshake;
        }

        private void GetHeaders(string clientResponse)
        {
            int methodIndex = clientResponse.IndexOf("GET", StringComparison.OrdinalIgnoreCase);
            int pathIndex = clientResponse.IndexOf("/", methodIndex, StringComparison.OrdinalIgnoreCase);
            int endPathIndex = clientResponse.IndexOf(" ", pathIndex, StringComparison.OrdinalIgnoreCase);
            string path = clientResponse.Substring(pathIndex, endPathIndex - pathIndex);
            _handshakeResult.Add(":path", path);

            string clientHeadersInBase64 = clientResponse.Substring(clientResponse.LastIndexOf(' ') + 1);
            byte[] buffer = Convert.FromBase64String(clientHeadersInBase64);
            string response = Encoding.UTF8.GetString(buffer);
            var headers = Regex.Matches(response, "^:.*$", RegexOptions.Multiline | RegexOptions.Compiled);
            foreach (Match header in headers)
            {
                string[] nameValue = header.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                _handshakeResult.Add(nameValue[0].ToLower().TrimEnd(':'), nameValue[1].TrimEnd('\r', '\n'));
            }
        }
    }
}
