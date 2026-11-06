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
        Task<string> LoadRoundPack(RoomClient room, Pack pack, string round)
        {
            if (preparationCancel != null) preparationCancel.Cancel(); preparationCancel = new CancellationTokenSource(); var token = preparationCancel.Token;
            return LoadRoundPackCore(room, pack, round, token);
        }
        async Task<string> LoadRoundPackCore(RoomClient room, Pack pack, string round, CancellationToken token)
        {
            try
            {
                var local = await PackContent.Prepare(pack, (asset, extension, progress, cancellation) => room.Asset(asset, extension, progress, cancellation, pack.contentKey), progress =>
                {
                    long now = Playback.Now; if (progress.Fraction < 1 && now - lastPreparationReport < 220) return; lastPreparationReport = now;
                    if (!disposed && client == room) room.Send(new Message { kind = "pack_progress", round = round, id = pack.contentKey, position = Math.Max(0, progress.Fraction), text = progress.Stage });
                }, token, true, PacksFolder);
                return local.video;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { if (!disposed && client == room) room.Send(new Message { kind = "pack_failed", round = round, text = e.GetBaseException().Message }); throw; }
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
