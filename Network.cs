﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Management;
using NetFwTypeLib;

namespace ACT.DFAssist
{
    public enum MatchEndType
    {
        CANCELLED = 0,
        ENTER_INSTANCE = 1
    }

    public enum MatchType
    {
        ROULETTE = 0,
        SELECTIVE = 1
    }

    public enum State
    {
        IDLE,
        QUEUED,
        MATCHED,
    }

    [Flags]
    public enum TcpFlags
    {
        NONE = 0,
        FIN = 1,
        SYN = 2,
        RST = 4,
        PSH = 8,
        ACK = 16,
        URG = 32,
        ECE = 64,
        CWR = 128,
        NS = 256,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TcpRow
    {
        public TcpState state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TcpTable
    {
        public uint length;
        public TcpRow row;
    }

    static class Network
    {
        public class Connection
        {
            public IPEndPoint LocalEndPoint { get; set; }
            public IPEndPoint RemoteEndPoint { get; set; }

            public override bool Equals(object obj)
            {
                if (obj == null || GetType() != obj.GetType())
                {
                    return false;
                }

                var connection = obj as Connection;

                return LocalEndPoint.Equals(connection?.LocalEndPoint) && RemoteEndPoint.Equals(connection?.RemoteEndPoint);
            }

            public override int GetHashCode()
            {
                if (LocalEndPoint == null | RemoteEndPoint == null)
                    return -1;

                return (LocalEndPoint.GetHashCode() + 0x0609) ^ RemoteEndPoint.GetHashCode();
            }

            public override string ToString()
            {
                return $"{LocalEndPoint} -> {RemoteEndPoint}";
            }
        }

        public struct IpPacket
        {
            public byte HeaderLength { get; }
            public ProtocolType Protocol { get; }
            public ProtocolFamily Version { get; }
            public IPAddress SourceIpAddress { get; }
            public IPAddress DestinationIpAddress { get; }

            public byte[] Data { get; }

            public bool IsValid { get; }

            public IpPacket(byte[] buffer)
            {
                try
                {
                    var versionAndHeaderLength = buffer[0];
                    Version = versionAndHeaderLength >> 4 == 4 ? ProtocolFamily.InterNetwork : ProtocolFamily.InterNetworkV6;
                    HeaderLength = (byte)((versionAndHeaderLength & 15) * 4); // 0b1111 = 15

                    Protocol = (ProtocolType)buffer[9];

                    SourceIpAddress = new IPAddress(BitConverter.ToUInt32(buffer, 12));
                    DestinationIpAddress = new IPAddress(BitConverter.ToUInt32(buffer, 16));

                    Data = buffer.Skip(HeaderLength).ToArray();

                    IsValid = true;
                }
                catch (Exception ex)
                {
                    Version = ProtocolFamily.Unknown;
                    HeaderLength = 0;

                    Protocol = ProtocolType.Unknown;

                    SourceIpAddress = null;
                    DestinationIpAddress = null;

                    Data = null;

                    IsValid = false;
                    MsgLog.Exception(ex, "l-packet-error-ip");
                }
            }
        }

        public class Capture
        {
            public const int TcpTableOwnerPidConnections = 4;

            private readonly string _exePath;
            private readonly byte[] _recvBuffer;
            private readonly object _lockAnalyse;

            private int _pid;
            private State _state;
            private Socket _socket;
            private List<Connection> _connections;
            public byte[] RcvallIplevel { get; }
            public bool IsRunning { get; private set; }

            public Capture()
            {
                _state = State.IDLE;
                _connections = new List<Connection>();
                _lockAnalyse = new object();
                _recvBuffer = new byte[0x20000];

                RcvallIplevel = new byte[] { 3, 0, 0, 0 };
                _exePath = Process.GetCurrentProcess().MainModule.FileName;
            }

            public void StartCapture(Process process)
            {
                _pid = process.Id;
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        MsgLog.Info("l-network-starting");

                        if (IsRunning)
                        {
                            MsgLog.Error("l-network-error-already-started");
                            return;
                        }

                        UpdateGameConnections(process);

                        if (_connections.Count < 2)
                        {
                            MsgLog.Error("l-network-error-no-connection");
                            return;
                        }

                        var localAddress = _connections[0].LocalEndPoint.Address;

                        RegisterToFirewall();

                        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                        _socket.Bind(new IPEndPoint(localAddress, 0));
                        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AcceptConnection, true);
                        _socket.IOControl(IOControlCode.ReceiveAll, RcvallIplevel, null);
                        _socket.ReceiveBufferSize = _recvBuffer.Length * 4;

                        _socket.BeginReceive(_recvBuffer, 0, _recvBuffer.Length, 0, OnReceive, null);
                        IsRunning = true;

                        MsgLog.Success("l-network-started");
                    }
                    catch (Exception ex)
                    {
                        MsgLog.Exception(ex, "l-network-error-starting");
                    }
                });
            }

            public void StopCapture()
            {
                try
                {
                    if (!IsRunning)
                    {
                        MsgLog.Error("l-network-error-already-stopped");
                        return;
                    }

                    _socket.Close();
                    _connections.Clear();
                    MsgLog.Info("l-network-stopping");
                }
                catch (Exception ex)
                {
                    MsgLog.Exception(ex, "l-network-error-stopping");
                }
            }

            public void UpdateGameConnections(Process process)
            {
                var update = _connections.Count < 2;
                var currentConnections = GetConnections(process);

                foreach (var connection in _connections)
                {
                    if (currentConnections.Contains(connection))
                        continue;

                    // Connection was lost, a new update is requested
                    update = true;
                    MsgLog.Error("l-network-detected-connection-closing");
                    break;
                }

                if (update)
                {
                    var lobbyEndPoint = GetLobbyEndPoint(process);
                    _connections = currentConnections.Where(x => !x.RemoteEndPoint.Equals(lobbyEndPoint)).ToList();

                    foreach (var connection in _connections)
                        MsgLog.Info("l-network-detected-connection", connection);
                }
            }

            private void OnReceive(IAsyncResult ar)
            {
                try
                {
                    var length = _socket.EndReceive(ar);
                    var buffer = _recvBuffer.Take(length).ToArray();
                    _socket.BeginReceive(_recvBuffer, 0, _recvBuffer.Length, 0, OnReceive, null);

                    FilterAndProcessPacket(buffer);
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is NullReferenceException)
                {
                    IsRunning = false;
                    _socket = null;
                    MsgLog.Success("l-network-stopped");
                }
                catch (Exception ex)
                {
                    MsgLog.Exception(ex, "l-network-error-receiving-packet");
                }
            }

            private void FilterAndProcessPacket(byte[] buffer)
            {
                try
                {
                    var ipPacket = new IpPacket(buffer);
                    if (!ipPacket.IsValid || ipPacket.Protocol != ProtocolType.Tcp)
                        return;

                    var tcpPacket = new TcpPacket(ipPacket.Data);
                    if (!tcpPacket.IsValid)
                        return;

                    if (!tcpPacket.Flags.HasFlag(TcpFlags.ACK | TcpFlags.PSH))
                        return;

                    var sourceEndPoint = new IPEndPoint(ipPacket.SourceIpAddress, tcpPacket.SourcePort);
                    var destinationEndPoint = new IPEndPoint(ipPacket.DestinationIpAddress, tcpPacket.DestinationPort);
                    var connection = new Connection { LocalEndPoint = sourceEndPoint, RemoteEndPoint = destinationEndPoint };
                    var reverseConnection = new Connection { LocalEndPoint = destinationEndPoint, RemoteEndPoint = sourceEndPoint };

                    if (!(_connections.Contains(connection) || _connections.Contains(reverseConnection)))
                        return;

                    if (!_connections.Contains(reverseConnection))
                        return;

                    lock (_lockAnalyse)
                        PacketAnalyzer.Analyze(_pid, tcpPacket.Payload, ref _state);
                }
                catch (Exception ex)
                {
                    MsgLog.Exception(ex, "l-network-error-filtering-packet");
                }
            }

            private void RegisterToFirewall()
            {
                try
                {
                    var netFwMgr = GetInstance<INetFwMgr>("HNetCfg.FwMgr");
                    var netAuthApps = netFwMgr.LocalPolicy.CurrentProfile.AuthorizedApplications;

                    var exists = false;
                    foreach (var netAuthAppObject in netAuthApps)
                    {
                        if (netAuthAppObject is INetFwAuthorizedApplication netAuthApp && netAuthApp.ProcessImageFileName == _exePath && netAuthApp.Enabled)
                        {
                            exists = true;
                        }
                    }

                    if (exists)
                        return;

                    var networkApp = GetInstance<INetFwAuthorizedApplication>("HNetCfg.FwAuthorizedApplication");

                    networkApp.Enabled = true;
                    networkApp.Name = "FFXIV_FATE";
                    networkApp.ProcessImageFileName = _exePath;
                    networkApp.Scope = NET_FW_SCOPE_.NET_FW_SCOPE_ALL;

                    netAuthApps.Add(networkApp);

                    MsgLog.Success("l-firewall-registered");
                }
                catch (Exception ex)
                {
                    MsgLog.Exception(ex, "l-firewall-error");
                }
            }

            private IPEndPoint GetLobbyEndPoint(Process process)
            {
                IPEndPoint ipep = null;
                string lobbyHost = null;
                var lobbyPort = 0;

                try
                {
                    using (var managementObjectSearcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
                    {
                        foreach (var managementBaseObject in managementObjectSearcher.Get())
                        {
                            var commandline = managementBaseObject["CommandLine"].ToString();
                            var args = commandline.Split(' ');

                            foreach (var arg in args)
                            {
                                var splitted = arg.Split('=');
                                if (splitted.Length != 2)
                                    continue;

                                switch (splitted[0])
                                {
                                    case "DEV.LobbyHost01":
                                        lobbyHost = splitted[1];
                                        break;
                                    case "DEV.LobbyPort01":
                                        lobbyPort = int.Parse(splitted[1]);
                                        break;
                                }
                            }
                        }
                    }

                    if (lobbyHost != null && lobbyPort > 0)
                    {
                        var address = Dns.GetHostAddresses(lobbyHost)[0];
                        ipep = new IPEndPoint(address, lobbyPort);
                    }
                }
                catch (Exception ex)
                {
                    MsgLog.Exception(ex, "l-network-error-finding-lobby");
                }

                return ipep;
            }

            private static List<Connection> GetConnections(Process process)
            {
                var connections = new List<Connection>();

                var tcpTable = IntPtr.Zero;
                var tcpTableLength = 0;

                if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, AddressFamily.InterNetwork, TcpTableOwnerPidConnections, 0) == 0)
                    return connections;

                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, AddressFamily.InterNetwork, TcpTableOwnerPidConnections, 0) == 0)
                    {
                        var table = (TcpTable)Marshal.PtrToStructure(tcpTable, typeof(TcpTable));
                        var rowPointer = new IntPtr(tcpTable.ToInt64() + Marshal.SizeOf(typeof(uint)));

                        for (var i = 0; i < table.length; i++)
                        {
                            var row = (TcpRow)Marshal.PtrToStructure(rowPointer, typeof(TcpRow));

                            if (row.owningPid == process.Id)
                            {
                                var local = new IPEndPoint(row.localAddr, (ushort)IPAddress.NetworkToHostOrder((short)row.localPort));
                                var remote = new IPEndPoint(row.remoteAddr, (ushort)IPAddress.NetworkToHostOrder((short)row.remotePort));

                                connections.Add(new Connection() { LocalEndPoint = local, RemoteEndPoint = remote });
                            }

                            rowPointer = new IntPtr(rowPointer.ToInt64() + Marshal.SizeOf(typeof(TcpRow)));
                        }
                    }
                }
                finally
                {
                    if (tcpTable != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(tcpTable);
                    }
                }

                return connections;
            }

            private static T GetInstance<T>(string typeName)
            {
                return (T)Activator.CreateInstance(Type.GetTypeFromProgID(typeName));
            }
        }

        public struct TcpPacket
        {
            public ushort SourcePort;
            public ushort DestinationPort;
            public byte DataOffset;
            public TcpFlags Flags;

            public byte[] Payload;

            public bool IsValid;

            public TcpPacket(byte[] buffer)
            {
                try
                {
                    SourcePort = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 0));
                    DestinationPort = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 2));

                    var offsetAndFlags = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 12));
                    DataOffset = (byte)((offsetAndFlags >> 12) * 4);
                    Flags = (TcpFlags)(offsetAndFlags & 511); // 0b111111111 = 511

                    Payload = buffer.Skip(DataOffset).ToArray();

                    IsValid = true;
                }
                catch (Exception ex)
                {
                    SourcePort = 0;
                    DestinationPort = 0;
                    DataOffset = 0;
                    Flags = TcpFlags.NONE;

                    Payload = null;

                    IsValid = false;

                    MsgLog.Exception(ex, "l-packet-error-tcp");
                }
            }
        }
    }
}
