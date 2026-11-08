using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed partial class RoomServer
    {
        sealed class WorkshopOfferRegistration { public string Selection, Key; }
        sealed class WorkshopRelayRoute
        {
            public Peer Requestor, Source;
            public Message Request;
            public DateTime Deadline;
        }
        readonly Dictionary<string, WorkshopOfferRegistration> workshopRelayOffers = new Dictionary<string, WorkshopOfferRegistration>();
        readonly Dictionary<string, WorkshopRelayRoute> workshopRelayRoutes = new Dictionary<string, WorkshopRelayRoute>();
        long workshopRelayBytes;
        internal long WorkshopRelayBytes { get { return Interlocked.Read(ref workshopRelayBytes); } }

        void PruneWorkshopRelay()
        {
            foreach (var id in workshopRelayOffers.Where(pair => pair.Value.Selection != pack.contentKey || !peers.Any(peer => peer.Id == pair.Key)).Select(pair => pair.Key).ToArray()) workshopRelayOffers.Remove(id);
            foreach (var id in workshopRelayRoutes.Where(pair => pair.Value.Request.code != pack.contentKey || pair.Value.Deadline < DateTime.UtcNow || !peers.Contains(pair.Value.Requestor) || !peers.Contains(pair.Value.Source)).Select(pair => pair.Key).ToArray()) workshopRelayRoutes.Remove(id);
        }
        void RegisterWorkshopOffer(Peer peer, string selectionToken, string contentKey)
        {
            PruneWorkshopRelay();
            if (pack.pendingDownload && pack.contentKey == selectionToken && PackTransferFiles.IsHash(contentKey))
                workshopRelayOffers[peer.Id] = new WorkshopOfferRegistration { Selection = selectionToken, Key = contentKey };
        }
        void RemoveWorkshopRelayPeer(Peer peer)
        {
            workshopRelayOffers.Remove(peer.Id);
            foreach (var id in workshopRelayRoutes.Where(pair => pair.Value.Requestor == peer || pair.Value.Source == peer).Select(pair => pair.Key).ToArray())
            {
                var route = workshopRelayRoutes[id]; workshopRelayRoutes.Remove(id);
                if (route.Requestor != peer) WorkshopRelayError(route.Requestor, route.Request, "Участник отключился во время передачи пака.");
            }
        }
        internal static bool WorkshopRelayAsset(string asset)
        {
            int index;
            return asset == "video" || asset == "background" || asset != null && asset.StartsWith("packfile:", StringComparison.Ordinal) && Int32.TryParse(asset.Substring(9), out index) && index >= 0 && index < 5000;
        }
        internal static Message WorkshopRelayReply(Message request, string error)
        {
            return new Message { kind = "workshop_relay_result", id = request.id, code = request.code, round = request.round, asset = request.asset, index = request.index, flag = request.flag, text = error };
        }
        static void WorkshopRelayError(Peer peer, Message request, string error)
        {
            try { peer.Send(WorkshopRelayReply(request, error)); } catch (IOException) { }
        }
        bool ProcessWorkshopRelay(Peer peer, Player player, Message message)
        {
            if (message.kind != "workshop_relay_request" && message.kind != "workshop_relay_result" && message.kind != "workshop_relay_cancel") return false;
            PruneWorkshopRelay();
            WorkshopRelayRoute route;
            if (message.kind == "workshop_relay_cancel")
            {
                if (workshopRelayRoutes.TryGetValue(message.id ?? "", out route) && route.Requestor == peer) workshopRelayRoutes.Remove(message.id);
                return true;
            }
            if (message.kind == "workshop_relay_result")
            {
                if (!workshopRelayRoutes.TryGetValue(message.id ?? "", out route) || route.Source != peer) return true;
                workshopRelayRoutes.Remove(message.id);
                var request = route.Request;
                if (!pack.pendingDownload || request.code != pack.contentKey || message.code != request.code || message.round != request.round || message.asset != request.asset || message.flag != request.flag || message.index != request.index) return true;
                if (message.text != null && message.text.Length > 240 || message.data != null && message.data.Length > (PackTransferFiles.ChunkSize + 2) / 3 * 4 || message.packPlan != null && (message.packPlan.key != request.round || message.packPlan.asset != request.asset || !PackTransferFiles.IsHash(message.packPlan.hash) || message.packPlan.bytes < 0 || message.packPlan.bytes > 2L * 1024 * 1024 * 1024 || message.packPlan.chunks == null || message.packPlan.chunks.Length > 8192 || message.packPlan.chunks.Any(hash => !PackTransferFiles.IsHash(hash))))
                {
                    WorkshopRelayError(route.Requestor, request, "Участник прислал некорректный ответ передачи пака."); return true;
                }
                var reply = WorkshopRelayReply(request, message.text); reply.data = message.data;
                if (message.packPlan != null) reply.packPlan = new PackTransferPlan { key = message.packPlan.key, asset = message.packPlan.asset, hash = message.packPlan.hash, bytes = message.packPlan.bytes, chunks = message.packPlan.chunks };
                if (!request.flag && message.data != null) Interlocked.Add(ref workshopRelayBytes, message.data.Length / 4 * 3 - (message.data.EndsWith("==", StringComparison.Ordinal) ? 2 : message.data.EndsWith("=", StringComparison.Ordinal) ? 1 : 0));
                try { route.Requestor.Send(reply); } catch (IOException) { }
                return true;
            }
            Guid parsed; WorkshopOfferRegistration offer;
            var source = peers.FirstOrDefault(member => member.Id == message.text && member != peer);
            if (!player.host || !pack.pendingDownload || state.phase != "lobby" || message.code != pack.contentKey || !Guid.TryParseExact(message.id, "N", out parsed) || !PackTransferFiles.IsHash(message.round) || source == null || !workshopRelayOffers.TryGetValue(source.Id, out offer) || offer.Selection != message.code || offer.Key != message.round || !WorkshopRelayAsset(message.asset) || message.index < 0 || message.index >= 8192)
            {
                WorkshopRelayError(peer, message, "Источник не предлагал файлы текущего выбранного пака."); return true;
            }
            if (workshopRelayRoutes.Count >= 32 || workshopRelayRoutes.ContainsKey(message.id))
            {
                WorkshopRelayError(peer, message, "Слишком много запросов передачи пака."); return true;
            }
            workshopRelayRoutes[message.id] = new WorkshopRelayRoute { Requestor = peer, Source = source, Request = message, Deadline = DateTime.UtcNow.AddSeconds(35) };
            try
            {
                source.Send(new Message { kind = "workshop_relay_request", id = message.id, code = message.code, round = message.round, asset = message.asset, index = message.index, flag = message.flag });
            }
            catch (IOException) { workshopRelayRoutes.Remove(message.id); WorkshopRelayError(peer, message, "Участник отключился во время передачи пака."); }
            return true;
        }
    }

    public sealed partial class RoomClient
    {
        sealed class WorkshopRelayPending
        {
            public Message Request;
            public readonly TaskCompletionSource<Message> Completion = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        readonly ConcurrentDictionary<string, WorkshopRelayPending> workshopRelayRequests = new ConcurrentDictionary<string, WorkshopRelayPending>();
        readonly SemaphoreSlim workshopRelayServeSlots = new SemaphoreSlim(4, 4);
        readonly CancellationTokenSource workshopRelayClosed = new CancellationTokenSource();
        int workshopRelayServing;

        bool ProcessWorkshopRelay(Message message)
        {
            if (message.kind == "workshop_relay_result")
            {
                WorkshopRelayPending pending;
                if (workshopRelayRequests.TryGetValue(message.id ?? "", out pending))
                {
                    var request = pending.Request;
                    if (message.code == request.code && message.round == request.round && message.asset == request.asset && message.index == request.index && message.flag == request.flag)
                        pending.Completion.TrySetResult(message);
                }
                return true;
            }
            if (message.kind != "workshop_relay_request") return false;
            if (Interlocked.Increment(ref workshopRelayServing) > 8)
            {
                Interlocked.Decrement(ref workshopRelayServing);
                try { Send(RoomServer.WorkshopRelayReply(message, "Слишком много запросов передачи пака.")); } catch (IOException) { }
                return true;
            }
            var ignored = ServeWorkshopRelay(message); return true;
        }
        async Task ServeWorkshopRelay(Message message)
        {
            bool entered = false;
            try
            {
                await workshopRelayServeSlots.WaitAsync(workshopRelayClosed.Token).ConfigureAwait(false); entered = true;
                var reply = RoomServer.WorkshopRelayReply(message, null);
                await Task.Run(() =>
                {
                    lock (workshopOfferGate)
                    {
                        if (disposed || knownPack == null || !knownPack.pendingDownload || knownPack.contentKey != message.code || offeredSelection != message.code || offeredPackKey != message.round) throw new IOException("Предложение выбранного пака устарело.");
                    }
                    if (!RoomServer.WorkshopRelayAsset(message.asset) || message.index < 0 || message.index >= 8192) throw new IOException("Этот файл не входит в выбранный пак.");
                    if (message.flag) reply.packPlan = packStore.Plan(message.round, message.asset);
                    else reply.data = Convert.ToBase64String(packStore.Read(message.round, message.asset, message.index));
                }, workshopRelayClosed.Token).ConfigureAwait(false);
                if (!disposed && !workshopRelayClosed.IsCancellationRequested) Send(reply);
            }
            catch (Exception error)
            {
                if (!disposed && !workshopRelayClosed.IsCancellationRequested)
                    try { Send(RoomServer.WorkshopRelayReply(message, error is IOException ? error.Message : "Не удалось передать файл выбранного пака.")); } catch (IOException) { }
            }
            finally { if (entered) workshopRelayServeSlots.Release(); Interlocked.Decrement(ref workshopRelayServing); }
        }
        void CloseWorkshopRelay(string reason)
        {
            workshopRelayClosed.Cancel();
            foreach (var pending in workshopRelayRequests.Values) pending.Completion.TrySetException(new IOException(reason));
            workshopRelayRequests.Clear();
        }
        async Task<Message> RequestWorkshopRelay(Pack remote, PackPeerEndpoint source, string asset, int chunk, bool plan, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string selection;
            lock (workshopOfferGate)
            {
                if (disposed || knownPack == null || !knownPack.pendingDownload || knownPack.workshopId != remote.workshopId) throw new IOException("Выбранный пак изменился.");
                selection = knownPack.contentKey;
            }
            if (source == null || String.IsNullOrEmpty(source.id) || source.host) throw new InvalidDataException("Некорректный источник пака.");
            if (workshopRelayRequests.Count >= 16) throw new IOException("Слишком много запросов передачи пака.");
            string id = Guid.NewGuid().ToString("N");
            var pending = new WorkshopRelayPending { Request = new Message { kind = "workshop_relay_request", id = id, text = source.id, code = selection, round = remote.contentKey, asset = asset, index = chunk, flag = plan } };
            workshopRelayRequests[id] = pending;
            try
            {
                Send(pending.Request);
                using (token.Register(() => pending.Completion.TrySetCanceled()))
                {
                    if (await Task.WhenAny(pending.Completion.Task, Task.Delay(30000, token)).ConfigureAwait(false) != pending.Completion.Task)
                    { token.ThrowIfCancellationRequested(); throw new TimeoutException("Участник не ответил через канал комнаты."); }
                    var result = await pending.Completion.Task.ConfigureAwait(false);
                    if (!String.IsNullOrEmpty(result.text)) throw new IOException(result.text);
                    return result;
                }
            }
            finally
            {
                WorkshopRelayPending ignored; workshopRelayRequests.TryRemove(id, out ignored);
                if (!pending.Completion.Task.IsCompleted || pending.Completion.Task.IsCanceled)
                    try { Send(new Message { kind = "workshop_relay_cancel", id = id }); } catch (IOException) { }
            }
        }
        public async Task<PackTransferPlan> WorkshopRelayPlan(Pack remote, PackPeerEndpoint source, string asset, CancellationToken token)
        {
            var expected = PackTransferFiles.Expected(remote, asset);
            var response = await RequestWorkshopRelay(remote, source, asset, 0, true, token).ConfigureAwait(false);
            var plan = response.packPlan;
            if (plan == null || plan.key != remote.contentKey || plan.asset != asset || plan.hash != expected.hash || plan.bytes != expected.bytes || plan.chunks == null || plan.chunks.Length != (expected.bytes + PackTransferFiles.ChunkSize - 1) / PackTransferFiles.ChunkSize || plan.chunks.Length > 8192 || plan.chunks.Any(hash => !PackTransferFiles.IsHash(hash))) throw new InvalidDataException("Некорректный план передачи пака через комнату.");
            plan.peers = new[] { source }; return plan;
        }
        public async Task<byte[]> WorkshopRelayChunk(Pack remote, PackPeerEndpoint source, string asset, int chunk, CancellationToken token)
        {
            var expected = PackTransferFiles.Expected(remote, asset);
            if (chunk < 0 || (long)chunk * PackTransferFiles.ChunkSize >= expected.bytes) throw new InvalidDataException("Некорректная часть выбранного пака.");
            var response = await RequestWorkshopRelay(remote, source, asset, chunk, false, token).ConfigureAwait(false);
            if (response.data == null || response.data.Length > (PackTransferFiles.ChunkSize + 2) / 3 * 4) throw new InvalidDataException("Некорректные данные передачи пака через комнату.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(response.data); } catch (FormatException) { throw new InvalidDataException("Повреждённая часть выбранного пака."); }
            if (bytes.Length != Math.Min(PackTransferFiles.ChunkSize, expected.bytes - (long)chunk * PackTransferFiles.ChunkSize)) throw new InvalidDataException("Неполная часть выбранного пака.");
            return bytes;
        }
    }
}
