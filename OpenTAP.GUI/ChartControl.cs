using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    public enum ChartType
    {
        [Display("XY Scatter")] Scatter,
        [Display("XY Line")] Line,
        [Display("Column")] Column
    }

    class ChartSeries
    {
        public string Name;
        public List<PointD> Points = new List<PointD>();
    }

    struct PointD
    {
        public double X, Y;
        public PointD(double x, double y) { X = x; Y = y; }
    }

    /// <summary>
    /// A small GDI+ chart: XY scatter, XY line or columns, optional log axes, legend, mouse-wheel zoom, drag to pan,
    /// double-click to reset and a tooltip for the nearest point. Replaces Terminal.Gui's GraphView used by the TUI.
    /// </summary>
    class ChartControl : Control
    {
        static readonly Color[] Palette =
        {
            Color.FromArgb(31, 119, 180), Color.FromArgb(255, 127, 14), Color.FromArgb(44, 160, 44), Color.FromArgb(214, 39, 40),
            Color.FromArgb(148, 103, 189), Color.FromArgb(140, 86, 75), Color.FromArgb(227, 119, 194), Color.FromArgb(127, 127, 127)
        };

        List<ChartSeries> series = new List<ChartSeries>();
        ChartType type;
        bool logX, logY;
        string xTitle = "", yTitle = "";

        // Visible area in (possibly log10-transformed) data coordinates. Null = fit to data.
        double viewMinX, viewMaxX, viewMinY, viewMaxY;
        bool hasView;

        Point? dragStart;
        (double MinX, double MaxX, double MinY, double MaxY) dragView;
        readonly ToolTip tooltip = new ToolTip();
        string lastTip;

        public ChartControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void SetData(List<ChartSeries> data, ChartType chartType, bool logXAxis, bool logYAxis, string xAxisTitle, string yAxisTitle)
        {
            series = data ?? new List<ChartSeries>();
            type = chartType;
            logX = logXAxis;
            logY = logYAxis;
            xTitle = xAxisTitle ?? "";
            yTitle = yAxisTitle ?? "";
            ResetZoom();
        }

        public void ResetZoom()
        {
            hasView = ComputeFit(out viewMinX, out viewMaxX, out viewMinY, out viewMaxY);
            Invalidate();
        }

        /// <summary> Zooms around the centre; factor &lt; 1 zooms in. </summary>
        public void Zoom(double factor) => ZoomAt(new PointF(PlotArea.Left + PlotArea.Width / 2f, PlotArea.Top + PlotArea.Height / 2f), factor);

        double TX(double x) => logX ? Math.Log10(x) : x;
        double TY(double y) => logY ? Math.Log10(y) : y;
        bool Valid(PointD p) => !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y) && (!logX || p.X > 0) && (!logY || p.Y > 0);

        bool ComputeFit(out double minX, out double maxX, out double minY, out double maxY)
        {
            var pts = series.SelectMany(s => s.Points).Where(Valid).ToList();
            minX = minY = 0; maxX = maxY = 1;
            if (pts.Count == 0) return false;
            minX = pts.Min(p => TX(p.X)); maxX = pts.Max(p => TX(p.X));
            minY = pts.Min(p => TY(p.Y)); maxY = pts.Max(p => TY(p.Y));
            if (type == ChartType.Column && !logY) { minY = Math.Min(minY, 0); maxY = Math.Max(maxY, 0); }
            Pad(ref minX, ref maxX);
            Pad(ref minY, ref maxY);
            return true;
        }

        static void Pad(ref double min, ref double max)
        {
            if (max - min < 1e-12)
            {
                var d = Math.Abs(min) > 1e-12 ? Math.Abs(min) * 0.1 : 1;
                min -= d; max += d;
            }
            else
            {
                var d = (max - min) * 0.05;
                min -= d; max += d;
            }
        }

        Rectangle PlotArea
        {
            get
            {
                int legendWidth = series.Count > 0 ? Math.Min(220, 40 + series.Max(s => TextRenderer.MeasureText(s.Name ?? "", Font).Width)) : 0;
                return new Rectangle(75, 15, Math.Max(10, ClientSize.Width - 75 - 15 - legendWidth), Math.Max(10, ClientSize.Height - 15 - 50));
            }
        }

        float PxX(double tx, Rectangle area) => (float)(area.Left + (tx - viewMinX) / (viewMaxX - viewMinX) * area.Width);
        float PxY(double ty, Rectangle area) => (float)(area.Bottom - (ty - viewMinY) / (viewMaxY - viewMinY) * area.Height);
        double DataX(float px, Rectangle area) => viewMinX + (px - area.Left) / (double)area.Width * (viewMaxX - viewMinX);
        double DataY(float py, Rectangle area) => viewMinY + (area.Bottom - py) / (double)area.Height * (viewMaxY - viewMinY);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = PlotArea;

            if (!hasView)
            {
                TextRenderer.DrawText(g, series.Count == 0 ? "Nothing to plot. Choose results and axes in Plot Settings." : "No plottable values.",
                    Font, ClientRectangle, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            DrawAxes(g, area);

            var state = g.Save();
            g.SetClip(area);
            for (int i = 0; i < series.Count; i++)
                DrawSeries(g, area, series[i], i);
            g.Restore(state);

            g.DrawRectangle(Pens.Gray, area);
            DrawLegend(g, area);
        }

        void DrawAxes(Graphics g, Rectangle area)
        {
            using var gridPen = new Pen(Color.FromArgb(230, 230, 230));
            var labelColor = Color.FromArgb(60, 60, 60);

            foreach (var t in Ticks(viewMinX, viewMaxX, Math.Max(2, area.Width / 90), logX))
            {
                var x = PxX(t, area);
                g.DrawLine(gridPen, x, area.Top, x, area.Bottom);
                var label = Label(t, logX);
                var size = TextRenderer.MeasureText(label, Font);
                TextRenderer.DrawText(g, label, Font, new Point((int)(x - size.Width / 2f), area.Bottom + 4), labelColor);
            }
            foreach (var t in Ticks(viewMinY, viewMaxY, Math.Max(2, area.Height / 50), logY))
            {
                var y = PxY(t, area);
                g.DrawLine(gridPen, area.Left, y, area.Right, y);
                var label = Label(t, logY);
                var size = TextRenderer.MeasureText(label, Font);
                TextRenderer.DrawText(g, label, Font, new Point(area.Left - size.Width - 4, (int)(y - size.Height / 2f)), labelColor);
            }

            using var bold = new Font(Font, FontStyle.Bold);
            var xLabel = xTitle + (logX ? " (log)" : "");
            var xs = TextRenderer.MeasureText(xLabel, bold);
            TextRenderer.DrawText(g, xLabel, bold, new Point(area.Left + (area.Width - xs.Width) / 2, area.Bottom + 24), Color.Black);

            var yLabel = yTitle + (logY ? " (log)" : "");
            var ys = g.MeasureString(yLabel, bold);
            var state = g.Save();
            g.TranslateTransform(4, area.Top + area.Height / 2f + ys.Width / 2f);
            g.RotateTransform(-90);
            g.DrawString(yLabel, bold, Brushes.Black, 0, 0);
            g.Restore(state);
        }

        static string Label(double t, bool log)
        {
            var v = log ? Math.Pow(10, t) : t;
            if (Math.Abs(v) < 1e-12) v = 0;
            return v.ToString("G4", CultureInfo.CurrentCulture);
        }

        /// <summary> "Nice" tick positions (1-2-5 steps), or whole decades on log axes when the range allows it. </summary>
        static IEnumerable<double> Ticks(double min, double max, int maxCount, bool log)
        {
            var range = max - min;
            if (range <= 0) yield break;
            if (log && range >= 2)
            {
                var stepDecades = Math.Max(1, Math.Ceiling(range / maxCount));
                for (var t = Math.Ceiling(min); t <= max; t += stepDecades)
                    yield return t;
                yield break;
            }
            if (log)
            {
                // Less than two decades: round numbers in real units, placed on the log scale.
                foreach (var v in Ticks(Math.Pow(10, min), Math.Pow(10, max), maxCount, false))
                    if (v > 0) yield return Math.Log10(v);
                yield break;
            }
            var rough = range / maxCount;
            var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            var norm = rough / mag;
            var step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
            for (var t = Math.Ceiling(min / step) * step; t <= max + step * 1e-9; t += step)
                yield return t;
        }

        void DrawSeries(Graphics g, Rectangle area, ChartSeries s, int index)
        {
            var color = Palette[index % Palette.Length];
            var pts = s.Points.Where(Valid).Select(p => new PointF(PxX(TX(p.X), area), PxY(TY(p.Y), area))).ToList();
            if (pts.Count == 0) return;

            if (type == ChartType.Column)
            {
                var xs = series.SelectMany(x => x.Points).Where(Valid).Select(p => PxX(TX(p.X), area)).Distinct().OrderBy(x => x).ToList();
                var spacing = xs.Count > 1 ? xs.Zip(xs.Skip(1), (a, b) => b - a).Min() : area.Width / 4f;
                var width = Math.Max(2f, Math.Min(60f, spacing * 0.8f / Math.Max(1, series.Count)));
                var offset = (index - (series.Count - 1) / 2f) * width;
                var baseline = PxY(logY ? viewMinY : Math.Max(viewMinY, Math.Min(viewMaxY, 0)), area);
                using var brush = new SolidBrush(Color.FromArgb(200, color));
                foreach (var p in pts)
                {
                    var top = Math.Min(p.Y, baseline);
                    g.FillRectangle(brush, p.X + offset - width / 2, top, width, Math.Max(1, Math.Abs(baseline - p.Y)));
                }
                return;
            }

            if (type == ChartType.Line && pts.Count > 1)
            {
                using var pen = new Pen(color, 1.5f);
                g.DrawLines(pen, pts.ToArray());
            }
            using var markerBrush = new SolidBrush(color);
            foreach (var p in pts)
                DrawMarker(g, markerBrush, p, index);
        }

        static void DrawMarker(Graphics g, Brush brush, PointF p, int shape)
        {
            const float r = 3.5f;
            switch (shape % 4)
            {
                case 0: g.FillEllipse(brush, p.X - r, p.Y - r, 2 * r, 2 * r); break;
                case 1: g.FillRectangle(brush, p.X - r, p.Y - r, 2 * r, 2 * r); break;
                case 2: g.FillPolygon(brush, new[] { new PointF(p.X, p.Y - r - 1), new PointF(p.X + r + 1, p.Y + r), new PointF(p.X - r - 1, p.Y + r) }); break;
                default: g.FillPolygon(brush, new[] { new PointF(p.X, p.Y - r - 1), new PointF(p.X + r + 1, p.Y), new PointF(p.X, p.Y + r + 1), new PointF(p.X - r - 1, p.Y) }); break;
            }
        }

        void DrawLegend(Graphics g, Rectangle area)
        {
            int y = area.Top;
            for (int i = 0; i < series.Count; i++)
            {
                var color = Palette[i % Palette.Length];
                using var brush = new SolidBrush(color);
                var center = new PointF(area.Right + 20, y + 8);
                if (type == ChartType.Column) g.FillRectangle(brush, center.X - 5, center.Y - 5, 10, 10);
                else DrawMarker(g, brush, center, i);
                var text = series[i].Name ?? "";
                var room = ClientSize.Width - area.Right - 35;
                TextRenderer.DrawText(g, text, Font, new Rectangle(area.Right + 30, y, Math.Max(10, room), 18), Color.Black, TextFormatFlags.EndEllipsis);
                y += 20;
            }
        }

        void ZoomAt(PointF pixel, double factor)
        {
            if (!hasView) return;
            var area = PlotArea;
            var cx = DataX(pixel.X, area);
            var cy = DataY(pixel.Y, area);
            viewMinX = cx - (cx - viewMinX) * factor;
            viewMaxX = cx + (viewMaxX - cx) * factor;
            viewMinY = cy - (cy - viewMinY) * factor;
            viewMaxY = cy + (viewMaxY - cy) * factor;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ZoomAt(e.Location, e.Delta > 0 ? 0.8 : 1.25);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left && hasView)
            {
                dragStart = e.Location;
                dragView = (viewMinX, viewMaxX, viewMinY, viewMaxY);
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragStart = null;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            ResetZoom();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!hasView) return;
            var area = PlotArea;
            if (dragStart is Point start)
            {
                var dx = (e.X - start.X) / (double)area.Width * (dragView.MaxX - dragView.MinX);
                var dy = (e.Y - start.Y) / (double)area.Height * (dragView.MaxY - dragView.MinY);
                viewMinX = dragView.MinX - dx; viewMaxX = dragView.MaxX - dx;
                viewMinY = dragView.MinY + dy; viewMaxY = dragView.MaxY + dy;
                Invalidate();
                return;
            }

            // Tooltip for the nearest point within a few pixels.
            string tip = null;
            double best = 64;
            foreach (var s in series)
            {
                foreach (var p in s.Points.Where(Valid))
                {
                    var dx = PxX(TX(p.X), area) - e.X;
                    var dy = PxY(TY(p.Y), area) - e.Y;
                    var d = dx * dx + dy * dy;
                    if (d < best)
                    {
                        best = d;
                        tip = $"{s.Name}\n{xTitle}: {p.X.ToString("G6", CultureInfo.CurrentCulture)}\n{yTitle}: {p.Y.ToString("G6", CultureInfo.CurrentCulture)}";
                    }
                }
            }
            if (tip != lastTip)
            {
                lastTip = tip;
                if (tip == null) tooltip.Hide(this);
                else tooltip.Show(tip, this, e.X + 12, e.Y + 12);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tooltip.Dispose();
            base.Dispose(disposing);
        }
    }
}
