using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        [StructLayout(LayoutKind.Sequential)] struct ClientRectangle { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr handle, out ClientRectangle rectangle);
        void CheckOverlayClientCoverage(string mode)
        {
            ClientRectangle clientRectangle;
            if (!GetClientRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out clientRectangle)) throw new Exception("Не удалось измерить игровое окно.");
            var deviceToDip = PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice;
            Point size = deviceToDip.Transform(new Point(clientRectangle.Right, clientRectangle.Bottom));
            var repaint = C<Border>("SteamOverlayRefresh");
            Rect actual = repaint.TransformToAncestor(window).TransformBounds(new Rect(repaint.RenderSize));
            if (Math.Abs(actual.Left) > 1 || Math.Abs(actual.Top) > 1 || Math.Abs(actual.Width - size.X) > 1 || Math.Abs(actual.Height - size.Y) > 1)
                throw new Exception("Слой Steam обрезан в режиме " + mode + ": окно=" + size.X + "×" + size.Y + ", слой=" + actual);
        }
        static void CheckButtonText(Button button)
        {
            if (!button.IsVisible || !(button.Content is string)) return;
            var text = new FormattedText((string)button.Content, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.White, VisualTreeHelper.GetDpi(button).PixelsPerDip);
            if (button.ActualHeight + .5 < text.Height + button.Padding.Top + button.Padding.Bottom + button.BorderThickness.Top + button.BorderThickness.Bottom ||
                button.ActualWidth + .5 < text.WidthIncludingTrailingWhitespace + button.Padding.Left + button.Padding.Right + button.BorderThickness.Left + button.BorderThickness.Right)
                throw new Exception("Текст не помещается в кнопку «" + button.Content + "»: " + button.ActualWidth + " × " + button.ActualHeight);
        }
        public async Task DisplaySmoke()
        {
            var report = new List<string>(); Settings saved = Json.Read<Settings>(Json.Write(settings)); bool interactive = Program.Interactive;
            try
            {
                await Until(() => Me != null, "Нет лобби.");
                var originalState = state;
                try
                {
                    state = Json.Read<RoomState>(Json.Write(state));
                    while (state.players.Count < 8) state.players.Add(new Player { id = "layout-" + state.players.Count, name = "Игрок " + (state.players.Count + 1), color = state.players.Count });
                    Page("Lobby"); RenderLobby(); window.UpdateLayout(); FitPlayers(); window.UpdateLayout();
                    var players = C<StackPanel>("PlayersList"); var last = (FrameworkElement)players.Children[7];
                    double bottom = last.TransformToAncestor(players).Transform(new Point(0, last.ActualHeight)).Y;
                    if (bottom > players.ActualHeight + 1 || players.Parent is ScrollViewer) throw new Exception("Восемь игроков не помещаются без прокрутки.");
                    SaveScreen("eight-players-preview.png"); report.Add("PASS: all eight players fit without a scroll container; row and avatar sizes adapt to the available height");
                }
                finally { state = originalState; RenderLobby(); }
                var lobbyRows = C<StackPanel>("PlayersList").Children.Cast<Grid>().ToArray();
                double avatarSize = lobbyRows[0].Children.OfType<Border>().First().Width;
                if (lobbyRows.Any(row => Math.Abs(row.Children.OfType<Border>().First().Width - avatarSize) > .1)) throw new Exception("Кружки свободных мест отличаются от аватарок.");
                if (MarshalDisplaySize() != 220 || new Settings().DisplayMode != "fullscreen") throw new Exception("Неверный формат настроек экрана или режим по умолчанию.");
                OpenSettings();
                if (C<ComboBox>("ResolutionCombo").Items.Cast<Resolution>().Any(value => value.Width == 0 || value.ToString().Contains("По разрешению"))) throw new Exception("В разрешениях осталась неопределённая подпись.");
                if (C<ComboBox>("ResolutionCombo").Items.Count < 2) throw new Exception("Не получены разрешения монитора.");
                C<ComboBox>("DisplayModeCombo").SelectedIndex = 1;
                if (C<ComboBox>("ResolutionCombo").IsEnabled) throw new Exception("Окно без рамки не использует разрешение рабочего стола.");
                C<ComboBox>("DisplayModeCombo").SelectedIndex = 2;
                if (!C<ComboBox>("ResolutionCombo").IsEnabled) throw new Exception("Оконное разрешение недоступно.");
                C<ComboBox>("ResolutionCombo").SelectedItem = C<ComboBox>("ResolutionCombo").Items.Cast<Resolution>().First(value => value.Width > 0);
                window.Opacity = 0; SaveDisplaySettings();
                var loaded = Json.Read<Settings>(File.ReadAllText(SettingsFile));
                if (loaded.DisplayMode != "windowed" || loaded.DisplayWidth != settings.DisplayWidth || loaded.DisplayHeight != settings.DisplayHeight) throw new Exception("Режим или разрешение не сохраняются.");
                settings.DisplayWidth = settings.DisplayHeight = 0; Program.Interactive = true;
                foreach (string mode in new[] { "fullscreen", "borderless", "windowed" })
                {
                    ApplyDisplayMode(mode); window.UpdateLayout(); await Task.Delay(100);
                    CheckOverlayClientCoverage(mode);
                    var root = C<Grid>("Root"); var surface = C<Grid>("WindowSurface");
                    Rect gameBounds = root.TransformToAncestor(surface).TransformBounds(new Rect(root.RenderSize));
                    if (Math.Abs(gameBounds.Left) > 1 || Math.Abs(gameBounds.Top) > 1 || Math.Abs(gameBounds.Width - surface.ActualWidth) > 1 || Math.Abs(gameBounds.Height - surface.ActualHeight) > 1)
                        throw new Exception("Интерфейс не заполняет окно в режиме " + mode + ": " + gameBounds);
                    if (mode == "windowed" && (window.WindowStyle != WindowStyle.SingleBorderWindow || fullScreen)) throw new Exception("Оконный режим не восстановил рамку.");
                    if (mode != "windowed")
                    {
                        if (window.WindowStyle != WindowStyle.None || !fullScreen) throw new Exception("Полный экран не убрал рамку.");
                        var source = PresentationSource.FromVisual(window); var pixels = source.CompositionTarget.TransformToDevice.Transform(new Point(window.Width, window.Height));
                        var bounds = CurrentScreen.Bounds;
                        if (Math.Abs(pixels.X - bounds.Width) > 2 || Math.Abs(pixels.Y - bounds.Height) > 2) throw new Exception("Игра не занимает весь экран.");
                    }
                    report.Add("PASS: display mode " + mode + " applies correctly; game interface and Steam repaint cover the entire native client area with DPI-aware bounds");
                }
                FullScreen(); if (!fullScreen) throw new Exception("F11 не открыл полный экран."); FullScreen(); if (fullScreen) throw new Exception("F11 не вернул оконный режим.");
                window.Left = -10000; window.Opacity = 1; Program.Interactive = interactive; OpenSettings(); SettingsSection("Display"); window.UpdateLayout(); SaveScreen("display-settings-preview.png");
                report.Add("PASS: native fullscreen default; available monitor resolutions; persisted window size and display mode; borderless native resolution; F11 toggles; original desktop mode remains unchanged by tests");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { Program.Interactive = interactive; settings = saved; Persist(); RestoreDesktopResolution(); File.WriteAllLines(System.IO.Path.Combine(Paths.Base, "display-ui-test.txt"), report); window.Close(); }
        }
        static int MarshalDisplaySize() { return System.Runtime.InteropServices.Marshal.SizeOf(typeof(DisplayMode)); }
    }
}
