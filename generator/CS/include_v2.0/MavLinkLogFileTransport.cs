using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace PX4Link
{
    public class MavLinkLogFileTransport: MavLinkGenericTransport
    {
        private string mLogFileName;
        private string _recordFileName;
        private ConcurrentQueue<UasMessage> mSendQueue = new ConcurrentQueue<UasMessage>();
        private AutoResetEvent mSendSignal = new AutoResetEvent(true);
        private AutoResetEvent isEndSignal = new AutoResetEvent(false);

        public string RecordFileName
        {
            get
            {
                return _recordFileName;
            }
        }

        public string LogFileName
        {
            get
            {
                return mLogFileName;
            }
        }

        public MavLinkLogFileTransport()
        {

        }

        public override void Initialize()
        {
            var path = ".\\Record";
            var mpath = path + "\\" + DateTime.Now.ToString("MM-dd-hh-mm-ss") + "-PX4Link.rec";
            _recordFileName = mpath;
            mIsActive = true;
            ThreadPool.QueueUserWorkItem(
                new WaitCallback(ProcessSendQueue));
        }

        public void RunLogFile(string logFileName)
        {
            mLogFileName = logFileName;
            ThreadPool.QueueUserWorkItem(
                new WaitCallback(Parse));
        }

        public override void Dispose()
        {
            mIsActive = false;
            mSendSignal.Set();
        }

        public void RefreshNewLogFile()
        {
            if (mIsActive)
            {
                isEndSignal.Reset();
                mIsActive = false;
                mSendSignal.Set();
                var path = ".\\Record";
                var mpath = path + "\\" + DateTime.Now.ToString("MM-dd-hh-mm-ss") + "-PX4Link.rec";
                _recordFileName = mpath;
                isEndSignal.WaitOne();
                mIsActive = true;
                ThreadPool.QueueUserWorkItem(
                    new WaitCallback(ProcessSendQueue));
            }
            else
            {
                var path = ".\\Record";
                var mpath = path + "\\" + DateTime.Now.ToString("MM-dd-hh-mm-ss") + "-PX4Link.rec";
                _recordFileName = mpath;
            }
        }

        public override void SendMessage(UasMessage msg)
        {
            // No messages are sent on this transport (only read from the logfile)
            mSendQueue.Enqueue(msg);

            // Signal send thread
            mSendSignal.Set();
        }

        private void ProcessSendQueue(object state)
        {
            byte seq = 0;
            using (FileStream s = new FileStream(_recordFileName, FileMode.Create))
            {
                using (BinaryWriter writer = new BinaryWriter(s))
                {
                    while (true)
                    {
                        UasMessage msg;

                        if (mSendQueue.TryDequeue(out msg))
                        {
                            //SendMavlinkMessage(msg);
                            writer.Write(MavLinkPacket.GetBytesForMessage(
                                msg, MavlinkSystemId, MavlinkComponentId, seq++, MavLinkGenericPacketWalker.PacketSignalByte));
                        }
                        else
                        {
                            // Queue is empty, sleep until signalled
                            mSendSignal.WaitOne();

                            if (!mIsActive) break;
                        }
                    }
                }
            }
            isEndSignal.Set();
        }

        // __ Impl ____________________________________________________________


        private void Parse(object state)
        {
            try
            {
                using (FileStream s = new FileStream(mLogFileName, FileMode.Open))
                {
                    using (BinaryReader reader = new BinaryReader(s))
                    {
                        while (true)
                        {
                            SyncStream(reader);
                            MavLinkPacket packet = MavLinkPacket.Deserialize(reader, 0);

                            if (packet.IsValid)
                            {
                                HandlePacketReceived(this, packet);
                            }
                        }
                    }
                }
            }
            catch (EndOfStreamException)
            { 
                
            }

            HandleReceptionEnded(this);
        }

        private void SyncStream(BinaryReader s)
        {
            while (s.ReadByte() != MavLinkGenericPacketWalker.PacketSignalByte)
            {
                // Skip bytes until a packet start is found
            }
        }

        public override void BeginHeartBeatLoop()
        {
            //throw new NotImplementedException();
        }
    }
}
