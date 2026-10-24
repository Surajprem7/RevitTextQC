using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitTextQC.UI
{
    /// <summary>Draws the ribbon icons at run time so the add-in ships without image files.</summary>
    internal static class IconFactory
    {
        // Every icon is drawn on a 32 x 32 grid and scaled to the size asked for.
        private const double Grid = 32;

        /// <param name="glyph">"QC" (text with a check), "S" (settings sliders) or anything else (update arrow).</param>
        public static ImageSource Create(string glyph, byte r, byte g, byte b, int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / Grid, size / Grid));
                var colour = new SolidColorBrush(Color.FromRgb(r, g, b));
                dc.DrawRoundedRectangle(colour, null, new Rect(1, 1, 30, 30), 7, 7);

                switch (glyph)
                {
                    case "QC":
                        DrawTextWithCheck(dc);
                        break;
                    case "S":
                        DrawSliders(dc, colour);
                        break;
                    default:
                        DrawUpdate(dc);
                        break;
                }
                dc.Pop();
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static Pen WhitePen(double thickness)
        {
            return new Pen(Brushes.White, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        }

        // Three lines of text, the last one carrying a tick: "text, checked".
        private static void DrawTextWithCheck(DrawingContext dc)
        {
            Pen line = WhitePen(2.6);
            dc.DrawLine(line, new Point(8, 9.5), new Point(24, 9.5));
            dc.DrawLine(line, new Point(8, 15.5), new Point(18, 15.5));
            dc.DrawLine(line, new Point(8, 21.5), new Point(13, 21.5));
            var tick = new StreamGeometry();
            using (StreamGeometryContext c = tick.Open())
            {
                c.BeginFigure(new Point(16.5, 21.5), false, false);
                c.LineTo(new Point(20, 25), true, true);
                c.LineTo(new Point(26, 17.5), true, true);
            }
            tick.Freeze();
            dc.DrawGeometry(null, WhitePen(3), tick);
        }

        // Two sliders with round handles.
        private static void DrawSliders(DrawingContext dc, Brush background)
        {
            Pen rail = WhitePen(2.6);
            dc.DrawLine(rail, new Point(7, 12), new Point(25, 12));
            dc.DrawLine(rail, new Point(7, 21), new Point(25, 21));
            dc.DrawEllipse(Brushes.White, null, new Point(20, 12), 3.8, 3.8);
            dc.DrawEllipse(Brushes.White, null, new Point(12, 21), 3.8, 3.8);
            dc.DrawEllipse(background, null, new Point(20, 12), 1.5, 1.5);
            dc.DrawEllipse(background, null, new Point(12, 21), 1.5, 1.5);
        }

        // An arrow pointing up out of a tray: "newer version".
        private static void DrawUpdate(DrawingContext dc)
        {
            Pen pen = WhitePen(2.8);
            dc.DrawLine(pen, new Point(16, 21), new Point(16, 8));
            var head = new StreamGeometry();
            using (StreamGeometryContext c = head.Open())
            {
                c.BeginFigure(new Point(10.5, 13), false, false);
                c.LineTo(new Point(16, 7.5), true, true);
                c.LineTo(new Point(21.5, 13), true, true);
            }
            head.Freeze();
            dc.DrawGeometry(null, pen, head);
            dc.DrawLine(pen, new Point(8.5, 25), new Point(23.5, 25));
        }
    }
}
