﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;

namespace PRoCon.Core.Remote.Layer {
    using PRoCon.Core.Remote;
    public class FrostbiteLayerConnection {

        private TcpClient m_tcpConnection;
        private NetworkStream m_tcpStream;

        private static readonly UInt32 MAX_GARBAGE_BYTES = 4194304;
        //private static readonly UInt16 BUFFER_SIZE = 16384;

        private byte[] a_receivedBuffer;
        private byte[] a_packetStream;

        private UInt32 m_ui32SequenceNumber;
        public UInt32 AcquireSequenceNumber {
            get {
                lock (new object()) {
                    return ++this.m_ui32SequenceNumber;
                }
            }
        }

        //private string m_strClientIPPort = String.Empty;
        public string IPPort {
            get {
                string strClientIPPort = String.Empty;

                // However if the connection is open just get it straight from the horses mouth.
                if (this.m_tcpConnection != null && this.m_tcpConnection.Connected == true) {
                    strClientIPPort = ((IPEndPoint)this.m_tcpConnection.Client.RemoteEndPoint).Address.ToString() + ":" + ((IPEndPoint)this.m_tcpConnection.Client.RemoteEndPoint).Port.ToString();
                }

                return strClientIPPort;
            }
        }

        #region Events

        public delegate void EmptyParameterHandler(FrostbiteLayerConnection sender);
        public event EmptyParameterHandler ConnectionClosed;

        public delegate void PacketDispatchHandler(FrostbiteLayerConnection sender, Packet packet);
        public event PacketDispatchHandler PacketSent;
        public event PacketDispatchHandler PacketReceived;

        #endregion

        public FrostbiteLayerConnection(TcpClient acceptedConnection) {
            this.a_receivedBuffer = new byte[4096];
            this.a_packetStream = null;

            this.m_tcpConnection = acceptedConnection;

            if ((this.m_tcpStream = this.m_tcpConnection.GetStream()) != null) {
                this.m_tcpStream.BeginRead(this.a_receivedBuffer, 0, this.a_receivedBuffer.Length, this.ReceiveCallback, this);
            }
            else {
                // Short lived..
                this.Shutdown();
            }
        }

        private void SendAsyncCallback(IAsyncResult ar) {

            try {
                this.m_tcpStream.EndWrite(ar);

                if (this.PacketSent != null) {
                    FrostbiteConnection.RaiseEvent(this.PacketSent.GetInvocationList(), this, (Packet)ar.AsyncState);
                }
            }
            catch (SocketException) {
                this.Shutdown();
            }
            catch (Exception) {
                this.Shutdown();
            }
        }

        public void SendAsync(Packet cpPacket) {
            try {
                byte[] a_bBytePacket = cpPacket.EncodePacket();

                this.m_tcpStream.BeginWrite(a_bBytePacket, 0, a_bBytePacket.Length, this.SendAsyncCallback, cpPacket);

            }
            catch (SocketException) {
                // TO DO: Error reporting, possibly in a log file.
                this.Shutdown();
            }
            catch (Exception) {
                this.Shutdown();
            }
        }

        private void ReceiveCallback(IAsyncResult ar) {

            if (this.m_tcpStream != null) {
                try {
                    int iBytesRead = this.m_tcpStream.EndRead(ar);
                    iBytesRead = ServeCrossDomainPolicy(iBytesRead);

                    if (iBytesRead > 0) {

                        // Create or resize our packet stream to hold the new data.
                        if (this.a_packetStream == null) {
                            this.a_packetStream = new byte[iBytesRead];
                        }
                        else {
                            Array.Resize(ref this.a_packetStream, this.a_packetStream.Length + iBytesRead);
                        }

                        Array.Copy(this.a_receivedBuffer, 0, this.a_packetStream, this.a_packetStream.Length - iBytesRead, iBytesRead);

                        UInt32 ui32PacketSize = Packet.DecodePacketSize(this.a_packetStream);

                        while (this.a_packetStream.Length >= ui32PacketSize && this.a_packetStream.Length > Packet.INT_PACKET_HEADER_SIZE) {

                            // Copy the complete packet from the beginning of the stream.
                            byte[] a_bCompletePacket = new byte[ui32PacketSize];
                            Array.Copy(this.a_packetStream, a_bCompletePacket, ui32PacketSize);

                            Packet cpCompletePacket = new Packet(a_bCompletePacket);
                            this.m_ui32SequenceNumber = Math.Max(this.m_ui32SequenceNumber, cpCompletePacket.SequenceNumber);

                            // Dispatch the completed packet.
                            if (this.PacketReceived != null) {
                                FrostbiteConnection.RaiseEvent(this.PacketReceived.GetInvocationList(), this, cpCompletePacket);
                            }
                            //this.DispatchPacket(cpCompletePacket);

                            // Now remove the completed packet from the beginning of the stream
                            byte[] a_bUpdatedSteam = new byte[this.a_packetStream.Length - ui32PacketSize];
                            Array.Copy(this.a_packetStream, ui32PacketSize, a_bUpdatedSteam, 0, this.a_packetStream.Length - ui32PacketSize);
                            this.a_packetStream = a_bUpdatedSteam;

                            ui32PacketSize = Packet.DecodePacketSize(this.a_packetStream);
                        }

                        // If we've recieved 16 kb's and still don't have a full command then shutdown the connection.
                        if (this.a_receivedBuffer.Length >= FrostbiteLayerConnection.MAX_GARBAGE_BYTES) {
                            this.a_receivedBuffer = null;
                            this.Shutdown();
                        }

                    }

                    if (iBytesRead == 0) {
                        this.Shutdown();
                        return;
                    }

                    IAsyncResult result = this.m_tcpStream.BeginRead(this.a_receivedBuffer, 0, this.a_receivedBuffer.Length, this.ReceiveCallback, null);

                    if (result.AsyncWaitHandle.WaitOne(180000, false) == false) {
                        this.Shutdown();
                    }
                }
                catch (Exception) {
                    this.Shutdown();
                }
            }
        }
        
        // TO DO: Better error reporting on this method.
        public void Shutdown() {
            try {

                if (this.ConnectionClosed != null) {
                    FrostbiteConnection.RaiseEvent(this.ConnectionClosed.GetInvocationList(), this);
                }

                if (this.m_tcpConnection != null) {
                    lock (new object()) {

                        if (this.m_tcpStream != null) {
                            this.m_tcpStream.Close();
                            this.m_tcpStream.Dispose();
                            this.m_tcpStream = null;
                        }

                        if (this.m_tcpConnection != null) {
                            this.m_tcpConnection.Close();
                            this.m_tcpConnection = null;
                        }
                    }
                }

            }
            catch (SocketException) {
                // TO DO: Error reporting, possibly in a log file.
            }
            catch (Exception) {

            }
        }

        #region CrossDomainCode
        // Represents NULL-terminated "<policy-file-request/>"  
        static private byte[] a_policyRequest = new byte[] { 
            0x3c, 0x70, 0x6f, 0x6c, 0x69, 0x63, 0x79, 0x2d, 0x66, 0x69, 0x6c,
            0x65, 0x2d, 0x72, 0x65, 0x71, 0x75, 0x65, 0x73, 0x74, 0x2f, 0x3e, 0x00 };

        private int ServeCrossDomainPolicy(int iBytesRead)
        {
            // Cross domain policy is only served once, at the begining of the TCP connection
            if (this.a_packetStream != null)
                return iBytesRead;

            if (iBytesRead >= a_policyRequest.Length)
            {
                // Compare buffers, to see if policy request was received
                int i = 0;
                for (; i < a_policyRequest.Length; i++)
                    if (a_receivedBuffer[i] != a_policyRequest[i])
                        break;

                if (i == a_policyRequest.Length)
                {
                    // Comparison succeeded
                    int iLocalPort = ((IPEndPoint)this.m_tcpConnection.Client.LocalEndPoint).Port;

                    String sPolicyResponse = "<?xml version=\"1.0\"?>" +
                                             "<!DOCTYPE cross-domain-policy " +
                                             "SYSTEM \"http://www.macromedia.com/xml/dtds/cross-domain-policy.dtd\">" +
                                             "<cross-domain-policy>" +
                                             "<allow-access-from domain=\"*\" to-ports=\"" + iLocalPort + "\" />" +
                                             "</cross-domain-policy>";

                    byte[] a_Response = Encoding.GetEncoding(1252).GetBytes(sPolicyResponse + Convert.ToChar(0x00));
                    this.m_tcpStream.Write(a_Response, 0, a_Response.Length);

                    // Remove the policy request from the begining of the receive buffer
                    iBytesRead -= a_policyRequest.Length;
                    Array.Copy(this.a_receivedBuffer, a_policyRequest.Length, this.a_receivedBuffer, 0, iBytesRead);
                }
            }

            return iBytesRead;
        }
        #endregion

    }
}
