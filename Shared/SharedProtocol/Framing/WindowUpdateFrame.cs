﻿
namespace SharedProtocol.Framing
{
    public class WindowUpdateFrame : Frame
    {
        // The number of bytes in the frame.
        private const int InitialFrameSize = 12;
                
        // Incoming
        public WindowUpdateFrame(Frame preamble)
            : base(preamble)
        {
        }

        // Outgoing
        public WindowUpdateFrame(int id, int delta)
            : base(new byte[InitialFrameSize])
        {
            StreamId = id;
            FrameType = FrameType.WindowUpdate;
            FrameLength = InitialFrameSize - Constants.FramePreambleSize; // 8
            Delta = delta;
        }

        // 31 bits
        public int Delta
        {
            get
            {
                return FrameHelpers.Get31BitsAt(Buffer, Constants.FramePreambleSize);
            }
            set
            {
                FrameHelpers.Set31BitsAt(Buffer, Constants.FramePreambleSize, value);
            }
        }
    }
}
