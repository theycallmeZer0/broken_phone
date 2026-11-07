using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        public async Task EmptyLobbySmoke()
        {
            var report = new List<string>();
            try
            {
                await Until(() => Me != null, "Комната без пака не создалась.");
                if (state.pack.Selected || C<TextBlock>("PackTitle").Text != "Пак не выбран" || B("StartButton").IsEnabled || B("ReadyButton").IsEnabled || C<System.Windows.FrameworkElement>("PosterArt").Visibility != System.Windows.Visibility.Visible || C<System.Windows.FrameworkElement>("EmptyPackSymbol") == null) throw new Exception("Нет векторного пустого состояния или доступен старт без пака.");
                if (PackLibrary.Installed().Any(pack => pack.isTestPack)) throw new Exception("Тестовый пак попал в обычную библиотеку.");
                Send(new Message { kind = "start" }); await Until(() => C<TextBlock>("ToastText").Text.Contains("Пак не выбран"), "Сервер не отклоняет старт без пака.");
                if (state.phase != "lobby") throw new Exception("Игра без выбранного пака началась.");
                SaveScreen("empty-lobby-preview.png"); report.Add("PASS: normal startup creates a network lobby with Pack Not Selected; sample artwork and test packs are absent; UI and server reject starting without a pack");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "empty-lobby-ui-test.txt"), report); window.Close(); }
        }
    }
}
