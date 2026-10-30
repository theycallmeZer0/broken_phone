using System;
using System.Windows;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed class SignalView : FrameworkElement
    {
        float[] envelope = new float[0];
        double coverage = 1, cursor;
        bool recording;
        public void Update(float[] wave, double filled, double position, bool live)
        {
            envelope = wave ?? new float[0]; coverage = Math.Max(0, Math.Min(1, filled)); cursor = Math.Max(0, Math.Min(1, position)); recording = live; InvalidateVisual();
        }
        static SolidColorBrush Color(byte r, byte g, byte b, byte a = 255) { var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b)); brush.Freeze(); return brush; }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); double w = ActualWidth, h = ActualHeight; if (w < 20 || h < 10) return;
            double radius = Math.Min(12, h / 4), margin = Math.Min(8, h / 5), plot = w - 2 * margin, center = h / 2, amplitude = h * .39;
            dc.DrawRoundedRectangle(Color(36, 33, 57), new Pen(Color(72, 64, 97), 1), new Rect(0, 0, w, h), radius, radius);
            dc.PushClip(new RectangleGeometry(new Rect(1, 1, w - 2, h - 2), radius - 1, radius - 1));
            var grid = new Pen(Color(62, 57, 83), 1);
            for (int i = 1; i < 8; i++) dc.DrawLine(grid, new Point(margin + plot * i / 8, margin), new Point(margin + plot * i / 8, h - margin));
            dc.DrawLine(grid, new Point(margin, center), new Point(w - margin, center));
            if (envelope.Length > 0)
            {
                int points = Math.Min(420, Math.Max(1, (int)plot)); var shape = new StreamGeometry();
                using (var path = shape.Open())
                {
                    path.BeginFigure(new Point(margin, center), true, true);
                    for (int i = 0; i < points; i++) path.LineTo(new Point(margin + plot * coverage * i / Math.Max(1, points - 1), center - amplitude * Math.Sqrt(Math.Max(0, Math.Min(1, envelope[Math.Min(envelope.Length - 1, i * envelope.Length / points)])))), true, false);
                    for (int i = points - 1; i >= 0; i--) path.LineTo(new Point(margin + plot * coverage * i / Math.Max(1, points - 1), center + amplitude * Math.Sqrt(Math.Max(0, Math.Min(1, envelope[Math.Min(envelope.Length - 1, i * envelope.Length / points)])))), true, false);
                }
                shape.Freeze(); dc.DrawGeometry(Color(171, 146, 234, 115), new Pen(Color(184, 162, 246), 1.3), shape);
            }
            if (recording || cursor > 0)
            {
                double playhead = margin + plot * cursor;
                dc.DrawLine(new Pen(recording ? Color(255, 125, 131) : Color(218, 208, 255), 1.5), new Point(playhead, margin), new Point(playhead, h - margin));
            }
            dc.Pop();
        }
    }
}
