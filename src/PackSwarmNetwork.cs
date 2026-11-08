using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed partial class RoomServer
    {
        readonly PackPeerStore packStore = new PackPeerStore();
        PackPeerServer packShareServer;
        readonly Dictionary<string, PackPeerEndpoint> packPeers = new Dictionary<string, PackPeerEndpoint>();
        readonly ConcurrentDictionary<string, Lazy<PackTransferPlan>> packPlans = new ConcurrentDictionary<string, Lazy<PackTransferPlan>>();
        internal long PackUploadBytes { get { return Interlocked.Read(ref packStore.SentBytes); } }
        public int PackSharePort { get { return packShareServer.Port; } }
        void StartPackSharing() { packShareServer = new PackPeerServer(packStore); ResetPackSharing(); }
        void ResetPackSharing() { packPeers.Clear(); packPlans.Clear(); packStore.Select(pack.contentKey); if (pack.Selected) packStore.Share(pack.PublicCopy(), pack); }
        bool ProcessPackSharing(Peer peer, Message message)
        {
            if (message.kind == "pack_share")
            {
                var source = message.packPeer;
                if (message.code == pack.contentKey && source != null && source.port > 0 && source.port <= 65535 && PackTransferFiles.IsHash(source.token))
                    packPeers[peer.Id] = new PackPeerEndpoint { id = peer.Id, address = ((IPEndPoint)peer.Socket.Client.RemoteEndPoint).Address.ToString(), port = source.port, token = source.token, steamId = state.players.First(p => p.id == peer.Id).steamId };
                return true;
            }
            if (message.kind != "pack_sources") return false;
            var selected = pack;
            Task.Run(() =>
            {
                try
                {
                    if (message.code != selected.contentKey || !selected.Selected) throw new IOException("Выбранный пак изменился.");
                    var expected = PackTransferFiles.Expected(selected, message.asset);
                    string cacheKey = selected.contentKey + ":" + message.asset;
                    var description = packPlans.GetOrAdd(cacheKey, _ => new Lazy<PackTransferPlan>(() =>
                    {
                        string path = PackTransferFiles.Local(selected, message.asset);
                        if (!PackSources.Matches(path, expected, CancellationToken.None)) throw new IOException("Файл выбранного пака изменился.");
                        return new PackTransferPlan { key = selected.contentKey, asset = message.asset, bytes = expected.bytes, hash = expected.hash, chunks = PackTransferFiles.Chunks(path) };
                    })).Value;
                    lock (gate)
                    {
                        if (disposed || selected.contentKey != pack.contentKey || !peers.Contains(peer)) throw new IOException("Выбранный пак изменился.");
                        var host = state.players.FirstOrDefault(p => p.host);
                        var sources = packPeers.Values.Where(p => p.id != peer.Id && p.id != (host == null ? "" : host.id)).ToList();
                        sources.Add(new PackPeerEndpoint { id = "host", host = true, port = packShareServer.Port, token = packShareServer.Token, steamId = host == null ? 0 : host.steamId });
                        peer.Send(new Message { kind = "pack_sources", id = message.id, packPlan = new PackTransferPlan { key = description.key, asset = description.asset, bytes = description.bytes, hash = description.hash, chunks = description.chunks, peers = sources.ToArray() } });
                    }
                }
                catch (Exception error) { try { peer.Send(new Message { kind = "pack_sources", id = message.id, text = error.Message }); } catch (IOException) { } }
            });
            return true;
        }
    }
    public sealed partial class RoomClient
    {
        readonly PackPeerStore packStore = new PackPeerStore();
        PackPeerServer packShareServer;
        string roomAddress;
        readonly object workshopOfferGate = new object();
        string offeredSelection, offeredPackKey;
        readonly ConcurrentDictionary<string, TaskCompletionSource<PackTransferPlan>> packRequests = new ConcurrentDictionary<string, TaskCompletionSource<PackTransferPlan>>();
        public Func<ulong, int> OpenPackTunnel;
        int packHelperCount;
        public int PackHelperCount { get { return Volatile.Read(ref packHelperCount); } }
        internal long PackUploadBytes { get { return Interlocked.Read(ref packStore.SentBytes); } }
        public int PackSharePort { get { return packShareServer == null ? 0 : packShareServer.Port; } }
        void StartPackSharing(string host)
        {
            roomAddress = host; packShareServer = new PackPeerServer(packStore);
            packStore.Available += () =>
            {
                try
                {
                    lock (workshopOfferGate)
                    {
                        if (!disposed && knownPack != null && !knownPack.pendingDownload) Send(new Message { kind = "pack_share", code = knownPack.contentKey, packPeer = new PackPeerEndpoint { port = packShareServer.Port, token = packShareServer.Token } });
                    }
                }
                catch (IOException) { }
            };
        }
        void SelectPackSharing(Pack selected)
        {
            lock (workshopOfferGate)
            {
                if (selected.pendingDownload && selected.contentKey == offeredSelection) { packStore.Select(offeredPackKey); return; }
                offeredSelection = null; offeredPackKey = null; packStore.Select(selected.contentKey);
            }
        }
        public void SharePack(Pack remote, Pack local) { packStore.Share(remote, local); }
        public void OfferWorkshopPack(string selectionToken, Pack local)
        {
            PackContent.ValidateDescriptor(local);
            lock (workshopOfferGate)
            {
                if (disposed || knownPack == null || !knownPack.pendingDownload || knownPack.contentKey != selectionToken || knownPack.workshopId != local.workshopId || packShareServer == null) return;
                offeredSelection = selectionToken; offeredPackKey = local.contentKey;
                packStore.Select(local.contentKey); packStore.Share(local.PublicCopy(), local);
                Send(new Message { kind = "workshop_offer", code = selectionToken, pack = local.PublicCopy(), packPeer = new PackPeerEndpoint { port = packShareServer.Port, token = packShareServer.Token } });
            }
        }
        public async Task<Pack> PrepareWorkshopOffer(Pack remote, PackPeerEndpoint source, Action<PackProgress> progress, CancellationToken token, string sourceFolder)
        {
            token.ThrowIfCancellationRequested(); PackContent.ValidateDescriptor(remote);
            if (source == null || String.IsNullOrEmpty(source.id) || source.host || source.port <= 0 || source.port > 65535 || !PackTransferFiles.IsHash(source.token)) throw new InvalidDataException("Некорректный источник пака.");
            var offeredStore = new PackPeerStore(); offeredStore.Select(remote.contentKey);
            var download = new PackSwarmDownload(offeredStore, (asset, cancellation) => WorkshopOfferPlan(remote, source, asset, cancellation), ConnectPackPeer);
            var relay = new PackSwarmDownload(offeredStore, async (asset, cancellation) =>
            {
                var plan = await WorkshopRelayPlan(remote, source, asset, cancellation).ConfigureAwait(false); plan.peers = new[] { source }; return plan;
            }, null, (endpoint, asset, index, cancellation) => WorkshopRelayChunk(remote, source, asset, index, cancellation));
            bool relayOnly = false;
            return await PackContent.Prepare(remote, async (asset, extension, report, cancellation) =>
            {
                if (!relayOnly)
                {
                    try { return await download.Fetch(remote, asset, extension, report, cancellation).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); throw; }
                    catch (Exception error) { cancellation.ThrowIfCancellationRequested(); if (!(error is IOException) && !(error is TimeoutException) && !(error is SocketException)) throw; }
                    relayOnly = true; progress(PackProgress.Step("Прямой канал недоступен. Получаем пак через соединение комнаты…"));
                }
                return await relay.Fetch(remote, asset, extension, report, cancellation).ConfigureAwait(false);
            }, progress, token, true, sourceFolder).ConfigureAwait(false);
        }
        async Task<PackTransferPlan> WorkshopOfferPlan(Pack remote, PackPeerEndpoint source, string asset, CancellationToken token)
        {
            using (var socket = await ConnectPackPeer(source, token).ConfigureAwait(false))
            using (token.Register(socket.Close))
            {
                try
                {
                    return await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested(); var stream = socket.GetStream();
                        Wire.Send(stream, socket, new Message { kind = "pack_plan", secret = source.token, code = remote.contentKey, asset = asset });
                        var reply = Wire.Read(stream);
                        if (reply.kind != "pack_plan" || reply.packPlan == null) throw new IOException(reply.text ?? "Участник не передал данные пака.");
                        var plan = reply.packPlan; plan.peers = new[] { source }; return plan;
                    }, token).ConfigureAwait(false);
                }
                catch (Exception) { token.ThrowIfCancellationRequested(); throw; }
            }
        }
        void ReceivePackSources(Message message)
        {
            TaskCompletionSource<PackTransferPlan> request;
            if (!packRequests.TryGetValue(message.id ?? "", out request)) return;
            if (message.packPlan == null) request.TrySetException(new IOException(message.text)); else request.TrySetResult(message.packPlan);
        }
        public async Task<PackTransferPlan> PackSourcesFor(Pack remote, string asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); string id = Guid.NewGuid().ToString("N");
            var request = new TaskCompletionSource<PackTransferPlan>(TaskCreationOptions.RunContinuationsAsynchronously); packRequests[id] = request;
            try
            {
                Send(new Message { kind = "pack_sources", id = id, code = remote.contentKey, asset = asset });
                if (await Task.WhenAny(request.Task, Task.Delay(30000, token)).ConfigureAwait(false) != request.Task) { token.ThrowIfCancellationRequested(); throw new TimeoutException("Не удалось получить список участников для загрузки пака."); }
                var result = await request.Task.ConfigureAwait(false);
                Interlocked.Exchange(ref packHelperCount, result.peers.Count(p => !p.host)); return result;
            }
            finally { TaskCompletionSource<PackTransferPlan> ignored; packRequests.TryRemove(id, out ignored); }
        }
        public async Task WaitForPackHelpers(Pack remote, CancellationToken token)
        {
            string asset = PackSources.Native(remote) ? "packfile:" + remote.sourceFiles.FindIndex(f => f.path == remote.sourceVideo) : "video";
            while (true)
            {
                var sources = await PackSourcesFor(remote, asset, token).ConfigureAwait(false);
                if (sources.peers.Any(p => !p.host)) return;
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        public async Task<string> PackAsset(Pack remote, string asset, string extension, Action<long, long> progress, CancellationToken token)
        {
            var expected = PackTransferFiles.Expected(remote, asset);
            try { return await new PackSwarmDownload(packStore, (name, cancel) => PackSourcesFor(remote, name, cancel), ConnectPackPeer).Fetch(remote, asset, extension, progress, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw; }
            catch (Exception error) { token.ThrowIfCancellationRequested(); if (!(error is IOException) && !(error is TimeoutException) && !(error is SocketException)) throw; }
            // The established room channel remains available when direct peer routes fail.
            string file = await Asset(asset, extension, progress, token, remote.contentKey).ConfigureAwait(false);
            if (!PackSources.Matches(file, expected, token)) throw new InvalidDataException("Повреждён файл, полученный у ведущего.");
            return file;
        }
        async Task<TcpClient> ConnectPackPeer(PackPeerEndpoint source, CancellationToken token)
        {
            string address = source.host ? roomAddress : source.address; int port = source.port;
            if (source.steamId != 0 && OpenPackTunnel != null) try { port = OpenPackTunnel(source.steamId); address = "127.0.0.1"; } catch (IOException) { }
            var socket = new TcpClient { NoDelay = true, ReceiveTimeout = 8000, SendTimeout = 8000 };
            try
            {
                using (token.Register(socket.Close))
                {
                    var pending = socket.ConnectAsync(address, port);
                    if (await Task.WhenAny(pending, Task.Delay(5000, token)).ConfigureAwait(false) != pending) { token.ThrowIfCancellationRequested(); throw new TimeoutException("Участник не отвечает."); }
                    await pending.ConfigureAwait(false); return socket;
                }
            }
            catch { socket.Close(); throw; }
        }
    }
}
