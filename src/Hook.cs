// Built twice from the same source (see build.cmd):
//   claude-widget-hook.exe  /target:winexe  called by Claude Code hooks and the status line (never opens a console)
//   claude-widget-cli.exe   /target:exe     snapshot / install / uninstall from a terminal
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

[assembly: AssemblyTitle("Claude Widget helper")]
[assembly: AssemblyProduct("Claude Widget")]
[assembly: AssemblyVersion("0.1.0.0")]

namespace ClaudeWidget
{
    static class HookProgram
    {
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                switch (mode)
                {
                    case "hook": Hook(ReadStdin()); return 0;
                    case "statusline": StatusLine(ReadStdin()); return 0;
                    case "snapshot": return PrintSnapshot(args);
                    case "windows": return PrintWindows();
                    case "watch": return Watch(args);
                    case "install": return Installer.Install(Console.Out, !args.Contains("--no-start"));
                    case "uninstall": return Installer.Uninstall(Console.Out);
                    default: Usage(); return mode.Length == 0 ? 0 : 2;
                }
            }
            catch (Exception e)
            {
                Log.Error(mode, e);
                if (mode == "hook" || mode == "statusline") return 0; // never disturb Claude
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine("claude-widget-cli snapshot [--json]   show what the widget sees right now");
            Console.WriteLine("claude-widget-cli windows              show which window a click on each session would bring forward");
            Console.WriteLine("claude-widget-cli watch [seconds]      keep one collector running (like the widget) and print every change");
            Console.WriteLine("claude-widget-cli install [--no-start] copy to ~/.claude/tools/claude-widget, add hooks + status line, Start Menu shortcut");
            Console.WriteLine("claude-widget-cli uninstall            remove hooks, status line, shortcut and autostart");
        }

        static string ReadStdin()
        {
            using (var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                return r.ReadToEnd();
        }

        static void WriteStdout(string s)
        {
            var bytes = new UTF8Encoding(false).GetBytes(s + "\n");
            using (var o = Console.OpenStandardOutput()) o.Write(bytes, 0, bytes.Length);
        }

        static string Pct(double v) { return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"; }

        /// <summary>Hook events: track which tool each session is running. Prints nothing
        /// (stdout of some hooks is fed back to Claude).</summary>
        static void Hook(string input)
        {
            var o = Json.TryParse(input) as JObj;
            if (o == null) return;
            string ev = Json.S(o, "hook_event_name") ?? "";
            string sid = Json.S(o, "session_id");
            if (string.IsNullOrEmpty(sid)) return;
            switch (ev)
            {
                case "PreToolUse":
                    string tool = Json.S(o, "tool_name") ?? "";
                    LiveTools.Start(sid, Json.S(o, "tool_use_id"), new ToolRun
                    {
                        Tool = tool,
                        Detail = LiveTools.Describe(tool, Json.O(o, "tool_input")),
                        Since = Paths.NowMs(),
                    });
                    break;
                case "PostToolUse":
                case "PostToolUseFailure":
                    LiveTools.End(sid, Json.S(o, "tool_use_id"));
                    break;
                case "UserPromptSubmit":
                case "Stop":
                    LiveTools.Clear(sid);
                    break;
                case "SessionEnd":
                    LiveTools.EndSession(sid);
                    break;
            }
        }

        /// <summary>Status line: save what Claude sends (plan limits, context size) for the
        /// widget, then print a compact line for the terminal footer.</summary>
        static void StatusLine(string input)
        {
            var o = Json.TryParse(input) as JObj;
            if (o == null) return;
            long now = Paths.NowMs();
            string sid = Json.S(o, "session_id");
            o["_captured_at"] = now;
            if (!string.IsNullOrEmpty(sid))
                Paths.WriteAtomic(Path.Combine(Paths.StatuslineDir, LiveTools.Safe(sid) + ".json"), Json.Write(o, false));

            var rl = Json.O(o, "rate_limits");
            var five = Json.O(rl, "five_hour");
            var week = Json.O(rl, "seven_day");
            if (five != null || week != null)
            {
                var lim = new JObj();
                lim["captured_at"] = now;
                lim["session_id"] = sid;
                lim["five_hour"] = five;
                lim["seven_day"] = week;
                Paths.WriteAtomic(Paths.LimitsFile, Json.Write(lim, true));
            }

            var model = Json.O(o, "model");
            var cw = Json.O(o, "context_window");
            string modelId = Fmt.BaseModel(Json.S(model, "id"));
            long? size = Json.L(cw, "context_window_size");
            if (!string.IsNullOrEmpty(modelId) && size.HasValue && size.Value > 0)
            {
                var models = (Json.TryParse(Paths.ReadShared(Paths.ModelsFile)) as JObj) ?? new JObj();
                if (Json.L(models, modelId) != size.Value)
                {
                    models[modelId] = size.Value;
                    Paths.WriteAtomic(Paths.ModelsFile, Json.Write(models, true));
                }
            }

            var parts = new List<string>();
            string display = Json.S(model, "display_name");
            if (!string.IsNullOrWhiteSpace(display)) parts.Add(display);
            double? used = Json.D(cw, "used_percentage");
            if (used.HasValue) parts.Add("ctx " + Pct(used.Value));
            double? f = Json.D(five, "used_percentage");
            if (f.HasValue) parts.Add("5h " + Pct(f.Value));
            double? w = Json.D(week, "used_percentage");
            if (w.HasValue) parts.Add("7d " + Pct(w.Value));
            WriteStdout(string.Join(" · ", parts));
        }

        /// <summary>Same incremental collector the widget uses; prints a line whenever a
        /// session's state, title or last prompt changes.</summary>
        static int Watch(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            int seconds = 60, parsed;
            if (args.Length > 1 && int.TryParse(args[1], out parsed)) seconds = parsed;
            var collector = new Collector();
            var last = new Dictionary<int, string>();
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                var snap = collector.Collect();
                var seen = new HashSet<int>();
                foreach (var s in snap.Sessions)
                {
                    seen.Add(s.Pid);
                    string line = s.State + " | " + s.Title + " | prompt: " + (s.LastPrompt == null ? "-" : Fmt.OneLine(s.LastPrompt, 50));
                    string prev;
                    if (!last.TryGetValue(s.Pid, out prev) || prev != line)
                    {
                        Console.WriteLine("{0:HH:mm:ss} pid {1,-6} {2}", DateTime.Now, s.Pid, line);
                        last[s.Pid] = line;
                    }
                }
                foreach (var pid in last.Keys.Where(p => !seen.Contains(p)).ToList())
                {
                    Console.WriteLine("{0:HH:mm:ss} pid {1,-6} gone", DateTime.Now, pid);
                    last.Remove(pid);
                }
                System.Threading.Thread.Sleep(1000);
            }
            return 0;
        }

        static int PrintWindows()
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            var collector = new Collector();
            collector.Collect();
            var started = DateTime.UtcNow;
            var snap = collector.Collect();
            Console.WriteLine("(an incremental refresh took {0} ms)", (int)(DateTime.UtcNow - started).TotalMilliseconds);
            foreach (var s in snap.Sessions)
            {
                var w = Procs.FindSessionWindow(s.Pid, s.Cwd, new[] { s.AiTitle, s.Title, s.Name });
                Console.WriteLine("{0,-40} {1,-9} -> {2}", Fmt.OneLine(s.Title, 38), s.Host, w == null ? "(no window found)" : "\"" + w.Title + "\"");
            }
            return 0;
        }

        static int PrintSnapshot(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            var started = DateTime.UtcNow;
            var snap = new Collector().Collect();
            if (args.Contains("--json"))
            {
                Console.WriteLine(Json.Write(snap.ToJson(), true));
                return 0;
            }
            long now = snap.GeneratedAt;
            Console.WriteLine("Sessions: {0} waiting, {1} busy, {2} done recently, {3} idle   (collected in {4} ms)",
                snap.Waiting, snap.Busy, snap.Done, snap.Idle, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            foreach (var s in snap.Sessions)
            {
                Console.WriteLine();
                Console.WriteLine("  [{0}] {1}", s.State.ToUpperInvariant(), s.Title);
                Console.WriteLine("      {0} · {1} · pid {2} · {3} for {4}", s.Host, s.Project, s.Pid, s.Status, Fmt.Duration(now - s.StatusSince));
                if (s.WaitingFor != null) Console.WriteLine("      waiting for: " + s.WaitingFor);
                if (s.CurrentTool != null)
                    Console.WriteLine("      running: {0} {1}{2}", LiveTools.Label(s.CurrentTool.Tool), s.CurrentTool.Detail,
                        s.RunningTools > 1 ? " (+" + (s.RunningTools - 1) + " more)" : "");
                var pct = s.ContextPct;
                Console.WriteLine("      {0} · context {1} of {2}{3} · tokens {4} (+{5} in subagents) in {6} requests · est. cost {7}",
                    s.ModelLabel ?? "model ?",
                    s.ContextTokens >= 0 ? Fmt.Tokens(s.ContextTokens) : "?",
                    s.ContextWindow > 0 ? Fmt.Tokens(s.ContextWindow) : "?",
                    pct.HasValue ? " (" + (s.ContextExact ? "" : "~") + Pct(pct.Value) + ")" : "",
                    Fmt.Tokens(s.Tokens.Total), Fmt.Tokens(s.SubagentTokens.Total), s.Tokens.Requests + s.SubagentTokens.Requests,
                    Fmt.Money(s.Tokens.Cost + s.SubagentTokens.Cost));
                if (!string.IsNullOrEmpty(s.LastPrompt)) Console.WriteLine("      last prompt: " + Fmt.OneLine(s.LastPrompt, 100));
            }
            var t = snap.Today;
            Console.WriteLine();
            Console.WriteLine("Today: est. {0} · {1} tokens in {2} requests (input {3}, output {4}, cache write {5}, cache read {6})",
                Fmt.Money(t.Cost), Fmt.Tokens(t.Total), t.Requests, Fmt.Tokens(t.Input), Fmt.Tokens(t.Output), Fmt.Tokens(t.CacheWrite), Fmt.Tokens(t.CacheRead));
            Console.WriteLine("Last 7 days: est. {0} · " + string.Join("  ", snap.Days.Select(d => d.Day.ToString("ddd", CultureInfo.InvariantCulture) + " " + Fmt.Money(d.Tokens.Cost))),
                Fmt.Money(snap.Days.Sum(d => d.Tokens.Cost)));
            Console.WriteLine("Prices: " + PriceBook.Source + (snap.Days.Any(d => d.Tokens.CostApprox) ? " (some models not in the table were estimated)" : ""));
            if (snap.Limits != null)
            {
                if (snap.Limits.FiveHour != null) Console.WriteLine("5-hour limit: {0} used, resets {1}", Pct(snap.Limits.FiveHour.UsedPct), Fmt.ResetsIn(snap.Limits.FiveHour.ResetsAt, now));
                if (snap.Limits.SevenDay != null) Console.WriteLine("Weekly limit: {0} used, resets {1}", Pct(snap.Limits.SevenDay.UsedPct), Fmt.ResetsIn(snap.Limits.SevenDay.ResetsAt, now));
                Console.WriteLine("(limits captured " + Fmt.Ago(now - snap.Limits.CapturedAt) + ")");
            }
            else Console.WriteLine("Plan limits: none captured yet (they come from the status line)");
            Console.WriteLine("Setup: hooks {0}, status line {1}", snap.HooksInstalled ? "installed" : "not installed",
                snap.StatusLineInstalled ? "installed" : (snap.StatusLineOther != null ? "custom (" + snap.StatusLineOther + ")" : "not installed"));
            return 0;
        }
    }
}
