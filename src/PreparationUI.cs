using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        CancellationTokenSource preparationCancel;
        string preparedAckRound;
        long lastPreparationReport;
        string downloadingPackKey;
        void EnsureSelectedPack()
        {
            if (state == null || !state.pack.Selected || state.phase != "lobby" && state.phase != "casting" && state.phase != "loading") return;
            if (videoDownload != null && videoDownloadClient == client && downloadingPackKey == state.pack.contentKey) return;
            videoDownloadClient = client; downloadingPackKey = state.pack.contentKey;
            videoDownload = LoadRoundPack(client, state.pack);
            var ignored = ObserveSelectedPack(videoDownload, client, downloadingPackKey);
        }
        async Task ObserveSelectedPack(Task<string> task, RoomClient room, string key)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!disposed && client == room && state.pack.contentKey == key) Toast(error.GetBaseException().Message); }
        }
        Task<string> LoadRoundPack(RoomClient room, Pack pack)
        {
            if (preparationCancel != null) preparationCancel.Cancel(); preparationCancel = new CancellationTokenSource(); var token = preparationCancel.Token;
            return LoadRoundPackCore(room, pack, token);
        }
        async Task<string> LoadRoundPackCore(RoomClient room, Pack pack, CancellationToken token)
        {
            try
            {
                var local = await PackContent.PrepareSelected(pack, steam != null && steam.Available ? (Func<WorkshopItem, Action<PackProgress>, CancellationToken, Task<Pack>>)workshop.DownloadProgress : null, (asset, extension, progress, cancellation) => room.Asset(asset, extension, progress, cancellation, pack.contentKey), progress =>
                {
                    long now = Playback.Now; if (progress.Fraction < 1 && now - lastPreparationReport < 220) return; lastPreparationReport = now;
                    if (!disposed && client == room && !token.IsCancellationRequested) room.Send(new Message { kind = "pack_download_progress", id = pack.contentKey, position = Math.Max(0, progress.Fraction), text = progress.Stage });
                }, token, PacksFolder);
                token.ThrowIfCancellationRequested();
                if (!disposed && client == room) room.Send(new Message { kind = "pack_download_ready", id = pack.contentKey });
                return local.video;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { if (!disposed && client == room && !token.IsCancellationRequested) room.Send(new Message { kind = "pack_download_failed", id = pack.contentKey, text = e.GetBaseException().Message }); throw; }
        }
        void AcknowledgePrepared()
        {
            if (state == null || state.phase != "loading" || !sourcePrepared || preparedAckRound == state.round) return;
            preparedAckRound = state.round; Send(new Message { kind = "pack_ready", round = state.round, id = state.pack.contentKey });
        }
        async Task PrepareTestPeer(RoomClient peer, RoomState loading)
        {
            try
            {
                await Task.Delay(600);
                await PackContent.Prepare(loading.pack, (asset, extension, progress, token) => peer.Asset(asset, extension, progress, token, loading.pack.contentKey), progress => { }, CancellationToken.None);
                peer.Send(new Message { kind = "pack_ready", round = loading.round, id = loading.pack.contentKey });
            }
            catch (Exception) { }
        }
    }
}
