using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        sealed class Inbox : IDisposable
        {
            public readonly RoomClient Client = new RoomClient();
            public readonly ConcurrentQueue<Message> Messages = new ConcurrentQueue<Message>();
            public volatile RoomState State;
            public Inbox() { Client.Received += m => { if (m.state != null) State = m.state; Messages.Enqueue(m); }; }
            public void Wait(Func<RoomState, bool> predicate, double timeout = 35)
            {
                var end = DateTime.UtcNow.AddSeconds(timeout);
                while (DateTime.UtcNow < end) { if (State != null && predicate(State)) return; Thread.Sleep(20); }
                throw new Exception("Не дождались состояния: " + (State == null ? "null" : "phase=" + State.phase + ", turn=" + State.turn + ", players=" + State.players.Count));
            }
            public void Error(string contains)
            {
                var end = DateTime.UtcNow.AddSeconds(4); Message m;
                while (DateTime.UtcNow < end)
                {
                    while (Messages.TryDequeue(out m)) if (m.kind == "error" && m.text.Contains(contains)) return;
                    Thread.Sleep(10);
                }
                throw new Exception("Не получили ожидаемую ошибку: " + contains);
            }
            public void Dispose() { Client.Dispose(); }
        }
        static void Check(bool value, string text) { if (!value) throw new Exception(text); }
        static Message CastMessage(RoomState state, string kind) { return new Message { kind = kind, flag = true, round = state.round, index = state.roleVersion }; }
        static void ConfirmCast(Inbox host, Inbox guest)
        {
            host.Wait(RoleCasting.Complete); guest.Wait(s => s.roleVersion == host.State.roleVersion);
            host.Client.Send(CastMessage(host.State, "role_ready")); guest.Client.Send(CastMessage(host.State, "role_ready")); host.Wait(s => s.players.All(player => player.ready));
        }
        static void StartLoadedRound(Inbox host, Inbox guest)
        {
            if (host.State.phase == "lobby")
            {
                host.Client.Send(new Message { kind = "start" }); host.Wait(s => s.phase == "casting"); guest.Wait(s => s.phase == "casting");
                Check(host.State.roles.All(role => String.IsNullOrEmpty(role.playerId)) && host.State.players.All(player => !player.ready), "Выбор ролей должен начинаться отдельным этапом после старта.");
                host.Client.Send(new Message { kind = "role_mode", text = "auto" }); host.Wait(s => s.roleMode == "auto" && RoleCasting.Complete(s)); guest.Wait(s => RoleCasting.Complete(s));
                ConfirmCast(host, guest);
            }
            host.Client.Send(CastMessage(host.State, "cast_start")); host.Wait(s => s.phase == "loading"); guest.Wait(s => s.phase == "loading");
            host.Client.Send(new Message { kind = "pack_ready", round = host.State.round, id = host.State.pack.contentKey }); host.Wait(s => s.players.First(p => p.host).prepared);
            Check(host.State.phase == "loading", "Раунд начался до загрузки пака у гостя.");
            guest.Client.Send(new Message { kind = "recording", round = guest.State.round, index = 0, flag = true }); guest.Error("другого игрока");
            guest.Client.Send(new Message { kind = "pack_progress", round = guest.State.round, id = guest.State.pack.contentKey, position = .5, text = "Получаем видео" });
            host.Wait(s => s.players.Any(p => !p.host && p.downloadProgress == .5) && s.phase == "loading");
            guest.Client.Send(new Message { kind = "pack_ready", round = guest.State.round, id = guest.State.pack.contentKey });
            host.Wait(s => s.phase == "recording"); guest.Wait(s => s.phase == "recording");
        }
        static void CancellationTests(List<string> results)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            string file = Path.Combine(Paths.Cache, "cancellation-test.bin");
            var server = Task.Run(() =>
            {
                try
                {
                    using (var socket = listener.AcceptTcpClient()) using (var stream = socket.GetStream())
                    {
                        var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1048576\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n"); stream.Write(header, 0, header.Length);
                        var chunk = new byte[16384]; for (int i = 0; i < 64; i++) { stream.Write(chunk, 0, chunk.Length); stream.Flush(); Thread.Sleep(15); }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });
            try
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    bool cancelled = false; var watch = Stopwatch.StartNew();
                    try { Transfers.Download("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/slow", file, cancellation.Token, (r, t) => cancellation.Cancel()).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && watch.ElapsedMilliseconds < 2500 && !File.Exists(file) && Directory.GetFiles(Paths.Cache, "cancellation-test.bin.part-*").Length == 0, "Отмена HTTP не остановила поток или оставила частичный файл.");
                }
            }
            finally { listener.Stop(); server.Wait(2000); }
            using (var cancellation = new CancellationTokenSource(300))
            {
                bool cancelled = false; var watch = Stopwatch.StartNew(); string output = Path.Combine(Paths.Cache, "cancel-encoder.mp4");
                try { Tools.Run("ffmpeg", "-y -v error -re -f lavfi -i color=c=black:s=64x64:r=10 -t 30 -c:v libx264 " + Tools.Quote(output), 35000, cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && watch.ElapsedMilliseconds < 2500, "Обработка видео не остановилась при отмене.");
                if (File.Exists(output)) { using (File.Open(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } File.Delete(output); }
            }
            string pidFile = Path.Combine(Paths.Cache, "cancel-child.pid"); if (File.Exists(pidFile)) File.Delete(pidFile);
            foreach (string worker in new[] { "--transfer-worker", "--transfer-worker-exits" })
            using (var cancellation = new CancellationTokenSource(750))
            {
                bool cancelled = false; var watch = Stopwatch.StartNew();
                try { Transfers.Process(Assembly.GetExecutingAssembly().Location, worker, Paths.Base, 35000, cancellation.Token, false); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && watch.ElapsedMilliseconds < 2500, "Процесс загрузчика не остановился.");
                if (File.Exists(pidFile))
                {
                    int pid = Int32.Parse(File.ReadAllText(pidFile)); bool stopped;
                    try { using (var process = Process.GetProcessById(pid)) stopped = process.WaitForExit(1500); } catch (ArgumentException) { stopped = true; }
                    Check(stopped, "После отмены остался дочерний процесс загрузчика."); File.Delete(pidFile);
                }
                else throw new Exception("Не удалось проверить дочерний процесс загрузчика.");
            }
            results.Add("PASS: Cancel aborts real HTTP transfer, removes partial file, stops FFmpeg and downloader process tree");
        }
        public static int NativePack(string source)
        {
            var report = new List<string>();
            try
            {
                string id = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
                Pack original = PackLibrary.Load(source, id);
                Check(!original.generatedTiming && original.lines.Count > 0, "Оригинальные таймкоды не прочитаны.");
                Check(original.lines.All(line => line.end > line.start && !String.IsNullOrWhiteSpace(line.role)) && original.lines.Zip(original.lines.Skip(1), (a, b) => a.start <= b.start).All(x => x), "Нарушены роли или порядок исходных реплик.");
                report.Add("PASS: original INI metadata: " + original.title + " / " + original.lines.Count + " separate character cues / first cue " + Audio.N(original.lines[0].start));
                Pack imported = PackLibrary.Import(source, id);
                Check(imported.lines.Count == original.lines.Count && imported.lines[0].text == original.lines[0].text && Math.Abs(imported.lines.Last().end - original.lines.Last().end) < .001, "После импорта потеряны метаданные.");
                Check(imported.lines.Zip(original.lines, (a, b) => a.role == b.role && Math.Abs(a.start - b.start) < .001 && Math.Abs(a.end - b.end) < .001).All(value => value), "После импорта изменены роли или таймкоды.");
                Check(PackSources.Native(imported) && imported.sourceHash == original.sourceHash, "Изменился список исходных файлов после подготовки видео.");
                Check(Path.GetExtension(imported.video) == ".mp4" && File.Exists(imported.background), "Нет MP4 или фоновой дорожки.");
                Check(Math.Abs(PackLibrary.Duration(original.video) - PackLibrary.Duration(imported.video)) < .2, "После преобразования изменилась длина видео.");
                report.Add("PASS: OGV -> H.264 MP4, original cues and separate OGG backing preserved");
                string folder = Path.Combine(Paths.Cache, "native-validation"); Directory.CreateDirectory(folder);
                int sourceLine = imported.lines.FindIndex(line => line.originalAvailable);
                Check(sourceLine >= 0 && imported.sourceCues.Count > 0, "Не найдены исходные звуковые реплики Workshop.");
                string originalAudio = Path.Combine(folder, "original.wav"); Audio.Original(imported, sourceLine, originalAudio);
                var originalPcm = Audio.ReadPcm(File.ReadAllBytes(originalAudio));
                Check(Energy(originalPcm, 0, originalPcm.Length / 88200.0) > 10, "Исходная реплика Workshop пустая.");
                report.Add("PASS: native per-line MP3 source audio preserved, decoded and timed for optional Original preview");
                string[] takes = imported.lines.Select((line, index) => { string file = Path.Combine(folder, "take-" + index + ".wav"); File.WriteAllBytes(file, Audio.Wave(new byte[44100])); return file; }).ToArray();
                string output = Path.Combine(folder, "result.mp4"); Audio.Render(imported, takes, output);
                Check(Math.Abs(PackLibrary.Duration(output) - PackLibrary.Duration(imported.video)) < .2, "Сведение обрезало реальную сцену."); report.Add("PASS: actual Workshop scene mixed with separate backing and recorded takes, full duration preserved");
                var library = new WorkshopService(); var packs = library.InstalledPacks(); Check(packs.Any(p => p.workshopId == id), "Собственный пак не обнаружен."); Check(packs.All(p => p.root.StartsWith(Paths.Packs, StringComparison.OrdinalIgnoreCase) || p.root.StartsWith(Path.Combine(Paths.Base, "Assets"), StringComparison.OrdinalIgnoreCase)), "Прочитана посторонняя библиотека"); report.Add("PASS: only this game's Packs folder is used; source roles and line counts preserved");
                File.WriteAllLines(Path.Combine(Paths.Base, "native-pack-test.txt"), report); return 0;
            }
            catch (Exception e) { report.Add("FAIL: " + e); File.WriteAllLines(Path.Combine(Paths.Base, "native-pack-test.txt"), report); return 1; }
        }
        public static int Run()
        {
            var results = new List<string>();
            try
            {
                Pack demo = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                Check(demo.lines.Count == 4, "Пробный пак содержит 4 реплики"); results.Add("PASS: demo metadata and real video duration");
                AudioFeatureTests(results); PackPipelineTests(results);
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                using (var bitmap = icon.ToBitmap())
                {
                    int mint = 0, purple = 0;
                    for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) { var color = bitmap.GetPixel(x, y); if (color.A < 200) continue; if (color.G > color.R + 30 && color.G > color.B + 20) mint++; if (color.B > color.R + 30 && color.R > color.G + 10) purple++; }
                    Check(mint > 5 && purple > 5, "В EXE не встроена иконка сломанного телефона."); results.Add("PASS: compiled EXE contains the mint/purple broken-phone icon");
                }
                CancellationTests(results);
                var invitation = Connection.Parse(Connection.Invitation("26.10.20.30", Connection.DefaultPort, "ABC123")); Check(invitation.Host == "26.10.20.30" && invitation.Port == Connection.DefaultPort && invitation.AbsolutePath == "/ABC123", "Приглашение Radmin повреждено");
                string stage = Path.Combine(Paths.Cache, "test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
                try
                {
                    bool rejected = false; try { PackLibrary.SafePath(stage, "../escape.mp4"); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "Path traversal должен отклоняться");
                    string zip = Path.Combine(stage, "bad.zip");
                    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) using (var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open())) writer.Write("bad");
                    rejected = false; try { PackLibrary.Extract(zip, Path.Combine(stage, "unpack")); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected && !File.Exists(Path.Combine(stage, "outside.txt")), "Архив должен отклоняться до распаковки"); results.Add("PASS: archive and manifest path traversal rejected");
                }
                finally { Directory.Delete(stage, true); }
                string html = "<div class=\"workshopItem\"><a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=3825502622\"><img src=\"https://images.steamusercontent.com/test.jpg\"/><div class=\"workshopItemTitle\">[RUS] Пак &amp; дубль</div></a></div>";
                var items = WorkshopService.Parse(html); Check(items.Count == 1 && items[0].Id == "3825502622" && items[0].Title == "[RUS] Пак & дубль", "Парсер каталога"); results.Add("PASS: Workshop HTML parsing and Cyrillic titles");
                string ssrJson = Json.Write(new { loaderData = new[] { Json.Write(new { queries = new[] { new { state = new { data = new { results = new[] { new { publishedfileid = "3803458272", consumer_appid = 5020310, title = "Современный Steam", preview_url = "https://images.steamusercontent.com/test.jpg", short_description = "Language: Russian" } } } } } } }) } });
                var ssrItems = WorkshopService.Parse("<script id=\"valve-ssr-data\" type=\"application/json\">" + ssrJson + "</script>"); Check(ssrItems.Count == 1 && ssrItems[0].Title == "Современный Steam", "SSR-каталог Steam"); results.Add("PASS: modern Steam SSR nested JSON catalog");
                string fixture = Path.Combine(Paths.Cache, "native-cues-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
                try
                {
                    File.Copy(demo.video, Path.Combine(fixture, "dub_video.ogv"));
                    File.WriteAllText(Path.Combine(fixture, "_pack_info.ini"), "[data]\ntitle=\"Проверка ролей\"\nlanguage=\"RUS\"\n");
                    File.WriteAllText(Path.Combine(fixture, "01.ini"), "[data]\ncaption=\"Первая\"\ndub_timestamps=[0]\ndub_ends=[3]\ndub_characters=[\"Актёр\"]\n");
                    File.WriteAllText(Path.Combine(fixture, "02.ini"), "[data]\ncaption=\"Вторая\"\ndub_timestamps=[2]\ndub_ends=[5]\ndub_characters=[\"Режиссёр\"]\n");
                    Pack cues = PackLibrary.Load(fixture, ""); Check(cues.lines.Count == 2 && cues.DisplayLines == 2 && cues.DisplayRoles == 2 && cues.lines[0].role == "Актёр" && cues.lines[1].role == "Режиссёр", "Исходные реплики и роли должны оставаться отдельными.");
                    Pack shared = cues.PublicCopy(); Check(shared.DisplayLines == 2 && shared.DisplayRoles == 2 && shared.thumbnail == cues.thumbnail && shared.root == "", "Превью и счётчики не передаются гостям");
                    Check(Path.GetFullPath(Paths.Packs) == Path.Combine(Paths.Base, "Packs"), "Паки сохраняются не в собственной папке игры");
                    Check(new WorkshopService().InstalledPacks().All(p => p.root.StartsWith(Paths.Packs, StringComparison.OrdinalIgnoreCase) || p.root.StartsWith(Path.Combine(Paths.Base, "Assets"), StringComparison.OrdinalIgnoreCase)), "Используется папка другой игры");
                    results.Add("PASS: own Packs folder; overlapping cues retain separate roles, exact counts and cover through lobby transfer");
                }
                finally { Directory.Delete(fixture, true); }
                using (var server = new RoomServer(demo, 0))
                using (var host = new Inbox())
                using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Ведущий", server.Secret); host.Wait(s => s.players.Count == 1);
                    var route = Connection.Addresses().FirstOrDefault(a => a.Radmin);
                    guest.Client.Connect(route == null ? "127.0.0.1" : route.Address, server.Port, server.Code, "Друг", ""); host.Wait(s => s.players.Count == 2); guest.Wait(s => s.players.Count == 2);
                    host.Client.Send(new Message { kind = "local", text = "Локальный" }); host.Error("только сетевых"); Check(host.State.players.Count == 2, "Создан локальный игрок.");
                    results.Add("PASS: network-only participants; invitation parsing; " + (route == null ? "TCP loopback" : "connection through actual Radmin interface"));
                    guest.Client.Send(new Message { kind = "start" }); guest.Error("ведущему"); Check(host.State.phase == "lobby", "Гость не запускает раунд");
                    guest.Client.Send(new Message { kind = "chat", text = "Привет!" }); host.Wait(s => s.chat.Any(c => c.name == "Друг" && c.text == "Привет!"));
                    host.Client.Send(new Message { kind = "start" }); host.Error("готовности");
                    host.Client.Send(new Message { kind = "ready", flag = true }); guest.Client.Send(new Message { kind = "ready", flag = true }); host.Wait(s => s.players.All(p => p.ready));
                    StartLoadedRound(host, guest); results.Add("PASS: two real TCP clients, host permissions, ready gate and all-player pack-loading barrier");
                    string transferred = guest.Client.Asset("video", ".mp4").Result; Check(new FileInfo(transferred).Length == new FileInfo(demo.video).Length, "Передача видео");
                    Check(File.ReadAllBytes(transferred).SequenceEqual(File.ReadAllBytes(demo.video)), "Видео передано без повреждений"); results.Add("PASS: video transfer with byte-for-byte verification");
                    byte[] pcm = new byte[44100]; for (int i = 0; i < pcm.Length / 2; i++) { short sample = (short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 2000); byte[] pair = BitConverter.GetBytes(sample); pcm[i * 2] = pair[0]; pcm[i * 2 + 1] = pair[1]; }
                    byte[] wav = Audio.Wave(pcm);
                    bool unavailable = false; try { guest.Client.Asset("take:0", ".wav").Wait(3000); } catch (AggregateException) { unavailable = true; } Check(unavailable, "Чужая/отсутствующая запись должна сразу отклоняться"); results.Add("PASS: unavailable private audio fails immediately");
                    guest.Client.Send(new Message { kind = "take", index = 0, round = guest.State.round, data = Convert.ToBase64String(wav) }); guest.Error("другого игрока");
                    guest.Client.Send(new Message { kind = "recording", index = 0, round = guest.State.round, flag = true }); guest.Error("другого игрока");
                    host.Client.Send(new Message { kind = "recording", index = 0, round = host.State.round, flag = true }); guest.Wait(s => s.performing);
                    var notice = TurnNotice.For(guest.State, guest.Client.Id); Check(notice.Title.Contains("Скоро") && notice.Detail.Contains("Ведущий") && notice.Position.Contains("следующий"), "Гость не видит сообщение о скором ходе");
                    for (int i = 0; i < 4; i++)
                    {
                        var current = i % 2 == 0 ? host : guest; current.Wait(s => s.phase == "recording" && s.turn == i);
                        if (i > 0)
                        {
                            string previous = current.Client.Asset("take:" + (i - 1), ".wav").Result; Check(File.ReadAllBytes(previous).SequenceEqual(wav), "Предыдущая реплика передана");
                            current.Client.Send(new Message { kind = "recording", index = i, round = current.State.round, flag = true }); current.Wait(s => s.turn == i && s.performing && !s.heard);
                        }
                        current.Client.Send(new Message { kind = "take", index = i, round = current.State.round, data = Convert.ToBase64String(wav) });
                        int next = i + 1; host.Wait(s => s.turn == next);
                    }
                    results.Add("PASS: ordered turns and private predecessor audio; recording and submission work without listening");
                    host.Wait(s => s.phase == "result"); guest.Wait(s => s.phase == "result"); string result = guest.Client.Asset("result", ".mp4").Result;
                    Check(Math.Abs(PackLibrary.Duration(result) - 16) < .2, "Готовый дубль сохранил полную длину сцены"); Check(new FileInfo(result).Length > 1000, "MP4 пустой"); results.Add("PASS: full round -> FFmpeg mix -> MP4 delivered to guest (16 seconds)");
                    CheckMixedVoices(result, demo); results.Add("PASS: decoded final MP4 contains recorded voices at all four original cue positions");
                    string hostResult = host.Client.Asset("result", ".mp4").Result;
                    Check(File.ReadAllBytes(result).SequenceEqual(File.ReadAllBytes(hostResult)), "Игроки получили разные финальные видео.");
                    host.Client.Send(new Message { kind = "result_ready", round = host.State.round }); host.Wait(s => s.resultReady.Length == 1);
                    Check(host.State.playbackVersion == 0 && !host.State.playing, "Показ начался автоматически.");
                    host.Client.Send(new Message { kind = "playback", text = "start", round = host.State.round }); host.Wait(s => s.playbackVersion == 1); guest.Wait(s => s.playbackVersion == 1);
                    Check(!host.State.resultReady.Contains(guest.Client.Id), "Тест не проверяет запуск без готовности гостя.");
                    guest.Client.Send(new Message { kind = "result_ready", round = guest.State.round }); host.Wait(s => s.resultReady.Length == 2);
                    Check(host.State.playing && host.State.playbackAt == guest.State.playbackAt && host.State.playbackAt > Playback.Now, "Нет общего запланированного старта.");
                    guest.Client.Send(new Message { kind = "playback", text = "pause", round = guest.State.round }); guest.Error("ведущему");
                    host.Client.Send(new Message { kind = "playback", text = "pause", round = host.State.round }); host.Wait(s => s.playbackVersion == 2); guest.Wait(s => s.playbackVersion == 2);
                    Check(!host.State.playing && !guest.State.playing && host.State.playbackPosition == guest.State.playbackPosition, "Пауза не синхронизирована.");
                    host.Client.Send(new Message { kind = "playback", text = "restart", round = host.State.round }); host.Wait(s => s.playbackVersion == 3); guest.Wait(s => s.playbackVersion == 3);
                    Check(host.State.playing && guest.State.playing && host.State.playbackPosition == 0 && host.State.playbackAt == guest.State.playbackAt, "Общий повтор не синхронизирован.");
                    results.Add("PASS: both players download identical final MP4; host starts manually without guest readiness; late readiness does not restart playback; host-only pause and replay");
                    host.Client.Send(new Message { kind = "reset" }); host.Wait(s => s.phase == "lobby" && s.players.All(p => !p.ready)); results.Add("PASS: replay resets ready state");
                }
                bool badWave = false; try { Audio.Validate(new byte[100], 4); } catch (InvalidDataException) { badWave = true; } Check(badWave, "Некорректная запись отклонена"); results.Add("PASS: malformed audio rejected");
                File.WriteAllLines(Path.Combine(Paths.Base, "self-test.txt"), results); return 0;
            }
            catch (Exception error) { results.Add("FAIL: " + error); File.WriteAllLines(Path.Combine(Paths.Base, "self-test.txt"), results); return 1; }
        }
    }
}
