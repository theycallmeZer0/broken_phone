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
            { "#F5F1E8", "#17151D" }, { "#FFFCF5", "#26222E" }, { "#292923", "#F2EEF4" },
            { "#858478", "#ABA1B7" }, { "#77776B", "#BDB2CA" }, { "#99968A", "#93879F" },
            { "#E4ECCA", "#C5B4D2" }, { "#F47E55", "#CB9ABD" }, { "#EEEADF", "#35303F" },
            { "#EAE5D9", "#302A39" }, { "#73915B", "#BFB0D2" }, { "#638352", "#D7B0CA" },
            { "#A19B8E", "#94869F" }, { "#D8D2C6", "#4A4154" }, { "#C2BCB0", "#8C7C98" },
            { "#B0A99A", "#8C7C98" }
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
