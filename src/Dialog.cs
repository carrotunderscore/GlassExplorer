using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GlassExplorer
{
    /// Liten mörk dialog i samma stil som appen (fråga, bekräfta, info).
    static class Dialog
    {
        public static string Prompt(Window owner, string title, string message, string initial, int selectLength = -1) =>
            Show(owner, title, message, initial, "OK", true, selectLength);

        public static bool Confirm(Window owner, string title, string message, string ok = "OK") =>
            Show(owner, title, message, null, ok, true) != null;

        public static void Info(Window owner, string title, string message) =>
            Show(owner, title, message, null, "OK", false);

        static string Show(Window owner, string title, string message, string input, string okText, bool cancel, int selectLength = -1)
        {
            var app = Application.Current;
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            if (owner != null && owner.IsVisible) w.Owner = owner;

            var stack = new StackPanel { Width = 380 };
            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)app.Resources["TextPrimary"], Margin = new Thickness(0, 0, 0, 8)
            });
            if (!string.IsNullOrEmpty(message))
                stack.Children.Add(new TextBlock
                {
                    Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    Foreground = (Brush)app.Resources["TextSecondary"], Margin = new Thickness(0, 0, 0, 14)
                });

            TextBox box = null;
            if (input != null)
            {
                box = new TextBox { Text = input, Style = (Style)app.Resources["DialogBox"], Margin = new Thickness(0, 0, 0, 16) };
                stack.Children.Add(box);
            }

            string result = null;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (cancel)
            {
                var c = new Button { Content = "Cancel", Style = (Style)app.Resources["PillButton"], Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
                c.Click += (s, e) => w.Close();
                buttons.Children.Add(c);
            }
            var ok = new Button
            {
                Content = okText, Style = (Style)app.Resources["PillButton"], IsDefault = true,
                Background = (Brush)app.Resources["AccentGradient"], Foreground = Brushes.White
            };
            ok.Click += (s, e) => { result = box?.Text ?? ""; w.Close(); };
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);

            w.Content = new Border
            {
                Margin = new Thickness(24), Padding = new Thickness(24, 20, 24, 20), CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1D, 0x36)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255)), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.6, Color = Colors.Black },
                Child = stack
            };
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch { } };
            w.Loaded += (s, e) =>
            {
                if (box == null) { ok.Focus(); return; }
                box.Focus();
                if (selectLength >= 0) box.Select(0, selectLength); else box.SelectAll();
            };
            w.ShowDialog();
            return result;
        }
    }
}
