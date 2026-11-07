using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrokenPhone
{
    // Every pack uses the same frame and fitting rule, independent of source dimensions.
    public sealed class PackCover : FrameworkElement
    {
        public const double AspectRatio = 16.0 / 9;
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register("Source", typeof(ImageSource), typeof(PackCover),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, SourceChanged));
        static readonly DependencyPropertyKey BackdropPropertyKey = DependencyProperty.RegisterReadOnly("Backdrop", typeof(ImageSource), typeof(PackCover), new PropertyMetadata(null));
        public static readonly DependencyProperty BackdropProperty = BackdropPropertyKey.DependencyProperty;
        public ImageSource Source { get { return (ImageSource)GetValue(SourceProperty); } set { SetValue(SourceProperty, value); } }
        public ImageSource Backdrop { get { return (ImageSource)GetValue(BackdropProperty); } }
        static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(37, 33, 45));
        static readonly Brush Shade = new SolidColorBrush(Color.FromArgb(110, 27, 23, 35));
        static PackCover() { Surface.Freeze(); Shade.Freeze(); }
        static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            var cover = (PackCover)sender; var oldBitmap = e.OldValue as BitmapSource; var bitmap = e.NewValue as BitmapSource;
            if (oldBitmap != null && !oldBitmap.IsFrozen) { oldBitmap.DownloadCompleted -= cover.DownloadCompleted; oldBitmap.DownloadFailed -= cover.DownloadFailed; }
            if (bitmap != null && !bitmap.IsFrozen && bitmap.IsDownloading) { bitmap.DownloadCompleted += cover.DownloadCompleted; bitmap.DownloadFailed += cover.DownloadFailed; }
            cover.RefreshBackdrop();
        }
        void DownloadCompleted(object sender, EventArgs e) { RefreshBackdrop(); InvalidateVisual(); }
        void DownloadFailed(object sender, ExceptionEventArgs e) { SetValue(BackdropPropertyKey, null); InvalidateVisual(); }
        static Size FrameSize(Size available)
        {
            double width = Double.IsInfinity(available.Width) ? 360 : available.Width;
            if (!Double.IsInfinity(available.Height)) width = Math.Min(width, available.Height * AspectRatio);
            width = Math.Max(0, width); return new Size(width, width / AspectRatio);
        }
        protected override Size MeasureOverride(Size availableSize) { return FrameSize(availableSize); }
        protected override Size ArrangeOverride(Size finalSize) { return FrameSize(finalSize); }
        static Rect ImageBounds(ImageSource source, Size frame, bool fill)
        {
            double scale = fill ? Math.Max(frame.Width / source.Width, frame.Height / source.Height) : Math.Min(frame.Width / source.Width, frame.Height / source.Height);
            double width = source.Width * scale, height = source.Height * scale;
            return new Rect((frame.Width - width) / 2, (frame.Height - height) / 2, width, height);
        }
        internal Rect ContentBounds { get { return Source == null || Source.Width <= 0 || Source.Height <= 0 ? Rect.Empty : new Rect(RenderSize); } }
        protected override void OnRender(DrawingContext drawing)
        {
            if (ActualWidth < 1 || ActualHeight < 1) return;
            drawing.PushClip(new RectangleGeometry(new Rect(RenderSize), 12, 12));
            drawing.DrawRectangle(Surface, null, new Rect(RenderSize));
            if (Backdrop != null) { drawing.DrawImage(Backdrop, new Rect(RenderSize)); drawing.DrawRectangle(Shade, null, new Rect(RenderSize)); }
            if (Source != null && !ContentBounds.IsEmpty) drawing.DrawImage(Source, ContentBounds);
            drawing.Pop();
        }
        void RefreshBackdrop()
        {
            // Bake a tiny soft bitmap once per cover; no live blur shader in the game scene.
            var source = Source;
            if (source == null || source.Width <= 0 || source.Height <= 0 || source is BitmapSource && ((BitmapSource)source).IsDownloading) { SetValue(BackdropPropertyKey, null); return; }
            const int width = 32, height = 18, stride = width * 4;
            var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var drawing = visual.RenderOpen()) { drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height))); drawing.DrawImage(source, ImageBounds(source, new Size(width, height), true)); drawing.Pop(); }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var pixels = new byte[stride * height]; bitmap.CopyPixels(pixels, stride, 0);
            for (int pass = 0; pass < 3; pass++)
            {
                var blurred = new byte[pixels.Length];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int channel = 0; channel < 4; channel++)
                {
                    int sum = 0;
                    for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) sum += pixels[Math.Max(0, Math.Min(height - 1, y + dy)) * stride + Math.Max(0, Math.Min(width - 1, x + dx)) * 4 + channel];
                    blurred[y * stride + x * 4 + channel] = (byte)(sum / 25);
                }
                pixels = blurred;
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride); result.Freeze(); SetValue(BackdropPropertyKey, result);
        }
    }
}
