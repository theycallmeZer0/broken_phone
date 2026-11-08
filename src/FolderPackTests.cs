using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            string fixture = Path.Combine(Paths.Cache, "fixed-source-test-" + Guid.NewGuid().ToString("N"));
            string local = Path.Combine(Paths.Packs, "fixed-source-test-" + Guid.NewGuid().ToString("N")), prepared = null;
            var guest = new RoomClient(); RoomState guestState = null;
            try
            {
                await Until(() => Me != null, "Нет лобби.");
                if (videoDownload != null) await videoDownload;
                string prefix = "Fixed source " + Guid.NewGuid().ToString("N");
                foreach (string folder in new[] { fixture, local })
                {
                    Directory.CreateDirectory(folder);
                    foreach (string file in Directory.GetFiles(Path.Combine(Paths.Base, "Assets", "Demo"))) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                    string metadataFile = Path.Combine(folder, "pack.json");
                    var metadata = Json.Read<Dictionary<string, object>>(File.ReadAllText(metadataFile));
                    metadata["internal_test"] = false; metadata["title"] = prefix + (folder == local ? " game" : " external");
                    File.WriteAllText(metadataFile, Json.Write(metadata)); File.WriteAllText(Path.Combine(folder, ".source"), "9900301");
                }
                var legacy = Json.Read<Dictionary<string, object>>(originalSettings);
                legacy["LocalPacks"] = true; legacy["PacksFolder"] = fixture;
                settings = Json.Read<Settings>(Json.Write(legacy));
                OpenSettings(); window.UpdateLayout();
                foreach (string removed in new[] { "PackSettingsTab", "PackSettingsSection", "PackSourceCombo", "LocalPackSettings", "SteamPackSettings", "PacksFolderInput", "BrowsePacksFolderButton" })
                    if (window.FindName(removed) != null) throw new Exception("В настройках остался выбор источника паков: " + removed);
                if (C<StackPanel>("PlayerSettingsSection").Visibility != Visibility.Visible || B("PlayerSettingsTab") == null || B("DisplaySettingsTab") == null)
                    throw new Exception("Оставшиеся настройки игрока и экрана недоступны.");
                SettingsSection("Display");
                if (C<StackPanel>("DisplaySettingsSection").Visibility != Visibility.Visible || C<StackPanel>("PlayerSettingsSection").Visibility != Visibility.Collapsed)
                    throw new Exception("Переключение настроек экрана не работает.");
                SaveScreen("fixed-pack-settings-preview.png");
                SettingsSection("Player"); await SaveSettings();
                var saved = Json.Read<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                if (saved.ContainsKey("LocalPacks") || saved.ContainsKey("PacksFolder")) throw new Exception("Устаревший источник паков сохранился в настройках.");
                report.Add("PASS: only player/audio and display settings remain; a legacy local-folder configuration loads safely and saving removes obsolete pack-source settings");

                showMyPacks = false; RenderPackSource(); Page("Workshop"); window.UpdateLayout();
                if (C<TextBlock>("WorkshopTitle").Text != "Мастерская" || !C<TextBlock>("WorkshopSubtitle").Text.Contains("Steam Workshop") || C<StackPanel>("PackDownloadToolbar").Visibility != Visibility.Visible || B("NextPage").Visibility != Visibility.Visible)
                    throw new Exception("Старые настройки переключили мастерскую с каталога Steam.");
                if (Path.GetFullPath(Paths.Packs) != Path.GetFullPath(Path.Combine(Paths.Base, "Packs"))) throw new Exception("Паки сохраняются вне папки игры.");
                showMyPacks = true; refreshMyPacks = true; RenderPackSource(); C<ComboBox>("LanguageCombo").SelectedIndex = 0; TBox("SearchInput").Text = prefix;
                await Search(); window.UpdateLayout();
                var cards = C<WrapPanel>("PackCards").Children.OfType<Border>().ToArray();
                if (cards.Length != 1 || !FolderPacks.SameFolder(Convert.ToString(cards[0].ToolTip), local) || installed.Any(pack => FolderPacks.SameFolder(pack.root, fixture)))
                    throw new Exception("Сохранённая коллекция прочитана из внешней папки вместо Packs рядом с игрой.");
                SaveScreen("fixed-pack-library-preview.png");
                report.Add("PASS: Steam Workshop remains the catalog despite legacy settings; My Packs reads the collection in game/Packs and excludes a valid pack in the old external folder");

                guest.Received += message => { if (message.state != null) guestState = message.state; };
                await Task.Run(() => guest.Connect("127.0.0.1", server.Port, server.Code, "Проверка коллекции", ""));
                var selected = installed.Single(pack => FolderPacks.SameFolder(pack.root, local));
                prepared = Path.Combine(Paths.Packs, "room-" + selected.contentKey);
                ((StackPanel)cards[0].Child).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.pack.Selected && state.pack.title == selected.title && guestState != null && guestState.pack.title == selected.title, "Скачанный пак не выбран или не передан гостю.");
                if (videoDownload != null) await videoDownload;
                if (guestState.pack.DisplayLines != selected.DisplayLines || guestState.pack.DisplayRoles != selected.DisplayRoles || String.IsNullOrEmpty(guestState.pack.thumbnail))
                    throw new Exception("В лобби потеряны метаданные скачанного пака.");
                report.Add("PASS: selecting a downloaded game/Packs card broadcasts the full pack, preview and counts to another TCP client and prepares it without an external source setting");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally
            {
                guest.Dispose(); settings = Json.Read<Settings>(originalSettings); Persist();
                File.WriteAllLines(Path.Combine(Paths.Base, "folder-packs-ui-test.txt"), report); window.Close();
                foreach (string folder in new[] { fixture, local, prepared })
                    if (folder != null && Directory.Exists(folder) && (Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Paths.Cache).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Paths.Packs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) Directory.Delete(folder, true);
            }
        }
    }
}
