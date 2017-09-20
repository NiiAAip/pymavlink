/*
The MIT License (MIT)

Copyright (c) 2013, David Suarez

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace PX4Link
{
    public class MavLinkUdpTransport: MavLinkGenericTransport
    {
        public int UdpListeningPort = 10;  // Any available port
        public int UdpTargetPort = 14550;
        public IPAddress TargetIpAddress = new IPAddress(new byte[] { 127, 0, 0, 1 });
        public int HeartBeatUpdateRateMs = 1000;
        
        private ConcurrentQueue<byte[]> mReceiveQueue = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<UasMessage> mSendQueue = new ConcurrentQueue<UasMessage>();
        private AutoResetEvent mReceiveSignal = new AutoResetEvent(true);
        private AutoResetEvent mSendSignal = new AutoResetEvent(true);
        private MavLinkAsyncWalker mMavLink = new MavLinkAsyncWalker();
        private UdpClient mUdpClient;
        //private bool mIsActive = true;


        public override void Initialize()
        {
            InitializeMavLink();
            InitializeUdpListener(UdpTargetPort);
            InitializeUdpSender(TargetIpAddress, UdpTargetPort);
        }

        public override void Dispose()
        {
            mIsActive = false;
            mUdpClient.Close();
            mReceiveSignal.Set();
            mSendSignal.Set();
        }

        private void InitializeMavLink()
        {
            mMavLink.PacketReceived += HandlePacketReceived;
        }

        private void InitializeUdpListener(int port)
        {
            // Create UDP listening socket on port
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, port);
            mUdpClient = new UdpClient(ep);

            mUdpClient.BeginReceive(
                new AsyncCallback(ReceiveCallback), ep);

            //ThreadPool.QueueUserWorkItem(
            //    new WaitCallback(ProcessReceiveQueue), null);
            new Thread(ProcessReceiveQueue) { IsBackground = true }.Start();
        }

        private void InitializeUdpSender(IPAddress targetIp, int targetPort)
        {
            ThreadPool.QueueUserWorkItem(
               new WaitCallback(ProcessSendQueue), new IPEndPoint(targetIp, targetPort));
        }


        // __ Receive _________________________________________________________


        private void ReceiveCallback(IAsyncResult ar)
        {
            try
            {
                IPEndPoint ep = ar.AsyncState as IPEndPoint;
                mReceiveQueue.Enqueue(mUdpClient.EndReceive(ar, ref ep));

                if (!mIsActive)
                {
                    mReceiveSignal.Set();
                    return;
                }

                mUdpClient.BeginReceive(new AsyncCallback(ReceiveCallback), ar);

                // Signal processReceive thread
                mReceiveSignal.Set();
            }
            catch (SocketException)
            {
                mIsActive = false;
            }
            catch
            {
                mIsActive = false;
            }
        }

        private void ProcessReceiveQueue(object state)
        {
            while (true)
            {
                byte[] buffer;

                if (mReceiveQueue.TryDequeue(out buffer))
                {
                    mMavLink.ProcessReceivedBytes(buffer, 0, buffer.Length);
                }
                else
                {
                    // Empty queue, sleep until signalled
                    mReceiveSignal.WaitOne();

                    if (!mIsActive) break;
                }
            }

            HandleReceptionEnded(this);
        }


        // __ Send ____________________________________________________________


        private void ProcessSendQueue(object state)
        {
            while (true)
            {
                UasMessage msg;

                if (mSendQueue.TryDequeue(out msg))
                {
                    SendMavlinkMessage(state as IPEndPoint, msg);
                }
                else
                {
                    // Queue is empty, sleep until signalled
                    mSendSignal.WaitOne();

                    if (!mIsActive) break;
                }
            }
        }

        private void SendMavlinkMessage(IPEndPoint ep, UasMessage msg)
        {
            byte[] buffer = mMavLink.SerializeMessage(msg, MavlinkSystemId, MavlinkComponentId, true);
            //buffer = BtyesToBytes(buffer);
            mUdpClient.Send(buffer, buffer.Length, ep);
        }

        private byte[] BtyesToBytes(byte[] buffer)
        {
            byte[] bytess = new byte[buffer.Length + 10];
            bytess[0] = 0xFE;//hand
            bytess[1] = (byte)buffer.Length;//length
            bytess[2] = 0;//seq 
            bytess[3] = 10;//SenderSystemId
            bytess[4] = 0;//SenderComponentId
            bytess[5] = 1;//TargetSystemId
            bytess[6] = 0;//TargetComponentId   
            bytess[7] = 0xFF;//MessageId
            Array.Copy(buffer, 0, bytess, 8, buffer.Length);
            UInt16 crc = GetPacketCrc(bytess);
            bytess[8 + buffer.Length] = (byte)(crc & 0xFF);
            bytess[9 + buffer.Length] = (byte)(crc >> 8);
            return bytess;
        }


        // __ Heartbeat _______________________________________________________


        public override void BeginHeartBeatLoop()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback(HeartBeatLoop), null);
        }

        private void HeartBeatLoop(object state)
        {
            while (true)
            {
                foreach (UasMessage m in UavState.GetHeartBeatObjects())
                {
                    SendMessage(m);
                }

                Thread.Sleep(HeartBeatUpdateRateMs);
            }
        }


        // __ API _____________________________________________________________


        public override void SendMessage(UasMessage msg)
        {
            mSendQueue.Enqueue(msg);

            // Signal send thread
            mSendSignal.Set();
        }

        // __ CRC _____________________________________________________________


        // CRC code adapted from Mavlink C# generator (https://github.com/mavlink/mavlink)

        const UInt16 X25CrcSeed = 0xffff;

        public static UInt16 X25CrcAccumulate(byte b, UInt16 crc)
        {
            unchecked
            {
                byte ch = (byte)(b ^ (byte)(crc & 0x00ff));
                ch = (byte)(ch ^ (ch << 4));
                return (UInt16)((crc >> 8) ^ (ch << 8) ^ (ch << 3) ^ (ch >> 4));
            }
        }

        public static UInt16 GetPacketCrc(byte[] p)
        {
            UInt16 crc = X25CrcSeed;

            for (int i = 1; i < p.Length - 2; ++i)
            {
                crc = X25CrcAccumulate(p[i], crc);
            }

            crc = X25CrcAccumulate(0, crc);

            return crc;
        }
    }
}
