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
        public async Task PackViewsSmoke()
        {
            var report = new List<string>();
            try
            {
                await Until(() => Me != null, "Нет лобби."); showMyPacks = false;
                Page("Workshop"); RenderPackSource(); C<ComboBox>("LanguageCombo").SelectedIndex = 0; TBox("SearchInput").Text = "";
                B("MyPacksFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => showMyPacks && !refreshMyPacks && C<TextBlock>("WorkshopTitle").Text == "Мои паки", "Мои паки не открылись внутри игры.");
                if (currentPage != "Workshop" || B("MyPacksFolderButton").ToolTip.ToString() != "Открыть мастерскую" || B("NextPage").Visibility != Visibility.Collapsed) throw new Exception("Переключатель не обновил отображение загруженных паков.");
                foreach (Border card in C<WrapPanel>("PackCards").Children) if (!Convert.ToString(card.ToolTip).StartsWith(Paths.Packs, StringComparison.OrdinalIgnoreCase)) throw new Exception("Мои паки содержат внешний источник вместо загруженной коллекции.");
                SaveScreen("my-packs-preview.png");
                report.Add("PASS: My Packs button displays the downloaded collection inside the game, without opening a file manager or client-side pagination");
                TBox("SearchInput").Text = "3808612351"; B("MyPacksFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !showMyPacks && packControls.ContainsKey("3808612351"), "Не загрузился настоящий каталог Workshop по ссылке/ID.", 40);
                if (C<TextBlock>("WorkshopTitle").Text != "Мастерская" || B("MyPacksFolderButton").ToolTip.ToString() != "Открыть мои паки") throw new Exception("Не работает обратное переключение в Workshop.");
                var owned = InstalledItem("3808612351");
                if (owned != null && !packControls["3808612351"].Status.Text.Contains("коллекции")) throw new Exception("Загруженный пак скрыт или не отмечен в Workshop.");
                SaveScreen("workshop-browser-preview.png");
                report.Add("PASS: switching back loads actual Steam Workshop metadata; installed items remain in the catalog and receive a downloaded marker when present locally");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "pack-views-ui-test.txt"), report); window.Close(); }
        }
    }
}
