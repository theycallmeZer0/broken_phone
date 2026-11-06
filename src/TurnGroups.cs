using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        sealed class TurnDraft { public string Raw, Processed; public int Effect; public bool Noise; }
        readonly Dictionary<int, TurnDraft> turnDrafts = new Dictionary<int, TurnDraft>();
        string draftRound;
        int groupStart = -1, groupEnd = -1, editingTurn = -1;
        int StudioTurn { get { return IsTurn && draftRound == state.round && (state.turn == groupStart || recordPending && state.turn <= groupEnd) ? editingTurn : state.turn; } }
        void ClearTurnGroup()
        {
            foreach (var draft in turnDrafts.Values) foreach (string file in new[] { draft.Raw, draft.Processed })
                if (!String.IsNullOrEmpty(file)) try { File.Delete(file); } catch (IOException) { }
            turnDrafts.Clear(); draftRound = null; groupStart = groupEnd = editingTurn = -1;
        }
        void EnsureTurnGroup()
        {
            if (recordPending) return;
            if (!IsTurn) { if (groupStart >= 0) ClearTurnGroup(); return; }
            if (draftRound == state.round && groupStart == state.turn) return;
            ClearTurnGroup(); draftRound = state.round; groupStart = editingTurn = state.turn; groupEnd = RoleCasting.BlockEnd(state);
        }
        bool DraftRecorded(int index) { return index < state.turn || IsTurn && (turnDrafts.ContainsKey(index) || index == StudioTurn && take != null); }
        void SaveTurnDraft()
        {
            if (!IsTurn || take == null || rawTake == null) return;
            TurnDraft draft;
            if (!turnDrafts.TryGetValue(StudioTurn, out draft)) { draft = new TurnDraft { Raw = Path.Combine(Paths.Cache, "draft-raw-" + Guid.NewGuid().ToString("N") + ".wav") }; turnDrafts[StudioTurn] = draft; }
            File.WriteAllBytes(draft.Raw, rawTake); draft.Processed = localTakeFile; draft.Effect = selectedEffect; draft.Noise = C<CheckBox>("NoiseGateCheck").IsChecked == true;
        }
        async Task OpenDraft(int index)
        {
            if (!IsTurn || microphone.Recording || processingTake || recordPending || index < groupStart || index > groupEnd) return;
            voice.Close(); voiceRevision++; voicePlaying = false; playbackFinished = null; playbackEnd = DateTime.MinValue; SaveTurnDraft();
            if (effectCancellation != null) effectCancellation.Cancel(); takeRevision++; editingTurn = index;
            C<SignalView>("Signal").SetGuide(null);
            take = rawTake = voicePcm = null; localTakeFile = null; processingTake = false; traceWave = new float[0]; traceDuration = 0;
            expectedRound = state.round; expectedTurn = index; recordDuration = state.pack.lines[index].end - state.pack.lines[index].start;
            TurnDraft draft;
            if (turnDrafts.TryGetValue(index, out draft))
            {
                selectedEffect = draft.Effect; C<CheckBox>("NoiseGateCheck").IsChecked = draft.Noise;
                rawTake = File.ReadAllBytes(draft.Raw); take = File.ReadAllBytes(draft.Processed); localTakeFile = draft.Processed;
                voicePcm = Audio.ReadPcm(take); traceWave = SignalAnalysis.Envelope(voicePcm, 400); traceDuration = voicePcm.Length / 88200.0;
            }
            Text("SignalLabel", take == null ? "ТВОЙ ГОЛОС" : "МОЙ ДУБЛЬ · " + VoiceEffects.Names[selectedEffect].ToUpperInvariant());
            Text("RecordingTimer", take == null ? "Реплика ещё не записана." : "Дубль сохранён. Можно прослушать или перезаписать.");
            C<SignalView>("Signal").Update(traceWave, 1, 0, false); RenderRound(); await PrepareRound();
        }
        async Task SubmitTurnGroup()
        {
            if (!IsTurn || !sourcePrepared || microphone.Recording || processingTake || recordPending) return;
            EnsureTurnGroup();
            if (editingTurn < groupEnd) { await OpenDraft(editingTurn + 1); return; }
            SaveTurnDraft();
            int missing = Enumerable.Range(groupStart, groupEnd - groupStart + 1).Where(index => !turnDrafts.ContainsKey(index)).DefaultIfEmpty(-1).First();
            if (missing >= 0) { await OpenDraft(missing); Toast("Запиши реплику " + (missing + 1) + ", чтобы передать ход следующему игроку."); return; }
            voice.Close(); voiceRevision++; voicePlaying = false; playbackFinished = null; playbackEnd = DateTime.MinValue;
            var room = client; string round = state.round; int end = groupEnd; var drafts = turnDrafts.ToDictionary(item => item.Key, item => item.Value.Processed);
            recordPending = true; RenderRound();
            try
            {
                for (int index = state.turn; index <= end; index++)
                {
                    int turn = index; byte[] bytes = File.ReadAllBytes(drafts[index]);
                    await Task.Run(() => room.Send(new Message { kind = "take", data = Convert.ToBase64String(bytes), index = turn, round = round }));
                    await Until(() => client != room || state == null || state.round != round || state.phase != "recording" || state.turn > turn, "Не удалось передать блок реплик.");
                    if (client != room || state == null || state.round != round) return;
                }
            }
            finally
            {
                recordPending = false;
                if (draftRound == round)
                {
                    if (IsTurn && state.round == round && state.turn <= end) { groupStart = state.turn; editingTurn = Math.Max(groupStart, Math.Min(editingTurn, end)); }
                    else ClearTurnGroup();
                }
                if (state != null && state.phase != "lobby") RenderRound();
            }
        }
    }
}
