using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassExplorer
{
    /// Smooth, frame-synced wheel scrolling. The offset is advanced on every rendered
    /// frame (CompositionTarget.Rendering), so motion runs at the monitor's refresh rate
    /// (e.g. 165 Hz) instead of jumping 3 lines per wheel notch.
    sealed class SmoothScroll
    {
        readonly Control host;
        ScrollViewer sv;
        double target;
        bool running;
        TimeSpan last;

        SmoothScroll(Control host)
        {
            this.host = host;
            host.PreviewMouseWheel += OnWheel;
        }

        public static void Attach(Control host) => _ = new SmoothScroll(host);

        static ScrollViewer Find(DependencyObject d)
        {
            if (d is ScrollViewer s) return s;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                if (Find(VisualTreeHelper.GetChild(d, i)) is ScrollViewer r) return r;
            return null;
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            sv ??= Find(host);
            if (sv == null || sv.ScrollableHeight <= 0) return;
            e.Handled = true;

            if (!running) target = sv.VerticalOffset;
            // ~ 3 lines per notch like Windows, scaled to the user's wheel setting
            double step = e.Delta / 120.0 * 40 * Math.Max(1, SystemParameters.WheelScrollLines);
            target = Math.Clamp(target - step, 0, sv.ScrollableHeight);

            if (!running)
            {
                running = true;
                last = TimeSpan.Zero;
                CompositionTarget.Rendering += OnFrame;
            }
        }

        void OnFrame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            double dt = last == TimeSpan.Zero ? 1.0 / 165 : (now - last).TotalSeconds;
            if (dt <= 0) return; // same frame reported twice
            last = now;

            target = Math.Clamp(target, 0, sv.ScrollableHeight);
            double cur = sv.VerticalOffset;
            double diff = target - cur;
            if (Math.Abs(diff) < 0.5)
            {
                sv.ScrollToVerticalOffset(target);
                Stop();
                return;
            }
            // Exponential ease-out, frame-rate independent (~120 ms to settle)
            double k = 1 - Math.Exp(-dt * 22);
            sv.ScrollToVerticalOffset(cur + diff * k);
        }

        void Stop()
        {
            running = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }
}
