using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BrokenPhone
{
    public static class Theme
    {
        static readonly Dictionary<string, string> Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "#F5F1E8", "#141720" }, { "#FFFCF5", "#202534" }, { "#292923", "#EFF1FA" },
            { "#858478", "#99A3BA" }, { "#77776B", "#AAB4CC" }, { "#99968A", "#818DA8" },
            { "#E4ECCA", "#99D8B0" }, { "#F47E55", "#AC95FF" }, { "#EEEADF", "#2C3345" },
            { "#EAE5D9", "#252C3D" }, { "#73915B", "#A69BFC" }, { "#638352", "#8FD7AA" },
            { "#A19B8E", "#7B87A2" }, { "#D8D2C6", "#39435B" }, { "#C2BCB0", "#65718A" },
            { "#B0A99A", "#65718A" }
        };
        public static SolidColorBrush Brush(string value)
        {
            string mapped; return (SolidColorBrush)new BrushConverter().ConvertFromString(Colors.TryGetValue(value, out mapped) ? mapped : value);
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void ApplyWindow(Window window)
        {
            window.SourceInitialized += delegate
            {
                try { int dark = 1; var handle = new WindowInteropHelper(window).Handle; if (DwmSetWindowAttribute(handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(handle, 19, ref dark, 4); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            };
        }
    }
}
