using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        public async Task JoinDialogSmoke()
        {
            var report = new List<string>();
            try
            {
                await Until(() => Me != null, "Лобби не создалось.");
                if (window.FindName("NetworkCombo") != null || window.FindName("RoomAddress") != null || window.FindName("ConnectionHelpButton") != null) throw new Exception("В лобби остался выбор подключения.");
                var originalClient = client; int windows = Application.Current.Windows.Count;
                B("JoinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                if (C<Border>("JoinOverlay").Visibility != Visibility.Visible || C<Grid>("GameShell").IsEnabled || Application.Current.Windows.Count != windows || B("JoinRoomSubmit").IsEnabled) throw new Exception("Окно входа не блокирует фон или открывается отдельным системным окном.");
                if (window.FindName("JoinRoomCancel") != null || !String.IsNullOrEmpty(Convert.ToString(TBox("JoinRoomInput").Tag)) || C<Border>("JoinRoomStatusPanel").Visibility != Visibility.Collapsed) throw new Exception("В форме остались дублирующая отмена или подсказки к коду.");
                foreach (string invalid in new[] { "ZZZZZZ", "26.1.2.3:47770/ABC123", "https://example.com/", "steam://joinlobby/480/1/1" })
                {
                    if (SetJoinCode(invalid) || TBox("JoinRoomInput").Text.Length > 0 || B("JoinRoomSubmit").IsEnabled || client != originalClient || C<Border>("JoinOverlay").Visibility != Visibility.Visible) throw new Exception("Поле принимает ссылку или адрес вместо кода.");
                }
                SetJoinCode("abc12"); if (B("JoinRoomSubmit").IsEnabled) throw new Exception("Неполный код разрешает вход.");
                SetJoinCode("abc123"); if (TBox("JoinRoomInput").Text != "ABC123" || !B("JoinRoomSubmit").IsEnabled) throw new Exception("Код не приводится к верхнему регистру.");
                await SubmitJoinDialog();
                if (!C<TextBlock>("JoinRoomStatus").Text.Contains("Запусти Steam") || client != originalClient) throw new Exception("Недоступный Steam не показан в форме входа.");
                TBox("JoinRoomInput").Clear(); window.UpdateLayout();
                if (C<Border>("JoinRoomStatusPanel").Visibility != Visibility.Collapsed) throw new Exception("Подсказка остаётся под пустым полем.");
                foreach (string name in new[] { "JoinRoomPaste", "JoinRoomSubmit" }) CheckButtonText(B(name));
                var dialog = C<Border>("JoinDialog"); var root = C<Grid>("Root");
                Rect bounds = dialog.TransformToAncestor(root).TransformBounds(new Rect(dialog.RenderSize));
                if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > root.ActualWidth || bounds.Bottom > root.ActualHeight) throw new Exception("Форма входа выходит за экран.");
                SaveScreen("join-room-preview.png");
                bool wasFullScreen = fullScreen;
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                if (C<Border>("JoinOverlay").Visibility != Visibility.Collapsed || !C<Grid>("GameShell").IsEnabled || fullScreen != wasFullScreen) throw new Exception("Esc не закрывает форму или меняет режим экрана.");
                OpenJoinDialog(); B("JoinRoomClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (client != originalClient || C<Border>("JoinOverlay").Visibility != Visibility.Collapsed) throw new Exception("Отмена меняет текущую комнату.");
                report.Add("PASS: Steam-only join form stays inside the game, fits the screen, blocks background input, and has unclipped buttons");
                report.Add("PASS: blank code field has no placeholder or helper; one close button; errors appear only when needed; six hexadecimal characters accepted; addresses/URLs rejected; Escape and close do not change display mode");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "join-ui-test.txt"), report); window.Close(); }
        }
    }
}
