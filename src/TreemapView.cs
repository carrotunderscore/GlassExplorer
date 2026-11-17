using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassExplorer
{
    /// WizTree/SpaceSniffer-style squarified treemap of a SizeNode tree.
    public sealed class TreemapView : FrameworkElement
    {
        readonly VisualCollection visuals;
        readonly DrawingVisual baseVisual = new(), hoverVisual = new();
        readonly List<(SizeNode node, Rect rect, SizeNode top)> hits = new();
        readonly Dictionary<(Color, int), Brush> brushCache = new();
        readonly DispatcherTimer resizeTimer;
        static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x08, 0x10, 0x22)), 1));
        static readonly Pen HoverPen = Frozen(new Pen(Brushes.White, 2));
        static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)));
        static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFC)));
        static readonly Brush LabelDim = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xF4, 0xF7, 0xFC)));
        const int MaxDepth = 6;

        SizeNode root;
        (SizeNode node, Rect rect, SizeNode top)? hovered;

        public event Action<SizeNode> HoverChanged;
        public event Action<SizeNode, SizeNode> NodeClicked;        // (deepest, top-level child)
        public event Action<SizeNode, SizeNode> NodeRightClicked;

        public TreemapView()
        {
            visuals = new VisualCollection(this) { baseVisual, hoverVisual };
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            resizeTimer.Tick += (s, e) => { resizeTimer.Stop(); Redraw(); };
        }

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];

        public SizeNode Root
        {
            get => root;
            set { root = value; hovered = null; Redraw(); }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            resizeTimer.Stop();
            resizeTimer.Start();
        }

        public void Redraw()
        {
            hits.Clear();
            using (var dc = baseVisual.RenderOpen())
            {
                var area = new Rect(0, 0, ActualWidth, ActualHeight);
                dc.DrawRectangle(Brushes.Transparent, null, area);
                if (root != null && area.Width > 4 && area.Height > 4)
                {
                    var kids = root.Display();
                    long total = Sum(kids);
                    int i = 0;
                    var index = new Dictionary<SizeNode, int>();
                    foreach (var k in kids) index[k] = i++;
                    Layout(kids, total, area, (k, r) => DrawNode(dc, k, r, 1, DiskScanner.ColorFor(k, index[k]), k));
                }
            }
            DrawHover();
        }

        static long Sum(List<SizeNode> l) { long s = 0; foreach (var n in l) s += n.Size; return s; }

        Brush Fill(Color c, int depth)
        {
            if (brushCache.TryGetValue((c, depth), out var b)) return b;
            // deeper levels get slightly darker so nesting is visible; cushion-like gradient
            double dark = 0.20 + 0.06 * (depth - 1);
            Color baseC = Mix(c, Color.FromRgb(0x0B, 0x14, 0x28), Math.Min(dark, 0.55));
            var g = new LinearGradientBrush(Mix(baseC, Colors.White, 0.16), Mix(baseC, Colors.Black, 0.22), new Point(0, 0), new Point(1, 1));
            g.Freeze();
            brushCache[(c, depth)] = g;
            return g;
        }

        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        void DrawNode(DrawingContext dc, SizeNode n, Rect r, int depth, Color color, SizeNode top)
        {
            if (r.Width < 1.5 || r.Height < 1.5) return;
            hits.Add((n, r, top));
            dc.DrawRectangle(Fill(color, depth), Edge, r);
            if (depth >= MaxDepth || n.IsPseudo || r.Width < 10 || r.Height < 10) return;

            var kids = n.Display();
            if (kids.Count == 0) return;

            Rect inner;
            bool label = r.Width > 74 && r.Height > (depth == 1 ? 40 : 46);
            if (label)
            {
                DrawLabel(dc, n, r, depth);
                inner = new Rect(r.X + 3, r.Y + 21, r.Width - 6, r.Height - 24);
            }
            else inner = new Rect(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
            if (inner.Width < 3 || inner.Height < 3) return;
            Layout(kids, Sum(kids), inner, (k, kr) => DrawNode(dc, k, kr, depth + 1, color, top));
        }

        void DrawLabel(DrawingContext dc, SizeNode n, Rect r, int depth)
        {
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var name = new FormattedText(n.DisplayName, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Bold, depth == 1 ? 12.5 : 11.5, LabelBrush, dip)
            { MaxTextWidth = Math.Max(1, r.Width - 12), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(name, new Point(r.X + 6, r.Y + 3));
            double used = name.WidthIncludingTrailingWhitespace + 8;
            if (r.Width - 12 - used > 40)
            {
                var size = new FormattedText(Fmt.Size(n.Size), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Regular, 11, LabelDim, dip)
                { MaxTextWidth = r.Width - 12 - used, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(size, new Point(r.X + 6 + used, r.Y + 4.5));
            }
        }

        /// Squarified treemap layout (Bruls, Huizing, van Wijk). nodes must be sorted biggest first.
        static void Layout(List<SizeNode> nodes, long total, Rect r, Action<SizeNode, Rect> emit)
        {
            double x = r.X, y = r.Y, w = r.Width, h = r.Height;
            double remaining = total;
            int i = 0;
            while (i < nodes.Count && w > 0.5 && h > 0.5 && remaining > 0)
            {
                double scale = w * h / remaining;
                bool vertical = w >= h;              // row runs along the shorter side
                double side = vertical ? h : w;
                int j = i; double rowSum = 0, best = double.MaxValue;
                while (j < nodes.Count)
                {
                    double s = nodes[j].Size;
                    if (s <= 0) { j++; continue; }
                    double sum = rowSum + s;
                    double area = sum * scale;
                    double maxA = nodes[i].Size * scale, minA = s * scale;
                    double worst = Math.Max(side * side * maxA / (area * area), area * area / (side * side * minA));
                    if (worst > best) break;
                    best = worst; rowSum = sum; j++;
                }
                if (rowSum <= 0) break;
                double thick = rowSum * scale / side;
                double off = 0;
                for (int k = i; k < j; k++)
                {
                    double len = nodes[k].Size * scale / thick;
                    if (len <= 0) continue;
                    emit(nodes[k], vertical ? new Rect(x, y + off, thick, len) : new Rect(x + off, y, len, thick));
                    off += len;
                }
                if (vertical) { x += thick; w -= thick; } else { y += thick; h -= thick; }
                remaining -= rowSum;
                i = j;
            }
        }

        (SizeNode, Rect, SizeNode)? HitAt(Point p)
        {
            for (int i = hits.Count - 1; i >= 0; i--)
                if (hits[i].rect.Contains(p)) return hits[i];
            return null;
        }

        void DrawHover()
        {
            using var dc = hoverVisual.RenderOpen();
            if (hovered is { } h)
            {
                var r = h.rect;
                r.Inflate(-1, -1);
                if (r.Width > 0 && r.Height > 0) dc.DrawRectangle(HoverFill, HoverPen, r);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitAt(e.GetPosition(this));
            if (hit?.Item1 == hovered?.node) return;
            hovered = hit;
            DrawHover();
            HoverChanged?.Invoke(hit?.Item1);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = null;
            DrawHover();
            HoverChanged?.Invoke(null);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (HitAt(e.GetPosition(this)) is { } h) NodeClicked?.Invoke(h.Item1, h.Item3);
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (HitAt(e.GetPosition(this)) is { } h) { NodeRightClicked?.Invoke(h.Item1, h.Item3); e.Handled = true; }
        }
    }
}
