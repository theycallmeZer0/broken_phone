using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        static void DisconnectAfterRecording(Pack pack, string when, List<string> report)
        {
            using (var server = new RoomServer(pack, 0)) using (var host = new Inbox()) using (var guest = new Inbox())
            {
                host.Client.Connect("127.0.0.1", server.Port, server.Code, "Host", server.Secret); host.Wait(s => s.players.Count == 1);
                guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Guest", ""); host.Wait(s => s.players.Count == 2); guest.Wait(s => s.players.Count == 2);
                host.Client.Send(new Message { kind = "ready", flag = true }); guest.Client.Send(new Message { kind = "ready", flag = true }); host.Wait(s => s.players.All(p => p.ready));
                StartLoadedRound(host, guest);
                string round = host.State.round, session = Path.Combine(Paths.Sessions, round);
                for (int index = 0; index < pack.lines.Count; index++)
                {
                    int turn = index; host.Wait(s => s.turn == turn); guest.Wait(s => s.turn == turn);
                    var actor = host.State.Active.id == host.Client.Id ? host : guest;
                    actor.Client.Send(new Message { kind = "take", round = round, index = turn, data = Convert.ToBase64String(ToneWave(.5, 330 + turn * 110)) });
                    host.Wait(s => s.turn > turn);
                }
                string before = null;
                if (when != "rendering")
                {
                    host.Wait(s => s.phase == "result"); guest.Wait(s => s.phase == "result");
                    before = PackContent.FileHash(host.Client.Asset("result", ".mp4").GetAwaiter().GetResult());
                    host.Client.Send(new Message { kind = "result_ready", round = round }); host.Wait(s => s.resultReady.Contains(host.Client.Id));
                    if (when == "playing")
                    {
                        guest.Client.Send(new Message { kind = "result_ready", round = round }); host.Wait(Playback.EveryoneReady);
                        host.Client.Send(new Message { kind = "playback", round = round, text = "start" }); host.Wait(s => s.playing && s.playbackVersion == 1);
                    }
                    else Check(!Playback.EveryoneReady(host.State), "Не воспроизведено ожидание неподготовленного гостя.");
                }
                var playback = host.State;
                guest.Dispose(); host.Wait(s => s.players.Count == 1);
                Check(host.State.round == round && (host.State.phase == "rendering" || host.State.phase == "result"), "Выход гостя прервал монтаж или просмотр: " + when);
                if (when == "playing") Check(host.State.playing && host.State.playbackVersion == playback.playbackVersion && host.State.playbackAt == playback.playbackAt, "Выход гостя изменил ход просмотра.");
                host.Wait(s => s.phase == "result");
                string result = Path.Combine(session, "result.mp4");
                Check(File.Exists(result) && Enumerable.Range(0, pack.lines.Count).All(i => File.Exists(Path.Combine(session, "take-" + i + ".wav"))), "После выхода гостя потеряны записи или готовый MP4.");
                string saved = host.Client.Asset("result", ".mp4").GetAwaiter().GetResult();
                Check(PackContent.FileHash(saved) == PackContent.FileHash(result) && (before == null || before == PackContent.FileHash(saved)), "После выхода гостя невозможно сохранить исходный результат.");
                host.Client.Send(new Message { kind = "result_ready", round = round }); host.Wait(Playback.EveryoneReady);
                int version = host.State.playbackVersion;
                host.Client.Send(new Message { kind = "playback", round = round, text = version == 0 ? "start" : "pause" }); host.Wait(s => s.playbackVersion > version);
                version = host.State.playbackVersion;
                host.Client.Send(new Message { kind = "playback", round = round, text = "restart" }); host.Wait(s => s.playbackVersion > version && s.playing);
                report.Add("PASS: guest disconnect during " + when + " retains round, recordings and downloadable MP4; remaining host can start/control/replay the result");
            }
        }
        public static int PlaybackDisconnectRegression()
        {
            var report = new List<string>();
            try
            {
                var pack = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                foreach (string when in new[] { "playing", "waiting", "rendering" }) DisconnectAfterRecording(pack, when, report);
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "playback-disconnect-test.txt"), report); }
        }
    }
}
