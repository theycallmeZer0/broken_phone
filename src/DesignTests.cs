using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrokenPhone
{
    public static class DesignTests
    {
        public static int Run()
        {
            Window window = null; GameWindow controller = null;
            try
            {
                new Application();
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000;
                controller = new GameWindow(window);
                ((FrameworkElement)window.FindName("LobbyPanel")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("WorkshopPanel")).Visibility = Visibility.Visible;
                var makeCard = typeof(GameWindow).GetMethod("CardBase", BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < 6; i++) makeCard.Invoke(controller, new object[] { "Проверка плитки " + i, "24 реплики · 4 роли", "Пак для проверки ширины сетки", null });
                window.Show(); window.UpdateLayout();
                var panel = (WrapPanel)window.FindName("PackCards");
                var cards = panel.Children.Cast<FrameworkElement>().ToArray();
                var first = cards[0].TransformToAncestor(panel).Transform(new Point());
                var third = cards[2].TransformToAncestor(panel).Transform(new Point());
                if (Math.Abs(first.Y - third.Y) > 1 || panel.ActualWidth - third.X - cards[2].ActualWidth > 20)
                    throw new Exception("Workshop leaves unused width: viewport=" + panel.ActualWidth + ", third tile x=" + third.X + ", y=" + third.Y + ", width=" + cards[2].ActualWidth);
                var root = (Grid)window.FindName("Root"); var background = (SolidColorBrush)root.Background;
                if (background.Color.R > 60 || background.Color.G > 60 || background.Color.B > 65) throw new Exception("Default theme is not dark.");
                foreach (int width in new[] { 1000, 1440, 1920 })
                {
                    root.Width = width; window.UpdateLayout(); int columns = Math.Max(1, (int)Math.Floor((panel.ActualWidth + 18) / 358));
                    var last = cards[columns - 1]; var right = last.TransformToAncestor(panel).Transform(new Point(last.ActualWidth, 0));
                    if (panel.ActualWidth - right.X > 3 || right.X > panel.ActualWidth + 1) throw new Exception("Grid does not fill width at " + width);
                }
                root.Width = 1440; window.UpdateLayout();
                if (root.Children.OfType<System.Windows.Shapes.Ellipse>().Any()) throw new Exception("В интерфейсе остались декоративные полукруги.");
                if (window.FindName("AddLocalButton") != null) throw new Exception("В интерфейсе остался режим за одним ПК.");
                if (((Image)window.FindName("BrandLogo")).Source == null || window.Icon == null) throw new Exception("Новый логотип не подключён к окну.");
                root.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(Path.Combine(Paths.Base, "workshop-preview.png"))) encoder.Save(output);
                ((FrameworkElement)window.FindName("WorkshopPanel")).Visibility = Visibility.Collapsed; ((FrameworkElement)window.FindName("LobbyPanel")).Visibility = Visibility.Visible; window.UpdateLayout();
                var chat = (Border)window.FindName("LobbyChat"); var history = (ScrollViewer)window.FindName("ChatScroll");
                if (chat.ActualWidth < 800 || history.ActualHeight < 70) throw new Exception("Чат не занимает полноценную панель в правой части лобби.");
                File.WriteAllText(Path.Combine(Paths.Base, "design-test.txt"), "PASS: responsive Workshop fills widths 1000/1440/1920; default dark theme; no decorative circles; network-only lobby; full chat panel; phone logo"); return 0;
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(Paths.Base, "design-test.txt"), "FAIL: " + error.GetBaseException()); return 1; }
            finally { if (controller != null) controller.Dispose(); if (window != null) window.Close(); }
        }
    }
}
