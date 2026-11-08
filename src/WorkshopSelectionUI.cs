using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        CancellationTokenSource pendingWorkshopCancel;
        string pendingWorkshopKey;
        string pendingWorkshopId, pendingWorkshopNeedsPeers;
        Task<Pack> pendingWorkshopTask;
        Action<PackProgress> retainedWorkshopProgress;
        RoomClient pendingWorkshopRoom;
        readonly Queue<Message> workshopOffers = new Queue<Message>();
        readonly HashSet<string> workshopOfferSources = new HashSet<string>();
        string acceptingWorkshopKey;
        long lastPendingWorkshopReport;
        Func<WorkshopItem, Action<PackProgress>, CancellationToken, Task<Pack>> pendingWorkshopLoader;

        void SelectWorkshopPack(WorkshopItem item)
        {
            if (!IsHost || state.phase != "lobby") { Toast("Пак выбирает ведущий в лобби."); return; }
            Send(new Message { kind = "workshop_select", pack = new Pack { workshopId = item.Id, title = item.Title, preview = item.Preview, lineCount = item.Lines, roleCount = item.Roles } });
            Page("Lobby");
        }
        bool PendingWorkshopCurrent(RoomClient room, string key)
        {
            return !disposed && client == room && state != null && state.phase == "lobby" && state.pack.pendingDownload && state.pack.contentKey == key;
        }
        void CancelPendingWorkshop()
        {
            if (pendingWorkshopCancel != null) { pendingWorkshopCancel.Cancel(); pendingWorkshopCancel = null; }
            pendingWorkshopKey = null; pendingWorkshopRoom = null; workshopOffers.Clear(); workshopOfferSources.Clear();
            pendingWorkshopId = pendingWorkshopNeedsPeers = null; pendingWorkshopTask = null; retainedWorkshopProgress = null;
        }
        void EnsurePendingWorkshop()
        {
            if (state == null || !state.pack.pendingDownload || state.phase != "lobby")
            {
                // Publishing the manifest must not restart another player's healthy
                // Steam download of this same selected Workshop item.
                if (state != null && state.pack.Selected && pendingWorkshopRoom == client && pendingWorkshopId == state.pack.workshopId)
                {
                    workshopOffers.Clear(); workshopOfferSources.Clear(); return;
                }
                CancelPendingWorkshop(); return;
            }
            if (pendingWorkshopRoom == client && pendingWorkshopKey == state.pack.contentKey) return;
            CancelPendingWorkshop(); pendingWorkshopRoom = client; pendingWorkshopKey = state.pack.contentKey; pendingWorkshopId = state.pack.workshopId;
            pendingWorkshopCancel = new CancellationTokenSource();
            pendingWorkshopTask = DownloadPendingWorkshop(client, state.pack, pendingWorkshopCancel);
        }
        Action<PackProgress> PendingWorkshopProgress(RoomClient room, string key, CancellationToken token)
        {
            return progress =>
            {
                if (token.IsCancellationRequested) return;
                var retained = retainedWorkshopProgress; if (retained != null) retained(progress);
                long now = Playback.Now;
                if (progress.Fraction > 0 && progress.Fraction < 1 && now - Interlocked.Read(ref lastPendingWorkshopReport) < 220) return;
                Interlocked.Exchange(ref lastPendingWorkshopReport, now);
                try { room.Send(new Message { kind = "pack_download_progress", id = key, position = Math.Max(0, progress.Fraction), text = progress.Stage }); }
                catch (IOException) { }
            };
        }
        async Task<Pack> DownloadPendingWorkshop(RoomClient room, Pack selection, CancellationTokenSource cancellation)
        {
            var token = cancellation.Token; string key = selection.contentKey;
            var progress = PendingWorkshopProgress(room, key, token);
            var candidates = installed.Where(pack => pack.workshopId == selection.workshopId).ToArray();
            try
            {
                var local = await Task.Run(() =>
                {
                    foreach (var candidate in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        try { return FolderPacks.Prepare(PackLibrary.Load(candidate.root, selection.workshopId, token), token); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { }
                    }
                    return null;
                }, token);
                if (local == null) local = await (pendingWorkshopLoader ?? workshop.DownloadProgress)(new WorkshopItem { Id = selection.workshopId, Title = selection.title }, progress, token);
                token.ThrowIfCancellationRequested();
                if (!PendingWorkshopCurrent(room, key)) return local;
                if (InstalledItem(selection.workshopId) == null) installed.Add(local);
                if (IsHost) CommitPendingWorkshop(room, key, local);
                else
                {
                    await Task.Run(() => room.OfferWorkshopPack(key, local), token);
                    progress(new PackProgress { Stage = "Пак загружен · передаём ведущему", Fraction = 1 });
                }
                return local;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception)
            {
                if (PendingWorkshopCurrent(room, key))
                {
                    progress(new PackProgress { Stage = "Ожидаем копию пака от участников", Fraction = 0 });
                    pendingWorkshopNeedsPeers = key; TryAcceptWorkshopOffers(room, key);
                }
                return null;
            }
            // The selection keeps its cancellation source until it is replaced;
            // an offer from another player may still finish this download.
        }
        void CommitPendingWorkshop(RoomClient room, string key, Pack local)
        {
            if (PendingWorkshopCurrent(room, key) && IsHost)
                room.Send(new Message { kind = "pack", code = key, pack = local });
        }
        void ReceiveWorkshopOffer(Message message)
        {
            if (!IsHost || !PendingWorkshopCurrent(client, message.code) || message.packPeer == null) return;
            if (!workshopOfferSources.Add(message.packPeer.id + ":" + message.pack.contentKey)) return;
            workshopOffers.Enqueue(message);
            TryAcceptWorkshopOffers(client, message.code);
        }
        void TryAcceptWorkshopOffers(RoomClient room, string key)
        {
            if (PendingWorkshopCurrent(room, key) && pendingWorkshopNeedsPeers == key && acceptingWorkshopKey != key && workshopOffers.Count > 0)
            {
                var ignored = AcceptWorkshopOffers(room, key, pendingWorkshopCancel.Token);
            }
        }
        Func<WorkshopItem, Action<PackProgress>, CancellationToken, Task<Pack>> SelectedWorkshopDownload(RoomClient room, Pack pack)
        {
            var pending = pendingWorkshopRoom == room && pendingWorkshopId == pack.workshopId ? pendingWorkshopTask : null;
            if (pending == null) return workshop.DownloadProgress;
            return async (item, progress, token) =>
            {
                retainedWorkshopProgress = progress;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var cancelled = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Pack downloaded;
                    using (token.Register(() => cancelled.TrySetCanceled()))
                    {
                        var completed = await Task.WhenAny(pending, cancelled.Task);
                        token.ThrowIfCancellationRequested(); downloaded = await completed;
                    }
                    token.ThrowIfCancellationRequested();
                    if (downloaded == null) throw new IOException("Steam не загрузил выбранный пак. Получаем файлы от участников.");
                    return downloaded;
                }
                finally { if (retainedWorkshopProgress == progress) retainedWorkshopProgress = null; }
            };
        }
        async Task AcceptWorkshopOffers(RoomClient room, string key, CancellationToken token)
        {
            acceptingWorkshopKey = key;
            try
            {
                while (PendingWorkshopCurrent(room, key) && workshopOffers.Count > 0)
                {
                    var offer = workshopOffers.Dequeue();
                    try
                    {
                        var local = await room.PrepareWorkshopOffer(offer.pack, offer.packPeer, PendingWorkshopProgress(room, key, token), token, Paths.Packs);
                        token.ThrowIfCancellationRequested();
                        CommitPendingWorkshop(room, key, local); return;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception)
                    {
                        if (PendingWorkshopCurrent(room, key)) PendingWorkshopProgress(room, key, token)(new PackProgress { Stage = "Участник недоступен · ожидаем другую копию", Fraction = 0 });
                    }
                }
            }
            finally { if (acceptingWorkshopKey == key) acceptingWorkshopKey = null; }
        }
    }
}
