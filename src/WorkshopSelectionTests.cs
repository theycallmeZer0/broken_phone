using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        static Message WaitWorkshopMessage(Inbox box, Func<Message, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(4); Message message;
            while (DateTime.UtcNow < deadline)
            {
                while (box.Messages.TryDequeue(out message)) if (predicate(message)) return message;
                Thread.Sleep(10);
            }
            throw new Exception("Не получили сообщение выбора или файлов Workshop.");
        }
        static void WorkshopBarrier(Inbox box, string text)
        {
            box.Client.Send(new Message { kind = "chat", text = text });
            box.Wait(state => state.chat.Any(line => line.text == text), 4);
        }
        public static int WorkshopSelectionRegression()
        {
            var report = new List<string>();
            try
            {
                var source = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "9900777");
                var metadata = new Pack { workshopId = source.workshopId, title = "Выбрано до загрузки", preview = "https://example.com/pack.jpg", lineCount = 4, roleCount = 2 };
                var pending = PendingWorkshop.Create(metadata);
                Check(pending.pendingDownload && !pending.Selected && pending.lines.Count == 0 && String.IsNullOrEmpty(pending.video) && String.IsNullOrEmpty(pending.root) && pending.workshopId == metadata.workshopId && pending.contentKey != PendingWorkshop.Create(metadata).contentKey, "Выбор требует локальных файлов или повторно использует токен.");
                var cleaned = PendingWorkshop.Create(new Pack { workshopId = source.workshopId, title = "Название\r\nпака", preview = "file:///C:/private.png", lineCount = -1, roleCount = -2, video = source.video, root = source.root, lines = source.lines });
                Check(cleaned.title == "Названиепака" && cleaned.preview == "" && cleaned.DisplayLines == 0 && cleaned.DisplayRoles == 0 && cleaned.lines.Count == 0 && String.IsNullOrEmpty(cleaned.root), "В отложенный выбор попали пути, строки или некорректные метаданные.");
                bool invalid = false; try { PendingWorkshop.Create(new Pack { workshopId = "../escape" }); } catch (InvalidDataException) { invalid = true; }
                Check(invalid, "Некорректный Workshop ID принят.");
                report.Add("PASS: metadata-only Workshop selection needs no local file, uses a fresh revision, and discards unsafe paths and preview URLs");

                using (var server = new RoomServer(new Pack { title = "Пак не выбран" }, 0)) using (var host = new Inbox()) using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Host", server.Secret); host.Wait(state => state.players.Count == 1);
                    guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Guest", "", 12345); host.Wait(state => state.players.Count == 2); guest.Wait(state => state.players.Count == 2);
                    guest.Client.Send(new Message { kind = "workshop_select", pack = metadata }); guest.Error("ведущему");
                    Check(!guest.State.pack.pendingDownload, "Гость выбрал пак вместо ведущего.");
                    host.Client.Send(new Message { kind = "workshop_select", pack = metadata });
                    guest.Wait(state => state.pack.pendingDownload && state.pack.title == metadata.title, 4); host.Wait(state => state.pack.pendingDownload, 4);
                    string first = host.State.pack.contentKey;
                    Check(guest.State.pack.preview == metadata.preview && guest.State.pack.workshopId == source.workshopId && guest.State.pack.DisplayLines == 4 && !guest.State.pack.Selected && guest.State.players.All(player => !player.ready && !player.prepared), "Выбор не передан гостю до получения файлов.");
                    host.Client.Send(new Message { kind = "start" }); host.Error("Пак не выбран");
                    Check(host.State.phase == "lobby", "Началась игра без скачанного пака.");
                    report.Add("PASS: host selection reaches another TCP client before any file download; guests cannot select and a pending pack cannot start a round");

                    var endpoint = new PackPeerEndpoint { id = "spoofed", address = "203.0.113.55", port = 32123, token = new string('a', 64), steamId = 999 };
                    guest.Client.Send(new Message { kind = "workshop_offer", code = first, pack = source.PublicCopy(), packPeer = endpoint });
                    var offered = WaitWorkshopMessage(host, message => message.kind == "workshop_offer");
                    Check(offered.code == first && offered.pack.contentKey == source.contentKey && offered.packPeer.address == "127.0.0.1" && offered.packPeer.id == guest.Client.Id && offered.packPeer.steamId == 12345 && offered.packPeer.port == endpoint.port && String.IsNullOrEmpty(offered.pack.root), "Предложение файлов содержит подменённую личность, адрес или путь.");
                    var damaged = source.PublicCopy(); damaged.videoHash = new string('0', 64);
                    guest.Client.Send(new Message { kind = "workshop_offer", code = first, pack = damaged, packPeer = endpoint }); guest.Error("данные");
                    report.Add("PASS: donor offers are hash-validated and sent only to the host with authenticated peer identity and address");

                    host.Client.Send(new Message { kind = "workshop_select", pack = metadata }); host.Wait(state => state.pack.pendingDownload && state.pack.contentKey != first, 4); guest.Wait(state => state.pack.pendingDownload && state.pack.contentKey != first, 4);
                    string second = host.State.pack.contentKey;
                    guest.Client.Send(new Message { kind = "workshop_offer", code = first, pack = source.PublicCopy(), packPeer = endpoint }); WorkshopBarrier(guest, "stale-offer-processed");
                    Check(!host.Messages.Any(message => message.kind == "workshop_offer" && message.code == first), "Предложение старого выбора дошло до ведущего.");
                    host.Client.Send(new Message { kind = "pack", code = first, pack = source }); WorkshopBarrier(host, "stale-commit-processed");
                    Check(host.State.pack.pendingDownload && host.State.pack.contentKey == second, "Старая загрузка заменила новый выбор.");
                    guest.Client.Send(new Message { kind = "pack", code = second, pack = source }); guest.Error("ведущему");
                    host.Client.Send(new Message { kind = "pack", code = second, pack = source });
                    host.Wait(state => state.pack.Selected && !state.pack.pendingDownload && state.pack.workshopId == source.workshopId, 4); guest.Wait(state => state.pack.Selected && !state.pack.pendingDownload, 4);
                    Check(host.State.players.All(player => !player.ready && !player.prepared) && host.State.roles.Count == 2, "Финальный выбор не восстановил проверку готовности и роли.");
                    report.Add("PASS: selecting again invalidates stale donor offers and stale host commits; only the host can publish the prepared descriptor, retaining readiness and role barriers");
                }
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "workshop-selection-test.txt"), report); }
        }
    }

    public sealed partial class GameWindow
    {
        public async Task WorkshopSelectionSmoke()
        {
            var report = new List<string>(); var guest = new RoomClient(); RoomState guestState = null;
            var firstRelease = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRelease = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously);
            const string firstId = "9900761", secondId = "9900762"; var calls = new List<string>();
            string donorFolder = null, donorCache = null;
            try
            {
                await Until(() => Me != null, "Нет лобби для выбора пака.");
                if (videoDownload != null) await videoDownload;
                var first = await Task.Run(() => PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), firstId));
                var second = await Task.Run(() => PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), secondId));
                if (InstalledItem(firstId) != null || InstalledItem(secondId) != null) throw new Exception("Тестовые паки уже установлены.");
                pendingWorkshopLoader = (item, progress, token) =>
                {
                    if (!state.pack.pendingDownload || state.pack.workshopId != item.Id || state.phase != "lobby") throw new Exception("Загрузка началась до выбора пака в лобби.");
                    calls.Add(item.Id); progress(PackProgress.Bytes("Проверочная загрузка", 1, 4));
                    if (item.Id == firstId) return firstRelease.Task;
                    if (item.Id == secondId) return secondRelease.Task;
                    return Task.FromException<Pack>(new IOException("Проверка: Steam не разрешает загрузку."));
                };
                guest.Received += message => { if (message.state != null) guestState = message.state; };
                await Task.Run(() => guest.Connect("127.0.0.1", server.Port, server.Code, "Проверка выбора", ""));
                await Until(() => state.players.Count == 2 && guestState != null, "Гость не подключился.");
                RenderPackSource(); Page("Workshop"); packControls.Clear(); C<WrapPanel>("PackCards").Children.Clear();
                WorkshopCard(new WorkshopItem { Id = firstId, Title = "Выбор до загрузки", Lines = 4, Roles = 2 });
                WorkshopCard(new WorkshopItem { Id = secondId, Title = "Новый выбор", Lines = 4, Roles = 2 });
                packControls[firstId].Download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.pack.pendingDownload && state.pack.workshopId == firstId && calls.Contains(firstId) && guestState.pack.pendingDownload && guestState.pack.workshopId == firstId, "Нескачанный пак не выбран и не передан гостю.");
                string firstKey = state.pack.contentKey;
                if (currentPage != "Lobby" || C<TextBlock>("PackTitle").Text != "Выбор до загрузки" || B("ReadyButton").IsEnabled || B("StartButton").IsEnabled || packDownloads.Snapshot().Any(job => job.Item.Id == firstId)) throw new Exception("Выбор сначала требует загрузки в библиотеку или разрешает старт до подготовки.");
                window.UpdateLayout(); SaveScreen("workshop-pending-preview.png");
                report.Add("PASS: clicking an uninstalled Workshop card selects and broadcasts its title before a blocked download; the lobby shows loading and readiness/start stay disabled");

                Page("Workshop"); packControls[secondId].Download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.pack.pendingDownload && state.pack.workshopId == secondId && calls.Contains(secondId) && guestState.pack.pendingDownload && guestState.pack.workshopId == secondId, "Другой пак не заменил текущую загрузку.");
                string secondKey = state.pack.contentKey;
                if (firstKey == secondKey || C<TextBlock>("PackTitle").Text != "Новый выбор") throw new Exception("Повторный выбор не изменил токен и карточку лобби.");
                firstRelease.TrySetResult(first);
                await Task.Delay(200);
                if (!state.pack.pendingDownload || state.pack.contentKey != secondKey || state.pack.workshopId != secondId) throw new Exception("Завершение старой загрузки заменило новый выбор.");
                secondRelease.TrySetResult(second);
                await Until(() => state.pack.Selected && !state.pack.pendingDownload && state.pack.workshopId == secondId && guestState.pack.Selected && !guestState.pack.pendingDownload && guestState.pack.workshopId == secondId, "Готовый пак не выбран у ведущего и гостя.");
                if (calls.Count != 2 || !B("ReadyButton").IsEnabled || state.roles.Count != 2 || state.players.Any(player => player.ready)) throw new Exception("Готовый пак не восстановил выбор ролей и проверку готовности.");
                report.Add("PASS: switching packs cancels the previous selection; its delayed result cannot commit; the new completed descriptor reaches both clients and restores the readiness barrier");

                const string donorId = "9900763";
                donorFolder = Path.Combine(Paths.Cache, "pending-ui-donor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(donorFolder);
                foreach (string file in Directory.GetFiles(Path.Combine(Paths.Base, "Assets", "Demo"))) File.Copy(file, Path.Combine(donorFolder, Path.GetFileName(file)));
                // A valid MP4 free box makes this fixture distinct from every cached demo copy.
                using (var output = new FileStream(Path.Combine(donorFolder, "video.mp4"), FileMode.Append))
                {
                    output.Write(new byte[] { 0, 0, 0, 24, 102, 114, 101, 101 }, 0, 8); byte[] unique = Guid.NewGuid().ToByteArray(); output.Write(unique, 0, unique.Length);
                }
                var metadata = Json.Read<Dictionary<string, object>>(File.ReadAllText(Path.Combine(donorFolder, "pack.json"))); metadata["title"] = "Пак от участника " + Guid.NewGuid().ToString("N");
                File.WriteAllText(Path.Combine(donorFolder, "pack.json"), Json.Write(metadata));
                var donor = await Task.Run(() => PackLibrary.Load(donorFolder, donorId)); donorCache = Path.Combine(Paths.Packs, "room-" + donor.contentKey);
                SelectWorkshopPack(new WorkshopItem { Id = donorId, Title = donor.title, Lines = donor.DisplayLines, Roles = donor.DisplayRoles });
                await Until(() => state.pack.pendingDownload && state.pack.workshopId == donorId && calls.Contains(donorId) && guestState.pack.pendingDownload && guestState.pack.workshopId == donorId, "Не начался выбор пака без доступа к Steam.");
                string donorKey = state.pack.contentKey;
                await Task.Run(() => guest.OfferWorkshopPack(donorKey, donor));
                await Until(() => state.pack.Selected && !state.pack.pendingDownload && state.pack.workshopId == donorId && guestState.pack.Selected && guestState.pack.workshopId == donorId, "Ведущий не принял копию пака участника после отказа Steam.");
                if (videoDownload != null) await videoDownload;
                if (guest.PackUploadBytes < donor.videoBytes || state.pack.contentKey != donor.contentKey || !File.Exists(Path.Combine(donorCache, "video.mp4")) || !B("ReadyButton").IsEnabled) throw new Exception("Пак не передан напрямую от участника или остался недоступен после загрузки.");
                report.Add("PASS: when the host loader fails, the real UI accepts a guest offer, receives unique verified video bytes directly over TCP, commits the full pack, and enables readiness without Steam");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally
            {
                pendingWorkshopLoader = null; firstRelease.TrySetCanceled(); secondRelease.TrySetCanceled(); guest.Dispose();
                File.WriteAllLines(Path.Combine(Paths.Base, "workshop-selection-ui-test.txt"), report); window.Close();
                if (donorFolder != null && Path.GetFullPath(donorFolder).StartsWith(Path.GetFullPath(Paths.Cache).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(donorFolder)) Directory.Delete(donorFolder, true);
                if (donorCache != null && Path.GetFullPath(donorCache).StartsWith(Path.GetFullPath(Paths.Packs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(donorCache)) Directory.Delete(donorCache, true);
            }
        }
    }
}
