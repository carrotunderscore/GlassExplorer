using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GlassExplorer
{
    public class TabState : INotifyPropertyChanged
    {
        public string Path;
        public List<string> Back = new(), Fwd = new();
        public List<string> Selection;
        public double Scroll;

        string title = "", glyph = "", tip = "";
        bool active;
        public string Title { get => title; set { title = value; Notify(nameof(Title)); } }
        public string Glyph { get => glyph; set { glyph = value; Notify(nameof(Glyph)); } }
        public string ToolTipText { get => tip; set { tip = value; Notify(nameof(ToolTipText)); } }
        public bool IsActive { get => active; set { if (active == value) return; active = value; Notify(nameof(IsActive)); } }

        void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow
    {
        readonly ObservableCollection<TabState> tabs = new();
        TabState activeTab;
        int bgOpened;

        /// Most recently focused window; other launches of the app open their folder as a tab here.
        public static MainWindow LastActive { get; private set; }

        static string GlyphFor(string path) =>
            path == Home ? "" : path == ThisPC ? "" : path != null && path.Length <= 3 ? "" : "";

        async Task NewTab(string path, bool activate = true)
        {
            var t = new TabState { Path = path ?? Home };
            t.Title = NameOf(t.Path);
            t.Glyph = GlyphFor(t.Path);
            t.ToolTipText = IsVirtual(t.Path) ? t.Title : t.Path;
            // background tabs line up after each other, to the right of the current tab (like a browser)
            int idx = activeTab == null ? tabs.Count : Math.Min(tabs.Count, tabs.IndexOf(activeTab) + 1 + (activate ? 0 : bgOpened++));
            tabs.Insert(idx, t);
            if (activate || activeTab == null) await SwitchTab(t);
            ScrollTabIntoView(t);
        }

        void SaveTabState()
        {
            if (activeTab == null || current == null) return;
            activeTab.Path = current;
            activeTab.Selection = Selected.Select(i => i.FullPath).ToList();
            activeTab.Scroll = GetScroll(ActiveList)?.VerticalOffset ?? 0;
        }

        async Task SwitchTab(TabState t)
        {
            if (t == null || t == activeTab) return;
            EndBand();
            SaveTabState();
            activeTab = t;
            bgOpened = 0;
            foreach (var x in tabs) x.IsActive = x == t;
            back = t.Back;
            fwd = t.Fwd;
            current = null;   // so Navigate doesn't push the old tab's folder into this tab's history
            await Navigate(t.Path ?? Home, addHistory: false);
            if (activeTab != t) return;
            if (current == null) await Navigate(Home, addHistory: false);

            if (t.Selection is { Count: > 0 } sel) SelectPaths(sel, scroll: false);
            if (t.Scroll > 0)
            {
                double off = t.Scroll;
                _ = Dispatcher.BeginInvoke(() => GetScroll(ActiveList)?.ScrollToVerticalOffset(off), DispatcherPriority.Loaded);
            }
            ScrollTabIntoView(t);
        }

        void CloseTab(TabState t)
        {
            if (t == null) return;
            if (tabs.Count <= 1) { Close(); return; }
            int idx = tabs.IndexOf(t);
            tabs.Remove(t);
            if (t == activeTab)
            {
                activeTab = null;
                _ = SwitchTab(tabs[Math.Min(idx, tabs.Count - 1)]);
            }
        }

        void CycleTab(int dir)
        {
            if (tabs.Count < 2 || activeTab == null) return;
            int i = (tabs.IndexOf(activeTab) + dir + tabs.Count) % tabs.Count;
            _ = SwitchTab(tabs[i]);
        }

        void SelectTabNumber(int n)
        {
            if (tabs.Count == 0) return;
            // Ctrl+9 = last tab, like browsers
            _ = SwitchTab(n >= 8 ? tabs[^1] : n < tabs.Count ? tabs[n] : null);
        }

        /// Called from UpdateChrome after every navigation.
        void UpdateTabInfo(string title)
        {
            if (activeTab == null) return;
            activeTab.Path = current;
            activeTab.Title = title;
            activeTab.Glyph = searchMode ? "" : GlyphFor(current);
            activeTab.ToolTipText = IsVirtual(current) ? title : current;
        }

        void ScrollTabIntoView(TabState t) =>
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (TabsList.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe) fe.BringIntoView();
            }, DispatcherPriority.Loaded);

        // ---------- tab strip events ----------
        async void Tab_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>(e.OriginalSource) != null) return;
            if ((s as FrameworkElement)?.DataContext is TabState t)
            {
                e.Handled = true;
                await SwitchTab(t);
            }
        }

        void Tab_MouseUp(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && (s as FrameworkElement)?.DataContext is TabState t)
            {
                e.Handled = true;
                CloseTab(t);
            }
        }

        void TabClose_Click(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is TabState t) CloseTab(t);
        }

        void NewTab_Click(object s, RoutedEventArgs e) => _ = NewTab(Home);

        void Tab_RightClick(object s, MouseButtonEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is not TabState t) return;
            e.Handled = true;
            var m = new ContextMenu();
            m.Items.Add(MI("New tab", "", () => _ = NewTab(Home), "Ctrl+T"));
            m.Items.Add(MI("Duplicate tab", "", () =>
            {
                if (t == activeTab) SaveTabState();
                _ = NewTab(t.Path);
            }));
            m.Items.Add(new Separator());
            m.Items.Add(MI("Close tab", "", () => CloseTab(t), "Ctrl+W"));
            m.Items.Add(MI("Close other tabs", "", async () =>
            {
                await SwitchTab(t);
                foreach (var o in tabs.Where(x => x != t).ToList()) tabs.Remove(o);
            }, enabled: tabs.Count > 1));
            m.Items.Add(MI("Close tabs to the right", "", () =>
            {
                int i = tabs.IndexOf(t);
                var right = tabs.Skip(i + 1).ToList();
                if (right.Contains(activeTab)) _ = SwitchTab(t);
                foreach (var o in right) tabs.Remove(o);
            }, enabled: tabs.IndexOf(t) < tabs.Count - 1));
            m.PlacementTarget = (UIElement)s;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            m.IsOpen = true;
        }

        /// Middle-click a folder to open it in a background tab (like a browser).
        void List_MiddleDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (ItemAt(e.OriginalSource)?.DataContext is FileItem it && it.IsContainer)
            {
                e.Handled = true;
                _ = NewTab(it.FullPath, activate: false);
            }
        }

        /// Another launch of the app (double-clicked folder, Win+E, …) handed us a path.
        public async void OpenFromOtherInstance(string path)
        {
            if (WindowState == WindowState.Minimized) WindowState = restoreState;
            Show();
            Activate();
            Topmost = true; Topmost = false;
            var p = ParseStartPath(path);
            await NewTab(string.IsNullOrEmpty(p) ? Home : p);
        }

        WindowState restoreState = WindowState.Normal;

        void TrackWindowState()
        {
            Activated += (s, e) => LastActive = this;
            StateChanged += (s, e) => { if (WindowState != WindowState.Minimized) restoreState = WindowState; };
            Closed += (s, e) =>
            {
                if (LastActive == this) LastActive = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w != this);
            };
            LastActive ??= this;
        }
    }
}
