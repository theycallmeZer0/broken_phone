using System;
using System.Collections.Concurrent;
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
        void ConfigurePackSharingSteam()
        {
            if (steam == null || !steam.Available || steam.LobbyId == 0 || client == null) return;
            try { steam.ListenForPackPeers(IsHost && server != null ? server.PackSharePort : client.PackSharePort); client.OpenPackTunnel = steam.OpenPackTunnel; }
            catch (Exception) { client.OpenPackTunnel = null; }
        }
        void EnsureSelectedPack()
        {
            EnsurePendingWorkshop();
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
                var assets = new ConcurrentDictionary<string, Lazy<Task<string>>>();
                var local = await PackContent.PrepareAssisted(pack, SelectedWorkshopDownload(room, pack), (asset, extension, progress, cancellation) => assets.GetOrAdd(asset, _ => new Lazy<Task<string>>(() => room.PackAsset(pack, asset, extension, progress, cancellation))).Value, progress =>
                {
                    long now = Playback.Now; if (progress.Fraction < 1 && now - lastPreparationReport < 220) return; lastPreparationReport = now;
                    if (!disposed && client == room && !token.IsCancellationRequested) room.Send(new Message { kind = "pack_download_progress", id = pack.contentKey, position = Math.Max(0, progress.Fraction), text = progress.Stage + (room.PackHelperCount > 0 ? " · помогают игроки: " + room.PackHelperCount : "") });
                }, token, Paths.Packs, cancellation => room.WaitForPackHelpers(pack, cancellation));
                token.ThrowIfCancellationRequested();
                await Task.Run(() => room.SharePack(pack, local), token);
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
