using System;
using System.Windows;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed class SignalView : FrameworkElement
    {
        float[] envelope = new float[0];
        float[] guide = new float[0];
        public bool HasGuide { get { return guide.Length > 0; } }
        public void SetGuide(float[] wave) { guide = wave ?? new float[0]; InvalidateVisual(); }
        double coverage = 1, cursor;
        bool recording;
        double timelinePoints;
        public SignalView() { SnapsToDevicePixels = true; UseLayoutRounding = true; }
        public void UpdateLive(float[] wave, double seconds, double positionSeconds)
        {
            envelope = wave ?? new float[0]; timelinePoints = Math.Max(1, seconds * 44100 / SignalAnalysis.SamplesPerPoint);
            coverage = 1; cursor = Math.Max(0, Math.Min(1, positionSeconds / Math.Max(.01, seconds))); recording = true; InvalidateVisual();
        }
        public void Update(float[] wave, double filled, double position, bool live)
        {
            envelope = wave ?? new float[0]; coverage = Math.Max(0, Math.Min(1, filled)); cursor = Math.Max(0, Math.Min(1, position)); recording = live; timelinePoints = 0; InvalidateVisual();
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
            if (guide.Length > 0)
            {
                int points = Math.Min(420, Math.Max(2, (int)plot)); var shape = new StreamGeometry();
                using (var path = shape.Open())
                {
                    path.BeginFigure(new Point(margin, center), true, true);
                    for (int i = 0; i < points; i++) path.LineTo(new Point(margin + plot * i / (points - 1), center - amplitude * Math.Sqrt(Math.Max(0, Math.Min(1, guide[Math.Min(guide.Length - 1, i * guide.Length / points)])))), true, false);
                    for (int i = points - 1; i >= 0; i--) path.LineTo(new Point(margin + plot * i / (points - 1), center + amplitude * Math.Sqrt(Math.Max(0, Math.Min(1, guide[Math.Min(guide.Length - 1, i * guide.Length / points)])))), true, false);
                }
                shape.Freeze(); dc.DrawGeometry(Color(157, 147, 187, 65), new Pen(Color(166, 155, 196, 175), 1), shape);
            }
            if (envelope.Length > 0)
            {
                int points = Math.Min(420, Math.Max(1, (int)plot)); var shape = new StreamGeometry();
                bool stable = recording && timelinePoints > 0;
                double scale = stable ? timelinePoints / points : 1;
                int visible = stable ? Math.Min(points, (int)(envelope.Length / scale)) : points;
                var peaks = new float[visible];
                for (int i = 0; i < visible; i++)
                {
                    if (!stable) peaks[i] = envelope[Math.Min(envelope.Length - 1, i * envelope.Length / points)];
                    else for (int sample = (int)(i * scale); sample < Math.Min(envelope.Length, (int)((i + 1) * scale)); sample++) peaks[i] = Math.Max(peaks[i], envelope[sample]);
                }
                using (var path = shape.Open())
                {
                    path.BeginFigure(new Point(margin, center), true, true);
                    for (int i = 0; i < visible; i++) path.LineTo(new Point(margin + plot * coverage * i / Math.Max(1, points - 1), center - amplitude * Math.Sqrt(Math.Max(0, Math.Min(1, peaks[i])))), true, false);
                    for (int i = visible - 1; i >= 0; i--) path.LineTo(new Point(margin + plot * coverage * i / Math.Max(1, points - 1), center + amplitude * Math.Sqrt(Math.Max(0, Math.Min(1, peaks[i])))), true, false);
                }
                shape.Freeze(); dc.DrawGeometry(guide.Length == 0 ? Color(171, 146, 234, 115) : Color(123, 218, 174, 130), new Pen(guide.Length == 0 ? Color(184, 162, 246) : Color(153, 232, 190), 1.3), shape);
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
