using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        internal static string DemoArchive()
        {
            string file = Path.Combine(Paths.Cache, "test-pack-" + Guid.NewGuid().ToString("N") + ".zip");
            ZipFile.CreateFromDirectory(Path.Combine(Paths.Base, "Assets", "Demo"), file, CompressionLevel.NoCompression, false); return file;
        }
        sealed class PackHttp : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            readonly byte[] data; volatile bool stopped;
            public int MaxActive; int active;
            public string Url { get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/pack"; } }
            public PackHttp(byte[] bytes)
            {
                data = bytes; listener.Start(); Task.Factory.StartNew(() =>
                {
                    while (!stopped) try { var socket = listener.AcceptTcpClient(); Task.Factory.StartNew(() => Serve(socket), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); } catch (SocketException) { break; } catch (ObjectDisposedException) { break; }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            void Serve(TcpClient socket)
            {
                int current = Interlocked.Increment(ref active); MaxActive = Math.Max(MaxActive, current);
                try
                {
                    using (socket) using (var stream = socket.GetStream())
                    {
                        var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + data.Length + "\r\nContent-Type: application/zip\r\nConnection: close\r\n\r\n"); stream.Write(header, 0, header.Length);
                        for (int i = 0; i < data.Length && !stopped; i += 8192) { int size = Math.Min(8192, data.Length - i); stream.Write(data, i, size); stream.Flush(); Thread.Sleep(8); }
                    }
                }
                catch (IOException) { } catch (SocketException) { }
                finally { Interlocked.Decrement(ref active); }
            }
            public void Dispose() { stopped = true; listener.Stop(); }
        }
        static void PackPipelineTests(List<string> report)
        {
            string archive = DemoArchive(); var folders = new[] { "9900101", "9900102", "9900103" }.Select(id => Path.Combine(Paths.Packs, id)).ToArray();
            try
            {
                var progress = new List<double>(); int calls = 0;
                using (var http = new PackHttp(File.ReadAllBytes(archive)))
                using (var queue = new PackDownloadQueue(async (item, update, token) =>
                {
                    Interlocked.Increment(ref calls); string local = Path.Combine(Paths.Cache, "queue-" + item.Id + ".zip");
                    await Transfers.Download(http.Url, local, token, (received, total) => { var p = PackProgress.Bytes("Тестовая загрузка", received, total); lock (progress) progress.Add(p.Fraction); update(p); });
                    return await Task.Run(() => PackLibrary.Import(local, item.Id, token), token);
                }))
                {
                    var first = queue.Add(new WorkshopItem { Id = "9900101", Title = "Первый" }); var second = queue.Add(new WorkshopItem { Id = "9900102", Title = "Второй" }); var third = queue.Add(new WorkshopItem { Id = "9900103", Title = "Третий" });
                    Check(ReferenceEquals(first, queue.Add(first.Item)), "Повторный клик создаёт дубликат загрузки."); queue.Cancel(third.Item.Id);
                    Task.WaitAll(first.Completion, second.Completion); try { third.Completion.Wait(); } catch (AggregateException) { }
                    Check(calls == 2 && http.MaxActive == 2 && queue.Snapshot().Count(j => j.State == "ready") == 2 && queue.Snapshot().Any(j => j.State == "cancelled"), "Неверно работают параллельная очередь и отмена.");
                    Check(progress.Any(p => p > 0 && p < 1) && progress.Any(p => p == 1) && Directory.Exists(folders[0]) && Directory.Exists(folders[1]), "Нет промежуточного прогресса или паки не сохранены.");
                    report.Add("PASS: two real HTTP pack downloads in parallel, exact progress, deduplicated clicks, queued cancellation and own-Packs import");
                }
                var remote = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "").PublicCopy(); remote.id = "network-cache-test";
                string cache = Path.Combine(Paths.Packs, "room-" + remote.contentKey); bool existed = Directory.Exists(cache); int transfers = 0;
                var source = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                try
                {
                    var prepared = PackContent.Prepare(remote, (asset, extension, update, token) => { transfers++; string path = asset == "video" ? source.video : source.background; update(new FileInfo(path).Length, new FileInfo(path).Length); return Task.FromResult(path); }, p => { }, CancellationToken.None, false).GetAwaiter().GetResult();
                    Check(prepared.contentKey == remote.contentKey && transfers == (remote.backgroundBytes > 0 ? 2 : 1) && prepared.DisplayRoles == remote.DisplayRoles && prepared.thumbnail == remote.thumbnail, "Подготовка потеряла файлы, роли или обложку.");
                    PackContent.Prepare(remote, (a, e, p, t) => { throw new Exception("Повторная загрузка готового пака."); }, p => { }, CancellationToken.None).GetAwaiter().GetResult();
                    remote.videoHash = new string('0', 64); bool rejected = false;
                    try { PackContent.Prepare(remote, (a, e, p, t) => Task.FromResult(source.video), p => { }, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "Подмена метаданных не отклонена.");
                    report.Add("PASS: complete lobby pack cached in own Packs with preview/counts, reused without transfer, invalid content hashes rejected");
                }
                finally { if (!existed && Directory.Exists(cache)) Directory.Delete(cache, true); }
                var parsed = WorkshopService.ParseProgress("Update state downloading, progress: 50.00 (512 / 1024)"); Check(parsed != null && parsed.Fraction == .5 && parsed.Total == 1024, "Не разобран реальный формат прогресса SteamCMD.");
            }
            finally { File.Delete(archive); foreach (var folder in folders) if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }

    public sealed partial class GameWindow
    {
        void CheckPackPreviewFits()
        {
            window.UpdateLayout(); var preview = C<PackCover>("PackPreview"); var artwork = C<Border>("LobbyPackArtwork");
            Rect bounds = preview.TransformToVisual(artwork).TransformBounds(new Rect(preview.RenderSize));
            if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > artwork.ActualWidth + 1 || bounds.Bottom > artwork.ActualHeight + 1)
                throw new Exception("Обложка выходит за скруглённый баннер: " + bounds);
            double rightGap = artwork.ActualWidth - bounds.Right;
            if (Math.Abs(rightGap - bounds.Top) > 1 || Math.Abs(rightGap - (artwork.ActualHeight - bounds.Bottom)) > 1)
                throw new Exception("Отступ справа отличается от верхнего или нижнего отступа.");
            if (Math.Abs(preview.ActualWidth / preview.ActualHeight - 16.0 / 9) > .01)
                throw new Exception("Формат превью в лобби отличается от 16:9: " + preview.ActualWidth + "×" + preview.ActualHeight);
            Rect content = preview.ContentBounds;
            if (Math.Abs(content.Left) > .1 || Math.Abs(content.Top) > .1 || Math.Abs(content.Width - preview.ActualWidth) > .1 || Math.Abs(content.Height - preview.ActualHeight) > .1)
                throw new Exception("Обложка не растянута на всю рамку.");
            Rect title = C<TextBlock>("PackTitle").TransformToVisual(artwork).TransformBounds(new Rect(C<TextBlock>("PackTitle").RenderSize));
            if (bounds.IntersectsWith(title)) throw new Exception("Название перекрывает обложку.");
        }
        void CheckCoverPixels(PackCover preview, Size sourceSize)
        {
            var root = C<Grid>("Root");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var points = new[] { new Point(.04, .5), new Point(.5, .04), new Point(.96, .5), new Point(.5, .96) };
            var colors = new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta };
            for (int i = 0; i < points.Length; i++)
            {
                Point position = preview.TransformToAncestor(root).Transform(new Point(points[i].X * preview.ActualWidth, points[i].Y * preview.ActualHeight));
                var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)position.X, (int)position.Y, 1, 1), pixel, 4, 0);
                if (Math.Abs(pixel[0] - colors[i].B) > 8 || Math.Abs(pixel[1] - colors[i].G) > 8 || Math.Abs(pixel[2] - colors[i].R) > 8) throw new Exception("Край обложки обрезан при смене пропорций: " + sourceSize + ", сторона=" + i + ", BGRA=" + String.Join(",", pixel));
            }
        }
        public async Task PackDownloadsSmoke()
        {
            var report = new List<string>(); string archive = SelfTests.DemoArchive(), selected = null; var guest = new RoomClient(); RoomState guestState = null;
            try
            {
                await Until(() => Me != null, "Нет лобби."); selected = state.pack.id;
                guest.Received += message => { if (message.state != null) guestState = message.state; }; await Task.Run(() => guest.Connect("127.0.0.1", server.Port, server.Code, "Проверка превью", ""));
                packDownloads.Dispose(); packDownloads = new PackDownloadQueue(async (item, progress, token) =>
                {
                    for (int i = 1; i <= 12; i++) { await Task.Delay(80, token); progress(PackProgress.Bytes("Загрузка", i * 100, 1200)); }
                    return await Task.Run(() => PackLibrary.Import(archive, item.Id, token), token);
                }); AttachDownloadQueue();
                RenderPackSource(); Page("Workshop"); packControls.Clear(); C<WrapPanel>("PackCards").Children.Clear();
                WorkshopCard(new WorkshopItem { Id = "9900201", Title = "Первый тестовый пак", Lines = 4, Roles = 2 }); WorkshopCard(new WorkshopItem { Id = "9900202", Title = "Второй тестовый пак", Lines = 4, Roles = 2 });
                packControls["9900201"].Select.IsChecked = true; packControls["9900202"].Select.IsChecked = true;
                if (!B("DownloadSelectedButton").IsEnabled) throw new Exception("Не работает выбор нескольких паков.");
                B("DownloadSelectedButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => packDownloads.Snapshot().Count(j => j.State == "loading") == 2 && packControls.Values.All(c => c.Download.IsEnabled), "Выбор пака ведущим недоступен во время фоновой загрузки.");
                await Task.Delay(200); SaveScreen("workshop-downloads-preview.png");
                packControls["9900201"].Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => packDownloads.Snapshot().Any(j => j.Item.Id == "9900202" && j.State == "ready") && packControls["9900202"].Download.IsEnabled, "Загрузка второго пака не завершилась.");
                if (state.pack.id != selected || currentPage != "Workshop" || packControls["9900201"].Download.IsEnabled == false) throw new Exception("Предзагрузка изменила лобби или кнопка отменённой загрузки не восстановилась.");
                report.Add("PASS: batch selection, two loading cards, host lobby selection remains available, live progress, individual cancel and no automatic lobby selection");
                packControls["9900202"].Download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.pack.Selected && !state.pack.pendingDownload && state.pack.workshopId == "9900202" && guestState != null && guestState.pack.Selected && !guestState.pack.pendingDownload && guestState.pack.workshopId == "9900202", "Подготовленный пак не передался гостю.");
                if (String.IsNullOrEmpty(guestState.pack.thumbnail) || guestState.pack.DisplayLines != 4 || guestState.pack.DisplayRoles != 2 || C<PackCover>("PackPreview").Source == null) throw new Exception("Гость не получил обложку, реплики или роли.");
                CheckPackPreviewFits(); SaveScreen("selected-pack-preview.png");
                var preview = C<PackCover>("PackPreview"); var source = preview.Source; var root = C<Grid>("Root"); double width = root.Width;
                try
                {
                    foreach (int layoutWidth in new[] { 1440, 1600, 2160 })
                    foreach (Size size in new[] { new Size(1920, 1080), new Size(900, 900), new Size(720, 1280), new Size(32, 24) })
                    {
                        root.Width = layoutWidth;
                        var drawing = new DrawingGroup();
                        using (var dc = drawing.Open())
                        {
                            dc.DrawRectangle(Brushes.White, null, new Rect(size));
                            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, size.Width * .08, size.Height));
                            dc.DrawRectangle(Brushes.Lime, null, new Rect(0, 0, size.Width, size.Height * .08));
                            dc.DrawRectangle(Brushes.Blue, null, new Rect(size.Width * .92, 0, size.Width * .08, size.Height));
                            dc.DrawRectangle(Brushes.Magenta, null, new Rect(0, size.Height * .92, size.Width, size.Height * .08));
                        }
                        preview.Source = new DrawingImage(drawing); CheckPackPreviewFits(); await Task.Delay(25); CheckCoverPixels(preview, size);
                    }
                }
                finally { root.Width = width; preview.Source = source; window.UpdateLayout(); }
                var backdrop = preview.Backdrop;
                for (int i = 0; i < 6; i++) { RenderLobby(); window.UpdateLayout(); await Task.Delay(20); if (!Object.ReferenceEquals(preview.Backdrop, backdrop)) throw new Exception("Фон пересоздаётся при неизменной обложке."); }
                if (backdrop == null || !backdrop.IsFrozen || C<Image>("PackBackdrop").Effect != null) throw new Exception("Под превью осталось живое размытие вместо статичного фона.");
                report.Add("PASS: explicit lobby selection immediately broadcasts pack preview, line and role counts to another TCP client");
                report.Add("PASS: landscape, square, portrait and tiny covers fill the entire 16:9 frame at three interface widths; all four source edges stay visible; equal top/bottom/right insets and no text overlap");
                report.Add("PASS: softened backdrop is frozen, has no live blur shader, and is reused across unchanged lobby updates");
            }
            catch (Exception e) { report.Add("FAIL: " + e); }
            finally
            {
                guest.Dispose(); File.WriteAllLines(System.IO.Path.Combine(Paths.Base, "pack-downloads-ui-test.txt"), report); window.Close(); File.Delete(archive);
                foreach (string id in new[] { "9900201", "9900202" }) { string path = System.IO.Path.Combine(Paths.Packs, id); if (Directory.Exists(path)) Directory.Delete(path, true); }
            }
        }
    }
}
