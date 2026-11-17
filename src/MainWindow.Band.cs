using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassExplorer
{
    /// Rubber-band selection: click on empty space and drag to select several files.
    public partial class MainWindow
    {
        ListBox bandList;
        ScrollViewer bandSv;
        Point bandStart;              // content coordinates (viewport + scroll offset)
        HashSet<FileItem> bandBase;   // selection kept while Ctrl-banding
        bool banding, bandShown;
        DispatcherTimer bandTimer;

        void BeginBand(ListBox lb, Point viewportPoint, bool additive)
        {
            var sv = GetScroll(lb);
            if (sv == null) return;
            bandList = lb;
            bandSv = sv;
            bandStart = new Point(viewportPoint.X, viewportPoint.Y + sv.VerticalOffset);
            bandBase = additive ? lb.SelectedItems.Cast<FileItem>().ToHashSet() : new HashSet<FileItem>();
            if (!additive) lb.UnselectAll();
            banding = true;
            bandShown = false;
            if (bandTimer == null)
            {
                bandTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                bandTimer.Tick += (s, e) => BandAutoScroll();
            }
            bandTimer.Start();
        }

        void BandAutoScroll()
        {
            if (!banding || bandSv == null) return;
            var p = Mouse.GetPosition(bandSv);
            double h = bandSv.ViewportHeightPx();
            double dy = p.Y < 0 ? -Math.Min(48, 4 - p.Y / 2) : p.Y > h ? Math.Min(48, 4 + (p.Y - h) / 2) : 0;
            if (dy == 0) return;
            bandSv.ScrollToVerticalOffset(Math.Clamp(bandSv.VerticalOffset + dy, 0, bandSv.ScrollableHeight));
            bandSv.UpdateLayout();
            UpdateBand(p);
        }

        void UpdateBand(Point viewportPoint)
        {
            if (!banding) return;
            var sv = bandSv;
            double off = sv.VerticalOffset;
            double w = sv.ActualWidth, h = sv.ViewportHeightPx();
            var cur = new Point(Math.Clamp(viewportPoint.X, 0, w), Math.Clamp(viewportPoint.Y, 0, h) + off);
            var r = new Rect(bandStart, cur);
            if (!bandShown && r.Width < 5 && r.Height < 5) return;
            bandShown = true;

            // draw (clipped to the visible part of the list)
            var vr = r;
            vr.Offset(0, -off);
            vr.Intersect(new Rect(0, 0, w, h));
            if (vr.IsEmpty) BandRect.Visibility = Visibility.Collapsed;
            else
            {
                var tl = sv.TranslatePoint(vr.TopLeft, BandCanvas);
                Canvas.SetLeft(BandRect, tl.X);
                Canvas.SetTop(BandRect, tl.Y);
                BandRect.Width = Math.Max(1, vr.Width);
                BandRect.Height = Math.Max(1, vr.Height);
                BandRect.Visibility = Visibility.Visible;
            }

            // hit test
            var lb = bandList;
            var hit = new HashSet<FileItem>(bandBase);
            if (lb == GridList)
            {
                foreach (var item in lb.Items)
                {
                    if (lb.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c || !c.IsVisible) continue;
                    Rect b;
                    try { b = c.TransformToAncestor(sv).TransformBounds(new Rect(c.RenderSize)); } catch { continue; }
                    b.Offset(0, off);
                    if (b.IntersectsWith(r)) hit.Add((FileItem)item);
                }
            }
            else
            {
                // details view is virtualized: rows are uniform, so work out the index range from one realized row
                var c = FirstRealized(sv);
                if (c != null && lb.Items.Count > 0)
                {
                    int idx = lb.ItemContainerGenerator.IndexFromContainer(c);
                    double rowH = c.ActualHeight + c.Margin.Top + c.Margin.Bottom;
                    var pos = c.TransformToAncestor(sv).Transform(new Point(0, 0));
                    double y0 = pos.Y - c.Margin.Top + off - idx * rowH;
                    bool xHit = r.Right >= pos.X && r.Left <= pos.X + c.ActualWidth;
                    if (idx >= 0 && rowH > 1 && xHit)
                    {
                        int first = Math.Max(0, (int)Math.Floor((r.Top - y0) / rowH));
                        int last = Math.Min(lb.Items.Count - 1, (int)Math.Floor((r.Bottom - y0 - c.Margin.Bottom) / rowH));
                        for (int i = first; i <= last; i++) hit.Add((FileItem)lb.Items[i]);
                    }
                }
            }

            // apply as a diff so the list doesn't flicker
            foreach (var s in lb.SelectedItems.Cast<FileItem>().Where(s => !hit.Contains(s)).ToList()) lb.SelectedItems.Remove(s);
            var selected = lb.SelectedItems.Cast<FileItem>().ToHashSet();
            foreach (var it in hit) if (!selected.Contains(it)) lb.SelectedItems.Add(it);
        }

        void EndBand()
        {
            if (!banding) return;
            banding = false;
            bandTimer?.Stop();
            BandRect.Visibility = Visibility.Collapsed;
            if (bandList != null && bandList.IsMouseCaptured) bandList.ReleaseMouseCapture();
            bandList = null;
            bandSv = null;
        }

        static ListBoxItem FirstRealized(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var ch = VisualTreeHelper.GetChild(d, i);
                if (ch is ListBoxItem li && li.IsVisible) return li;
                if (FirstRealized(ch) is ListBoxItem r) return r;
            }
            return null;
        }
    }

    static class ScrollViewerExt
    {
        /// Visible height in pixels (ViewportHeight is in items for item-based scrolling).
        public static double ViewportHeightPx(this ScrollViewer sv) =>
            sv.Template?.FindName("PART_ScrollContentPresenter", sv) is FrameworkElement p ? p.ActualHeight : sv.ActualHeight;
    }
}
