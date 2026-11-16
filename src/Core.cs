// Shared code for the Claude Widget executables.
// Built with the C# 5 compiler that ships with Windows (.NET Framework 4.x), see build.cmd.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClaudeWidget
{
    // ------------------------------------------------------------------ JSON

    /// <summary>JSON object that keeps key order, so settings.json round-trips cleanly.</summary>
    public sealed class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        readonly List<string> keys = new List<string>();
        readonly Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);

        public object this[string key]
        {
            get { object v; return map.TryGetValue(key, out v) ? v : null; }
            set { if (!map.ContainsKey(key)) keys.Add(key); map[key] = value; }
        }

        public bool Has(string key) { return map.ContainsKey(key); }
        public int Count { get { return keys.Count; } }

        public bool Remove(string key)
        {
            if (!map.Remove(key)) return false;
            keys.Remove(key);
            return true;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var k in keys) yield return new KeyValuePair<string, object>(k, map[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    /// <summary>Small JSON reader/writer. Objects -> JObj, arrays -> List&lt;object&gt;,
    /// integers -> long, other numbers -> double. Keys listed in skipKeys are
    /// skipped without being parsed (used to jump over large message content).</summary>
    public static class Json
    {
        public static object Parse(string s) { return Parse(s, null); }

        public static object Parse(string s, ICollection<string> skipKeys)
        {
            var p = new Parser(s, skipKeys);
            p.Ws();
            var v = p.Value();
            p.Ws();
            return v;
        }

        public static object TryParse(string s) { return TryParse(s, null); }

        public static object TryParse(string s, ICollection<string> skipKeys)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return Parse(s, skipKeys); } catch { return null; }
        }

        sealed class Parser
        {
            readonly string s;
            readonly ICollection<string> skip;
            int i;

            public Parser(string s, ICollection<string> skip) { this.s = s; this.skip = skip; }

            Exception Err() { return new FormatException("Invalid JSON near position " + i); }

            public void Ws()
            {
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') i++;
                    else break;
                }
            }

            public object Value()
            {
                if (i >= s.Length) throw Err();
                char c = s[i];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return Num();
            }

            void Expect(string w)
            {
                if (i + w.Length > s.Length || string.CompareOrdinal(s, i, w, 0, w.Length) != 0) throw Err();
                i += w.Length;
            }

            JObj Obj()
            {
                var o = new JObj();
                i++;
                Ws();
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws();
                    if (i >= s.Length || s[i] != '"') throw Err();
                    string k = Str();
                    Ws();
                    if (i >= s.Length || s[i] != ':') throw Err();
                    i++;
                    Ws();
                    if (skip != null && skip.Contains(k)) { SkipValue(); o[k] = null; }
                    else o[k] = Value();
                    Ws();
                    if (i >= s.Length) throw Err();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw Err();
                }
            }

            List<object> Arr()
            {
                var a = new List<object>();
                i++;
                Ws();
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    Ws();
                    a.Add(Value());
                    Ws();
                    if (i >= s.Length) throw Err();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw Err();
                }
            }

            string Str()
            {
                i++;
                StringBuilder sb = null;
                int start = i;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '"')
                    {
                        string r = sb == null ? s.Substring(start, i - start) : sb.Append(s, start, i - start).ToString();
                        i++;
                        return r;
                    }
                    if (c == '\\')
                    {
                        if (sb == null) sb = new StringBuilder();
                        sb.Append(s, start, i - start);
                        i++;
                        if (i >= s.Length) throw Err();
                        char e = s[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > s.Length) throw Err();
                                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                                break;
                            default: throw Err();
                        }
                        start = i;
                        continue;
                    }
                    i++;
                }
                throw Err();
            }

            void SkipString()
            {
                i++;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '\\') { i += 2; continue; }
                    i++;
                    if (c == '"') return;
                }
                throw Err();
            }

            void SkipValue()
            {
                if (i >= s.Length) throw Err();
                char c = s[i];
                if (c == '"') { SkipString(); return; }
                if (c == '{' || c == '[')
                {
                    int depth = 0;
                    while (i < s.Length)
                    {
                        char d = s[i];
                        if (d == '"') { SkipString(); continue; }
                        if (d == '{' || d == '[') depth++;
                        else if (d == '}' || d == ']')
                        {
                            depth--;
                            if (depth == 0) { i++; return; }
                        }
                        i++;
                    }
                    throw Err();
                }
                Value();
            }

            object Num()
            {
                int start = i;
                bool frac = false;
                if (i < s.Length && s[i] == '-') i++;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c >= '0' && c <= '9') i++;
                    else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { frac = true; i++; }
                    else break;
                }
                string t = s.Substring(start, i - start);
                long l;
                if (!frac && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw Err();
            }
        }

        // ---- writing

        public static string Write(object v, bool pretty)
        {
            var sb = new StringBuilder();
            WriteValue(sb, v, pretty, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int n) { sb.Append('\n'); sb.Append(' ', n * 2); }

        static IEnumerable<KeyValuePair<string, object>> Pairs(object v)
        {
            var jo = v as JObj;
            if (jo != null) return jo;
            var dict = v as IDictionary;
            if (dict == null) return null;
            var list = new List<KeyValuePair<string, object>>();
            foreach (DictionaryEntry e in dict) list.Add(new KeyValuePair<string, object>(Convert.ToString(e.Key, CultureInfo.InvariantCulture), e.Value));
            return list;
        }

        static void WriteValue(StringBuilder sb, object v, bool pretty, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            var str = v as string;
            if (str != null) { WriteString(sb, str); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var pairs = Pairs(v);
            if (pairs != null)
            {
                var items = pairs.ToList();
                if (items.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (int k = 0; k < items.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    if (pretty) Indent(sb, depth + 1);
                    WriteString(sb, items[k].Key);
                    sb.Append(pretty ? ": " : ":");
                    WriteValue(sb, items[k].Value, pretty, depth + 1);
                }
                if (pretty) Indent(sb, depth);
                sb.Append('}');
                return;
            }
            var seq = v as IEnumerable;
            if (seq != null)
            {
                var items = seq.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int k = 0; k < items.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    if (pretty) Indent(sb, depth + 1);
                    WriteValue(sb, items[k], pretty, depth + 1);
                }
                if (pretty) Indent(sb, depth);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- accessors (all null-safe)

        public static JObj O(object o, string k) { var j = o as JObj; return j == null ? null : j[k] as JObj; }
        public static string S(object o, string k) { var j = o as JObj; return j == null ? null : j[k] as string; }
        public static List<object> A(object o, string k) { var j = o as JObj; return j == null ? null : j[k] as List<object>; }

        public static long? L(object o, string k)
        {
            var j = o as JObj;
            if (j == null) return null;
            var v = j[k];
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            var s = v as string;
            long r;
            if (s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return null;
        }

        public static double? D(object o, string k)
        {
            var j = o as JObj;
            if (j == null) return null;
            var v = j[k];
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            return null;
        }

        public static bool? B(object o, string k)
        {
            var j = o as JObj;
            if (j == null) return null;
            var v = j[k];
            if (v is bool) return (bool)v;
            return null;
        }
    }

    // ------------------------------------------------------------------ paths

    public static class Paths
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }

        public static string ClaudeDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                return string.IsNullOrWhiteSpace(env) ? Path.Combine(Home, ".claude") : env;
            }
        }

        public static string SessionsDir { get { return Path.Combine(ClaudeDir, "sessions"); } }
        public static string ProjectsDir { get { return Path.Combine(ClaudeDir, "projects"); } }
        public static string SettingsFile { get { return Path.Combine(ClaudeDir, "settings.json"); } }

        /// <summary>Everything the widget writes: ~/.claude/widget</summary>
        public static string WidgetDir { get { return Path.Combine(ClaudeDir, "widget"); } }
        public static string LiveDir { get { return Path.Combine(WidgetDir, "live"); } }
        public static string StatuslineDir { get { return Path.Combine(WidgetDir, "statusline"); } }
        public static string LimitsFile { get { return Path.Combine(WidgetDir, "limits.json"); } }
        public static string ModelsFile { get { return Path.Combine(WidgetDir, "models.json"); } }
        public static string PricesFile { get { return Path.Combine(WidgetDir, "prices.json"); } }
        public static string ConfigFile { get { return Path.Combine(WidgetDir, "widget-config.json"); } }
        public static string LogFile { get { return Path.Combine(WidgetDir, "widget.log"); } }

        /// <summary>Installed copies of the executables: ~/.claude/tools/claude-widget</summary>
        public static string ToolsDir { get { return Path.Combine(ClaudeDir, "tools", "claude-widget"); } }
        public static string InstalledHookExe { get { return Path.Combine(ToolsDir, "claude-widget-hook.exe"); } }
        public static string InstalledWidgetExe { get { return Path.Combine(ToolsDir, "ClaudeWidget.exe"); } }

        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }

        /// <summary>Reads a file other processes may be writing. Null if missing or locked.</summary>
        public static string ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Utf8, true))
                    return r.ReadToEnd();
            }
            catch { return null; }
        }

        /// <summary>Writes via temp file + replace so readers never see half a file.</summary>
        public static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp" + Process.GetCurrentProcess().Id;
            File.WriteAllText(tmp, text, Utf8);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { File.Copy(tmp, path, true); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    public static class Log
    {
        public static void Info(string msg)
        {
            try
            {
                Directory.CreateDirectory(Paths.WidgetDir);
                File.AppendAllText(Paths.LogFile, DateTime.Now.ToString("s") + " [info] " + msg + "\n", Paths.Utf8);
            }
            catch { }
        }

        public static void Error(string where, Exception e)
        {
            try
            {
                Directory.CreateDirectory(Paths.WidgetDir);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(Paths.LogFile, DateTime.Now.ToString("s") + " [" + where + "] " + e + "\n", Paths.Utf8);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Win32

    static class Native
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint STILL_ACTIVE = 259;
        public const uint TH32CS_SNAPPROCESS = 0x2;
        public const uint GW_OWNER = 4;
        public const int SW_RESTORE = 9;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll")]
        public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }

    public sealed class ProcEntry
    {
        public uint Pid, Ppid;
        public string Name;
    }

    public sealed class WinInfo
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string Title;
    }

    public static class Procs
    {
        /// <summary>Creation time (FILETIME ticks) if the process is running, else null.</summary>
        public static long? ProcessStart(uint pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                uint code;
                if (!Native.GetExitCodeProcess(h, out code) || code != Native.STILL_ACTIVE) return null;
                long c, e, k, u;
                if (!Native.GetProcessTimes(h, out c, out e, out k, out u)) return null;
                return c;
            }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>True when pid is alive and (if known) started at procStart.
        /// Guards against Windows reusing the pid of a crashed session.</summary>
        public static bool IsSameProcess(uint pid, long? procStart)
        {
            long? actual = ProcessStart(pid);
            if (!actual.HasValue) return false;
            if (!procStart.HasValue) return true;
            return Math.Abs(actual.Value - procStart.Value) <= 10000000L; // 1 s slack
        }

        public static Dictionary<uint, ProcEntry> Table()
        {
            var map = new Dictionary<uint, ProcEntry>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new Native.PROCESSENTRY32W();
                e.dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32W));
                if (Native.Process32FirstW(snap, ref e))
                {
                    do
                    {
                        map[e.th32ProcessID] = new ProcEntry { Pid = e.th32ProcessID, Ppid = e.th32ParentProcessID, Name = e.szExeFile ?? "" };
                    } while (Native.Process32NextW(snap, ref e));
                }
            }
            finally { Native.CloseHandle(snap); }
            return map;
        }

        public static List<uint> Ancestors(uint pid, Dictionary<uint, ProcEntry> table)
        {
            var list = new List<uint>();
            var seen = new HashSet<uint> { pid };
            uint cur = pid;
            ProcEntry entry;
            while (table.TryGetValue(cur, out entry) && list.Count < 12)
            {
                uint parent = entry.Ppid;
                if (parent == 0 || !seen.Add(parent) || !table.ContainsKey(parent)) break;
                list.Add(parent);
                cur = parent;
            }
            return list;
        }

        static string NameOf(uint pid, Dictionary<uint, ProcEntry> table)
        {
            ProcEntry e;
            return table.TryGetValue(pid, out e) ? e.Name.ToLowerInvariant() : "";
        }

        /// <summary>Friendly name of the app that hosts a session: "Cursor", "Terminal", ...</summary>
        public static string HostLabel(uint pid, string entrypoint, Dictionary<uint, ProcEntry> table)
        {
            foreach (var a in Ancestors(pid, table))
            {
                switch (NameOf(a, table))
                {
                    case "cursor.exe": return "Cursor";
                    case "code.exe":
                    case "code - insiders.exe": return "VS Code";
                    case "windsurf.exe": return "Windsurf";
                    case "windowsterminal.exe": return "Terminal";
                    case "wezterm-gui.exe": return "WezTerm";
                    case "alacritty.exe": return "Alacritty";
                    case "idea64.exe":
                    case "pycharm64.exe":
                    case "webstorm64.exe":
                    case "rider64.exe": return "JetBrains";
                }
            }
            if (entrypoint == "cli") return "Terminal";
            if (entrypoint == "claude-vscode") return "IDE";
            if (string.IsNullOrEmpty(entrypoint)) return "Claude";
            if (entrypoint.StartsWith("sdk", StringComparison.Ordinal)) return "SDK";
            return entrypoint;
        }

        public static List<WinInfo> TopWindows()
        {
            var list = new List<WinInfo>();
            Native.EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                int len = Native.GetWindowTextLengthW(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                Native.GetWindowTextW(h, sb, sb.Capacity);
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                list.Add(new WinInfo { Hwnd = h, Pid = pid, Title = sb.ToString() });
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        public static bool FocusWindow(IntPtr h)
        {
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            uint ignored;
            uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out ignored);
            uint me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            Native.BringWindowToTop(h);
            bool ok = Native.SetForegroundWindow(h);
            if (attached) Native.AttachThreadInput(me, fgThread, false);
            return ok;
        }

        /// <summary>Brings the window hosting a Claude session to the front.
        /// IDE sessions: the IDE window whose title names the folder ("file - folder - Cursor").
        /// Terminal sessions: Claude sets the terminal title to the session's AI title.</summary>
        public static bool FocusSession(int pid, string cwd, IEnumerable<string> titleHints)
        {
            var w = FindSessionWindow(pid, cwd, titleHints);
            return w != null && FocusWindow(w.Hwnd);
        }

        /// <summary>The window FocusSession would bring forward, or null.</summary>
        public static WinInfo FindSessionWindow(int pid, string cwd, IEnumerable<string> titleHints)
        {
            var table = Table();
            var wins = TopWindows();
            string folder = "";
            try { folder = (Path.GetFileName((cwd ?? "").TrimEnd('\\', '/')) ?? "").ToLowerInvariant(); } catch { }
            var chain = Ancestors((uint)pid, table);

            string[] ides = { "cursor.exe", "code.exe", "code - insiders.exe", "windsurf.exe" };
            string ide = chain.Select(p => NameOf(p, table)).FirstOrDefault(n => ides.Contains(n));
            if (ide != null)
            {
                var candidates = wins.Where(w => NameOf(w.Pid, table) == ide).ToList();
                var byFolder = candidates.FirstOrDefault(w => folder.Length > 0 &&
                    w.Title.ToLowerInvariant().Split(new[] { " - " }, StringSplitOptions.None).Any(part => part.Trim() == folder));
                var pick = byFolder ?? candidates.FirstOrDefault();
                if (pick != null) return pick;
            }

            foreach (var raw in titleHints ?? Enumerable.Empty<string>())
            {
                if (raw == null) continue;
                string hint = raw.Trim().ToLowerInvariant();
                if (hint.Length < 3) continue;
                var w = wins.FirstOrDefault(x => x.Title.ToLowerInvariant().Contains(hint));
                if (w != null) return w;
            }

            foreach (var a in chain)
            {
                var w = wins.FirstOrDefault(x => x.Pid == a);
                if (w != null) return w;
            }

            return wins.FirstOrDefault(x => NameOf(x.Pid, table) == "windowsterminal.exe");
        }
    }

    // ------------------------------------------------------------------ transcripts

    public sealed class Tokens
    {
        public long Input, Output, CacheWrite, CacheRead, Requests;
        /// <summary>Estimated cost in USD at API prices.</summary>
        public double Cost;
        /// <summary>True if some of the cost used a guessed price (model not in the price table).</summary>
        public bool CostApprox;

        public long Total { get { return Input + Output + CacheWrite + CacheRead; } }

        public void Add(Tokens o)
        {
            if (o == null) return;
            Input += o.Input; Output += o.Output; CacheWrite += o.CacheWrite; CacheRead += o.CacheRead; Requests += o.Requests;
            Cost += o.Cost;
            CostApprox |= o.CostApprox;
        }

        public void MaxWith(Tokens o)
        {
            Input = Math.Max(Input, o.Input); Output = Math.Max(Output, o.Output);
            CacheWrite = Math.Max(CacheWrite, o.CacheWrite); CacheRead = Math.Max(CacheRead, o.CacheRead);
            Cost = Math.Max(Cost, o.Cost);
            CostApprox |= o.CostApprox;
        }

        public JObj ToJson()
        {
            var o = new JObj();
            o["input"] = Input; o["output"] = Output; o["cache_write"] = CacheWrite; o["cache_read"] = CacheRead;
            o["total"] = Total; o["requests"] = Requests;
            o["cost_usd"] = Math.Round(Cost, 4); o["cost_approx"] = CostApprox;
            return o;
        }
    }

    // ------------------------------------------------------------------ prices

    /// <summary>USD per million tokens (web search: per request).</summary>
    public sealed class Price
    {
        public double Input, Output, Write5m, Write1h, Read, WebSearch;
        /// <summary>Optional higher tier for prompts above LongAbove tokens.</summary>
        public long LongAbove;
        public Price Long;

        public Price Times(double f)
        {
            return new Price { Input = Input * f, Output = Output * f, Write5m = Write5m * f, Write1h = Write1h * f, Read = Read * f, WebSearch = WebSearch };
        }
    }

    /// <summary>Per-model API prices, the same table Claude Code uses for /cost and the status
    /// line's cost. Read from the installed Claude Code's baked model catalog (cached per version
    /// in ~/.claude/widget/prices.json), else the copy below taken from Claude Code 2.1.325.
    /// Cost formula matches Claude Code: 1-hour cache writes priced separately, x1.1 for US-only
    /// inference, plus web searches. Verified within 0.01% of Claude Code's own session cost.</summary>
    public static class PriceBook
    {
        const int CacheFormat = 1;
        static readonly object Gate = new object();
        static Dictionary<string, Price> tiers;
        static List<KeyValuePair<string, string>> models; // model id -> tier, catalog order
        static string loadedVersion;
        static long lastUpdateCheck;
        public static string Source = "";

        static Price P(double i, double o, double w5, double w1, double r)
        {
            return new Price { Input = i, Output = o, Write5m = w5, Write1h = w1, Read = r, WebSearch = 0.01 };
        }

        static void LoadBaked()
        {
            tiers = new Dictionary<string, Price>
            {
                { "tier_2_10", P(2, 10, 2.5, 4, 0.2) },
                { "tier_2_10_cache_read_0_10", P(2, 10, 2.5, 4, 0.1) },
                { "tier_3_15", P(3, 15, 3.75, 6, 0.3) },
                { "tier_5_25", P(5, 25, 6.25, 10, 0.5) },
                { "tier_15_75", P(15, 75, 18.75, 30, 1.5) },
                { "tier_10_50", P(10, 50, 12.5, 20, 1) },
                { "tier_10_50_cache_read_0_25", P(10, 50, 12.5, 20, 0.25) },
                { "tier_4_20_cache_read_0_20", P(4, 20, 5, 8, 0.2) },
                { "tier_8_40_cache_read_0_20", P(8, 40, 10, 16, 0.2) },
                { "haiku_35", P(0.8, 4, 1, 1.6, 0.08) },
                { "haiku_45", P(1, 5, 1.25, 2, 0.1) },
            };
            var h55 = P(0.1, 0.5, 0.125, 0.2, 0.01);
            h55.LongAbove = 100000;
            h55.Long = P(0.5, 2.5, 0.625, 1, 0.05);
            tiers["haiku_55"] = h55;
            models = new List<KeyValuePair<string, string>>();
            foreach (var pair in new[]
            {
                "claude-3-5-haiku=haiku_35", "claude-haiku-4-5=haiku_45", "claude-haiku-5-5=haiku_55",
                "claude-3-5-sonnet=tier_3_15", "claude-3-7-sonnet=tier_3_15", "claude-sonnet-4-0=tier_3_15",
                "claude-sonnet-4-5=tier_3_15", "claude-sonnet-4-6=tier_3_15", "claude-sonnet-5=tier_2_10",
                "claude-sonnet-5-5=tier_2_10_cache_read_0_10", "claude-opus-4-0=tier_15_75", "claude-opus-4-1=tier_15_75",
                "claude-opus-4-5=tier_5_25", "claude-opus-4-6=tier_5_25", "claude-opus-4-7=tier_5_25",
                "claude-opus-4-8=tier_5_25", "claude-opus-5=tier_5_25", "claude-opus-5-5=tier_4_20_cache_read_0_20",
                "claude-fable-5=tier_10_50", "claude-fable-5-1=tier_10_50_cache_read_0_25", "claude-fable-5-2=tier_8_40_cache_read_0_20",
                "claude-mythos-5=tier_10_50", "claude-mythos-5-1=tier_10_50_cache_read_0_25", "claude-mythos-5-2=tier_8_40_cache_read_0_20",
            })
            {
                int eq = pair.IndexOf('=');
                models.Add(new KeyValuePair<string, string>(pair.Substring(0, eq), pair.Substring(eq + 1)));
            }
            Source = "built-in price table (Claude Code 2.1.325)";
        }

        /// <summary>Fast mode prices are code, not catalog data, in Claude Code; copied from 2.1.325.</summary>
        static Price FastPrice(string model, Price standard)
        {
            switch (model)
            {
                case "claude-opus-5-5": return P(8, 40, 10, 16, 0.4);
                case "claude-fable-5-2":
                case "claude-mythos-5-2": return P(16, 80, 20, 32, 0.4);
                case "claude-opus-4-8":
                case "claude-opus-5": return P(10, 50, 12.5, 20, 1);
            }
            return model.Contains("opus") ? P(30, 150, 37.5, 60, 3) : standard.Times(2);
        }

        static void Ensure()
        {
            if (tiers != null) return;
            try { Load(); }
            catch (Exception e) { Log.Error("prices", e); }
            if (tiers == null || models == null) LoadBaked();
        }

        /// <summary>Re-read prices if Claude Code updated itself (checked every 10 minutes).</summary>
        public static void CheckForUpdate()
        {
            lock (Gate)
            {
                long now = Paths.NowMs();
                if (now - lastUpdateCheck < 10 * 60 * 1000) return;
                lastUpdateCheck = now;
                string version;
                FindClaudeBinary(out version);
                if (tiers != null && version != null && version != loadedVersion) { tiers = null; models = null; }
                Ensure();
            }
        }

        static void Load()
        {
            string version;
            string exe = FindClaudeBinary(out version);
            long size = 0;
            if (exe != null) { try { size = new FileInfo(exe).Length; } catch { } }
            var cache = Json.TryParse(Paths.ReadShared(Paths.PricesFile)) as JObj;
            bool cacheMatches = cache != null && Json.L(cache, "format") == CacheFormat &&
                                Json.S(cache, "claude_version") == version && Json.L(cache, "binary_size") == size;
            if (cacheMatches && FromJson(cache)) { loadedVersion = version; Source = "Claude Code " + version + " price table"; return; }
            if (exe != null)
            {
                string text = ReadCatalogText(exe);
                var t = new Dictionary<string, Price>();
                var m = new List<KeyValuePair<string, string>>();
                if (text != null && ParseCatalog(text, t, m))
                {
                    tiers = t;
                    models = m;
                    loadedVersion = version;
                    Source = "Claude Code " + version + " price table";
                    try { Paths.WriteAtomic(Paths.PricesFile, Json.Write(ToJson(version, size), true)); } catch { }
                    return;
                }
                Log.Info("prices: could not read the model catalog from " + exe + "; using the built-in table");
            }
            if (cache != null && FromJson(cache)) { Source = "cached price table (Claude Code " + Json.S(cache, "claude_version") + ")"; return; }
        }

        static string FindClaudeBinary(out string version)
        {
            version = null;
            try
            {
                string dir = Path.Combine(Paths.Home, ".local", "share", "claude", "versions");
                if (Directory.Exists(dir))
                {
                    string best = null;
                    Version bestV = null;
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        Version v;
                        if (Version.TryParse(Path.GetFileName(f), out v) && (bestV == null || v > bestV)) { bestV = v; best = f; }
                    }
                    if (best != null) { version = Path.GetFileName(best); return best; }
                }
                string bin = Path.Combine(Paths.Home, ".local", "bin", "claude.exe");
                if (File.Exists(bin)) { version = "claude.exe-" + new FileInfo(bin).Length; return bin; }
            }
            catch { }
            return null;
        }

        /// <summary>Finds Claude Code's baked model catalog ({"//":"Hand-maintained baked-in model
        /// catalog ...", pricing_tiers: ..., models: [...]}) inside its executable.</summary>
        static string ReadCatalogText(string exe)
        {
            byte[] marker = Encoding.ASCII.GetBytes("Hand-maintained baked-in model catalog");
            using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                const int Chunk = 8 << 20;
                var buf = new byte[Chunk + marker.Length];
                long basePos = 0, found = -1;
                int carry = 0;
                while (found < 0)
                {
                    int n = fs.Read(buf, carry, Chunk);
                    if (n <= 0) break;
                    int len = carry + n;
                    int at = Find(buf, len, marker);
                    if (at >= 0) { found = basePos + at; break; }
                    carry = Math.Min(marker.Length - 1, len);
                    Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
                    basePos += len - carry;
                }
                if (found < 0) return null;

                long start = Math.Max(0, found - 64);
                fs.Seek(start, SeekOrigin.Begin);
                var win = new byte[256 * 1024];
                int got = 0, r;
                while (got < win.Length && (r = fs.Read(win, got, win.Length - got)) > 0) got += r;
                string text = Encoding.UTF8.GetString(win, 0, got);
                int m = text.IndexOf("Hand-maintained", StringComparison.Ordinal);
                int open = m < 0 ? -1 : text.LastIndexOf('{', m);
                if (open < 0) return null;
                int depth = 0;
                char quote = '\0';
                for (int i = open; i < text.Length; i++)
                {
                    char c = text[i];
                    if (quote != '\0')
                    {
                        if (c == '\\') { i++; continue; }
                        if (c == quote) quote = '\0';
                        continue;
                    }
                    if (c == '"' || c == '\'' || c == '`') { quote = c; continue; }
                    if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) return text.Substring(open, i - open + 1);
                }
                return null;
            }
        }

        static int Find(byte[] hay, int len, byte[] needle)
        {
            int last = len - needle.Length;
            int i = 0;
            while (i <= last)
            {
                int q = Array.IndexOf(hay, needle[0], i, last - i + 1);
                if (q < 0) return -1;
                int k = 1;
                while (k < needle.Length && hay[q + k] == needle[k]) k++;
                if (k == needle.Length) return q;
                i = q + 1;
            }
            return -1;
        }

        static double Num(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

        static bool ParseCatalog(string text, Dictionary<string, Price> outTiers, List<KeyValuePair<string, string>> outModels)
        {
            const string N = "([0-9.eE+-]+)";
            var tierRx = new Regex(@"(\w+):\{input:" + N + ",output:" + N + ",cache_write_5m:" + N + ",cache_write_1h:" + N +
                                   ",cache_read:" + N + ",web_search:" + N +
                                   @"(?:,long_prompt:\{above_prompt_tokens:" + N + ",input:" + N + ",output:" + N +
                                   ",cache_write_5m:" + N + ",cache_write_1h:" + N + ",cache_read:" + N + @"\})?\}");
            int modelsAt = text.IndexOf("models:", StringComparison.Ordinal);
            if (modelsAt < 0) return false;
            foreach (Match m in tierRx.Matches(text.Substring(0, modelsAt)))
            {
                var g = m.Groups;
                var p = new Price { Input = Num(g[2].Value), Output = Num(g[3].Value), Write5m = Num(g[4].Value), Write1h = Num(g[5].Value), Read = Num(g[6].Value), WebSearch = Num(g[7].Value) };
                if (g[8].Success)
                {
                    p.LongAbove = (long)Num(g[8].Value);
                    p.Long = new Price { Input = Num(g[9].Value), Output = Num(g[10].Value), Write5m = Num(g[11].Value), Write1h = Num(g[12].Value), Read = Num(g[13].Value), WebSearch = p.WebSearch };
                }
                outTiers[g[1].Value] = p;
            }
            var ids = new Regex("\\{id:\"([^\"]+)\"").Matches(text, modelsAt).Cast<Match>().ToList();
            for (int i = 0; i < ids.Count; i++)
            {
                int end = i + 1 < ids.Count ? ids[i + 1].Index : text.Length;
                var pr = new Regex("pricing:\"([^\"]+)\"").Match(text.Substring(ids[i].Index, end - ids[i].Index));
                if (pr.Success) outModels.Add(new KeyValuePair<string, string>(ids[i].Groups[1].Value, pr.Groups[1].Value));
            }
            return outTiers.Count >= 3 && outModels.Count >= 5 && outModels.All(kv => outTiers.ContainsKey(kv.Value));
        }

        static JObj PriceJson(Price p)
        {
            var o = new JObj();
            o["input"] = p.Input; o["output"] = p.Output; o["cache_write_5m"] = p.Write5m; o["cache_write_1h"] = p.Write1h;
            o["cache_read"] = p.Read; o["web_search"] = p.WebSearch;
            if (p.Long != null) { o["long_above"] = p.LongAbove; o["long"] = PriceJson(p.Long); }
            return o;
        }

        static Price PriceFrom(JObj o)
        {
            if (o == null) return null;
            var p = new Price
            {
                Input = Json.D(o, "input") ?? 0, Output = Json.D(o, "output") ?? 0, Write5m = Json.D(o, "cache_write_5m") ?? 0,
                Write1h = Json.D(o, "cache_write_1h") ?? 0, Read = Json.D(o, "cache_read") ?? 0, WebSearch = Json.D(o, "web_search") ?? 0,
            };
            var l = Json.O(o, "long");
            if (l != null) { p.LongAbove = Json.L(o, "long_above") ?? 0; p.Long = PriceFrom(l); p.Long.WebSearch = p.WebSearch; }
            return p;
        }

        static JObj ToJson(string version, long size)
        {
            var o = new JObj();
            o["format"] = (long)CacheFormat;
            o["claude_version"] = version;
            o["binary_size"] = size;
            o["note"] = "Read from Claude Code's baked model catalog. USD per million tokens.";
            var t = new JObj();
            foreach (var kv in tiers) t[kv.Key] = PriceJson(kv.Value);
            o["tiers"] = t;
            var m = new JObj();
            foreach (var kv in models) m[kv.Key] = kv.Value;
            o["models"] = m;
            return o;
        }

        static bool FromJson(JObj o)
        {
            var t = Json.O(o, "tiers");
            var m = Json.O(o, "models");
            if (t == null || m == null) return false;
            var nt = new Dictionary<string, Price>();
            foreach (var kv in t) nt[kv.Key] = PriceFrom(kv.Value as JObj);
            var nm = m.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value as string)).Where(kv => kv.Value != null && nt.ContainsKey(kv.Value)).ToList();
            if (nt.Count < 3 || nm.Count < 5) return false;
            tiers = nt;
            models = nm;
            return true;
        }

        /// <summary>Standard price for a model id. exact=false when the model is unknown and a
        /// family match (or Claude Code's own $5/$25 fallback) was used.</summary>
        public static Price For(string model, out bool exact)
        {
            lock (Gate)
            {
                Ensure();
                string m = Regex.Replace(Fmt.BaseModel(model) ?? "", @"-\d{8}$", "");
                string tier = models.Where(kv => kv.Key == m).Select(kv => kv.Value).FirstOrDefault()
                              ?? models.Where(kv => m.StartsWith(kv.Key + "-", StringComparison.Ordinal))
                                       .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
                if (tier != null) { exact = true; return tiers[tier]; }
                exact = false;
                foreach (var family in new[] { "haiku", "sonnet", "opus", "fable", "mythos" })
                {
                    if (!m.Contains(family)) continue;
                    var newest = models.LastOrDefault(kv => kv.Key.Contains(family));
                    if (newest.Key != null) return tiers[newest.Value];
                }
                return P(5, 25, 6.25, 10, 0.5);
            }
        }

        /// <summary>Cost of one API response, computed the way Claude Code does.</summary>
        public static double Cost(string model, JObj usage, out bool exact)
        {
            var p = For(model, out exact);
            if (Json.S(usage, "speed") == "fast") p = FastPrice(Regex.Replace(Fmt.BaseModel(model) ?? "", @"-\d{8}$", ""), p);
            long input = Json.L(usage, "input_tokens") ?? 0, output = Json.L(usage, "output_tokens") ?? 0;
            long read = Json.L(usage, "cache_read_input_tokens") ?? 0, write = Json.L(usage, "cache_creation_input_tokens") ?? 0;
            long oneHour = Math.Max(0, Math.Min(write, Json.L(Json.O(usage, "cache_creation"), "ephemeral_1h_input_tokens") ?? 0));
            if (p.Long != null && input + read + write > p.LongAbove) p = p.Long;
            double tokens = (input * p.Input + output * p.Output + read * p.Read + oneHour * p.Write1h + (write - oneHour) * p.Write5m) / 1e6;
            if (Json.S(usage, "inference_geo") == "us") tokens *= 1.1;
            long searches = Json.L(Json.O(usage, "server_tool_use"), "web_search_requests") ?? 0;
            return tokens + searches * p.WebSearch;
        }
    }

    /// <summary>Incremental reader for one transcript (.jsonl). Only appended bytes are
    /// read on each update. One API response is written as several "assistant"
    /// records sharing a requestId, so usage is counted once per request.</summary>
    public sealed class TranscriptFile
    {
        sealed class Req { public DateTime? Day; public Tokens T; }

        static readonly HashSet<string> SkipKeys = new HashSet<string> { "content" };
        static readonly byte[] TypeKey = Encoding.ASCII.GetBytes("\"type\":\"");
        static readonly byte[] Assistant = Encoding.ASCII.GetBytes("assistant\"");
        static readonly byte[] AiTitleTag = Encoding.ASCII.GetBytes("ai-title\"");
        static readonly byte[] LastPromptTag = Encoding.ASCII.GetBytes("last-prompt\"");

        public readonly string FilePath;
        long offset;
        byte[] partial = new byte[0];
        readonly Dictionary<string, Req> reqs = new Dictionary<string, Req>();

        public long ContextTokens = -1;
        public string ContextModel;
        public string AiTitle, LastPrompt;

        public TranscriptFile(string path) { FilePath = path; }

        void Reset()
        {
            offset = 0; partial = new byte[0]; reqs.Clear();
            ContextTokens = -1; ContextModel = null; AiTitle = null; LastPrompt = null;
        }

        public bool Update()
        {
            long len;
            try
            {
                var fi = new FileInfo(FilePath);
                if (!fi.Exists) return false;
                len = fi.Length;
            }
            catch { return false; }
            if (len < offset) Reset();
            if (len == offset) return false;

            // Read in 1 MB chunks so a large transcript never needs one big buffer.
            try
            {
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    long remaining = len - offset;
                    var chunk = new byte[(int)Math.Min(remaining, 1 << 20)];
                    while (remaining > 0)
                    {
                        int n = fs.Read(chunk, 0, (int)Math.Min(remaining, chunk.Length));
                        if (n <= 0) break;
                        remaining -= n;
                        offset += n;
                        Feed(chunk, n);
                    }
                }
            }
            catch { }
            return true;
        }

        void Feed(byte[] chunk, int count)
        {
            byte[] data = chunk;
            int length = count;
            if (partial.Length > 0)
            {
                data = new byte[partial.Length + count];
                Buffer.BlockCopy(partial, 0, data, 0, partial.Length);
                Buffer.BlockCopy(chunk, 0, data, partial.Length, count);
                length = data.Length;
            }
            int start = 0;
            while (start < length)
            {
                int nl = Array.IndexOf(data, (byte)'\n', start, length - start);
                if (nl < 0) break;
                Ingest(data, start, nl - start);
                start = nl + 1;
            }
            partial = new byte[length - start];
            Buffer.BlockCopy(data, start, partial, 0, partial.Length);
        }

        static bool At(byte[] d, int pos, int end, byte[] pat)
        {
            if (pos + pat.Length > end) return false;
            for (int k = 0; k < pat.Length; k++) if (d[pos + k] != pat[k]) return false;
            return true;
        }

        /// <summary>1 = assistant, 2 = ai-title, 3 = last-prompt, 0 = not interesting.</summary>
        static int Classify(byte[] d, int start, int len)
        {
            int end = start + len;
            byte first = TypeKey[0];
            int i = start;
            while (i < end)
            {
                int q = Array.IndexOf(d, first, i, end - i);
                if (q < 0) return 0;
                if (At(d, q, end, TypeKey))
                {
                    int v = q + TypeKey.Length;
                    if (At(d, v, end, Assistant)) return 1;
                    if (At(d, v, end, AiTitleTag)) return 2;
                    if (At(d, v, end, LastPromptTag)) return 3;
                }
                i = q + 1;
            }
            return 0;
        }

        void Ingest(byte[] d, int start, int len)
        {
            if (len <= 0 || Classify(d, start, len) == 0) return;
            var rec = Json.TryParse(Encoding.UTF8.GetString(d, start, len), SkipKeys) as JObj;
            if (rec == null) return;
            string type = rec["type"] as string;
            if (type == "ai-title")
            {
                var t = rec["aiTitle"] as string;
                if (!string.IsNullOrWhiteSpace(t)) AiTitle = t.Trim();
            }
            else if (type == "last-prompt")
            {
                var p = rec["lastPrompt"] as string;
                if (!string.IsNullOrWhiteSpace(p)) LastPrompt = p.Trim();
            }
            else if (type == "assistant") IngestAssistant(rec);
        }

        void IngestAssistant(JObj rec)
        {
            var msg = rec["message"] as JObj;
            var usage = Json.O(msg, "usage");
            if (usage == null) return;
            string model = Json.S(msg, "model") ?? "";
            if (model == "<synthetic>") return;

            var t = new Tokens
            {
                Input = Json.L(usage, "input_tokens") ?? 0,
                Output = Json.L(usage, "output_tokens") ?? 0,
                CacheWrite = Json.L(usage, "cache_creation_input_tokens") ?? 0,
                CacheRead = Json.L(usage, "cache_read_input_tokens") ?? 0,
                Requests = 1,
            };
            bool exactPrice;
            t.Cost = PriceBook.Cost(model, usage, out exactPrice);
            t.CostApprox = !exactPrice;

            DateTime? day = null;
            DateTimeOffset when;
            var ts = rec["timestamp"] as string;
            if (ts != null && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when))
                day = when.ToLocalTime().Date;

            string key = (rec["requestId"] as string) ?? Json.S(msg, "id") ?? (rec["uuid"] as string) ?? ("line-" + reqs.Count);
            Req r;
            if (reqs.TryGetValue(key, out r)) r.T.MaxWith(t);
            else reqs[key] = new Req { Day = day, T = t };

            if (Json.B(rec, "isSidechain") != true)
            {
                ContextTokens = t.Input + t.CacheWrite + t.CacheRead;
                ContextModel = model;
            }
        }

        public Tokens Totals()
        {
            var t = new Tokens();
            foreach (var r in reqs.Values) t.Add(r.T);
            return t;
        }

        public void AddByDay(Dictionary<DateTime, Tokens> byDay)
        {
            foreach (var r in reqs.Values)
            {
                if (!r.Day.HasValue) continue;
                Tokens t;
                if (!byDay.TryGetValue(r.Day.Value, out t)) { t = new Tokens(); byDay[r.Day.Value] = t; }
                t.Add(r.T);
            }
        }
    }

    // ------------------------------------------------------------------ live tool markers (hooks)

    public sealed class ToolRun
    {
        public string Tool, Detail;
        public long Since;
    }

    /// <summary>~/.claude/widget/live/&lt;session&gt;/&lt;tool_use_id&gt;.run (tool started) and .done (finished).
    /// One file per tool call, so parallel async hooks never race on a shared file,
    /// and a PreToolUse that starts late is ignored once its .done marker exists.</summary>
    public static class LiveTools
    {
        public static string Safe(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length >= 100) break;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_') sb.Append(c);
            }
            return sb.ToString();
        }

        static string Dir(string sessionId)
        {
            string id = Safe(sessionId);
            return id.Length == 0 ? null : Path.Combine(Paths.LiveDir, id);
        }

        public static void Start(string sessionId, string toolUseId, ToolRun run)
        {
            string dir = Dir(sessionId), id = Safe(toolUseId);
            if (dir == null || id.Length == 0 || File.Exists(Path.Combine(dir, id + ".done"))) return;
            var o = new JObj();
            o["tool"] = run.Tool; o["detail"] = run.Detail; o["since"] = run.Since;
            Paths.WriteAtomic(Path.Combine(dir, id + ".run"), Json.Write(o, false));
        }

        public static void End(string sessionId, string toolUseId)
        {
            string dir = Dir(sessionId), id = Safe(toolUseId);
            if (dir == null || id.Length == 0) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".done"), "");
            try { File.Delete(Path.Combine(dir, id + ".run")); } catch { }
        }

        /// <summary>Turn finished or a new prompt started: nothing is running any more.</summary>
        public static void Clear(string sessionId)
        {
            string dir = Dir(sessionId);
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }
        }

        public static void EndSession(string sessionId)
        {
            string dir = Dir(sessionId);
            if (dir == null || !Directory.Exists(dir)) return;
            try { Directory.Delete(dir, true); } catch { }
        }

        /// <summary>Running tools, newest first. Markers older than 30 min are leftovers.</summary>
        public static List<ToolRun> Running(string sessionId)
        {
            var list = new List<ToolRun>();
            string dir = Dir(sessionId);
            if (dir == null || !Directory.Exists(dir)) return list;
            long cutoff = Paths.NowMs() - 30 * 60 * 1000;
            foreach (var f in Directory.GetFiles(dir, "*.run"))
            {
                var o = Json.TryParse(Paths.ReadShared(f)) as JObj;
                if (o == null) continue;
                var run = new ToolRun { Tool = Json.S(o, "tool") ?? "", Detail = Json.S(o, "detail") ?? "", Since = Json.L(o, "since") ?? 0 };
                if (run.Since >= cutoff) list.Add(run);
            }
            return list.OrderByDescending(r => r.Since).ToList();
        }

        /// <summary>"mcp__windows-mcp__Click" -> "windows-mcp · Click"</summary>
        public static string Label(string tool)
        {
            if (tool != null && tool.StartsWith("mcp__", StringComparison.Ordinal))
            {
                string rest = tool.Substring(5);
                int sep = rest.IndexOf("__", StringComparison.Ordinal);
                if (sep > 0) return rest.Substring(0, sep) + " · " + rest.Substring(sep + 2);
            }
            return tool ?? "";
        }

        /// <summary>One-line summary of what a tool call is doing.</summary>
        public static string Describe(string tool, JObj input)
        {
            Func<string, string> s = k => Json.S(input, k);
            Func<string, string> baseName = p =>
            {
                if (p == null) return null;
                try { return Path.GetFileName(p.TrimEnd('\\', '/')); } catch { return p; }
            };
            string detail;
            switch (tool ?? "")
            {
                case "Bash":
                case "PowerShell": detail = s("description") ?? s("command"); break;
                case "Read":
                case "Write":
                case "Edit":
                case "MultiEdit": detail = baseName(s("file_path")); break;
                case "NotebookEdit": detail = baseName(s("notebook_path")); break;
                case "Grep":
                case "Glob": detail = s("pattern"); break;
                case "WebFetch": detail = s("url"); break;
                case "WebSearch": detail = s("query"); break;
                case "Agent":
                case "Task": detail = s("description"); break;
                case "Skill": detail = s("skill"); break;
                default:
                    detail = input == null ? null : input.Select(kv => kv.Value as string).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    break;
            }
            return Fmt.OneLine(detail, 90);
        }
    }

    // ------------------------------------------------------------------ snapshot model

    public sealed class SessionInfo
    {
        /// <summary>A session that went idle less than this long ago shows as "done".</summary>
        public const long DoneWindowMs = 10 * 60 * 1000;

        public int Pid;
        public string SessionId, Title, Name, AiTitle, LastPrompt, Cwd, Project, Host, Entrypoint, Kind, Status, WaitingFor, Model, ModelLabel, Version, Transcript;
        /// <summary>What to show: "waiting", "busy", "done" (finished recently) or "idle".</summary>
        public string State;
        public long StatusSince, StartedAt;
        public long ContextTokens = -1, ContextWindow;
        public bool ContextExact;
        public Tokens Tokens = new Tokens(), SubagentTokens = new Tokens();
        public ToolRun CurrentTool;
        public int RunningTools;

        public double? ContextPct
        {
            get
            {
                if (ContextTokens < 0 || ContextWindow <= 0) return null;
                return 100.0 * ContextTokens / ContextWindow;
            }
        }

        public JObj ToJson()
        {
            var o = new JObj();
            o["pid"] = (long)Pid; o["session_id"] = SessionId; o["title"] = Title; o["name"] = Name;
            o["state"] = State; o["status"] = Status; o["waiting_for"] = WaitingFor; o["status_since"] = StatusSince;
            o["host"] = Host; o["entrypoint"] = Entrypoint; o["kind"] = Kind; o["cwd"] = Cwd; o["project"] = Project;
            o["last_prompt"] = LastPrompt == null ? null : Fmt.OneLine(LastPrompt, 120);
            o["model"] = Model; o["model_label"] = ModelLabel;
            o["context_tokens"] = ContextTokens; o["context_window"] = ContextWindow; o["context_window_exact"] = ContextExact;
            var pct = ContextPct;
            o["context_pct"] = pct.HasValue ? (object)Math.Round(pct.Value, 1) : null;
            o["tokens"] = Tokens.ToJson(); o["subagent_tokens"] = SubagentTokens.ToJson();
            if (CurrentTool != null)
            {
                var t = new JObj();
                t["tool"] = CurrentTool.Tool; t["detail"] = CurrentTool.Detail; t["since"] = CurrentTool.Since;
                o["current_tool"] = t;
            }
            else o["current_tool"] = null;
            o["running_tools"] = (long)RunningTools;
            o["version"] = Version; o["transcript"] = Transcript;
            return o;
        }
    }

    public sealed class LimitWindow
    {
        public double UsedPct;
        public long ResetsAt; // unix seconds
    }

    public sealed class Limits
    {
        public LimitWindow FiveHour, SevenDay;
        public long CapturedAt;
    }

    public sealed class DayTokens
    {
        public DateTime Day;
        public Tokens Tokens;
    }

    public sealed class Snapshot
    {
        public long GeneratedAt;
        public List<SessionInfo> Sessions = new List<SessionInfo>();
        public Tokens Today = new Tokens();
        public List<DayTokens> Days = new List<DayTokens>();
        public Limits Limits;
        public int Busy, Waiting, Done, Idle;
        public bool HooksInstalled, StatusLineInstalled;
        public string StatusLineOther;

        public JObj ToJson()
        {
            var o = new JObj();
            o["generated_at"] = GeneratedAt;
            var c = new JObj(); c["busy"] = (long)Busy; c["waiting"] = (long)Waiting; c["done"] = (long)Done; c["idle"] = (long)Idle;
            o["counts"] = c;
            o["today"] = Today.ToJson();
            o["days"] = Days.Select(d => { var j = new JObj(); j["date"] = d.Day.ToString("yyyy-MM-dd"); j["total"] = d.Tokens.Total; j["requests"] = d.Tokens.Requests; j["cost_usd"] = Math.Round(d.Tokens.Cost, 4); return (object)j; }).ToList();
            o["price_source"] = PriceBook.Source;
            if (Limits != null)
            {
                var l = new JObj();
                l["captured_at"] = Limits.CapturedAt;
                l["five_hour"] = WinJson(Limits.FiveHour);
                l["seven_day"] = WinJson(Limits.SevenDay);
                o["limits"] = l;
            }
            else o["limits"] = null;
            var setup = new JObj(); setup["hooks"] = HooksInstalled; setup["status_line"] = StatusLineInstalled; setup["other_status_line"] = StatusLineOther;
            o["setup"] = setup;
            o["sessions"] = Sessions.Select(s => (object)s.ToJson()).ToList();
            return o;
        }

        static object WinJson(LimitWindow w)
        {
            if (w == null) return null;
            var o = new JObj(); o["used_percentage"] = w.UsedPct; o["resets_at"] = w.ResetsAt;
            return o;
        }
    }

    // ------------------------------------------------------------------ collector

    /// <summary>Builds a Snapshot from ~/.claude/sessions, transcripts, hook markers and
    /// status line captures. Keep one instance alive: transcripts are read incrementally.</summary>
    public sealed class Collector
    {
        readonly Dictionary<string, TranscriptFile> files = new Dictionary<string, TranscriptFile>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> mainBySession = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<string>> subsBySession = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> hostCache = new Dictionary<string, string>();
        long lastScan, lastSetup;
        bool hooksInstalled, statusInstalled;
        string statusOther;

        public Snapshot Collect()
        {
            long now = Paths.NowMs();
            var snap = new Snapshot { GeneratedAt = now };

            var live = ReadLiveSessions();
            var liveIds = new HashSet<string>(live.Select(o => Json.S(o, "sessionId") ?? ""), StringComparer.OrdinalIgnoreCase);
            bool missing = liveIds.Any(id => id.Length > 0 && !mainBySession.ContainsKey(id));
            PriceBook.CheckForUpdate();
            if (now - lastScan > 10000 || (missing && now - lastScan > 2000)) Scan(liveIds, now);
            foreach (var f in files.Values) f.Update();

            // tokens per day, last 7 days
            var byDay = new Dictionary<DateTime, Tokens>();
            foreach (var f in files.Values) f.AddByDay(byDay);
            var today = DateTime.Now.Date;
            for (int k = 6; k >= 0; k--)
            {
                var d = today.AddDays(-k);
                Tokens t;
                if (!byDay.TryGetValue(d, out t)) t = new Tokens();
                snap.Days.Add(new DayTokens { Day = d, Tokens = t });
            }
            snap.Today = snap.Days[snap.Days.Count - 1].Tokens;

            snap.Limits = ReadLimits(now);
            var models = Json.TryParse(Paths.ReadShared(Paths.ModelsFile)) as JObj;

            Dictionary<uint, ProcEntry> table = null;
            foreach (var o in live)
            {
                var s = new SessionInfo();
                s.Pid = (int)(Json.L(o, "pid") ?? 0);
                s.SessionId = Json.S(o, "sessionId") ?? "";
                s.Cwd = Json.S(o, "cwd") ?? "";
                s.Project = ProjectName(s.Cwd);
                s.Name = Json.S(o, "name") ?? s.Project;
                s.Kind = Json.S(o, "kind") ?? "";
                s.Entrypoint = Json.S(o, "entrypoint") ?? "";
                s.Version = Json.S(o, "version");
                s.Status = (Json.S(o, "status") ?? "idle").ToLowerInvariant();
                s.WaitingFor = Json.S(o, "waitingFor");
                s.StartedAt = Json.L(o, "startedAt") ?? 0;
                s.StatusSince = Json.L(o, "statusUpdatedAt") ?? Json.L(o, "updatedAt") ?? s.StartedAt;

                string hostKey = s.Pid + ":" + (Json.S(o, "procStart") ?? "");
                string host;
                if (!hostCache.TryGetValue(hostKey, out host))
                {
                    if (table == null) table = Procs.Table();
                    host = Procs.HostLabel((uint)s.Pid, s.Entrypoint, table);
                    hostCache[hostKey] = host;
                }
                s.Host = host;

                string mainPath;
                TranscriptFile main = null;
                if (mainBySession.TryGetValue(s.SessionId, out mainPath)) files.TryGetValue(mainPath, out main);
                if (main != null)
                {
                    s.Transcript = mainPath;
                    s.Tokens = main.Totals();
                    s.AiTitle = main.AiTitle;
                    s.LastPrompt = main.LastPrompt;
                    if (main.ContextTokens >= 0) { s.ContextTokens = main.ContextTokens; s.Model = main.ContextModel; }
                }
                List<string> subs;
                if (subsBySession.TryGetValue(s.SessionId, out subs))
                {
                    foreach (var p in subs)
                    {
                        TranscriptFile sf;
                        if (files.TryGetValue(p, out sf)) s.SubagentTokens.Add(sf.Totals());
                    }
                }

                // Title: a name you gave it (/rename), else Claude's AI title, else your last
                // prompt, else the folder. Never the auto-generated slug ("my-project-ea").
                if (Json.S(o, "nameSource") == "user" && !string.IsNullOrWhiteSpace(s.Name)) s.Title = s.Name;
                else if (!string.IsNullOrWhiteSpace(s.AiTitle)) s.Title = s.AiTitle;
                else if (!string.IsNullOrWhiteSpace(s.LastPrompt)) s.Title = Fmt.OneLine(s.LastPrompt, 70);
                else s.Title = s.Project;

                if (s.Status == "waiting") s.State = "waiting";
                else if (s.Status != "idle") s.State = "busy";
                // Idle, went idle recently, and has actually answered something (not a session
                // that was opened and never used).
                else if (now - s.StatusSince < SessionInfo.DoneWindowMs && s.StatusSince - s.StartedAt > 5000 && s.Tokens.Requests > 0) s.State = "done";
                else s.State = "idle";

                ResolveContextWindow(s, models);
                s.ModelLabel = Fmt.ModelLabel(s.Model);

                if (s.Status != "idle")
                {
                    var runs = LiveTools.Running(s.SessionId);
                    s.RunningTools = runs.Count;
                    s.CurrentTool = runs.FirstOrDefault();
                }
                snap.Sessions.Add(s);
            }

            // No exact size known yet (status line not seen): if any session of a model is past
            // 200k tokens, that model has a larger window, so use 1M for all its sessions.
            var bigModels = new HashSet<string>(snap.Sessions.Where(x => x.ContextTokens > 200000 && x.Model != null)
                .Select(x => Fmt.BaseModel(x.Model)));
            foreach (var x in snap.Sessions)
                if (!x.ContextExact && x.Model != null && bigModels.Contains(Fmt.BaseModel(x.Model))) x.ContextWindow = 1000000;

            snap.Sessions = snap.Sessions.OrderBy(x => Rank(x.State)).ThenBy(x => x.StartedAt).ToList();
            snap.Waiting = snap.Sessions.Count(x => x.State == "waiting");
            snap.Busy = snap.Sessions.Count(x => x.State == "busy");
            snap.Done = snap.Sessions.Count(x => x.State == "done");
            snap.Idle = snap.Sessions.Count(x => x.State == "idle");

            if (now - lastSetup > 10000) { lastSetup = now; CheckSetup(); }
            snap.HooksInstalled = hooksInstalled;
            snap.StatusLineInstalled = statusInstalled;
            snap.StatusLineOther = statusOther;
            return snap;
        }

        static int Rank(string state)
        {
            switch (state)
            {
                case "waiting": return 0;
                case "busy": return 1;
                case "done": return 2;
                default: return 3;
            }
        }

        static string ProjectName(string cwd)
        {
            try
            {
                var n = Path.GetFileName((cwd ?? "").TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(n) ? cwd : n;
            }
            catch { return cwd; }
        }

        List<JObj> ReadLiveSessions()
        {
            var list = new List<JObj>();
            if (!Directory.Exists(Paths.SessionsDir)) return list;
            foreach (var f in Directory.GetFiles(Paths.SessionsDir, "*.json"))
            {
                var o = Json.TryParse(Paths.ReadShared(f)) as JObj;
                if (o == null) continue;
                long? pid = Json.L(o, "pid");
                if (!pid.HasValue || pid.Value <= 0) continue;
                if (Json.B(o, "spare") == true) continue; // pre-warmed process, not a real session
                if (!Procs.IsSameProcess((uint)pid.Value, Json.L(o, "procStart"))) continue; // stale file
                list.Add(o);
            }
            return list;
        }

        void Scan(HashSet<string> liveIds, long now)
        {
            lastScan = now;
            mainBySession.Clear();
            subsBySession.Clear();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.UtcNow.AddDays(-8);
            if (Directory.Exists(Paths.ProjectsDir))
            {
                foreach (var proj in SafeDirs(Paths.ProjectsDir))
                {
                    foreach (var f in SafeFiles(proj, "*.jsonl"))
                    {
                        string sid = Path.GetFileNameWithoutExtension(f);
                        if (!liveIds.Contains(sid) && LastWrite(f) < cutoff) continue;
                        Track(f, keep);
                        mainBySession[sid] = f;
                    }
                    foreach (var sessionDir in SafeDirs(proj))
                    {
                        string sid = Path.GetFileName(sessionDir);
                        string subDir = Path.Combine(sessionDir, "subagents");
                        if (!Directory.Exists(subDir)) continue;
                        foreach (var f in SafeFiles(subDir, "*.jsonl"))
                        {
                            if (!liveIds.Contains(sid) && LastWrite(f) < cutoff) continue;
                            Track(f, keep);
                            List<string> list;
                            if (!subsBySession.TryGetValue(sid, out list)) { list = new List<string>(); subsBySession[sid] = list; }
                            list.Add(f);
                        }
                    }
                }
            }
            foreach (var k in files.Keys.Where(k => !keep.Contains(k)).ToList()) files.Remove(k);
        }

        void Track(string path, HashSet<string> keep)
        {
            keep.Add(path);
            if (!files.ContainsKey(path)) files[path] = new TranscriptFile(path);
        }

        static DateTime LastWrite(string f) { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } }
        static string[] SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return new string[0]; } }
        static string[] SafeFiles(string d, string pattern) { try { return Directory.GetFiles(d, pattern); } catch { return new string[0]; } }

        static void ResolveContextWindow(SessionInfo s, JObj models)
        {
            long window = 0;
            bool exact = false;
            var sl = Json.TryParse(Paths.ReadShared(Path.Combine(Paths.StatuslineDir, LiveTools.Safe(s.SessionId) + ".json"))) as JObj;
            if (sl != null)
            {
                string slModel = Json.S(Json.O(sl, "model"), "id");
                long? size = Json.L(Json.O(sl, "context_window"), "context_window_size");
                if (size.HasValue && size.Value > 0 && (s.Model == null || Fmt.BaseModel(slModel) == Fmt.BaseModel(s.Model)))
                {
                    window = size.Value;
                    exact = true;
                }
                if (s.Model == null) s.Model = slModel;
            }
            if (window == 0 && models != null && s.Model != null)
            {
                long? m = Json.L(models, Fmt.BaseModel(s.Model));
                if (m.HasValue && m.Value > 0) { window = m.Value; exact = true; }
            }
            if (window == 0 && s.ContextTokens >= 0) window = s.ContextTokens > 200000 ? 1000000 : 200000;
            s.ContextWindow = window;
            s.ContextExact = exact;
        }

        static Limits ReadLimits(long now)
        {
            var o = Json.TryParse(Paths.ReadShared(Paths.LimitsFile)) as JObj;
            if (o == null) return null;
            var l = new Limits
            {
                CapturedAt = Json.L(o, "captured_at") ?? 0,
                FiveHour = Window(Json.O(o, "five_hour"), now),
                SevenDay = Window(Json.O(o, "seven_day"), now),
            };
            return l.FiveHour == null && l.SevenDay == null ? null : l;
        }

        static LimitWindow Window(JObj w, long now)
        {
            if (w == null) return null;
            double? pct = Json.D(w, "used_percentage");
            long resets = Json.L(w, "resets_at") ?? 0;
            if (!pct.HasValue) return null;
            if (resets > 0 && resets * 1000 < now) return null; // window already reset; old number is stale
            return new LimitWindow { UsedPct = pct.Value, ResetsAt = resets };
        }

        void CheckSetup()
        {
            hooksInstalled = false;
            statusInstalled = false;
            statusOther = null;
            var root = Json.TryParse(Paths.ReadShared(Paths.SettingsFile)) as JObj;
            if (root == null) return;
            var hooks = root["hooks"] as JObj;
            if (hooks != null)
                hooksInstalled = hooks.Any(kv => { var groups = kv.Value as List<object>; return groups != null && groups.Any(Installer.IsOurs); });
            string cmd = Json.S(Json.O(root, "statusLine"), "command");
            if (cmd != null)
            {
                if (cmd.IndexOf("claude-widget-hook", StringComparison.OrdinalIgnoreCase) >= 0) statusInstalled = true;
                else statusOther = cmd;
            }
        }
    }

    // ------------------------------------------------------------------ formatting

    public static class Fmt
    {
        public static string Tokens(long n)
        {
            var c = CultureInfo.InvariantCulture;
            if (n < 1000) return n.ToString(c);
            if (n < 10000) return (n / 1000.0).ToString("0.#", c) + "k";
            if (n < 1000000) return (n / 1000).ToString(c) + "k";
            if (n < 10000000) return (n / 1000000.0).ToString("0.##", c) + "M";
            if (n < 1000000000) return (n / 1000000.0).ToString("0.#", c) + "M";
            return (n / 1000000000.0).ToString("0.##", c) + "B";
        }

        public static string Money(double usd)
        {
            var c = CultureInfo.InvariantCulture;
            if (usd <= 0) return "$0";
            if (usd < 0.01) return "<$0.01";
            if (usd < 1000) return "$" + usd.ToString("0.00", c);
            if (usd < 100000) return "$" + usd.ToString("#,0", c);
            return "$" + (usd / 1000).ToString("0", c) + "k";
        }

        public static string Duration(long ms)
        {
            long s = Math.Max(0, ms / 1000);
            if (s < 60) return s + "s";
            long m = s / 60;
            if (m < 60) return m + "m";
            long h = m / 60;
            if (h < 24) return h + "h " + (m % 60) + "m";
            return (h / 24) + "d " + (h % 24) + "h";
        }

        public static string Ago(long ms)
        {
            if (ms < 60000) return "just now";
            return Duration(ms) + " ago";
        }

        /// <summary>"2h 10m" for soon, "Thu 09:00" when more than a day away.</summary>
        public static string ResetsIn(long resetsAtUnixSeconds, long nowMs)
        {
            long ms = resetsAtUnixSeconds * 1000 - nowMs;
            if (ms <= 0) return "now";
            if (ms < 24L * 3600 * 1000) return "in " + Duration(ms);
            var local = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(resetsAtUnixSeconds).ToLocalTime();
            return local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        }

        public static string BaseModel(string model)
        {
            if (model == null) return null;
            int b = model.IndexOf('[');
            return (b >= 0 ? model.Substring(0, b) : model).Trim().ToLowerInvariant();
        }

        /// <summary>"claude-opus-5-5" -> "Opus 5.5", "claude-3-5-sonnet-20241022" -> "Sonnet 3.5"</summary>
        public static string ModelLabel(string model)
        {
            string m = BaseModel(model);
            if (string.IsNullOrEmpty(m)) return null;
            if (m.StartsWith("claude-", StringComparison.Ordinal)) m = m.Substring(7);
            string name = null;
            var nums = new List<string>();
            foreach (var part in m.Split('-'))
            {
                if (part.Length == 0) continue;
                if (part.All(char.IsDigit)) { if (part.Length <= 2) nums.Add(part); }
                else if (name == null) name = char.ToUpperInvariant(part[0]) + part.Substring(1);
            }
            if (name == null) return model;
            return nums.Count > 0 ? name + " " + string.Join(".", nums) : name;
        }

        public static string OneLine(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in s.Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
                if (sb.Length > max) break;
            }
            string r = sb.ToString();
            return r.Length > max ? r.Substring(0, max - 1) + "…" : r;
        }
    }

    // ------------------------------------------------------------------ installer

    /// <summary>Adds/removes the widget's hooks and status line in ~/.claude/settings.json,
    /// next to whatever is already there (backup written first).</summary>
    public static class Installer
    {
        public const string Marker = "claude-widget-hook";
        static readonly string[] ToolEvents = { "PreToolUse", "PostToolUse", "PostToolUseFailure" };
        static readonly string[] TurnEvents = { "UserPromptSubmit", "Stop", "SessionEnd" };

        public static bool IsOurs(object group)
        {
            var hooks = Json.A(group, "hooks");
            return hooks != null && hooks.Any(h => (Json.S(h, "command") ?? "").IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string Fwd(string p) { return p.Replace('\\', '/'); }

        public static int Install(TextWriter log, bool start)
        {
            string src = AppDomain.CurrentDomain.BaseDirectory;
            string hookSrc = Path.Combine(src, "claude-widget-hook.exe");
            string widgetSrc = Path.Combine(src, "ClaudeWidget.exe");
            string cliSrc = Path.Combine(src, "claude-widget-cli.exe");
            if (!File.Exists(hookSrc) || !File.Exists(widgetSrc))
            {
                log.WriteLine("error: run this from the folder that has ClaudeWidget.exe and claude-widget-hook.exe (run build.cmd first).");
                return 1;
            }

            StopRunningWidget(log);
            Directory.CreateDirectory(Paths.ToolsDir);
            CopyExe(hookSrc, Paths.InstalledHookExe, log);
            CopyExe(widgetSrc, Paths.InstalledWidgetExe, log);
            if (File.Exists(cliSrc)) CopyExe(cliSrc, Path.Combine(Paths.ToolsDir, "claude-widget-cli.exe"), log);
            log.WriteLine("copied executables to " + Paths.ToolsDir);

            int rc = EditSettings(false, log);
            if (rc != 0) return rc;

            CreateShortcut(log);
            if (start)
            {
                Process.Start(new ProcessStartInfo(Paths.InstalledWidgetExe) { UseShellExecute = true, WorkingDirectory = Paths.ToolsDir });
                log.WriteLine("started the widget");
            }
            return 0;
        }

        public static int Uninstall(TextWriter log)
        {
            int rc = EditSettings(true, log);
            StopRunningWidget(log);
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    if (k != null) k.DeleteValue("ClaudeWidget", false);
            }
            catch { }
            try { File.Delete(ShortcutPath); } catch { }
            log.WriteLine("removed Start Menu shortcut and autostart entry");
            log.WriteLine("files left in place (delete by hand if you like): " + Paths.ToolsDir + " and " + Paths.WidgetDir);
            return rc;
        }

        static void CopyExe(string from, string to, TextWriter log)
        {
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(to))
            {
                try { File.Delete(to); }
                catch
                {
                    // In use (a hook is running right now): move it aside, Windows allows renaming a running exe.
                    string old = to + ".old-" + Paths.NowMs();
                    File.Move(to, old);
                }
            }
            File.Copy(from, to);
            foreach (var old in Directory.GetFiles(Path.GetDirectoryName(to), Path.GetFileName(to) + ".old-*"))
            {
                try { File.Delete(old); } catch { }
            }
        }

        static void StopRunningWidget(TextWriter log)
        {
            EventWaitHandle quit;
            if (!EventWaitHandle.TryOpenExisting(@"Local\ClaudeWidget.Quit", out quit)) return;
            quit.Set();
            quit.Dispose();
            for (int i = 0; i < 50; i++)
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(@"Local\ClaudeWidget.Single", out m)) { log.WriteLine("stopped the running widget"); return; }
                m.Dispose();
                Thread.Sleep(100);
            }
            log.WriteLine("warning: the running widget did not exit in time");
        }

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Widget.lnk"); }
        }

        static void CreateShortcut(TextWriter log)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                Type t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Paths.InstalledWidgetExe });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Paths.ToolsDir });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Live Claude Code sessions, tokens and plan limits" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
                log.WriteLine("created Start Menu shortcut: " + ShortcutPath);
            }
            catch (Exception e) { log.WriteLine("warning: could not create Start Menu shortcut: " + e.Message); }
        }

        static int EditSettings(bool uninstall, TextWriter log)
        {
            string path = Paths.SettingsFile;
            JObj root;
            bool crlf = false;
            if (File.Exists(path))
            {
                string original = File.ReadAllText(path, Paths.Utf8);
                crlf = original.Contains("\r\n");
                root = Json.TryParse(original) as JObj;
                if (root == null) { log.WriteLine("error: " + path + " is not valid JSON; not touching it."); return 1; }
                // Back up only the state from before the widget was ever installed.
                string backup = path + ".bak-claude-widget";
                if (original.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    File.Copy(path, backup, true);
                    log.WriteLine("backed up settings to " + backup);
                }
                else if (File.Exists(backup)) log.WriteLine("keeping the earlier backup " + backup);
            }
            else root = new JObj();

            string exe = Fwd(Paths.InstalledHookExe);
            var hooks = root["hooks"] as JObj;
            if (hooks == null) { hooks = new JObj(); if (!uninstall) root["hooks"] = hooks; }

            foreach (var ev in ToolEvents.Concat(TurnEvents))
            {
                var groups = (hooks[ev] as List<object>) ?? new List<object>();
                var kept = groups.Where(g => !IsOurs(g)).ToList();
                if (!uninstall)
                {
                    var h = new JObj();
                    h["type"] = "command";
                    h["command"] = exe;
                    h["args"] = new List<object> { "hook" };
                    h["timeout"] = 5L;
                    h["async"] = true;
                    var g = new JObj();
                    g["hooks"] = new List<object> { h };
                    kept.Add(g);
                }
                if (kept.Count > 0) hooks[ev] = kept;
                else hooks.Remove(ev);
            }
            if (hooks.Count == 0) root.Remove("hooks");

            var sl = root["statusLine"] as JObj;
            string slCmd = Json.S(sl, "command") ?? "";
            bool slOurs = slCmd.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!uninstall)
            {
                if (sl == null || slOurs)
                {
                    var n = new JObj();
                    n["type"] = "command";
                    n["command"] = (exe.Contains(" ") ? "\"" + exe + "\"" : exe) + " statusline";
                    n["padding"] = 0L;
                    root["statusLine"] = n;
                    log.WriteLine("status line: set to the widget's (shows model, context and plan limits)");
                }
                else
                {
                    log.WriteLine("status line: kept your existing one (" + slCmd + "); plan limits stay off.");
                }
            }
            else if (slOurs)
            {
                root.Remove("statusLine");
                log.WriteLine("status line: removed");
            }

            string text = Json.Write(root, true) + "\n";
            if (crlf) text = text.Replace("\n", "\r\n"); // keep the file's line endings
            Paths.WriteAtomic(path, text);
            log.WriteLine((uninstall ? "removed" : "added") + " hooks for: " + string.Join(", ", ToolEvents.Concat(TurnEvents)));
            return 0;
        }
    }
}
