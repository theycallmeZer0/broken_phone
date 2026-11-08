using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed partial class SteamBridge
    {
        sealed class Tunnel : IDisposable
        {
            public SteamBridge Bridge; public TcpClient Socket; public uint Connection; public ulong Peer; public Guid Id; public bool HostSide;
            public readonly BlockingCollection<byte[]> Incoming = new BlockingCollection<byte[]>(256);
            public readonly ManualResetEvent Accepted = new ManualResetEvent(false);
            public volatile bool Closed;
            public string Key { get { return Peer + ":" + Id + ":" + HostSide; } }
            public void Run()
            {
                Background(() =>
                {
                    try
                    {
                        if (!Accepted.WaitOne(20000) || Closed) throw new IOException("Steam P2P не ответил.");
                        var stream = Socket.GetStream(); var buffer = new byte[32768]; int read;
                        while (!Closed && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        { var bytes = new byte[read]; Buffer.BlockCopy(buffer, 0, bytes, 0, read); Bridge.SendPacket(this, 2, bytes); }
                    }
                    catch (Exception e) { if (String.IsNullOrEmpty(Bridge.transportError)) Bridge.transportError = e.Message; }
                    finally { Close(true); }
                });
                Background(() =>
                {
                    try { var stream = Socket.GetStream(); foreach (var bytes in Incoming.GetConsumingEnumerable()) { if (Closed) break; stream.Write(bytes, 0, bytes.Length); } }
                    catch (Exception e) { if (String.IsNullOrEmpty(Bridge.transportError)) Bridge.transportError = e.Message; }
                    finally { Close(true); }
                });
            }
            public void Close(bool notify)
            {
                lock (Bridge.Gate)
                {
                    if (Closed) return; Closed = true; Accepted.Set(); Incoming.CompleteAdding(); Socket.Close(); Bridge.tunnels.Remove(Key);
                }
                if (notify) try { Bridge.SendPacket(this, 3, new byte[0]); } catch { }
                Bridge.ReleasePackConnection(Connection);
            }
            public void Dispose() { Close(false); }
        }
        sealed class SteamConnection { public ulong Peer; public bool HostSide, Pack; }
        readonly Dictionary<uint, SteamConnection> steamConnections = new Dictionary<uint, SteamConnection>();
        uint steamListener;
        uint packSteamListener;
        int packPort;
        readonly Dictionary<string, Tunnel> tunnels = new Dictionary<string, Tunnel>();
        readonly List<TcpListener> tunnelListeners = new List<TcpListener>();
        public int OpenTunnel(ulong peer)
        {
            return OpenTunnel(peer, 5);
        }
        public int OpenPackTunnel(ulong peer) { return OpenTunnel(peer, 6); }
        public void ListenForPackPeers(int port)
        {
            lock (Gate)
            {
                if (!Available || LobbyId == 0 || port <= 0) return;
                packPort = port;
                if (packSteamListener == 0) packSteamListener = SteamNative.SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P(networking, 6, 0, IntPtr.Zero);
                if (packSteamListener == 0) throw new IOException("Steam не открыл канал помощи с загрузкой пака.");
            }
        }
        void ReleasePackConnection(uint connection)
        {
            lock (Gate)
            {
                SteamConnection info;
                if (steamConnections.TryGetValue(connection, out info) && info.Pack && !tunnels.Values.Any(t => t.Connection == connection))
                {
                    steamConnections.Remove(connection);
                    if (Available) SteamNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(networking, connection, 1000, "Pack transfer closed", false);
                }
            }
        }
        int OpenTunnel(ulong peer, int virtualPort)
        {
            lock (Gate)
            {
                if (!Available || !AllowedPeer(peer)) throw new IOException("Участник Steam не состоит в этой комнате.");
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1); tunnelListeners.Add(listener); int generation = roomGeneration; int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Background(() =>
                {
                    try
                    {
                        var socket = listener.AcceptTcpClient(); socket.NoDelay = true; socket.SendTimeout = 15000;
                        var tunnel = new Tunnel { Bridge = this, Socket = socket, Peer = peer, Id = Guid.NewGuid() };
                        lock (Gate) { if (disposed || generation != roomGeneration || !AllowedPeer(peer)) { socket.Close(); return; } var identity = new SteamNative.Identity { Type = 16, Size = 8, SteamId = peer };
                            tunnel.Connection = SteamNative.SteamAPI_ISteamNetworkingSockets_ConnectP2P(networking, ref identity, virtualPort, 0, IntPtr.Zero);
                            if (tunnel.Connection == 0) { socket.Close(); return; }
                            steamConnections.Add(tunnel.Connection, new SteamConnection { Peer = peer, Pack = virtualPort == 6 }); tunnels.Add(tunnel.Key, tunnel); }
                        tunnel.Run(); SendPacket(tunnel, 1, new byte[0]);
                    }
                    catch (Exception) { }
                    finally { lock (Gate) tunnelListeners.Remove(listener); listener.Stop(); }
                });
                return port;
            }
        }
        void SendPacket(Tunnel tunnel, byte kind, byte[] data)
        {
            var packet = new byte[21 + data.Length]; packet[0] = 66; packet[1] = 80; packet[2] = 84; packet[3] = 5; packet[4] = kind;
            Buffer.BlockCopy(tunnel.Id.ToByteArray(), 0, packet, 5, 16); Buffer.BlockCopy(data, 0, packet, 21, data.Length);
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!disposed)
            {
                int result;
                lock (Gate) { if (!Available) throw new IOException("Steam отключён."); long number; result = SteamNative.SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(networking, tunnel.Connection, packet, (uint)packet.Length, 9, out number); }
                if (result == 1) { sentPackets++; return; }
                if (result != 25 || DateTime.UtcNow >= deadline || tunnel.Closed)
                {
                    throw new IOException("Ошибка передачи Steam P2P: " + result);
                }
                Thread.Sleep(8);
            }
            throw new IOException("Steam закрыт.");
        }
        void SocketStatus(IntPtr callback)
        {
            uint connection = unchecked((uint)Marshal.ReadInt32(callback)); IntPtr info = IntPtr.Add(callback, 8);
            int state = Marshal.ReadInt32(info, 176); uint listener = unchecked((uint)Marshal.ReadInt32(info, 144));
            ulong peer = SteamNative.SteamAPI_SteamNetworkingIdentity_GetSteamID64(info);
            if (state == 1 && listener != 0)
            {
                bool packChannel = listener == packSteamListener && packSteamListener != 0;
                if ((!packChannel && (!Hosting || listener != steamListener)) || !AllowedPeer(peer) || steamConnections.Count >= 64) { SteamNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(networking, connection, 1000, "Not a lobby member", false); return; }
                if (SteamNative.SteamAPI_ISteamNetworkingSockets_AcceptConnection(networking, connection) != 1) return;
                steamConnections[connection] = new SteamConnection { Peer = peer, HostSide = true, Pack = packChannel }; sessionRequests++;
            }
            else if (state == 4 || state == 5)
            {
                foreach (var tunnel in tunnels.Values.Where(t => t.Connection == connection).ToArray()) tunnel.Close(false);
                steamConnections.Remove(connection); SteamNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(networking, connection, 1000, "Disconnected", false);
            }
        }
        void ReceiveSocketPackets()
        {
            var messages = new IntPtr[64];
            foreach (var connection in steamConnections.ToArray())
            {
                int count = SteamNative.SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(networking, connection.Key, messages, messages.Length);
                for (int i = 0; i < count; i++)
                {
                    IntPtr message = messages[i];
                    try
                    {
                        receivedPackets++; int size = Marshal.ReadInt32(message, 8); if (size < 21 || size > 32789) continue;
                        ulong peer = SteamNative.SteamAPI_SteamNetworkingIdentity_GetSteamID64(IntPtr.Add(message, 16));
                        if (peer != connection.Value.Peer || !AllowedPeer(peer)) { rejectedPackets++; continue; }
                        byte[] bytes = new byte[size]; Marshal.Copy(Marshal.ReadIntPtr(message), bytes, 0, size); RoutePacket(peer, connection.Value.HostSide ? 0 : 1, bytes, connection.Key);
                    }
                    finally { SteamNative.SteamAPI_SteamNetworkingMessage_t_Release(message); }
                }
            }
        }
        void RoutePacket(ulong peer, int channel, byte[] packet, uint connection)
        {
            if (packet[0] != 66 || packet[1] != 80 || packet[2] != 84 || packet[3] != 5) return;
            var idBytes = new byte[16]; Buffer.BlockCopy(packet, 5, idBytes, 0, 16); Guid id = new Guid(idBytes); string key = peer + ":" + id + ":" + (channel == 0); Tunnel tunnel;
            if (packet[4] == 1)
            {
                SteamConnection route; if (!steamConnections.TryGetValue(connection, out route)) return;
                int port = route.Pack ? packPort : hostPort;
                if (channel != 0 || !route.Pack && !Hosting || port == 0 || tunnels.ContainsKey(key) || tunnels.Values.Count(t => t.HostSide) >= (route.Pack ? 56 : 7)) return;
                var socket = new TcpClient(); socket.NoDelay = true; socket.SendTimeout = 15000;
                tunnel = new Tunnel { Bridge = this, Socket = socket, Id = id, Peer = peer, HostSide = true, Connection = connection }; tunnels.Add(key, tunnel);
                Background(() =>
                {
                    try { var localConnect = socket.ConnectAsync("127.0.0.1", port); if (!localConnect.Wait(5000)) throw new IOException("Нет подключения к ведущему."); tunnel.Accepted.Set(); SendPacket(tunnel, 4, new byte[0]); tunnel.Run(); }
                    catch (Exception e) { transportError = "host pipe: " + e.Message; tunnel.Close(true); }
                });
                return;
            }
            if (!tunnels.TryGetValue(key, out tunnel) || tunnel.Closed) return;
            if (packet[4] == 4 && !tunnel.HostSide) tunnel.Accepted.Set();
            else if (packet[4] == 3) tunnel.Close(false);
            else if (packet[4] == 2)
            {
                var bytes = new byte[packet.Length - 21]; Buffer.BlockCopy(packet, 21, bytes, 0, bytes.Length);
                if (!tunnel.Incoming.TryAdd(bytes)) tunnel.Close(false);
            }
        }
        void CloseTunnels()
        {
            lock (Gate)
            {
                foreach (var listener in tunnelListeners.ToArray()) listener.Stop(); tunnelListeners.Clear();
                foreach (var tunnel in tunnels.Values.ToArray()) tunnel.Close(false); tunnels.Clear();
                if (Available) { foreach (uint connection in steamConnections.Keys.ToArray()) SteamNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(networking, connection, 1000, "Room closed", false); if (steamListener != 0) SteamNative.SteamAPI_ISteamNetworkingSockets_CloseListenSocket(networking, steamListener); if (packSteamListener != 0) SteamNative.SteamAPI_ISteamNetworkingSockets_CloseListenSocket(networking, packSteamListener); }
                steamConnections.Clear(); steamListener = packSteamListener = 0; packPort = 0;
            }
        }
    }
}
