using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// UDP packet type sent from client during connection phase.
/// </summary>
public enum ClientUdpPacketType : byte
{
    UDP_HERE = 0,       // "I'm here on UDP!" - sent until first frame received
    PLAYER_INPUT = 1    // Normal game input
}

/// <summary>
/// Represents a connected client on the server side.
/// </summary>
public class ConnectedClient
{
    public int ClientId;
    public StreamPeerTcp TcpConnection;
    public string UdpAddress;
    public int UdpPort;
    public bool UdpConfirmed;  // True once we've received a UDP packet from this client
    public bool TcpAckReceived;

    // Buffer for accumulating incoming TCP data (for length-prefixed framing)
    public byte[] TcpReceiveBuffer = new byte[65536];
    public int TcpReceiveBufferOffset = 0;
}

/// <summary>
/// Server-side network layer handling TCP and UDP connections.
/// </summary>
public class GodotNetworkLayer_Server
{
    private TcpServer _tcpServer;
    private PacketPeerUdp _udpSocket;
    private Dictionary<int, ConnectedClient> _clients = new Dictionary<int, ConnectedClient>();
    private int _nextClientId = 0;
    private int _tcpPort;
    private int _udpPort;

    // Callbacks
    public Action<int> OnClientTcpConnected;                    // Client connected via TCP
    public Action<int> OnClientTcpDisconnected;                 // Client TCP disconnected
    public Action<int> OnClientUdpConfirmed;                    // Client's first UDP packet received
    public Action<int, byte[], int> OnTcpDataReceived;          // TCP data from client
    public Action<int, byte[], int> OnUdpDataReceived;          // UDP data from client

    public bool AllClientsUdpConfirmed
    {
        get
        {
            foreach (ConnectedClient client in _clients.Values)
            {
                if (!client.UdpConfirmed) return false;
            }
            return _clients.Count > 0;
        }
    }

    public int ConnectedClientCount => _clients.Count;

    public GodotNetworkLayer_Server()
    {
    }

    /// <summary>
    /// Start listening for TCP connections and UDP packets.
    /// </summary>
    public Error StartServer(int tcpPort, int udpPort)
    {
        _tcpPort = tcpPort;
        _udpPort = udpPort;

        // Start TCP server
        _tcpServer = new TcpServer();
        Error tcpError = _tcpServer.Listen((ushort)tcpPort);
        if (tcpError != Error.Ok)
        {
            GD.PrintErr($"Failed to start TCP server on port {tcpPort}: {tcpError}");
            return tcpError;
        }
        GD.Print($"TCP server listening on port {tcpPort}");

        // Start UDP socket
        _udpSocket = new PacketPeerUdp();
        Error udpError = _udpSocket.Bind(udpPort);
        if (udpError != Error.Ok)
        {
            GD.PrintErr($"Failed to bind UDP socket on port {udpPort}: {udpError}");
            _tcpServer.Stop();
            return udpError;
        }
        GD.Print($"UDP socket bound on port {udpPort}");

        return Error.Ok;
    }

    /// <summary>
    /// Stop the server and close all connections.
    /// </summary>
    public void StopServer()
    {
        foreach (ConnectedClient client in _clients.Values)
        {
            client.TcpConnection?.DisconnectFromHost();
        }
        _clients.Clear();

        _tcpServer?.Stop();
        _udpSocket?.Close();
    }

    /// <summary>
    /// Poll for new connections and incoming data. Call this every frame.
    /// </summary>
    public void Poll()
    {
        PollTcpConnections();
        PollTcpData();
        PollUdpData();
    }

    private void PollTcpConnections()
    {
        if (_tcpServer == null) return;

        while (_tcpServer.IsConnectionAvailable())
        {
            StreamPeerTcp tcpConnection = _tcpServer.TakeConnection();
            if (tcpConnection != null)
            {
                int clientId = _nextClientId++;
                ConnectedClient client = new ConnectedClient
                {
                    ClientId = clientId,
                    TcpConnection = tcpConnection,
                    UdpConfirmed = false,
                    TcpAckReceived = false
                };
                _clients[clientId] = client;

                GD.Print($"Client {clientId} connected via TCP from {tcpConnection.GetConnectedHost()}:{tcpConnection.GetConnectedPort()}");
                OnClientTcpConnected?.Invoke(clientId);
            }
        }
    }

    private void PollTcpData()
    {
        List<int> disconnectedClients = new List<int>();

        foreach (ConnectedClient client in _clients.Values)
        {
            StreamPeerTcp tcp = client.TcpConnection;
            if (tcp == null) continue;

            tcp.Poll();
            StreamPeerTcp.Status status = tcp.GetStatus();

            if (status == StreamPeerTcp.Status.Error || status == StreamPeerTcp.Status.None)
            {
                disconnectedClients.Add(client.ClientId);
                continue;
            }

            if (status != StreamPeerTcp.Status.Connected) continue;

            // Read available data
            int available = tcp.GetAvailableBytes();
            if (available > 0)
            {
                byte[] data = tcp.GetData(available)[1].AsByteArray();
                ProcessIncomingTcpData(client, data);
            }
        }

        foreach (int clientId in disconnectedClients)
        {
            GD.Print($"Client {clientId} disconnected");
            _clients.Remove(clientId);
            OnClientTcpDisconnected?.Invoke(clientId);
        }
    }

    private void ProcessIncomingTcpData(ConnectedClient client, byte[] newData)
    {
        // Append to receive buffer
        Array.Copy(newData, 0, client.TcpReceiveBuffer, client.TcpReceiveBufferOffset, newData.Length);
        client.TcpReceiveBufferOffset += newData.Length;

        // Process complete packets (length-prefixed: 4 bytes size + data)
        while (client.TcpReceiveBufferOffset >= 4)
        {
            int packetSize = BitConverter.ToInt32(client.TcpReceiveBuffer, 0);
            if (packetSize <= 0 || packetSize > 65000)
            {
                GD.PrintErr($"Invalid TCP packet size from client {client.ClientId}: {packetSize}");
                client.TcpReceiveBufferOffset = 0;
                break;
            }

            if (client.TcpReceiveBufferOffset >= 4 + packetSize)
            {
                // Extract complete packet
                byte[] packet = new byte[packetSize];
                Array.Copy(client.TcpReceiveBuffer, 4, packet, 0, packetSize);

                // Shift remaining data
                int remaining = client.TcpReceiveBufferOffset - 4 - packetSize;
                if (remaining > 0)
                {
                    Array.Copy(client.TcpReceiveBuffer, 4 + packetSize, client.TcpReceiveBuffer, 0, remaining);
                }
                client.TcpReceiveBufferOffset = remaining;

                // Deliver packet
                OnTcpDataReceived?.Invoke(client.ClientId, packet, packetSize);
            }
            else
            {
                // Not enough data yet
                break;
            }
        }
    }

    private void PollUdpData()
    {
        if (_udpSocket == null) return;

        while (_udpSocket.GetAvailablePacketCount() > 0)
        {
            byte[] packet = _udpSocket.GetPacket();
            string senderIp = _udpSocket.GetPacketIp();
            int senderPort = _udpSocket.GetPacketPort();

            // Find which client this is from (by matching TCP connection IP, or create mapping)
            ConnectedClient client = FindOrMapClientByUdp(senderIp, senderPort);
            if (client != null)
            {
                if (!client.UdpConfirmed)
                {
                    client.UdpConfirmed = true;
                    client.UdpAddress = senderIp;
                    client.UdpPort = senderPort;
                    GD.Print($"Client {client.ClientId} UDP confirmed from {senderIp}:{senderPort}");
                    OnClientUdpConfirmed?.Invoke(client.ClientId);
                }

                OnUdpDataReceived?.Invoke(client.ClientId, packet, packet.Length);
            }
            else
            {
                GD.Print($"Received UDP from unknown source: {senderIp}:{senderPort}");
            }
        }
    }

    private ConnectedClient FindOrMapClientByUdp(string ip, int port)
    {
        // First check if any client already has this UDP endpoint
        foreach (ConnectedClient client in _clients.Values)
        {
            if (client.UdpAddress == ip && client.UdpPort == port)
            {
                return client;
            }
        }

        // Otherwise, match by TCP IP (UDP port will differ due to NAT)
        foreach (ConnectedClient client in _clients.Values)
        {
            if (!client.UdpConfirmed && client.TcpConnection.GetConnectedHost() == ip)
            {
                return client;
            }
        }

        return null;
    }

    /// <summary>
    /// Send TCP data to a specific client. Uses length-prefix framing.
    /// </summary>
    public Error SendTcpToClient(int clientId, byte[] data, int size)
    {
        if (!_clients.TryGetValue(clientId, out ConnectedClient client))
        {
            return Error.InvalidParameter;
        }

        StreamPeerTcp tcp = client.TcpConnection;
        if (tcp == null || tcp.GetStatus() != StreamPeerTcp.Status.Connected)
        {
            return Error.ConnectionError;
        }

        // Send length prefix (4 bytes) + data
        byte[] sizeBytes = BitConverter.GetBytes(size);
        tcp.PutData(sizeBytes);
        tcp.PutData(data[..size]);

        return Error.Ok;
    }

    /// <summary>
    /// Send UDP data to a specific client.
    /// </summary>
    public Error SendUdpToClient(int clientId, byte[] data, int size)
    {
        if (!_clients.TryGetValue(clientId, out ConnectedClient client))
        {
            return Error.InvalidParameter;
        }

        if (!client.UdpConfirmed)
        {
            return Error.ConnectionError;  // Don't know their UDP endpoint yet
        }

        _udpSocket.SetDestAddress(client.UdpAddress, client.UdpPort);
        return _udpSocket.PutPacket(data[..size]);
    }

    /// <summary>
    /// Send TCP data to all connected clients.
    /// </summary>
    public void BroadcastTcp(byte[] data, int size)
    {
        foreach (int clientId in _clients.Keys)
        {
            SendTcpToClient(clientId, data, size);
        }
    }

    /// <summary>
    /// Send UDP data to all clients with confirmed UDP.
    /// </summary>
    public void BroadcastUdp(byte[] data, int size)
    {
        foreach (int clientId in _clients.Keys)
        {
            SendUdpToClient(clientId, data, size);
        }
    }

    public ConnectedClient GetClient(int clientId)
    {
        _clients.TryGetValue(clientId, out ConnectedClient client);
        return client;
    }

    public IEnumerable<ConnectedClient> GetAllClients()
    {
        return _clients.Values;
    }
}

/// <summary>
/// Client-side network layer handling TCP and UDP connections.
/// </summary>
public class GodotNetworkLayer_Client
{
    private StreamPeerTcp _tcp;
    private PacketPeerUdp _udp;
    private string _serverAddress;
    private int _serverTcpPort;
    private int _serverUdpPort;
    private bool _connected;
    private bool _receivedFirstFrameState;

    // TCP receive buffer for length-prefixed framing
    private byte[] _tcpReceiveBuffer = new byte[65536];
    private int _tcpReceiveBufferOffset = 0;

    // UDP "I'm here" spam
    private byte[] _udpHerePacket = new byte[] { (byte)ClientUdpPacketType.UDP_HERE };
    private double _lastUdpHereTime = 0;
    private const double UDP_HERE_INTERVAL = 0.05;  // 20hz

    // Callbacks
    public Action OnConnected;
    public Action OnDisconnected;
    public Action<byte[], int> OnTcpDataReceived;
    public Action<byte[], int> OnUdpDataReceived;

    public bool IsConnected => _connected;
    public bool ReceivedFirstFrameState
    {
        get => _receivedFirstFrameState;
        set => _receivedFirstFrameState = value;
    }

    public GodotNetworkLayer_Client()
    {
    }

    /// <summary>
    /// Connect to the server via TCP and set up UDP.
    /// </summary>
    public Error ConnectToServer(string address, int tcpPort, int udpPort)
    {
        _serverAddress = address;
        _serverTcpPort = tcpPort;
        _serverUdpPort = udpPort;
        _connected = false;
        _receivedFirstFrameState = false;

        // Connect TCP
        _tcp = new StreamPeerTcp();
        Error tcpError = _tcp.ConnectToHost(address, tcpPort);
        if (tcpError != Error.Ok)
        {
            GD.PrintErr($"Failed to initiate TCP connection to {address}:{tcpPort}: {tcpError}");
            return tcpError;
        }

        // Set up UDP (bind to any available port)
        _udp = new PacketPeerUdp();
        Error udpError = _udp.Bind(0);  // 0 = any available port
        if (udpError != Error.Ok)
        {
            GD.PrintErr($"Failed to bind UDP socket: {udpError}");
            _tcp.DisconnectFromHost();
            return udpError;
        }

        // Set UDP destination
        _udp.SetDestAddress(address, udpPort);

        GD.Print($"Connecting to server at {address} (TCP:{tcpPort}, UDP:{udpPort})");
        return Error.Ok;
    }

    /// <summary>
    /// Disconnect from server.
    /// </summary>
    public void Disconnect()
    {
        _tcp?.DisconnectFromHost();
        _udp?.Close();
        _connected = false;
        _receivedFirstFrameState = false;
    }

    /// <summary>
    /// Poll for connection status and incoming data. Call every frame.
    /// </summary>
    public void Poll(double currentTime)
    {
        PollTcp();
        PollUdp();

        // Spam "I'm here" UDP packets until we receive first frame state
        if (_connected && !_receivedFirstFrameState)
        {
            if (currentTime - _lastUdpHereTime >= UDP_HERE_INTERVAL)
            {
                _udp.PutPacket(_udpHerePacket);
                _lastUdpHereTime = currentTime;
            }
        }
    }

    private void PollTcp()
    {
        if (_tcp == null) return;

        _tcp.Poll();
        StreamPeerTcp.Status status = _tcp.GetStatus();

        if (status == StreamPeerTcp.Status.Connected && !_connected)
        {
            _connected = true;
            GD.Print("TCP connected to server");
            OnConnected?.Invoke();
        }
        else if ((status == StreamPeerTcp.Status.Error || status == StreamPeerTcp.Status.None) && _connected)
        {
            _connected = false;
            GD.Print("TCP disconnected from server");
            OnDisconnected?.Invoke();
            return;
        }

        if (status != StreamPeerTcp.Status.Connected) return;

        // Read available data
        int available = _tcp.GetAvailableBytes();
        if (available > 0)
        {
            byte[] data = _tcp.GetData(available)[1].AsByteArray();
            ProcessIncomingTcpData(data);
        }
    }

    private void ProcessIncomingTcpData(byte[] newData)
    {
        // Append to receive buffer
        Array.Copy(newData, 0, _tcpReceiveBuffer, _tcpReceiveBufferOffset, newData.Length);
        _tcpReceiveBufferOffset += newData.Length;

        // Process complete packets (length-prefixed: 4 bytes size + data)
        while (_tcpReceiveBufferOffset >= 4)
        {
            int packetSize = BitConverter.ToInt32(_tcpReceiveBuffer, 0);
            if (packetSize <= 0 || packetSize > 65000)
            {
                GD.PrintErr($"Invalid TCP packet size from server: {packetSize}");
                _tcpReceiveBufferOffset = 0;
                break;
            }

            if (_tcpReceiveBufferOffset >= 4 + packetSize)
            {
                // Extract complete packet
                byte[] packet = new byte[packetSize];
                Array.Copy(_tcpReceiveBuffer, 4, packet, 0, packetSize);

                // Shift remaining data
                int remaining = _tcpReceiveBufferOffset - 4 - packetSize;
                if (remaining > 0)
                {
                    Array.Copy(_tcpReceiveBuffer, 4 + packetSize, _tcpReceiveBuffer, 0, remaining);
                }
                _tcpReceiveBufferOffset = remaining;

                // Deliver packet
                OnTcpDataReceived?.Invoke(packet, packetSize);
            }
            else
            {
                // Not enough data yet
                break;
            }
        }
    }

    private void PollUdp()
    {
        if (_udp == null) return;

        while (_udp.GetAvailablePacketCount() > 0)
        {
            byte[] packet = _udp.GetPacket();
            OnUdpDataReceived?.Invoke(packet, packet.Length);
        }
    }

    /// <summary>
    /// Send TCP data to server. Uses length-prefix framing.
    /// </summary>
    public Error SendTcpToServer(byte[] data, int size)
    {
        if (_tcp == null || !_connected)
        {
            return Error.ConnectionError;
        }

        // Send length prefix (4 bytes) + data
        byte[] sizeBytes = BitConverter.GetBytes(size);
        _tcp.PutData(sizeBytes);
        _tcp.PutData(data[..size]);

        return Error.Ok;
    }

    /// <summary>
    /// Send UDP data to server.
    /// </summary>
    public Error SendUdpToServer(byte[] data, int size)
    {
        if (_udp == null)
        {
            return Error.ConnectionError;
        }

        return _udp.PutPacket(data[..size]);
    }
}
