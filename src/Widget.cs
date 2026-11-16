// Claude Widget: an always-on-top panel showing live Claude Code sessions,
// what each is doing, token usage and plan limits. WPF, built in code (no XAML).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Claude Widget")]
[assembly: AssemblyProduct("Claude Widget")]
[assembly: AssemblyVersion("0.2.0.0")]

namespace ClaudeWidget
{
    static class WidgetProgram
    {
        public const string MutexName = @"Local\ClaudeWidget.Single";
        public const string ShowEventName = @"Local\ClaudeWidget.Show";
        public const string QuitEventName = @"Local\ClaudeWidget.Quit";

        [STAThread]
        static void Main()
        {
            bool created;
            var single = new Mutex(true, MutexName, out created);
            if (!created)
            {
                // Already running: ask that instance to show itself.
                EventWaitHandle show;
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out show)) show.Set();
                return;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Log.Error("ui", e.Exception); e.Handled = true; };
            new WidgetWindow();
            app.Run();
            GC.KeepAlive(single);
        }
    }

    // ------------------------------------------------------------------ look

    static class Theme
    {
        // Status colors differ in hue AND each state has its own icon shape.
        public static readonly Color BusyColor = Color.FromRgb(0x5A, 0xA2, 0xFF);   // blue
        public static readonly Color WaitColor = Color.FromRgb(0xFF, 0xAD, 0x52);   // amber
        public static readonly Color DoneColor = Color.FromRgb(0x3D, 0xD6, 0x8C);   // green
        public static readonly Color IdleColor = Color.FromRgb(0x8A, 0x90, 0x9C);   // grey
        public static readonly Color HotColor = Color.FromRgb(0xFF, 0x6B, 0x6B);

        public static readonly Brush Busy = B(BusyColor), Wait = B(WaitColor), Done = B(DoneColor), Idle = B(IdleColor), Hot = B(HotColor);
        public static readonly Brush BusySoft = B(Alpha(BusyColor, 0x2A)), WaitSoft = B(Alpha(WaitColor, 0x2E));
        public static readonly Brush DoneSoft = B(Alpha(DoneColor, 0x26)), IdleSoft = B(Alpha(IdleColor, 0x24));
        public static readonly Brush BusyTrack = B(Alpha(BusyColor, 0x40));

        public static readonly Brush Text = B(Color.FromRgb(0xEC, 0xEE, 0xF1));
        public static readonly Brush Text2 = B(Color.FromRgb(0xC9, 0xCD, 0xD4));
        public static readonly Brush Sub = B(Color.FromRgb(0x9A, 0xA1, 0xAB));
        public static readonly Brush Faint = B(Color.FromRgb(0x70, 0x77, 0x83));

        public static readonly Brush Window = B(Color.FromRgb(0x12, 0x14, 0x19));
        public static readonly Brush Header = B(Color.FromRgb(0x18, 0x1B, 0x22));
        public static readonly Brush RowBg = B(Color.FromRgb(0x1B, 0x1E, 0x26));
        public static readonly Brush RowHover = B(Color.FromRgb(0x24, 0x28, 0x32));
        public static readonly Brush RowWaiting = B(Color.FromRgb(0x2A, 0x23, 0x19));
        public static readonly Brush RowBorder = B(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        public static readonly Brush Line = B(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        public static readonly Brush Track = B(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        public static readonly Brush BarDim = B(Color.FromArgb(0x4C, 0xFF, 0xFF, 0xFF));
        public static readonly Brush Hover = B(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        public static readonly Brush Chip = B(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        static Color Alpha(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }
        static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static Brush ForPct(double pct)
        {
            if (pct >= 90) return Hot;
            if (pct >= 75) return Wait;
            return Busy;
        }

        public static string ToolGlyph(string tool)
        {
            switch (tool ?? "")
            {
                case "Bash":
                case "PowerShell": return "";   // command prompt
                case "Read": return "";         // document
                case "Write":
                case "Edit":
                case "MultiEdit":
                case "NotebookEdit": return ""; // pencil
                case "Grep":
                case "Glob": return "";         // search
                case "WebFetch":
                case "WebSearch": return "";    // globe
                case "Agent":
                case "Task": return "";         // people
                case "Skill": return "";        // lightning
            }
            if (tool != null && tool.StartsWith("mcp__", StringComparison.Ordinal)) return ""; // link
            return "";                          // play
        }
    }

    static class UI
    {
        public static TextBlock Text(string text, double size, Brush fg)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = fg };
        }

        public static TextBlock Caps(string text)
        {
            var t = Text(text, 11, Theme.Faint);
            t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        public static Border Divider()
        {
            return new Border { Height = 1, Background = Theme.Line };
        }

        public static Border Pill(string text, Brush fg, Brush bg)
        {
            var t = Text(text, 12, fg);
            t.FontWeight = FontWeights.SemiBold;
            return new Border
            {
                Background = bg, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 8, 2),
                Margin = new Thickness(0, 2, 5, 2), VerticalAlignment = VerticalAlignment.Center, Child = t,
            };
        }

        /// <summary>Flat icon button that does not start a window drag.</summary>
        public static Border IconButton(string glyph, string tip, Action onClick, out TextBlock glyphText)
        {
            var g = new TextBlock
            {
                Text = glyph, FontFamily = Theme.Icons, FontSize = 11, Foreground = Theme.Sub,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Border
            {
                Width = 30, Height = 28, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent,
                Child = g, ToolTip = tip, Cursor = Cursors.Hand,
            };
            b.MouseEnter += delegate { b.Background = Theme.Hover; g.Foreground = Theme.Text; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; g.Foreground = Theme.Sub; };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            glyphText = g;
            return b;
        }
    }

    /// <summary>Rounded progress bar sized with star columns, so no measuring is needed.</summary>
    sealed class MiniBar : Grid
    {
        readonly ColumnDefinition filled = new ColumnDefinition(), rest = new ColumnDefinition();
        readonly Border fill;

        public MiniBar(double height)
        {
            Height = height;
            ColumnDefinitions.Add(filled);
            ColumnDefinitions.Add(rest);
            var track = new Border { Background = Theme.Track, CornerRadius = new CornerRadius(height / 2) };
            SetColumnSpan(track, 2);
            Children.Add(track);
            fill = new Border { CornerRadius = new CornerRadius(height / 2), MinWidth = height };
            Children.Add(fill);
            Set(0, Theme.Busy);
        }

        public void Set(double pct, Brush brush)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            filled.Width = new GridLength(pct, GridUnitType.Star);
            rest.Width = new GridLength(100 - pct, GridUnitType.Star);
            fill.Background = brush;
            fill.Visibility = pct > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>busy = spinning blue arc, waiting = pulsing amber dot,
    /// done = green check, idle = grey ring.</summary>
    sealed class StatusIcon : Grid
    {
        readonly Ellipse track, dot, ring, doneBg;
        readonly TextBlock check;
        readonly Path arc;
        readonly RotateTransform spin = new RotateTransform(0, 8, 8);
        string state;

        public StatusIcon()
        {
            Width = 16; Height = 16;
            track = new Ellipse { Width = 16, Height = 16, Stroke = Theme.BusyTrack, StrokeThickness = 2 };
            arc = new Path
            {
                Data = Geometry.Parse("M 8,1 A 7,7 0 0 1 15,8"), Stroke = Theme.Busy, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = spin,
            };
            dot = new Ellipse { Width = 11, Height = 11, Fill = Theme.Wait };
            ring = new Ellipse { Width = 12, Height = 12, Stroke = Theme.Idle, StrokeThickness = 2 };
            doneBg = new Ellipse { Width = 16, Height = 16, Fill = Theme.DoneSoft };
            check = new TextBlock
            {
                Text = "", FontFamily = Theme.Icons, FontSize = 9, Foreground = Theme.Done, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(track); Children.Add(arc); Children.Add(dot); Children.Add(ring); Children.Add(doneBg); Children.Add(check);
            SetState("idle");
        }

        public void SetState(string s)
        {
            if (s == state) return;
            state = s;
            bool busy = s == "busy", waiting = s == "waiting", done = s == "done", idle = !busy && !waiting && !done;
            track.Visibility = arc.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            dot.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
            doneBg.Visibility = check.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            ring.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;

            // Low frame rates on purpose: each animation frame repaints the window.
            if (busy)
            {
                var a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(a, 10);
                spin.BeginAnimation(RotateTransform.AngleProperty, a);
            }
            else spin.BeginAnimation(RotateTransform.AngleProperty, null);

            if (waiting)
            {
                var p = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(p, 8);
                dot.BeginAnimation(OpacityProperty, p);
            }
            else dot.BeginAnimation(OpacityProperty, null);
        }
    }

    // ------------------------------------------------------------------ one session

    sealed class SessionRow : Border
    {
        public SessionInfo Info;
        readonly StatusIcon icon = new StatusIcon();
        readonly TextBlock title, hostText, elapsed, activityGlyph, activity, project, ctxText, meta;
        readonly MiniBar ctxBar = new MiniBar(5);
        readonly FrameworkElement line2, line3;
        bool hover;
        string state, tipText;

        public SessionRow(Action<SessionRow> onClick)
        {
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand;
            MouseEnter += delegate { hover = true; Paint(); };
            MouseLeave += delegate { hover = false; Paint(); };
            MouseLeftButtonUp += delegate { onClick(this); };
            ToolTipService.SetInitialShowDelay(this, 800);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 3, 0, 0);
            g.Children.Add(icon);

            var col = new StackPanel();
            Grid.SetColumn(col, 1);
            g.Children.Add(col);

            // Line 1: title, host, time in this state
            var l1 = new DockPanel();
            elapsed = UI.Text("", 12.5, Theme.Sub);
            elapsed.Margin = new Thickness(8, 0, 0, 0);
            elapsed.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(elapsed, Dock.Right);
            hostText = UI.Text("", 11.5, Theme.Sub);
            var chip = new Border
            {
                Background = Theme.Chip, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = hostText,
            };
            DockPanel.SetDock(chip, Dock.Right);
            title = UI.Text("", 14.5, Theme.Text);
            title.FontWeight = FontWeights.SemiBold;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            l1.Children.Add(elapsed);
            l1.Children.Add(chip);
            l1.Children.Add(title);
            col.Children.Add(l1);

            // Line 2: what it is doing / waiting for / last prompt
            var l2 = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            activityGlyph = new TextBlock { FontFamily = Theme.Icons, FontSize = 12, Margin = new Thickness(0, 2, 7, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(activityGlyph, Dock.Left);
            activity = UI.Text("", 13, Theme.Sub);
            activity.TextTrimming = TextTrimming.CharacterEllipsis;
            l2.Children.Add(activityGlyph);
            l2.Children.Add(activity);
            col.Children.Add(l2);
            line2 = l2;

            // Line 3: project, context, model, tokens. A DockPanel so long folder names and
            // the token text shorten with an ellipsis instead of running off the edge.
            var l3 = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            project = UI.Text("", 11.5, Theme.Faint);
            project.MaxWidth = 112;
            project.TextTrimming = TextTrimming.CharacterEllipsis;
            project.Margin = new Thickness(0, 0, 9, 0);
            ctxBar.Width = 48;
            ctxBar.VerticalAlignment = VerticalAlignment.Center;
            ctxText = UI.Text("", 11.5, Theme.Faint);
            ctxText.Margin = new Thickness(6, 0, 0, 0);
            meta = UI.Text("", 11.5, Theme.Faint);
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            foreach (var el in new FrameworkElement[] { project, ctxBar, ctxText }) DockPanel.SetDock(el, Dock.Left);
            l3.Children.Add(project);
            l3.Children.Add(ctxBar);
            l3.Children.Add(ctxText);
            l3.Children.Add(meta);
            col.Children.Add(l3);
            line3 = l3;

            Child = g;
            Paint();
        }

        void Paint()
        {
            bool waiting = state == "waiting";
            Background = waiting ? Theme.RowWaiting : (hover ? Theme.RowHover : Theme.RowBg);
            BorderBrush = waiting ? Theme.WaitSoft : Theme.RowBorder;
        }

        public void Update(SessionInfo s, bool compact, long now)
        {
            Info = s;
            state = s.State;
            Paint();
            icon.SetState(s.State);
            title.Text = s.Title;
            hostText.Text = s.Host;
            long dur = Math.Max(0, now - s.StatusSince);
            elapsed.Text = s.State == "done" ? Fmt.Duration(dur) + " ago" : Fmt.Duration(dur);
            elapsed.Foreground = s.State == "waiting" ? Theme.Wait : (s.State == "done" ? Theme.Done : Theme.Sub);

            bool active = s.State == "busy" || s.State == "waiting";
            string prompt = string.IsNullOrWhiteSpace(s.LastPrompt) ? null : "“" + Fmt.OneLine(s.LastPrompt, 160) + "”";
            if (s.State == "waiting")
            {
                activityGlyph.Text = "";
                activityGlyph.Foreground = Theme.Wait;
                activity.Text = "Needs you · " + (s.WaitingFor ?? "waiting for input");
                activity.Foreground = Theme.Wait;
            }
            else if (s.State == "busy")
            {
                if (s.CurrentTool != null)
                {
                    string t = LiveTools.Label(s.CurrentTool.Tool);
                    if (!string.IsNullOrEmpty(s.CurrentTool.Detail)) t += " · " + s.CurrentTool.Detail;
                    if (s.RunningTools > 1) t += "  (+" + (s.RunningTools - 1) + ")";
                    activityGlyph.Text = Theme.ToolGlyph(s.CurrentTool.Tool);
                    activity.Text = t;
                }
                else
                {
                    activityGlyph.Text = "";
                    activity.Text = prompt ?? "Working…";
                }
                activityGlyph.Foreground = Theme.Busy;
                activity.Foreground = Theme.Text2;
            }
            else
            {
                bool done = s.State == "done";
                activityGlyph.Text = done ? "" : "";
                activityGlyph.Foreground = done ? Theme.Done : Theme.Faint;
                activity.Text = prompt ?? "No prompts yet";
                activity.Foreground = done ? Theme.Text2 : Theme.Sub;
            }
            // Compact view keeps this line for sessions that are busy or need you.
            line2.Visibility = !compact || active ? Visibility.Visible : Visibility.Collapsed;

            project.Text = s.Project;
            var pct = s.ContextPct;
            if (pct.HasValue)
            {
                ctxBar.Visibility = Visibility.Visible;
                ctxBar.Set(pct.Value, Theme.ForPct(pct.Value));
                ctxText.Text = (s.ContextExact ? "" : "~") + Math.Round(pct.Value).ToString(CultureInfo.InvariantCulture) + "%";
            }
            else
            {
                ctxBar.Visibility = Visibility.Collapsed;
                ctxText.Text = "";
            }
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(s.ModelLabel)) parts.Add(s.ModelLabel);
            double cost = s.Tokens.Cost + s.SubagentTokens.Cost;
            if (cost > 0) parts.Add(Fmt.Money(cost));
            long tok = s.Tokens.Total + s.SubagentTokens.Total;
            if (tok > 0) parts.Add(Fmt.Tokens(tok) + " tok");
            meta.Text = (parts.Count > 0 && pct.HasValue ? "  · " : "") + string.Join("  · ", parts);
            line3.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

            Padding = compact ? new Thickness(10, 8, 10, 8) : new Thickness(12, 11, 12, 11);
            Margin = compact ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 0, 7);

            string tip = s.Cwd + "\n" +
                         s.Host + " · pid " + s.Pid + " · started " + Fmt.Ago(now - s.StartedAt) +
                         (s.Version != null ? " · Claude Code " + s.Version : "") + "\n" +
                         (pct.HasValue ? "Context: " + Fmt.Tokens(s.ContextTokens) + " of " + Fmt.Tokens(s.ContextWindow) + "\n" : "") +
                         "Estimated cost: " + Fmt.Money(s.Tokens.Cost + s.SubagentTokens.Cost) + " at API prices\n" +
                         "Tokens: " + Fmt.Tokens(s.Tokens.Output + s.SubagentTokens.Output) + " output, " +
                         Fmt.Tokens(s.Tokens.CacheRead + s.SubagentTokens.CacheRead) + " cache read, " +
                         (s.Tokens.Requests + s.SubagentTokens.Requests) + " requests" +
                         (s.SubagentTokens.Requests > 0 ? " (incl. subagents)" : "") + "\n" +
                         "Click to bring its window to the front";
            if (tip != tipText) { tipText = tip; ToolTip = tip; }
        }
    }

    // ------------------------------------------------------------------ settings

    sealed class WidgetConfig
    {
        public double? Left, Top;
        public double Scale = 1.0;
        public bool Compact, Topmost = true, Notify = true, HideHintShown;

        public static WidgetConfig Load()
        {
            var c = new WidgetConfig();
            var o = Json.TryParse(Paths.ReadShared(Paths.ConfigFile)) as JObj;
            if (o == null) return c;
            c.Left = Json.D(o, "left");
            c.Top = Json.D(o, "top");
            c.Scale = Math.Max(0.85, Math.Min(1.6, Json.D(o, "scale") ?? 1.0));
            c.Compact = Json.B(o, "compact") ?? false;
            c.Topmost = Json.B(o, "topmost") ?? true;
            c.Notify = Json.B(o, "notify") ?? true;
            c.HideHintShown = Json.B(o, "hide_hint_shown") ?? false;
            return c;
        }

        public void Save()
        {
            try
            {
                var o = new JObj();
                if (Left.HasValue) o["left"] = Math.Round(Left.Value);
                if (Top.HasValue) o["top"] = Math.Round(Top.Value);
                o["scale"] = Math.Round(Scale, 2);
                o["compact"] = Compact;
                o["topmost"] = Topmost;
                o["notify"] = Notify;
                o["hide_hint_shown"] = HideHintShown;
                Paths.WriteAtomic(Paths.ConfigFile, Json.Write(o, true));
            }
            catch (Exception e) { Log.Error("config", e); }
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "ClaudeWidget";

        public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(Name) != null;
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + ExePath + "\"");
                else k.DeleteValue(Name, false);
            }
        }
    }

    // ------------------------------------------------------------------ the window

    sealed class WidgetWindow : Window
    {
        const double BaseWidth = 400;
        static readonly double[] Sizes = { 1.0, 1.15, 1.3 };
        static readonly string[] SizeNames = { "Normal", "Large", "Extra large" };

        readonly WidgetConfig cfg = WidgetConfig.Load();
        readonly EventWaitHandle showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, WidgetProgram.ShowEventName);
        readonly EventWaitHandle quitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, WidgetProgram.QuitEventName);
        readonly Dictionary<int, SessionRow> rows = new Dictionary<int, SessionRow>();
        readonly Dictionary<int, string> lastStatus = new Dictionary<int, string>();
        readonly Dictionary<int, long> busySince = new Dictionary<int, long>();
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        readonly List<WF.ToolStripMenuItem> sizeItems = new List<WF.ToolStripMenuItem>();

        WF.NotifyIcon tray;
        WF.ToolStripMenuItem compactItem, topItem, notifyItem, startupItem;
        TextBlock compactLimits, footer, emptyText, compactGlyph, moreHint;
        ScrollViewer sessionsScroll;
        WrapPanel chipsPanel;
        StackPanel limitsPanel, usagePanel, sessionsPanel;
        FrameworkElement fullOnly, limitsBlock, zoomHost;
        Snapshot last;
        string limitsSig, usageSig, chipsSig;
        int balloonPid;
        bool firstSnapshot = true;
        volatile bool stopping;

        public WidgetWindow()
        {
            Title = "Claude Widget";
            // Opaque window with a hidden frame: Windows 11 draws the rounded corners and the
            // shadow itself. (A transparent layered window would be re-copied in full on every
            // repaint, which made the spinner cost ~5% of a CPU core.)
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            Background = Theme.Window;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            SourceInitialized += delegate { HideFromAltTab(); };
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = cfg.Topmost;
            SizeToContent = SizeToContent.Height;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            Content = BuildUi();
            ApplyScale();
            PlaceWindow();
            ApplyCompact();

            saveTimer.Tick += delegate
            {
                saveTimer.Stop();
                cfg.Left = Left;
                cfg.Top = Top;
                cfg.Save();
            };
            LocationChanged += delegate { saveTimer.Stop(); saveTimer.Start(); FitToScreen(); };
            // Ctrl + mouse wheel resizes the text.
            PreviewMouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                e.Handled = true;
                SetScale(cfg.Scale + (e.Delta > 0 ? 0.05 : -0.05));
            };

            BuildTray();
            Show();
            StartThreads();
        }

        // ---------- layout

        UIElement BuildUi()
        {
            var root = new StackPanel();
            root.Children.Add(BuildHeader());
            root.Children.Add(UI.Divider());

            var full = new StackPanel();
            limitsPanel = new StackPanel { Margin = new Thickness(16, 10, 16, 12) };
            var limitsWrap = new StackPanel();
            limitsWrap.Children.Add(limitsPanel);
            limitsWrap.Children.Add(UI.Divider());
            limitsBlock = limitsWrap;
            full.Children.Add(limitsWrap);
            usagePanel = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
            full.Children.Add(usagePanel);
            full.Children.Add(UI.Divider());
            fullOnly = full;
            root.Children.Add(full);

            sessionsPanel = new StackPanel();
            emptyText = UI.Text("No Claude Code sessions running", 13, Theme.Faint);
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            emptyText.Margin = new Thickness(10, 14, 10, 14);
            var list = new StackPanel { Margin = new Thickness(10, 10, 10, 4) };
            list.Children.Add(emptyText);
            list.Children.Add(sessionsPanel);
            // The list never runs off the bottom of the screen: FitToScreen() caps its height to
            // the room left on the current monitor; it scrolls with the mouse wheel and a hint
            // shows when more sessions are below (a light Windows scrollbar would clash).
            sessionsScroll = new ScrollViewer
            {
                Content = list, MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            sessionsScroll.ScrollChanged += delegate { UpdateMoreHint(); };
            root.Children.Add(sessionsScroll);
            moreHint = UI.Text("↓  more sessions below — scroll", 11.5, Theme.Sub);
            moreHint.HorizontalAlignment = HorizontalAlignment.Center;
            moreHint.Margin = new Thickness(0, 0, 0, 8);
            moreHint.Visibility = Visibility.Collapsed;
            root.Children.Add(moreHint);

            footer = UI.Text("", 11.5, Theme.Faint);
            footer.TextWrapping = TextWrapping.Wrap;
            footer.Margin = new Thickness(16, 0, 16, 12);
            footer.Visibility = Visibility.Collapsed;
            root.Children.Add(footer);

            zoomHost = new Border { Background = Theme.Window, Child = root };
            return zoomHost;
        }

        UIElement BuildHeader()
        {
            var g = new Grid { MinHeight = 50, Background = Theme.Header };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            var logo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 10, 0) };
            logo.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Theme.Busy, Margin = new Thickness(0, 0, 3, 0) });
            logo.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Theme.Wait, Margin = new Thickness(0, 0, 3, 0) });
            logo.Children.Add(new Ellipse { Width = 7, Height = 7, Stroke = Theme.Idle, StrokeThickness = 1.5 });
            g.Children.Add(logo);

            var name = UI.Text("Claude", 16, Theme.Text);
            name.FontFamily = Theme.Display;
            name.FontWeight = FontWeights.SemiBold;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(0, 0, 10, 1);
            Grid.SetColumn(name, 1);
            g.Children.Add(name);

            chipsPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            Grid.SetColumn(chipsPanel, 2);
            g.Children.Add(chipsPanel);

            compactLimits = UI.Text("", 12, Theme.Sub);
            compactLimits.VerticalAlignment = VerticalAlignment.Center;
            compactLimits.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(compactLimits, 3);
            g.Children.Add(compactLimits);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            TextBlock hideGlyph;
            buttons.Children.Add(UI.IconButton("", "Compact / full view", ToggleCompact, out compactGlyph));
            buttons.Children.Add(UI.IconButton("", "Hide (click the tray icon to bring it back)", HideToTray, out hideGlyph));
            Grid.SetColumn(buttons, 4);
            g.Children.Add(buttons);
            return g;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        /// <summary>Tool-window style keeps the widget out of Alt+Tab.</summary>
        void HideFromAltTab()
        {
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }

        void PlaceWindow()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var wa = SystemParameters.WorkArea;
            double left = wa.Right - Width - 12, top = wa.Top + 12;
            if (cfg.Left.HasValue && cfg.Top.HasValue)
            {
                double l = cfg.Left.Value, t = cfg.Top.Value;
                double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
                double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
                if (l > vl - Width / 2 && l < vl + vw - 80 && t > vt - 20 && t < vt + vh - 60) { left = l; top = t; }
            }
            Left = left;
            Top = top;
        }

        void ApplyScale()
        {
            zoom.ScaleX = zoom.ScaleY = cfg.Scale;
            // Only use a transform when actually zoomed: at 100% it would make every spinner
            // frame repaint the whole panel.
            zoomHost.LayoutTransform = Math.Abs(cfg.Scale - 1.0) < 0.001 ? null : zoom;
            Width = Math.Round(BaseWidth * cfg.Scale);
            for (int i = 0; i < sizeItems.Count; i++) sizeItems[i].Checked = Math.Abs(Sizes[i] - cfg.Scale) < 0.01;
        }

        /// <summary>Caps the session list so the window ends above the bottom of the work area
        /// of the monitor it is on.</summary>
        void FitToScreen()
        {
            try
            {
                if (!IsLoaded || sessionsScroll == null) return;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var source = PresentationSource.FromVisual(this);
                if (hwnd == IntPtr.Zero || source == null || source.CompositionTarget == null) return;
                var work = WF.Screen.FromHandle(hwnd).WorkingArea; // device pixels
                double bottom = source.CompositionTarget.TransformFromDevice.Transform(new Point(work.Left, work.Bottom)).Y;
                double rest = ActualHeight - sessionsScroll.ActualHeight * cfg.Scale; // everything but the list
                double room = (bottom - Top - 8 - rest) / cfg.Scale;
                double max = Math.Max(150, Math.Min(620, room));
                if (Math.Abs(sessionsScroll.MaxHeight - max) > 1) sessionsScroll.MaxHeight = max;
                UpdateMoreHint();
            }
            catch (Exception e) { Log.Error("fit", e); }
        }

        void UpdateMoreHint()
        {
            if (sessionsScroll == null || moreHint == null) return;
            bool more = sessionsScroll.ExtentHeight - sessionsScroll.ViewportHeight - sessionsScroll.VerticalOffset > 4;
            var v = more ? Visibility.Visible : Visibility.Collapsed;
            if (moreHint.Visibility != v) moreHint.Visibility = v;
        }

        void SetScale(double scale)
        {
            cfg.Scale = Math.Max(0.85, Math.Min(1.6, Math.Round(scale, 2)));
            ApplyScale();
            cfg.Save();
        }

        // ---------- tray

        void BuildTray()
        {
            tray = new WF.NotifyIcon { Text = "Claude Widget", Visible = true };
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeWidget.app.ico"))
                    tray.Icon = new System.Drawing.Icon(s, WF.SystemInformation.SmallIconSize);
            }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }

            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Show / hide", null, delegate { ToggleVisible(); });

            var sizeMenu = new WF.ToolStripMenuItem("Text size");
            for (int i = 0; i < Sizes.Length; i++)
            {
                var item = new WF.ToolStripMenuItem(SizeNames[i]) { Tag = Sizes[i] };
                item.Click += (s, e) => SetScale((double)((WF.ToolStripMenuItem)s).Tag);
                sizeItems.Add(item);
                sizeMenu.DropDownItems.Add(item);
            }
            sizeMenu.DropDownItems.Add(new WF.ToolStripSeparator());
            sizeMenu.DropDownItems.Add(new WF.ToolStripMenuItem("Tip: Ctrl + mouse wheel over the widget") { Enabled = false });
            ApplyScale();

            compactItem = new WF.ToolStripMenuItem("Compact view") { Checked = cfg.Compact };
            compactItem.Click += delegate { ToggleCompact(); };
            topItem = new WF.ToolStripMenuItem("Always on top") { Checked = cfg.Topmost };
            topItem.Click += delegate
            {
                cfg.Topmost = !cfg.Topmost;
                topItem.Checked = cfg.Topmost;
                Topmost = cfg.Topmost;
                cfg.Save();
            };
            notifyItem = new WF.ToolStripMenuItem("Notify when a session finishes or needs me") { Checked = cfg.Notify };
            notifyItem.Click += delegate
            {
                cfg.Notify = !cfg.Notify;
                notifyItem.Checked = cfg.Notify;
                cfg.Save();
            };
            startupItem = new WF.ToolStripMenuItem("Start with Windows");
            try { startupItem.Checked = Autostart.IsEnabled(); } catch { }
            startupItem.Click += delegate
            {
                try
                {
                    Autostart.Set(!startupItem.Checked);
                    startupItem.Checked = Autostart.IsEnabled();
                }
                catch (Exception e) { Log.Error("autostart", e); }
            };
            menu.Items.Add(sizeMenu);
            menu.Items.Add(compactItem);
            menu.Items.Add(topItem);
            menu.Items.Add(notifyItem);
            menu.Items.Add(startupItem);
            menu.Items.Add("Move to top-right corner", null, delegate
            {
                cfg.Left = null;
                cfg.Top = null;
                PlaceWindow();
                Show();
            });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Quit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ToggleVisible(); };
            tray.BalloonTipClicked += delegate { FocusPid(balloonPid); };
        }

        void ToggleVisible()
        {
            if (IsVisible) Hide();
            else { Show(); Activate(); }
        }

        void HideToTray()
        {
            Hide();
            if (!cfg.HideHintShown)
            {
                cfg.HideHintShown = true;
                cfg.Save();
                tray.ShowBalloonTip(4000, "Claude Widget is still running", "Click the tray icon to show it again.", WF.ToolTipIcon.None);
            }
        }

        void ToggleCompact()
        {
            cfg.Compact = !cfg.Compact;
            cfg.Save();
            if (compactItem != null) compactItem.Checked = cfg.Compact;
            ApplyCompact();
            if (last != null) Render(last);
        }

        void ApplyCompact()
        {
            fullOnly.Visibility = cfg.Compact ? Visibility.Collapsed : Visibility.Visible;
            compactLimits.Visibility = cfg.Compact ? Visibility.Visible : Visibility.Collapsed;
            compactGlyph.Text = cfg.Compact ? "" : "";
        }

        void Quit()
        {
            if (stopping) return;
            stopping = true;
            saveTimer.Stop();
            cfg.Left = Left;
            cfg.Top = Top;
            cfg.Save();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        // ---------- background work

        void StartThreads()
        {
            new Thread(Worker) { IsBackground = true, Name = "collector" }.Start();
            new Thread(() =>
            {
                var handles = new WaitHandle[] { showEvt, quitEvt };
                while (true)
                {
                    int i = WaitHandle.WaitAny(handles);
                    if (i == 0) Dispatcher.BeginInvoke(new Action(() => { Show(); Activate(); }));
                    else { Dispatcher.BeginInvoke(new Action(Quit)); return; }
                }
            }) { IsBackground = true, Name = "signals" }.Start();
        }

        void Worker()
        {
            var collector = new Collector();
            bool first = true;
            while (!stopping)
            {
                try
                {
                    var snap = collector.Collect();
                    Dispatcher.BeginInvoke(new Action(() => Render(snap)));
                    if (first)
                    {
                        // The first pass reads a week of transcripts; give that memory back.
                        first = false;
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect();
                    }
                }
                catch (Exception e) { Log.Error("collect", e); }
                Thread.Sleep(1000);
            }
        }

        // ---------- rendering

        void Render(Snapshot snap)
        {
            if (stopping) return;
            last = snap;
            long now = snap.GeneratedAt;
            RenderChips(snap);
            RenderLimits(snap, now);
            RenderUsage(snap);
            RenderCompactSummary(snap, now);
            RenderSessions(snap, now);
            RenderFooter(snap);
            Dispatcher.BeginInvoke(new Action(FitToScreen), DispatcherPriority.Loaded);
            Notify(snap, now);
            string tip = "Claude · " + CountsLine(snap) + (snap.Today.Cost > 0 ? " · " + Fmt.Money(snap.Today.Cost) + " today" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        static string CountsLine(Snapshot s)
        {
            if (s.Sessions.Count == 0) return "no sessions";
            var parts = new List<string>();
            if (s.Waiting > 0) parts.Add(s.Waiting + " waiting");
            if (s.Busy > 0) parts.Add(s.Busy + " busy");
            if (s.Done > 0) parts.Add(s.Done + " done");
            if (s.Idle > 0) parts.Add(s.Idle + " idle");
            return string.Join(" · ", parts);
        }

        void RenderChips(Snapshot s)
        {
            string sig = s.Sessions.Count + "/" + s.Waiting + "/" + s.Busy + "/" + s.Done + "/" + s.Idle;
            if (sig == chipsSig) return;
            chipsSig = sig;
            chipsPanel.Children.Clear();
            if (s.Sessions.Count == 0) { chipsPanel.Children.Add(UI.Text("no sessions", 12, Theme.Faint)); return; }
            if (s.Waiting > 0) chipsPanel.Children.Add(UI.Pill(s.Waiting + " waiting", Theme.Wait, Theme.WaitSoft));
            if (s.Busy > 0) chipsPanel.Children.Add(UI.Pill(s.Busy + " busy", Theme.Busy, Theme.BusySoft));
            if (s.Done > 0) chipsPanel.Children.Add(UI.Pill(s.Done + " done", Theme.Done, Theme.DoneSoft));
            if (s.Idle > 0) chipsPanel.Children.Add(UI.Pill(s.Idle + " idle", Theme.Sub, Theme.IdleSoft));
        }

        void RenderLimits(Snapshot snap, long now)
        {
            var L = snap.Limits;
            string sig = L == null ? "none|" + snap.StatusLineInstalled
                : Sig(L.FiveHour, now) + "|" + Sig(L.SevenDay, now) + "|" + Fmt.Ago(now - L.CapturedAt);
            if (sig == limitsSig) return;
            limitsSig = sig;
            limitsPanel.Children.Clear();

            // Without the status line there is nothing to show here; the footer explains why.
            limitsBlock.Visibility = L != null || snap.StatusLineInstalled ? Visibility.Visible : Visibility.Collapsed;
            if (L == null)
            {
                var msg = UI.Text("Plan limits appear once a terminal Claude session refreshes its status line.", 12, Theme.Faint);
                msg.TextWrapping = TextWrapping.Wrap;
                limitsPanel.Children.Add(msg);
                return;
            }
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var ago = UI.Text("updated " + Fmt.Ago(now - L.CapturedAt), 11, Theme.Faint);
            DockPanel.SetDock(ago, Dock.Right);
            head.Children.Add(ago);
            head.Children.Add(UI.Caps("PLAN USAGE"));
            limitsPanel.Children.Add(head);
            if (L.FiveHour != null) limitsPanel.Children.Add(LimitRow("5-hour", L.FiveHour, now));
            if (L.SevenDay != null) limitsPanel.Children.Add(LimitRow("Weekly", L.SevenDay, now));
        }

        static string Sig(LimitWindow w, long now)
        {
            return w == null ? "-" : Math.Round(w.UsedPct) + "/" + Fmt.ResetsIn(w.ResetsAt, now);
        }

        static UIElement LimitRow(string label, LimitWindow w, long now)
        {
            var g = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lab = UI.Text(label, 13, Theme.Text2);
            lab.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(lab);

            var bar = new MiniBar(8);
            bar.Set(w.UsedPct, Theme.ForPct(w.UsedPct));
            bar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);

            var pct = UI.Text(Math.Round(w.UsedPct).ToString(CultureInfo.InvariantCulture) + "%", 15, Theme.Text);
            pct.FontFamily = Theme.Display;
            pct.FontWeight = FontWeights.SemiBold;
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pct, 2);
            g.Children.Add(pct);

            var reset = UI.Text("resets " + Fmt.ResetsIn(w.ResetsAt, now), 11.5, Theme.Faint);
            reset.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetRow(reset, 1);
            Grid.SetColumn(reset, 1);
            Grid.SetColumnSpan(reset, 2);
            g.Children.Add(reset);
            return g;
        }

        void RenderUsage(Snapshot snap)
        {
            var t = snap.Today;
            string sig = t.Total + "|" + t.Requests + "|" + Math.Round(t.Cost, 2) + "|" + string.Join(",", snap.Days.Select(d => d.Tokens.Total));
            if (sig == usageSig) return;
            usageSig = sig;
            usagePanel.Children.Clear();

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(UI.Caps("TODAY"));
            var big = new TextBlock { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
            big.Inlines.Add(new Run(Fmt.Money(t.Cost)) { FontSize = 28, FontFamily = Theme.Display, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });
            big.Inlines.Add(new Run("  est. cost") { FontSize = 13, Foreground = Theme.Sub });
            big.ToolTip = string.Format(CultureInfo.InvariantCulture,
                "Estimated at API prices, using {0}.\n" +
                "On a Pro / Max / Team plan this is not what you pay; the plan limits above are what count.\n\n" +
                "Input {1}  ·  Output {2}\nCache write {3}  ·  Cache read {4}\n{5} requests{6}",
                PriceBook.Source, Fmt.Tokens(t.Input), Fmt.Tokens(t.Output), Fmt.Tokens(t.CacheWrite), Fmt.Tokens(t.CacheRead), t.Requests,
                t.CostApprox ? "\nIncludes models missing from the price table (estimated from their family)." : "");
            ToolTipService.SetShowDuration(big, 30000);
            left.Children.Add(big);
            left.Children.Add(UI.Text(Fmt.Tokens(t.Total) + " tokens  ·  " + t.Requests + " requests", 12.5, Theme.Sub));
            g.Children.Add(left);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            right.Children.Add(WeekChart(snap.Days));
            var week = UI.Text("7 days  " + Fmt.Money(snap.Days.Sum(d => d.Tokens.Cost)), 11, Theme.Faint);
            week.HorizontalAlignment = HorizontalAlignment.Center;
            week.Margin = new Thickness(0, 4, 0, 0);
            right.Children.Add(week);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            usagePanel.Children.Add(g);
        }

        /// <summary>Bars show estimated cost per day (tokens when no cost is known).</summary>
        static UIElement WeekChart(List<DayTokens> days)
        {
            bool byCost = days.Any(d => d.Tokens.Cost > 0);
            Func<DayTokens, double> value = d => byCost ? d.Tokens.Cost : d.Tokens.Total;
            double max = Math.Max(1e-9, days.Max(value));
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < days.Count; i++)
            {
                var d = days[i];
                bool today = i == days.Count - 1;
                double v = value(d);
                var col = new StackPanel { Margin = new Thickness(3, 0, 3, 0), Background = Brushes.Transparent };
                var holder = new Grid { Height = 46, Width = 12 };
                double h = v <= 0 ? 3 : Math.Max(4, 46.0 * v / max);
                holder.Children.Add(new Border
                {
                    Height = h, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3),
                    Background = today ? Theme.Busy : (v <= 0 ? Theme.Track : Theme.BarDim),
                });
                col.Children.Add(holder);
                var letter = UI.Text(d.Day.ToString("ddd", CultureInfo.InvariantCulture).Substring(0, 1), 10.5, today ? Theme.Text2 : Theme.Faint);
                letter.HorizontalAlignment = HorizontalAlignment.Center;
                letter.Margin = new Thickness(0, 3, 0, 0);
                if (today) letter.FontWeight = FontWeights.SemiBold;
                col.Children.Add(letter);
                col.ToolTip = d.Day.ToString("ddd d MMM", CultureInfo.InvariantCulture) + " · est. " + Fmt.Money(d.Tokens.Cost) +
                              " · " + Fmt.Tokens(d.Tokens.Total) + " tokens";
                row.Children.Add(col);
            }
            return row;
        }

        /// <summary>The header text shown in compact view: today's cost and the 5-hour limit.</summary>
        void RenderCompactSummary(Snapshot snap, long now)
        {
            var parts = new List<string>();
            if (snap.Today.Cost > 0) parts.Add(Fmt.Money(snap.Today.Cost));
            var L = snap.Limits;
            if (L != null && L.FiveHour != null) parts.Add("5h " + Math.Round(L.FiveHour.UsedPct).ToString(CultureInfo.InvariantCulture) + "%");
            compactLimits.Text = string.Join("  ·  ", parts);
        }

        void RenderSessions(Snapshot snap, long now)
        {
            var ordered = new List<SessionRow>();
            var seen = new HashSet<int>();
            foreach (var s in snap.Sessions)
            {
                SessionRow row;
                if (!rows.TryGetValue(s.Pid, out row))
                {
                    row = new SessionRow(r => FocusRow(r));
                    rows[s.Pid] = row;
                }
                row.Update(s, cfg.Compact, now);
                ordered.Add(row);
                seen.Add(s.Pid);
            }
            foreach (var pid in rows.Keys.Where(p => !seen.Contains(p)).ToList()) rows.Remove(pid);

            bool same = sessionsPanel.Children.Count == ordered.Count;
            for (int i = 0; same && i < ordered.Count; i++) same = sessionsPanel.Children[i] == ordered[i];
            if (!same)
            {
                sessionsPanel.Children.Clear();
                foreach (var r in ordered) sessionsPanel.Children.Add(r);
            }
            emptyText.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RenderFooter(Snapshot snap)
        {
            string msg = null;
            if (!snap.HooksInstalled && !snap.StatusLineInstalled)
                msg = "Live tool names and plan limits are off. Run  claude-widget-cli install  to add them.";
            else if (!snap.HooksInstalled)
                msg = "Hooks are not installed, so live tool names are off.";
            else if (!snap.StatusLineInstalled)
                msg = snap.StatusLineOther != null
                    ? "You have your own status line, so plan limits are off."
                    : "The status line is not installed, so plan limits are off.";
            footer.Text = msg ?? "";
            footer.Visibility = msg == null || cfg.Compact ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- notifications

        void Notify(Snapshot snap, long now)
        {
            var current = new Dictionary<int, string>();
            foreach (var s in snap.Sessions)
            {
                current[s.Pid] = s.Status;
                string prev;
                bool had = lastStatus.TryGetValue(s.Pid, out prev);
                bool working = s.Status != "idle";
                if (working && !busySince.ContainsKey(s.Pid)) busySince[s.Pid] = firstSnapshot ? s.StatusSince : now;

                if (!firstSnapshot && had && prev != s.Status && cfg.Notify)
                {
                    if (s.Status == "waiting" && s.WaitingFor != "dialog open")
                    {
                        Balloon(s, "Needs you · " + s.Title,
                            Capitalize(s.WaitingFor ?? "waiting for input") + " — " + s.Host + " · " + s.Project);
                    }
                    else if (s.Status == "idle")
                    {
                        long start;
                        if (busySince.TryGetValue(s.Pid, out start) && now - start >= 20000)
                            Balloon(s, "Done · " + s.Title,
                                "Finished after " + Fmt.Duration(now - start) + " — " + s.Host + " · " + s.Project);
                    }
                }
                if (!working) busySince.Remove(s.Pid);
            }
            lastStatus.Clear();
            foreach (var kv in current) lastStatus[kv.Key] = kv.Value;
            foreach (var pid in busySince.Keys.Where(p => !current.ContainsKey(p)).ToList()) busySince.Remove(pid);
            firstSnapshot = false;
        }

        static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        void Balloon(SessionInfo s, string title, string text)
        {
            balloonPid = s.Pid;
            if (title.Length > 63) title = title.Substring(0, 62) + "…";
            if (text.Length > 250) text = text.Substring(0, 249) + "…";
            tray.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.None);
        }

        // ---------- focus

        void FocusRow(SessionRow row)
        {
            if (row.Info != null) FocusSession(row.Info);
        }

        void FocusPid(int pid)
        {
            if (last == null) return;
            var s = last.Sessions.FirstOrDefault(x => x.Pid == pid);
            if (s != null) FocusSession(s);
        }

        static void FocusSession(SessionInfo s)
        {
            try { Procs.FocusSession(s.Pid, s.Cwd, new[] { s.AiTitle, s.Title, s.Name }); }
            catch (Exception e) { Log.Error("focus", e); }
        }
    }
}
