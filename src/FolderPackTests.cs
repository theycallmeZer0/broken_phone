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
    public sealed partial class GameWindow
    {
        public async Task FolderPacksSmoke()
        {
            var report = new List<string>(); string originalSettings = Json.Write(settings);
            string fixture = Path.Combine(Paths.Cache, "folder-test-" + Guid.NewGuid().ToString("N")), imported = null;
            var guest = new RoomClient(); RoomState guestState = null;
            try
            {
                await Until(() => Me != null, "Нет лобби.");
                string collection = Path.Combine(fixture, "Коллекция с пробелами"), jsonRoot = Path.Combine(collection, "JSON пак"), nativeRoot = Path.Combine(collection, "Подборка", "9900301");
                Directory.CreateDirectory(jsonRoot); Directory.CreateDirectory(nativeRoot);
                foreach (string file in Directory.GetFiles(Path.Combine(Paths.Base, "Assets", "Demo"))) File.Copy(file, Path.Combine(jsonRoot, Path.GetFileName(file)));
                var metadata = Json.Read<Dictionary<string, object>>(File.ReadAllText(Path.Combine(jsonRoot, "pack.json"))); metadata["title"] = "Название из метаданных JSON"; File.WriteAllText(Path.Combine(jsonRoot, "pack.json"), Json.Write(metadata));
                File.Copy(Path.Combine(jsonRoot, "video.mp4"), Path.Combine(nativeRoot, "dub_video.ogv"));
                File.WriteAllText(Path.Combine(nativeRoot, "_pack_info.ini"), "[data]\ntitle=\"Название из метаданных Dub Together\"\nlanguage=\"RUS\"\n");
                File.WriteAllText(Path.Combine(nativeRoot, "01.ini"), "[data]\ndub_timestamps=[0,4,8,12]\ndub_ends=[4,8,12,16]\ndub_characters=[\"Первый\",\"Второй\",\"Первый\",\"Второй\"]\n");
                string broken = Path.Combine(collection, "Повреждённый пак"); Directory.CreateDirectory(broken); File.WriteAllText(Path.Combine(broken, "pack.json"), "{}");
                var single = await Task.Run(() => FolderPacks.Scan(nativeRoot));
                if (single.Packs.Count != 1 || single.Packs[0].workshopId != "9900301" || single.Packs[0].DisplayRoles != 2 || single.Packs[0].DisplayLines != 4) throw new Exception("Одиночный нативный пак не распознан.");
                var before = Directory.GetFiles(collection, "*", SearchOption.AllDirectories).ToDictionary(file => file, file => PackContent.FileHash(file));
                foreach (string file in before.Keys) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                OpenSettings(); C<ComboBox>("PackSourceCombo").SelectedIndex = 0; TBox("PacksFolderInput").Text = collection; await SaveSettings();
                var saved = Json.Read<Settings>(File.ReadAllText(SettingsFile));
                if (!saved.LocalPacks || saved.PacksFolder != collection) throw new Exception("Папка не сохранена из меню настроек.");
                OpenSettings(); window.UpdateLayout(); TBox("PacksFolderInput").BringIntoView(); await Task.Delay(120); window.UpdateLayout(); SaveScreen("folder-settings-preview.png");
                Page("Workshop"); TBox("SearchInput").Text = ""; await Search(); window.UpdateLayout();
                if (C<WrapPanel>("PackCards").Children.Count != 2 || C<StackPanel>("PackDownloadToolbar").Visibility != Visibility.Collapsed || !C<TextBlock>("WorkshopStatus").Text.Contains("Пропущено: 1")) throw new Exception("Коллекция или повреждённый пак обработаны неправильно.");
                SaveScreen("folder-packs-preview.png");
                TBox("SearchInput").Text = "JSON"; await Search(); if (C<WrapPanel>("PackCards").Children.Count != 1) throw new Exception("Не работает поиск по названию из метаданных.");
                TBox("SearchInput").Text = ""; await Search();
                report.Add("PASS: settings persist a Cyrillic folder path; single and nested JSON/INI packs discovered; metadata titles preserved; corrupt pack skipped; local search works without a Steam catalog request");
                guest.Received += message => { if (message.state != null) guestState = message.state; };
                await Task.Run(() => guest.Connect("127.0.0.1", server.Port, server.Code, "Проверка локального пака", ""));
                var card = C<WrapPanel>("PackCards").Children.OfType<Border>().Single(b => Object.Equals(b.ToolTip, nativeRoot));
                imported = Path.Combine(Paths.Packs, "folder-" + single.Packs[0].contentKey);
                ((StackPanel)card.Child).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => downloadCancellation == null && state.pack.title == single.Packs[0].title && guestState != null && guestState.pack.title == single.Packs[0].title, "Пак не выбран или не передан гостю.");
                var prepared = await Task.Run(() => PackLibrary.Load(imported, "9900301"));
                if (Path.GetExtension(prepared.video) != ".mp4" || !FolderPacks.InLibrary(prepared) || guestState.pack.DisplayRoles != 2 || guestState.pack.DisplayLines != 4 || String.IsNullOrEmpty(guestState.pack.thumbnail)) throw new Exception("При импорте потеряны видео или метаданные для сети.");
                if (Directory.GetFiles(collection, "*", SearchOption.AllDirectories).Length != before.Count || before.Any(pair => PackContent.FileHash(pair.Key) != pair.Value)) throw new Exception("Исходная папка изменена.");
                report.Add("PASS: selecting a read-only native pack copies and converts it to own Packs, leaves source files unchanged, broadcasts preview and counts to another TCP client");
                string empty = Path.Combine(fixture, "Пустая"); Directory.CreateDirectory(empty);
                Page("Workshop"); await ApplyPackSource(true, empty);
                if (C<WrapPanel>("PackCards").Children.Count != 0 || !C<TextBlock>("WorkshopStatus").Text.Contains("нет паков")) throw new Exception("Пустая папка не обработана.");
                bool rejected = false;
                try { await ApplyPackSource(true, Path.Combine(fixture, "Несуществующая")); } catch (DirectoryNotFoundException) { rejected = true; }
                if (!rejected || settings.PacksFolder != empty) throw new Exception("Недоступная папка заменила выбранный источник.");
                report.Add("PASS: empty collection shown correctly; unavailable source folder rejected without changing the configured source");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally
            {
                guest.Dispose(); settings = Json.Read<Settings>(originalSettings); Persist();
                File.WriteAllLines(Path.Combine(Paths.Base, "folder-packs-ui-test.txt"), report); window.Close();
                if (Directory.Exists(fixture)) { foreach (string file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(fixture, true); }
                if (imported != null && Directory.Exists(imported)) Directory.Delete(imported, true);
            }
        }
    }
}
