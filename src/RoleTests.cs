using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        internal static string OverlappingRoleFixture()
        {
            string root = Path.Combine(Paths.Cache, "overlapping-roles-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            File.Copy(Path.Combine(Paths.Base, "Assets", "Demo", "video.mp4"), Path.Combine(root, "dub_video.ogv"));
            File.WriteAllText(Path.Combine(root, "_pack_info.ini"), "[data]\ntitle=\"Пересекающиеся реплики\"\nlanguage=\"RUS\"\n");
            File.WriteAllText(Path.Combine(root, "01.ini"), "[data]\ncaption=\"Первая\"\ndub_timestamps=[0]\ndub_ends=[3]\ndub_characters=[\"Актёр\"]\n");
            File.WriteAllText(Path.Combine(root, "02.ini"), "[data]\ncaption=\"Вторая\"\ndub_timestamps=[2]\ndub_ends=[5]\ndub_characters=[\"Режиссёр\"]\n");
            return root;
        }

        public static int RoleLinesRegression()
        {
            var report = new List<string>(); string fixture = OverlappingRoleFixture(), imported = null;
            try
            {
                Pack pack = PackLibrary.Load(fixture, "");
                Check(pack.lines.Count == 2, "Две пересекающиеся реплики превратились в " + pack.lines.Count + ".");
                Check(pack.lines[0].role == "Актёр" && pack.lines[1].role == "Режиссёр" && pack.lines[0].start == 0 && pack.lines[0].end == 3 && pack.lines[1].start == 2 && pack.lines[1].end == 5, "Потеряны исходные роли или таймкоды.");
                report.Add("PASS: two overlapping native cues remain two separate lines with their exact roles and timestamps");
                string key = "role-regression-" + Guid.NewGuid().ToString("N"); imported = Path.Combine(Paths.Packs, key);
                var copy = PackLibrary.Import(fixture, "", System.Threading.CancellationToken.None, key);
                Check(copy.lines.Count == 2 && copy.lines[0].sourceKey == pack.lines[0].sourceKey && copy.lines[1].role == "Режиссёр", "Импорт склеил реплики или потерял роль.");
                string manifest = Path.Combine(imported, "pack.json"); var legacy = Json.Read<Dictionary<string, object>>(File.ReadAllText(manifest));
                legacy["merged_timing"] = true; legacy["lines"] = new[] { new Line { start = 0, end = 5, role = "Несколько персонажей", text = "Первая / Вторая" } }; File.WriteAllText(manifest, Json.Write(legacy));
                var restored = PackLibrary.Load(imported, "");
                Check(restored.lines.Count == 2 && !restored.mergedTiming && restored.lines[0].role == "Актёр" && Path.GetExtension(restored.video) == ".mp4", "Старый объединённый пак не восстановлен по исходным INI.");
                report.Add("PASS: import and canonical reload preserve overlapping roles; previously merged packs recover separate cues from original INI without losing prepared MP4");
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "role-lines-test.txt"), report); Directory.Delete(fixture, true); if (imported != null && Directory.Exists(imported)) Directory.Delete(imported, true); }
        }

        static double FrequencyLevel(byte[] pcm, double start, double end, double frequency)
        {
            int first = (int)(start * 44100), last = Math.Min(pcm.Length / 2, (int)(end * 44100)); double sin = 0, cos = 0;
            for (int i = first; i < last; i++) { double phase = 2 * Math.PI * frequency * i / 44100, value = BitConverter.ToInt16(pcm, i * 2); sin += value * Math.Sin(phase); cos += value * Math.Cos(phase); }
            return 2 * Math.Sqrt(sin * sin + cos * cos) / Math.Max(1, last - first);
        }

        public static int RoleMixRegression()
        {
            var report = new List<string>(); string root = Path.Combine(Paths.Cache, "role-mix-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var pack = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), ""); pack.background = "";
                pack.lines = new List<Line> { new Line { start = 0, end = 3, role = "Первый" }, new Line { start = 2, end = 5, role = "Второй" } };
                string first = Path.Combine(root, "first.wav"), second = Path.Combine(root, "second.wav"), output = Path.Combine(root, "result.mp4"), decoded = Path.Combine(root, "decoded.wav");
                File.WriteAllBytes(first, ToneWave(3, 330, 5000)); File.WriteAllBytes(second, ToneWave(3, 770, 5000));
                Audio.Render(pack, new[] { first, second }, output);
                Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(output) + " -vn -ar 44100 -ac 1 -c:a pcm_s16le " + Tools.Quote(decoded), 30000);
                byte[] pcm = Audio.ReadPcm(File.ReadAllBytes(decoded)); double a = FrequencyLevel(pcm, 2.15, 2.85, 330), b = FrequencyLevel(pcm, 2.15, 2.85, 770);
                Check(a > 900 && b > 900, "На пересечении потерян голос: 330 Hz=" + a.ToString("0") + ", 770 Hz=" + b.ToString("0"));
                Check(FrequencyLevel(pcm, .2, .8, 330) > 900 && FrequencyLevel(pcm, 3.2, 3.8, 770) > 900 && FrequencyLevel(pcm, 5.3, 5.8, 330) < 80, "Голоса сдвинуты относительно исходных таймкодов.");
                report.Add("PASS: decoded final MP4 retains BOTH voices during overlapping timestamps and correct isolated voices outside the overlap");
                pack.lines[0].sourceKey = "first"; pack.lines[1].sourceKey = "second"; pack.original = second;
                pack.sourceCues = new List<SourceCue> { new SourceCue { key = "first", file = first, start = 0, end = 3 }, new SourceCue { key = "second", file = second, start = 2, end = 5 } };
                string original = Path.Combine(root, "original.wav"); Audio.Original(pack, 0, original); byte[] originalPcm = Audio.ReadPcm(File.ReadAllBytes(original));
                Check(FrequencyLevel(originalPcm, 2.15, 2.85, 330) > 900 && FrequencyLevel(originalPcm, 2.15, 2.85, 770) < 80, "Оригинальная реплика смешалась с другой ролью.");
                report.Add("PASS: original-line preview selects the exact source cue and excludes the other character even when their timestamps overlap"); return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error.Message); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "role-mix-test.txt"), report); Directory.Delete(root, true); }
        }

        public static int RolesNetworkRegression()
        {
            var report = new List<string>();
            try
            {
                Pack pack = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), ""); pack.lines[3].role = "Третий"; PackContent.Describe(pack, System.Threading.CancellationToken.None);
                using (var server = new RoomServer(pack, 0)) using (var host = new Inbox()) using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Ведущий", server.Secret); host.Wait(s => s.players.Count == 1);
                    guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Друг", ""); host.Wait(s => s.players.Count == 2 && s.roles.Count == 3); guest.Wait(s => s.players.Count == 2);
                    Check(host.State.phase == "lobby" && host.State.roleMode == "pending" && host.State.roles.All(role => String.IsNullOrEmpty(role.playerId)), "Роли назначены до начала игры.");
                    Check(host.State.roles.Sum(role => role.lines) == 4 && host.State.roles.First(role => role.role == "Режиссёр").lines == 2, "Потеряны исходные роли или количество реплик.");
                    host.Client.Send(new Message { kind = "role_mode", text = "manual" }); host.Error("Начать игру");
                    guest.Client.Send(new Message { kind = "role_select", id = "Режиссёр", flag = true }); guest.Error("Начать игру");
                    host.Client.Send(new Message { kind = "ready", flag = true }); guest.Client.Send(new Message { kind = "ready", flag = true }); host.Wait(s => s.players.All(player => player.ready));
                    host.Client.Send(new Message { kind = "start" }); host.Wait(s => s.phase == "casting"); guest.Wait(s => s.phase == "casting");
                    Check(host.State.roleMode == "pending" && host.State.order.Length == 0 && host.State.roles.All(role => String.IsNullOrEmpty(role.playerId)) && host.State.players.All(player => !player.ready), "Начало игры пропустило обязательный выбор.");
                    host.Client.Send(CastMessage(host.State, "cast_start")); host.Error("каждому игроку");
                    guest.Client.Send(CastMessage(host.State, "cast_start")); guest.Error("ведущему");
                    guest.Client.Send(new Message { kind = "pack_ready", round = guest.State.round, id = guest.State.pack.contentKey });
                    host.Client.Send(new Message { kind = "recording", round = host.State.round, index = 0, flag = true }); host.Error("другого игрока");
                    guest.Client.Send(CastMessage(host.State, "role_ready")); guest.Error("свою роль");
                    host.Client.Send(new Message { kind = "role_mode", text = "auto" }); host.Wait(s => s.roleMode == "auto" && RoleCasting.Complete(s));
                    guest.Client.Send(new Message { kind = "ready", flag = true }); guest.Error("лобби");
                    Check(host.State.roles.Where(role => role.playerId == host.Client.Id).Sum(role => role.lines) == 2 && host.State.roles.Where(role => role.playerId == guest.Client.Id).Sum(role => role.lines) == 2, "Автоматическое распределение не учитывает количество реплик.");
                    host.Client.Send(CastMessage(host.State, "role_ready")); host.Wait(s => s.players.First(player => player.host).ready);
                    host.Client.Send(CastMessage(host.State, "cast_start")); host.Error("подтверждения");
                    Check(host.State.phase == "casting", "Автоматический режим обошёл подтверждение гостя.");
                    report.Add("PASS: lobby has no assigned roles; Start Game enters mandatory casting; pending, recording and pack-ready bypasses rejected; automatic assignment is balanced and requires participant confirmation");
                    guest.Client.Send(new Message { kind = "role_mode", text = "manual" }); guest.Error("ведущему");
                    host.Client.Send(new Message { kind = "role_mode", text = "manual" }); host.Wait(s => s.roleMode == "manual" && s.roles.All(role => String.IsNullOrEmpty(role.playerId))); guest.Wait(s => s.roleMode == "manual");
                    guest.Client.Send(new Message { kind = "role_select", id = "Режиссёр", flag = true }); host.Wait(s => s.roles.First(role => role.role == "Режиссёр").playerId == guest.Client.Id);
                    host.Client.Send(new Message { kind = "role_select", id = "Режиссёр", flag = true }); host.Error("другой игрок");
                    host.Client.Send(new Message { kind = "role_select", id = "Актёр", flag = true }); guest.Client.Send(new Message { kind = "role_select", id = "Третий", flag = true }); host.Wait(RoleCasting.Complete);
                    ConfirmCast(host, guest); Message staleConfirmation = CastMessage(host.State, "role_ready");
                    guest.Client.Send(new Message { kind = "role_select", id = "Третий", flag = false }); host.Wait(s => s.roles.First(role => role.role == "Третий").playerId == "" && s.players.All(player => !player.ready));
                    guest.Client.Send(staleConfirmation); guest.Error("изменились");
                    guest.Client.Send(new Message { kind = "role_select", id = "Режиссёр", flag = false }); host.Wait(s => s.roles.First(role => role.role == "Режиссёр").playerId == "");
                    host.Client.Send(new Message { kind = "role_select", id = "Режиссёр", flag = true }); host.Client.Send(new Message { kind = "role_select", id = "Третий", flag = true }); host.Wait(s => s.roles.All(role => role.playerId == host.Client.Id));
                    Check(!RoleCasting.Complete(host.State), "Игрок без роли допущен как зритель.");
                    guest.Client.Send(CastMessage(host.State, "role_ready")); guest.Error("свою роль");
                    host.Client.Send(CastMessage(host.State, "cast_start")); host.Error("каждому игроку");
                    host.Client.Send(new Message { kind = "role_select", id = "Режиссёр", flag = false }); host.Client.Send(new Message { kind = "role_select", id = "Третий", flag = false }); host.Wait(s => s.roles.Count(role => String.IsNullOrEmpty(role.playerId)) == 2);
                    guest.Client.Send(new Message { kind = "role_select", id = "Режиссёр", flag = true }); guest.Client.Send(new Message { kind = "role_select", id = "Третий", flag = true }); host.Wait(RoleCasting.Complete);
                    report.Add("PASS: manual ownership cannot be stolen; release/reselection clears confirmations and rejects stale confirmation messages; assigning all roles to one player cannot bypass the requirement that everyone owns a role");
                    ConfirmCast(host, guest);
                    StartLoadedRound(host, guest);
                    Check(host.State.order.SequenceEqual(new[] { guest.Client.Id, host.Client.Id, guest.Client.Id, guest.Client.Id }), "Очередь не соответствует подтверждённым ролям.");
                    host.Client.Send(new Message { kind = "recording", round = host.State.round, index = 0, flag = true }); host.Error("другого игрока");
                    for (int index = 0; index < 4; index++)
                    {
                        int turn = index; host.Wait(s => s.turn == turn); guest.Wait(s => s.turn == turn); var actor = host.State.Active.id == host.Client.Id ? host : guest;
                        actor.Client.Send(new Message { kind = "take", round = actor.State.round, index = turn, data = Convert.ToBase64String(ToneWave(1, 330 + index * 110)) }); host.Wait(s => s.turn > turn);
                    }
                    host.Wait(s => s.phase == "result"); guest.Wait(s => s.phase == "result");
                    string result = guest.Client.Asset("result", ".mp4").GetAwaiter().GetResult(); Check(new FileInfo(result).Length > 1000, "Гость не получил смонтированное видео.");
                    report.Add("PASS: confirmed casting proceeds through all-player loading; character ownership controls recording and complete MP4 delivery");
                    host.Client.Send(new Message { kind = "reset" }); host.Wait(s => s.phase == "lobby" && s.roleMode == "pending" && s.roles.All(role => String.IsNullOrEmpty(role.playerId)));
                    host.Client.Send(new Message { kind = "ready", flag = true }); guest.Client.Send(new Message { kind = "ready", flag = true }); host.Wait(s => s.players.All(player => player.ready));
                    host.Client.Send(new Message { kind = "start" }); host.Wait(s => s.phase == "casting"); guest.Dispose(); host.Wait(s => s.phase == "lobby" && s.players.Count == 1 && s.roles.All(role => String.IsNullOrEmpty(role.playerId)));
                    report.Add("PASS: replay and disconnect during casting return to a clean lobby with no role assignment");
                }
                foreach (var line in pack.lines) line.role = "Одна роль"; PackContent.Describe(pack, System.Threading.CancellationToken.None);
                using (var server = new RoomServer(pack, 0)) using (var host = new Inbox()) using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Ведущий", server.Secret); host.Wait(s => s.players.Count == 1);
                    guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Друг", ""); host.Wait(s => s.players.Count == 2);
                    host.Client.Send(new Message { kind = "ready", flag = true }); guest.Client.Send(new Message { kind = "ready", flag = true }); host.Wait(s => s.players.All(player => player.ready));
                    host.Client.Send(new Message { kind = "start" }); host.Error("меньше ролей"); Check(host.State.phase == "lobby", "Пак с нехваткой ролей создаёт незавершаемую игру.");
                }
                report.Add("PASS: a pack with fewer roles than participants is rejected before starting the game"); return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "roles-network-test.txt"), report); }
        }

    }
}
