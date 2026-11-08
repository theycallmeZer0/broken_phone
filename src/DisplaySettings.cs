using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DisplayMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string device;
            public short spec, driver, size, extra;
            public uint fields;
            public int x, y;
            public uint orientation, fixedOutput;
            public short color, duplex, yResolution, tt, collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string form;
            public short logPixels;
            public uint bits, width, height, flags, frequency, icmMethod, icmIntent, media, dither, reserved1, reserved2, panningWidth, panningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int number, ref DisplayMode mode);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string device, ref DisplayMode mode, IntPtr window, uint flags, IntPtr parameter);
        sealed class Resolution
        {
            public int Width, Height;
            public override string ToString() { return Width + " × " + Height; }
        }
        string changedDisplay;
        DisplayMode originalDisplay;
        bool applyingDisplay, displayResolutionSuspended;

        static DisplayMode EmptyDisplayMode() { return new DisplayMode { size = (short)Marshal.SizeOf(typeof(DisplayMode)) }; }
        Forms.Screen CurrentScreen { get { return Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle); } }
        void InitializeDisplaySettings()
        {
            B("PlayerSettingsTab").Click += delegate { SettingsSection("Player"); };
            B("DisplaySettingsTab").Click += delegate { SettingsSection("Display"); };
            C<StackPanel>("PlayersList").SizeChanged += delegate { FitPlayers(); };
            C<ComboBox>("DisplayModeCombo").SelectionChanged += delegate { UpdateDisplayHint(); };
            C<Grid>("WindowSurface").SizeChanged += delegate(object sender, SizeChangedEventArgs e)
            {
                if (!Program.Interactive || e.NewSize.Width < 1 || e.NewSize.Height < 1) return;
                // Keep one uniform scale while using all of widescreen and ultrawide displays.
                var root = C<Grid>("Root"); double ratio = e.NewSize.Width / e.NewSize.Height;
                root.Width = Math.Max(1440, 900 * ratio); root.Height = Math.Max(900, 1440 / ratio);
            };
            window.SourceInitialized += delegate { if (Program.Interactive) ApplyDisplayMode(settings.DisplayMode); };
            window.Closed += delegate { RestoreDesktopResolution(); };
            window.StateChanged += delegate { if (window.WindowState == WindowState.Minimized) SuspendDesktopResolution(); };
            window.Deactivated += delegate { if (!SteamOverlayHandoff) SuspendDesktopResolution(); };
            window.Activated += delegate { if (displayResolutionSuspended && !SteamOverlayHandoff && !applyingDisplay && fullScreen && Program.Interactive) ApplyDisplayMode(settings.DisplayMode); };
        }
        void SettingsSection(string section)
        {
            if (section != "Player") StopMicrophonePanel();
            foreach (string name in new[] { "Player", "Display" })
            {
                C<StackPanel>(name + "SettingsSection").Visibility = name == section ? Visibility.Visible : Visibility.Collapsed;
                B(name + "SettingsTab").Background = Brush(name == section ? "#39324F" : "#26222E");
            }
        }
        void FitPlayers()
        {
            var panel = C<StackPanel>("PlayersList"); if (panel.ActualHeight < 1 || panel.Children.Count == 0) return;
            double available = panel.ActualHeight - panel.Children.Count * 4;
            double rowHeight = Math.Min(54, Math.Max(28, available / panel.Children.Count));
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var row = (Grid)panel.Children[i]; row.Height = rowHeight;
                var avatar = row.Children.OfType<Border>().FirstOrDefault();
                if (avatar != null)
                {
                    double size = Math.Min(44, row.Height - 4); avatar.Width = avatar.Height = size; avatar.CornerRadius = new CornerRadius(size / 2);
                    avatar.Clip = new System.Windows.Media.EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
                }
            }
        }
        void OpenDisplaySettings()
        {
            string device = CurrentScreen.DeviceName;
            var desktop = changedDisplay == device ? originalDisplay : EmptyDisplayMode();
            if (changedDisplay != device) EnumDisplaySettings(device, -1, ref desktop);
            if (desktop.width == 0 || desktop.height == 0) { desktop.width = (uint)CurrentScreen.Bounds.Width; desktop.height = (uint)CurrentScreen.Bounds.Height; }
            var native = new Resolution { Width = (int)desktop.width, Height = (int)desktop.height };
            var choices = new List<Resolution> { native };
            for (int i = 0; ; i++)
            {
                var mode = EmptyDisplayMode(); if (!EnumDisplaySettings(device, i, ref mode)) break;
                if (mode.width >= 1024 && mode.height >= 720 && !choices.Any(value => value.Width == mode.width && value.Height == mode.height)) choices.Add(new Resolution { Width = (int)mode.width, Height = (int)mode.height });
            }
            if (settings.DisplayWidth > 0 && !choices.Any(value => value.Width == settings.DisplayWidth && value.Height == settings.DisplayHeight)) choices.Add(new Resolution { Width = settings.DisplayWidth, Height = settings.DisplayHeight });
            C<ComboBox>("ResolutionCombo").ItemsSource = choices.OrderBy(value => value.Width).ThenBy(value => value.Height).ToArray();
            C<ComboBox>("ResolutionCombo").SelectedItem = choices.FirstOrDefault(value => value.Width == settings.DisplayWidth && value.Height == settings.DisplayHeight) ?? native;
            C<ComboBox>("DisplayModeCombo").SelectedIndex = settings.DisplayMode == "borderless" ? 1 : settings.DisplayMode == "windowed" ? 2 : 0;
            UpdateDisplayHint();
        }
        void UpdateDisplayHint()
        {
            bool borderless = C<ComboBox>("DisplayModeCombo").SelectedIndex == 1;
            C<ComboBox>("ResolutionCombo").IsEnabled = !borderless;
            Text("DisplayHint", borderless ? "Окно без рамки занимает весь экран в разрешении рабочего стола." : "Полный экран — F11. Esc — вернуться в окно. Разрешение применяется при сохранении.");
        }
        void SaveDisplaySettings()
        {
            var resolution = C<ComboBox>("ResolutionCombo").SelectedItem as Resolution;
            if (resolution != null) { settings.DisplayWidth = resolution.Width; settings.DisplayHeight = resolution.Height; }
            string mode = C<ComboBox>("DisplayModeCombo").SelectedIndex == 1 ? "borderless" : C<ComboBox>("DisplayModeCombo").SelectedIndex == 2 ? "windowed" : "fullscreen";
            if (Program.Interactive) ApplyDisplayMode(mode); else settings.DisplayMode = mode;
            Persist();
        }
        void RestoreDesktopResolution()
        {
            if (changedDisplay == null) return;
            if (ChangeDisplaySettingsEx(changedDisplay, ref originalDisplay, IntPtr.Zero, 0, IntPtr.Zero) == 0) changedDisplay = null;
        }
        void SuspendDesktopResolution()
        {
            if (changedDisplay == null) return;
            displayResolutionSuspended = true; RestoreDesktopResolution();
        }
        void ApplyDisplayMode(string requested)
        {
            if (applyingDisplay) return; applyingDisplay = true;
            try
            {
                displayResolutionSuspended = false; RestoreDesktopResolution(); var screen = CurrentScreen;
                string mode = requested == "windowed" || requested == "borderless" ? requested : "fullscreen";
                if (mode == "fullscreen" && settings.DisplayWidth > 0)
                {
                    var current = EmptyDisplayMode(); EnumDisplaySettings(screen.DeviceName, -1, ref current);
                    var selected = current; selected.width = (uint)settings.DisplayWidth; selected.height = (uint)settings.DisplayHeight; selected.fields = 0x80000 | 0x100000;
                    if (current.width == selected.width && current.height == selected.height) { }
                    else if (ChangeDisplaySettingsEx(screen.DeviceName, ref selected, IntPtr.Zero, 2, IntPtr.Zero) == 0 && ChangeDisplaySettingsEx(screen.DeviceName, ref selected, IntPtr.Zero, 4, IntPtr.Zero) == 0)
                    { originalDisplay = current; changedDisplay = screen.DeviceName; screen = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle); }
                    else { settings.DisplayWidth = settings.DisplayHeight = 0; Toast("Это разрешение недоступно. Используем разрешение экрана."); }
                }
                window.WindowState = WindowState.Normal;
                window.WindowStyle = mode == "windowed" ? WindowStyle.SingleBorderWindow : WindowStyle.None;
                window.ResizeMode = mode == "windowed" ? ResizeMode.CanResize : ResizeMode.NoResize;
                var source = PresentationSource.FromVisual(window); var fromDevice = source == null ? System.Windows.Media.Matrix.Identity : source.CompositionTarget.TransformFromDevice;
                var bounds = screen.Bounds; var area = screen.WorkingArea;
                var topLeft = fromDevice.Transform(new Point(mode == "windowed" ? area.Left : bounds.Left, mode == "windowed" ? area.Top : bounds.Top));
                var size = fromDevice.Transform(new Point(mode == "windowed" ? area.Width : bounds.Width, mode == "windowed" ? area.Height : bounds.Height));
                window.MinWidth = Math.Min(1000, size.X); window.MinHeight = Math.Min(700, size.Y);
                double width = size.X, height = size.Y;
                if (mode == "windowed")
                {
                    width = Math.Min(size.X, Math.Max(window.MinWidth, (settings.DisplayWidth > 0 ? settings.DisplayWidth : 1440) * fromDevice.M11));
                    height = Math.Min(size.Y, Math.Max(window.MinHeight, (settings.DisplayHeight > 0 ? settings.DisplayHeight : 900) * fromDevice.M22));
                }
                window.Left = topLeft.X + (size.X - width) / 2; window.Top = topLeft.Y + (size.Y - height) / 2;
                window.Width = width; window.Height = height; fullScreen = mode != "windowed"; settings.DisplayMode = mode;
                C<ComboBox>("DisplayModeCombo").SelectedIndex = mode == "borderless" ? 1 : mode == "windowed" ? 2 : 0;
            }
            finally { applyingDisplay = false; }
        }
    }
}
