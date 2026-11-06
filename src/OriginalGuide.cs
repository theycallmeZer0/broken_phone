using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        sealed class OriginalClip { public string File; public float[] Wave; }
        readonly Dictionary<int, Task<OriginalClip>> originalClips = new Dictionary<int, Task<OriginalClip>>();
        string originalRound;
        RoomClient originalClient;
        CancellationTokenSource originalCancellation;

        void ClearOriginalClips()
        {
            if (originalCancellation != null) { originalCancellation.Cancel(); originalCancellation.Dispose(); originalCancellation = null; }
            originalClips.Clear(); originalRound = null; originalClient = null; C<SignalView>("Signal").SetGuide(null);
        }
        Task<OriginalClip> OriginalClipFor(RoomClient room, string round, int index)
        {
            if (originalClient != room || originalRound != round)
            {
                ClearOriginalClips(); originalClient = room; originalRound = round; originalCancellation = new CancellationTokenSource();
            }
            Task<OriginalClip> clip;
            if (!originalClips.TryGetValue(index, out clip)) { clip = ReadOriginalClip(room, index, originalCancellation.Token); originalClips[index] = clip; }
            return clip;
        }
        async Task<OriginalClip> ReadOriginalClip(RoomClient room, int index, CancellationToken token)
        {
            string file = await room.Asset("original:" + index, ".wav", null, token);
            float[] wave = await Task.Run(() => SignalAnalysis.Envelope(Audio.ReadPcm(File.ReadAllBytes(file)), 400), token);
            return new OriginalClip { File = file, Wave = wave };
        }
        async Task PrepareOriginalGuide(RoomClient room, string round, int index)
        {
            if (!state.pack.lines[index].originalAvailable) { C<SignalView>("Signal").SetGuide(null); return; }
            try
            {
                var clip = await OriginalClipFor(room, round, index);
                if (disposed || client != room || state == null || state.round != round || !IsTurn || StudioTurn != index) return;
                C<SignalView>("Signal").SetGuide(clip.Wave);
                C<SignalView>("Signal").ToolTip = "Серая волна — оригинал. Зелёная — твоя запись. Обе используют время текущей реплики.";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (client == room && state != null && state.round == round && IsTurn && StudioTurn == index) C<SignalView>("Signal").SetGuide(null); }
        }
    }
}
