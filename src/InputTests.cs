using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        public async Task InputSmoke()
        {
            var report = new List<string>();
            try
            {
                foreach (string name in new[] { "ChatInput", "SearchInput", "NameInput" })
                {
                    if (name == "SearchInput") Page("Workshop"); else if (name == "NameInput") OpenSettings(); else Page("Lobby");
                    window.UpdateLayout(); var box = TBox(name); box.Text = "Проверка: АБВГД, ёжик 123"; box.BringIntoView(); box.Focus(); box.CaretIndex = box.Text.Length; window.UpdateLayout(); await Task.Delay(100);
                    var host = box.Template.FindName("PART_ContentHost", box) as ScrollViewer; Rect letter = box.GetRectFromCharacterIndex(0);
                    double height = box.FontSize * box.FontFamily.LineSpacing;
                    report.Add(name + ": input=" + box.ActualHeight + ", viewport=" + (host == null ? -1 : host.ViewportHeight) + ", line=" + height + ", rect=" + letter);
                    SaveScreen("input-" + name + ".png");
                    if (host == null || host.ViewportHeight < height - 1 || letter.IsEmpty || letter.Top < 0 || letter.Bottom > box.ActualHeight + .5) throw new Exception("Текст обрезан в " + name);
                }
                report.Insert(0, "PASS: Cyrillic text and caret remain visible in chat, search and settings inputs");
            }
            catch (Exception e) { report.Insert(0, "FAIL: " + e); }
            finally { File.WriteAllLines(System.IO.Path.Combine(Paths.Base, "input-ui-test.txt"), report); window.Close(); }
        }
    }
}
