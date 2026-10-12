using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitTextQC.UI
{
    /// <summary>Draws the ribbon icons at run time so the add-in ships without image files.</summary>
    internal static class IconFactory
    {
        public static ImageSource Create(string glyph, byte r, byte g, byte b, int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var fill = new SolidColorBrush(Color.FromRgb(r, g, b));
                dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);

                var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                double fontSize = glyph.Length > 1 ? size * 0.44 : size * 0.6;
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
