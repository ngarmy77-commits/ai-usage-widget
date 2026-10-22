// AI 사용량 위젯 (Claude + Codex) - 단독 실행 파일
// 빌드: build.ps1 (Windows 내장 .NET Framework 4.x 의 csc.exe 사용, C# 5 문법)
//
// 실행 방식
//   AIUsageWidget.exe               트레이 위젯 실행 (처음 실행 시 Claude 연동·자동 실행 설정을 물어봄)
//   AIUsageWidget.exe --statusline  Claude Code 상태표시줄 명령 (stdin JSON의 rate_limits를 세션별 파일로 저장)
//   AIUsageWidget.exe --dump        현재 읽은 값을 JSON으로 출력 (점검용)
//
// 데이터 출처 (모두 이 PC 안의 파일만 읽음, 인터넷 접속·로그인 토큰 사용 없음)
//   Claude 사용률 : %APPDATA%\UsageWidget\statusline\<세션ID>.json  (상태표시줄 명령이 저장)
//                   상태표시줄은 터미널 Claude Code(CLI)에서만 실행된다. 데스크탑앱 세션은 값을 남기지 않는다.
//   Codex 사용률  : ~/.codex/sessions/**/rollout-*.jsonl 의 마지막 rate_limits
//   오늘 토큰     : ~/.claude/projects/**/*.jsonl, ~/.codex/sessions/**/rollout-*.jsonl

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("AI 사용량 위젯")]
[assembly: AssemblyDescription("Claude·Codex 사용률을 트레이에서 확인하는 위젯")]
[assembly: AssemblyProduct("AI Usage Widget")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace AIUsageWidget
{
    static class Paths
    {
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string ClaudeDir = Path.Combine(Home, ".claude");
        public static readonly string CodexDir = Path.Combine(Home, ".codex");
        public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageWidget");
        public static readonly string StatuslineDir = Path.Combine(AppDir, "statusline");
        public static readonly string WidgetSettings = Path.Combine(AppDir, "settings.json");
        public static readonly string ClaudeSettings = Path.Combine(ClaudeDir, "settings.json");
        public static string ExePath { get { return Assembly.GetEntryAssembly().Location; } }
    }

    // ---------- JSON ----------
    static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        public static object Parse(string s) { return S.DeserializeObject(s); }

        public static object Get(object o, params string[] keys)
        {
            foreach (var k in keys)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(k, out o)) return null;
            }
            return o;
        }

        public static string Str(object o) { return o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture); }

        public static double? Num(object o)
        {
            if (o == null) return null;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static long Long(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        // 보기 좋은 들여쓰기 JSON (Claude 설정 파일을 사람이 읽기 좋게 유지)
        public static string Pretty(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, int depth)
        {
            string pad = new string(' ', (depth + 1) * 2), endPad = new string(' ', depth * 2);
            var d = o as Dictionary<string, object>;
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in d)
                {
                    sb.Append(pad).Append(Quote(kv.Key)).Append(": ");
                    Write(sb, kv.Value, depth + 1);
                    sb.Append(++i < d.Count ? ",\n" : "\n");
                }
                sb.Append(endPad).Append("}");
                return;
            }
            var list = o as System.Collections.IList;
            if (list != null && !(o is string))
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(pad);
                    Write(sb, list[i], depth + 1);
                    sb.Append(i + 1 < list.Count ? ",\n" : "\n");
                }
                sb.Append(endPad).Append("]");
                return;
            }
            if (o == null) sb.Append("null");
            else if (o is bool) sb.Append((bool)o ? "true" : "false");
            else if (o is string) sb.Append(Quote((string)o));
            else if (o is int || o is long || o is decimal || o is double || o is float) sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
            else sb.Append(Quote(Convert.ToString(o, CultureInfo.InvariantCulture)));
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }

    static class Util
    {
        public static readonly CultureInfo Ko = new CultureInfo("ko-KR");

        public static DateTime? IsoToLocal(object v)
        {
            string s = Json.Str(v);
            if (string.IsNullOrEmpty(s)) return null;
            DateTimeOffset d;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d)) return d.LocalDateTime;
            return null;
        }

        // 초 또는 밀리초 단위 유닉스 시각, 혹은 ISO 문자열
        public static DateTime? EpochToLocal(object v)
        {
            if (v == null) return null;
            if (v is string) return IsoToLocal(v);
            long n = Json.Long(v);
            if (n <= 0) return null;
            if (n > 100000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(n).LocalDateTime;
            return DateTimeOffset.FromUnixTimeSeconds(n).LocalDateTime;
        }

        public static string ReadShared(string path, long maxBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (maxBytes > 0 && fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
                using (var sr = new StreamReader(fs, Encoding.UTF8)) return sr.ReadToEnd();
            }
        }

        public static string FormatTokens(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("N2", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1000.0).ToString("N1", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        public static string FormatReset(DateTime? t)
        {
            if (t == null) return "";
            DateTime now = DateTime.Now, v = t.Value;
            if (v <= now) return "초기화됨";
            TimeSpan span = v - now;
            string s = v.Date == now.Date ? v.ToString("HH:mm", Ko) : v.ToString("M'/'d(ddd) HH:mm", Ko);
            if (span.TotalHours < 24) s += string.Format(" · {0}시간 {1}분 남음", (int)Math.Floor(span.TotalHours), span.Minutes);
            else s += string.Format(" · {0}일 {1}시간 남음", span.Days, span.Hours);
            return s;
        }

        public static void WriteStdout(string text)
        {
            using (var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))) w.Write(text);
        }
    }

    // ---------- 상태표시줄 명령 ----------
    static class Statusline
    {
        public static int Run()
        {
            string raw = "";
            try { using (var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) raw = r.ReadToEnd(); } catch { }
            object input = null;
            try { input = Json.Parse(raw.TrimStart('﻿')); } catch { }
            object rl = Json.Get(input, "rate_limits");

            try
            {
                Directory.CreateDirectory(Paths.StatuslineDir);
                string tp = Json.Str(Json.Get(input, "transcript_path"));
                string tm = null;
                try { if (!string.IsNullOrEmpty(tp) && File.Exists(tp)) tm = File.GetLastWriteTimeUtc(tp).ToString("o"); } catch { }
                string sid = Json.Str(Json.Get(input, "session_id"));
                var outd = new Dictionary<string, object>();
                outd["received_at"] = DateTime.UtcNow.ToString("o");
                outd["session_id"] = sid;
                outd["transcript_mtime"] = tm;
                outd["rate_limits"] = rl;
                string id = Regex.Replace(string.IsNullOrEmpty(sid) ? "unknown" : sid, "[^A-Za-z0-9_-]", "_");
                string file = Path.Combine(Paths.StatuslineDir, id + ".json"), tmp = file + ".tmp";
                File.WriteAllText(tmp, Json.Pretty(outd), new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);

                // 3일 넘게 갱신되지 않은 세션 파일 정리
                DateTime cutoff = DateTime.Now.AddDays(-3);
                foreach (var f in Directory.GetFiles(Paths.StatuslineDir))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                }
            }
            catch { }

            string line = rl == null ? "사용률 정보 없음"
                : "5시간 " + Pct(Json.Get(rl, "five_hour")) + " · 주간 " + Pct(Json.Get(rl, "seven_day"));
            Util.WriteStdout(line);
            return 0;
        }

        static string Pct(object w)
        {
            double? p = Json.Num(Json.Get(w, "used_percentage"));
            return p == null ? "-" : Math.Round(p.Value).ToString(CultureInfo.InvariantCulture) + "%";
        }
    }

    // ---------- 사용률·토큰 읽기 ----------
    class LimitResult
    {
        public bool Ok;
        public string Plan = "";
        public double? FiveHour, Week;
        public DateTime? FiveReset, WeekReset, SnapshotAt;
        public string Error = "";

        public Dictionary<string, object> ToDict()
        {
            Func<DateTime?, object> f = t => t == null ? null : (object)t.Value.ToString("yyyy-MM-dd HH:mm:ss");
            return new Dictionary<string, object> {
                { "Ok", Ok }, { "Plan", Plan }, { "FiveHour", FiveHour }, { "FiveReset", f(FiveReset) },
                { "Week", Week }, { "WeekReset", f(WeekReset) }, { "SnapshotAt", f(SnapshotAt) }, { "Error", Error } };
        }
    }

    class FileState { public long Pos; public long[] Before; public long[] Today; }

    class TokenSum { public long Input, Cached, Output; public int Count; }

    class UsageReader
    {
        DateTime tokDay = DateTime.MinValue;
        Dictionary<string, FileState> claudeFiles, codexFiles;
        Dictionary<string, long[]> claudeMsgs;

        // Claude: 세션마다 마지막으로 받은 값이 달라서 오래 쉬는 세션은 예전 값을 갖고 있다.
        // 같은 사용 기간(resets_at 동일) 안에서는 사용률이 줄지 않으므로
        // 5시간·주간 각각 "초기화 시각이 가장 늦은 값 → 그중 사용률이 가장 높은 값"을 최신 값으로 고른다.
        public LimitResult ClaudeLimits()
        {
            var r = new LimitResult();
            if (!Directory.Exists(Paths.ClaudeDir)) { r.Error = "Claude Code 미설치"; return r; }
            DateTime? snap5 = null, snapW = null;
            if (Directory.Exists(Paths.StatuslineDir))
            {
                foreach (var f in Directory.GetFiles(Paths.StatuslineDir, "*.json"))
                {
                    object c;
                    try { c = Json.Parse(File.ReadAllText(f, Encoding.UTF8)); } catch { continue; }
                    object rl = Json.Get(c, "rate_limits");
                    if (rl == null) continue;
                    DateTime? t = Util.IsoToLocal(Json.Get(c, "transcript_mtime")) ?? Util.IsoToLocal(Json.Get(c, "received_at"));
                    Pick(Json.Get(rl, "five_hour"), t, ref r.FiveHour, ref r.FiveReset, ref snap5);
                    Pick(Json.Get(rl, "seven_day"), t, ref r.Week, ref r.WeekReset, ref snapW);
                }
            }
            r.SnapshotAt = snap5 == null ? snapW : snapW == null ? snap5 : (snap5 > snapW ? snap5 : snapW);
            r.Ok = r.FiveHour != null || r.Week != null;
            if (!r.Ok) r.Error = "터미널에서 Claude Code를 쓰면 표시됩니다";
            return r;
        }

        static void Pick(object w, DateTime? t, ref double? pct, ref DateTime? reset, ref DateTime? snap)
        {
            if (w == null) return;
            double? p = Json.Num(Json.Get(w, "used_percentage"));
            if (p == null) return;
            DateTime rs = Util.EpochToLocal(Json.Get(w, "resets_at")) ?? DateTime.MinValue;
            DateTime cur = reset ?? DateTime.MinValue;
            if (pct == null || rs > cur || (rs == cur && p > pct))
            {
                pct = p;
                reset = rs == DateTime.MinValue ? (DateTime?)null : rs;
                snap = t;
            }
        }

        List<FileInfo> CodexRecentFiles(int days)
        {
            var list = new List<FileInfo>();
            for (int i = 0; i <= days; i++)
            {
                DateTime d = DateTime.Now.AddDays(-i);
                string dir = Path.Combine(Paths.CodexDir, "sessions", d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));
                if (Directory.Exists(dir)) list.AddRange(new DirectoryInfo(dir).GetFiles("rollout-*.jsonl"));
            }
            return list;
        }

        public LimitResult CodexLimits()
        {
            var r = new LimitResult();
            if (!Directory.Exists(Paths.CodexDir)) { r.Error = "Codex 미설치"; return r; }
            var all = CodexRecentFiles(14);
            if (all.Count == 0) { r.Error = "최근 14일 Codex 사용 기록 없음"; return r; }
            // Codex는 기록 파일을 열어 둔 채로 쓰기 때문에 Windows가 수정 시각을 늦게 갱신한다.
            // 수정 시각 최신 5개와 이름(세션 시작 시각) 최신 5개를 모두 보고, 기록 시각이 가장 늦은 값을 쓴다.
            var files = all.OrderByDescending(f => f.LastWriteTime).Take(5)
                .Concat(all.OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(5))
                .GroupBy(f => f.FullName).Select(g => g.First());
            LimitResult best = null;
            foreach (var f in files)
            {
                var c = LastCodexLimit(f.FullName);
                if (c != null && (best == null || c.SnapshotAt > best.SnapshotAt)) best = c;
            }
            if (best != null) return best;
            r.Error = "로그에 사용률 기록 없음";
            return r;
        }

        // 기록 파일의 마지막 사용률 기록 (없으면 null)
        static LimitResult LastCodexLimit(string path)
        {
            string[] lines;
            try { lines = Util.ReadShared(path, 2 * 1024 * 1024).Split('\n'); } catch { return null; }
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string ln = lines[i];
                if (!(ln.Contains("\"token_count\"") && ln.Contains("\"used_percent\""))) continue;
                object j;
                try { j = Json.Parse(ln); } catch { continue; }
                object rl = Json.Get(j, "payload", "rate_limits");
                if (rl == null) continue;
                string lid = Json.Str(Json.Get(rl, "limit_id"));
                if (!string.IsNullOrEmpty(lid) && lid != "codex") continue;
                var r = new LimitResult();
                foreach (var key in new[] { "primary", "secondary" })
                {
                    object w = Json.Get(rl, key);
                    if (w == null) continue;
                    double? pct = Json.Num(Json.Get(w, "used_percent"));
                    DateTime? rst = Util.EpochToLocal(Json.Get(w, "resets_at"));
                    if (Json.Long(Json.Get(w, "window_minutes")) <= 360) { r.FiveHour = pct; r.FiveReset = rst; }
                    else { r.Week = pct; r.WeekReset = rst; }
                }
                r.Plan = Json.Str(Json.Get(rl, "plan_type")) ?? "";
                r.SnapshotAt = Util.IsoToLocal(Json.Get(j, "timestamp"));
                r.Ok = r.FiveHour != null || r.Week != null;
                if (r.Ok && r.SnapshotAt != null) return r;
            }
            return null;
        }

        // 지난번 읽은 위치 이후에 추가된 완성된 줄만 돌려준다
        static List<string> ReadNewLines(string path, FileState st)
        {
            var res = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length < st.Pos) st.Pos = 0;
                long len = fs.Length - st.Pos;
                if (len <= 0) return res;
                fs.Seek(st.Pos, SeekOrigin.Begin);
                var buf = new byte[len];
                int read = 0;
                while (read < len) { int n = fs.Read(buf, read, (int)(len - read)); if (n <= 0) break; read += n; }
                if (read == 0) return res;
                int last = Array.LastIndexOf(buf, (byte)10, read - 1);
                if (last < 0) return res;
                string text = Encoding.UTF8.GetString(buf, 0, last + 1);
                st.Pos += last + 1;
                res.AddRange(text.Split('\n'));
            }
            return res;
        }

        void ResetIfNewDay()
        {
            if (DateTime.Today == tokDay) return;
            tokDay = DateTime.Today;
            claudeFiles = new Dictionary<string, FileState>();
            codexFiles = new Dictionary<string, FileState>();
            claudeMsgs = new Dictionary<string, long[]>();
        }

        public TokenSum ClaudeTodayTokens()
        {
            ResetIfNewDay();
            string proj = Path.Combine(Paths.ClaudeDir, "projects");
            if (Directory.Exists(proj))
            {
                foreach (var f in new DirectoryInfo(proj).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                {
                    // 열린 채로 쓰이는 파일은 수정 시각·크기가 늦게 갱신되므로 하루 여유를 두고,
                    // 새 내용 여부는 ReadNewLines가 파일을 직접 열어 실제 크기로 판단한다
                    if (f.LastWriteTime < tokDay.AddDays(-1)) continue;
                    FileState st;
                    if (!claudeFiles.TryGetValue(f.FullName, out st)) { st = new FileState(); claudeFiles[f.FullName] = st; }
                    List<string> lines;
                    try { lines = ReadNewLines(f.FullName, st); } catch { continue; }
                    foreach (var ln in lines)
                    {
                        if (!(ln.Contains("\"usage\"") && ln.Contains("\"assistant\""))) continue;
                        object j;
                        try { j = Json.Parse(ln); } catch { continue; }
                        if (Json.Str(Json.Get(j, "type")) != "assistant") continue;
                        object u = Json.Get(j, "message", "usage");
                        if (u == null) continue;
                        DateTime? t = Util.IsoToLocal(Json.Get(j, "timestamp"));
                        if (t == null || t.Value.Date != tokDay) continue;
                        // 같은 응답이 여러 줄에 중복 기록되므로 메시지 ID로 한 번만 센다
                        string id = Json.Str(Json.Get(j, "message", "id"));
                        if (string.IsNullOrEmpty(id)) id = Json.Str(Json.Get(j, "requestId"));
                        if (string.IsNullOrEmpty(id)) id = Json.Str(Json.Get(j, "uuid"));
                        if (string.IsNullOrEmpty(id)) continue;
                        claudeMsgs[id] = new[] {
                            Json.Long(Json.Get(u, "input_tokens")), Json.Long(Json.Get(u, "cache_creation_input_tokens")),
                            Json.Long(Json.Get(u, "cache_read_input_tokens")), Json.Long(Json.Get(u, "output_tokens")) };
                    }
                }
            }
            var s = new TokenSum();
            foreach (var v in claudeMsgs.Values) { s.Input += v[0] + v[1] + v[2]; s.Cached += v[2]; s.Output += v[3]; }
            s.Count = claudeMsgs.Count;
            return s;
        }

        static long JsonInt(string body, string name)
        {
            var m = Regex.Match(body, "\"" + name + "\":(\\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        public TokenSum CodexTodayTokens()
        {
            ResetIfNewDay();
            foreach (var f in CodexRecentFiles(7))
            {
                // Codex는 기록 파일을 열어 둔 채로 쓰므로 수정 시각·크기를 믿지 않고 최근 7일 파일을 모두 확인한다
                // (처음 한 번만 전체를 읽고, 이후에는 늘어난 부분만 읽는다)
                FileState st;
                if (!codexFiles.TryGetValue(f.FullName, out st)) { st = new FileState(); codexFiles[f.FullName] = st; }
                List<string> lines;
                try { lines = ReadNewLines(f.FullName, st); } catch { continue; }
                foreach (var ln in lines)
                {
                    if (!ln.Contains("\"total_token_usage\"")) continue;
                    var mt = Regex.Match(ln, "^\\{\"timestamp\":\"([^\"]+)\"");
                    var mu = Regex.Match(ln, "\"total_token_usage\":\\{([^}]*)\\}");
                    if (!(mt.Success && mu.Success)) continue;
                    DateTime? t = Util.IsoToLocal(mt.Groups[1].Value);
                    if (t == null) continue;
                    string body = mu.Groups[1].Value;
                    var vals = new[] { JsonInt(body, "input_tokens"), JsonInt(body, "cached_input_tokens"), JsonInt(body, "output_tokens") };
                    if (t.Value.Date < tokDay) st.Before = vals; else if (t.Value.Date == tokDay) st.Today = vals;
                }
            }
            // 세션별 누적값: (오늘 마지막 누적) - (어제까지 마지막 누적)
            var s = new TokenSum();
            foreach (var st in codexFiles.Values)
            {
                if (st.Today == null) continue;
                long[] b = st.Before ?? new long[3];
                s.Input += st.Today[0] - b[0]; s.Cached += st.Today[1] - b[1]; s.Output += st.Today[2] - b[2]; s.Count++;
            }
            return s;
        }
    }

    // ---------- Claude 연동·자동 실행 설정 ----------
    static class Setup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "AIUsageWidget";

        public static string OurCommand() { return "\"" + Paths.ExePath + "\" --statusline"; }

        static bool IsOurs(string cmd)
        {
            return cmd != null && cmd.IndexOf("--statusline", StringComparison.OrdinalIgnoreCase) >= 0
                && cmd.IndexOf("AIUsageWidget", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string CurrentCommand()
        {
            try
            {
                if (!File.Exists(Paths.ClaudeSettings)) return null;
                var d = Json.Parse(File.ReadAllText(Paths.ClaudeSettings, Encoding.UTF8).TrimStart('﻿'));
                return Json.Str(Json.Get(d, "statusLine", "command"));
            }
            catch { return null; }
        }

        public static bool IsConfigured() { return CurrentCommand() == OurCommand(); }

        public static bool IsOursButMoved()
        {
            string cmd = CurrentCommand();
            return IsOurs(cmd) && cmd != OurCommand();
        }

        // interactive=false 는 점검용 (묻지 않고 진행)
        // 반환: 결과 설명 문구
        public static string ConfigureClaude(string settingsPath, bool interactive)
        {
            if (!Directory.Exists(Path.GetDirectoryName(settingsPath))) return "Claude Code가 설치되어 있지 않습니다.";
            Dictionary<string, object> d;
            string original = null;
            if (File.Exists(settingsPath))
            {
                original = File.ReadAllText(settingsPath, Encoding.UTF8);
                try { d = Json.Parse(original.TrimStart('﻿')) as Dictionary<string, object>; } catch { d = null; }
                if (d == null) return "Claude 설정 파일을 해석할 수 없어 변경하지 않았습니다: " + settingsPath;
            }
            else d = new Dictionary<string, object>();

            string cmd = Json.Str(Json.Get(d, "statusLine", "command"));
            string ours = OurCommand();
            if (cmd == ours && Json.Get(d, "statusLine", "refreshInterval") != null) return "이미 연동되어 있습니다.";

            if (interactive && !IsOurs(cmd))
            {
                string msg = cmd == null
                    ? "Claude 사용률을 표시하려면 Claude Code 설정에 상태표시줄 연동을 추가해야 합니다.\n\n" +
                      "변경 파일: " + settingsPath + "\n(원본은 같은 폴더에 백업합니다)\n\n추가할까요?"
                    : "Claude Code에 이미 다른 상태표시줄 설정이 있습니다.\n\n현재: " + cmd + "\n\n" +
                      "위젯 연동으로 바꿀까요? 기존 상태표시줄은 더 이상 표시되지 않습니다.\n(원본은 같은 폴더에 백업합니다)";
                if (MessageBox.Show(msg, "AI 사용량 - Claude 연동", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return "Claude 연동을 건너뛰었습니다.";
            }

            if (original != null)
                File.WriteAllText(settingsPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), original, new UTF8Encoding(false));
            d["statusLine"] = new Dictionary<string, object> { { "type", "command" }, { "command", ours }, { "refreshInterval", 60 } };
            File.WriteAllText(settingsPath, Json.Pretty(d) + "\n", new UTF8Encoding(false));
            return cmd != null && IsOurs(cmd) ? "실행 파일 위치가 바뀌어 연동 경로를 고쳤습니다." : "Claude 연동을 추가했습니다.";
        }

        public static bool AutoStartEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        public static void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Paths.ExePath + "\"");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
        }

        // 자동 실행이 켜져 있는데 실행 파일을 옮긴 경우 경로를 맞춘다
        public static void FixAutoStartPath()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                var v = k.GetValue(RunName) as string;
                string want = "\"" + Paths.ExePath + "\"";
                if (v != null && v != want) k.SetValue(RunName, want);
            }
        }
    }

    // ---------- 화면 ----------
    class BarRow
    {
        public Panel Fill; public Label Pct; public Label Reset;
    }

    class WidgetApp : ApplicationContext
    {
        static readonly Color CBg = Color.FromArgb(32, 33, 38), CFg = Color.FromArgb(236, 236, 240), CSub = Color.FromArgb(150, 152, 162),
            CBarBg = Color.FromArgb(58, 60, 68), CGood = Color.FromArgb(76, 175, 120), CWarn = Color.FromArgb(230, 160, 50),
            CBad = Color.FromArgb(225, 80, 80), CIdle = Color.FromArgb(110, 112, 122);
        const int BarWidth = 120;

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        readonly Font fHead = new Font("Malgun Gothic", 9.5f, FontStyle.Bold), fBold = new Font("Malgun Gothic", 9f, FontStyle.Bold),
            fSmall = new Font("Malgun Gothic", 8f);
        readonly Form form = new Form();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly UsageReader reader = new UsageReader();

        Label lblClaudeHead, lblClaudeStat, lblCodexHead, lblCodexStat, lblTokClaude, lblTokCodex, lblUpdated;
        BarRow rowClaude5, rowClaudeW, rowCodex5, rowCodexW;
        ToolStripMenuItem miPin, miAuto, miClaude;
        IntPtr trayHandle = IntPtr.Zero;
        bool pinned, setupDone, exiting;
        DateTime hiddenAt = DateTime.MinValue, tokLast = DateTime.MinValue;
        LimitResult claude = new LimitResult(), codex = new LimitResult();
        TokenSum tokC, tokX;

        public WidgetApp()
        {
            BuildForm();
            BuildMenu();
            LoadSettings();
            try { Setup.FixAutoStartPath(); } catch { }
            // 이미 연동했는데 실행 파일을 옮긴 경우에만 묻지 않고 경로를 고친다
            try { if (Setup.IsOursButMoved()) Setup.ConfigureClaude(Paths.ClaudeSettings, false); } catch { }

            SetTrayIcon(null);
            tray.Visible = true;
            UpdateAll(true);
            timer.Interval = 60000;
            timer.Tick += (s, e) => { try { UpdateAll(false); } catch { } };
            timer.Start();
            if (pinned) form.Show();
            if (!setupDone) FirstRunSetup();
        }

        void FirstRunSetup()
        {
            setupDone = true;
            SaveSettings();
            string r = Setup.ConfigureClaude(Paths.ClaudeSettings, true);
            if (!Setup.AutoStartEnabled() &&
                MessageBox.Show("Windows를 켤 때 위젯을 자동으로 실행할까요?\n(나중에 트레이 메뉴에서 바꿀 수 있습니다)", "AI 사용량",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try { Setup.SetAutoStart(true); } catch { }
            }
            miAuto.Checked = Setup.AutoStartEnabled();
            tray.ShowBalloonTip(5000, "AI 사용량", r + "\n작업 표시줄 오른쪽 트레이 아이콘을 클릭하면 창이 열립니다.", ToolTipIcon.Info);
        }

        Label Lbl(string text, int x, int y, int w, int h, Font font, Color color)
        {
            var l = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = font, ForeColor = color, BackColor = Color.Transparent };
            form.Controls.Add(l);
            return l;
        }

        BarRow Row(string label, int y)
        {
            Lbl(label, 14, y, 44, 18, fSmall, CSub);
            var back = new Panel { Location = new Point(60, y + 4), Size = new Size(BarWidth, 10), BackColor = CBarBg };
            var fill = new Panel { Location = new Point(0, 0), Size = new Size(0, 10) };
            back.Controls.Add(fill);
            form.Controls.Add(back);
            var pct = Lbl("-", 184, y, 56, 18, fBold, CFg);
            pct.TextAlign = ContentAlignment.TopRight;
            var rst = Lbl("", 244, y + 1, 202, 18, fSmall, CSub);
            return new BarRow { Fill = fill, Pct = pct, Reset = rst };
        }

        void BuildForm()
        {
            form.Text = "AI 사용량";
            form.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            form.MaximizeBox = false;
            form.TopMost = true;
            form.ShowInTaskbar = false;
            form.BackColor = CBg;
            form.ForeColor = CFg;
            form.ClientSize = new Size(450, 236);
            form.StartPosition = FormStartPosition.Manual;
            form.KeyPreview = true;

            lblClaudeHead = Lbl("Claude", 12, 8, 140, 20, fHead, CFg);
            lblClaudeStat = Lbl("", 150, 10, 296, 18, fSmall, CSub);
            lblClaudeStat.TextAlign = ContentAlignment.TopRight;
            rowClaude5 = Row("5시간", 32);
            rowClaudeW = Row("주간", 52);

            lblCodexHead = Lbl("Codex", 12, 80, 140, 20, fHead, CFg);
            lblCodexStat = Lbl("", 150, 82, 296, 18, fSmall, CSub);
            lblCodexStat.TextAlign = ContentAlignment.TopRight;
            rowCodex5 = Row("5시간", 104);
            rowCodexW = Row("주간", 124);

            form.Controls.Add(new Panel { Location = new Point(12, 150), Size = new Size(426, 1), BackColor = CBarBg });
            Lbl("오늘 토큰", 12, 156, 140, 18, fBold, CFg);
            lblTokClaude = Lbl("Claude  -", 14, 176, 432, 18, fSmall, CFg);
            lblTokCodex = Lbl("Codex   -", 14, 194, 432, 18, fSmall, CFg);
            lblUpdated = Lbl("", 12, 216, 330, 16, fSmall, CSub);

            var btn = new Button { Text = "새로고침", Font = fSmall, FlatStyle = FlatStyle.Flat, ForeColor = CFg, BackColor = CBg,
                Location = new Point(368, 210), Size = new Size(70, 22) };
            btn.FlatAppearance.BorderColor = CBarBg;
            btn.Click += (s, e) => UpdateAll(true);
            form.Controls.Add(btn);

            try { form.Icon = Icon.ExtractAssociatedIcon(Paths.ExePath); } catch { }

            form.Deactivate += (s, e) => { if (!pinned && !menu.Visible) HideWidget(); };
            form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) HideWidget(); };
            form.ResizeEnd += (s, e) => SaveSettings();
            form.FormClosing += (s, e) =>
            {
                // 창의 X는 종료가 아니라 트레이로 숨기기
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideWidget(); }
            };

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            form.Location = new Point(wa.Right - form.Width - 16, wa.Bottom - form.Height - 16);
        }

        void BuildMenu()
        {
            menu.Items.Add("창 열기 / 닫기", null, (s, e) => ToggleWidget());
            miPin = new ToolStripMenuItem("고정 (자동으로 닫지 않음)", null, (s, e) => { pinned = !pinned; miPin.Checked = pinned; SaveSettings(); });
            menu.Items.Add(miPin);
            menu.Items.Add("지금 새로고침", null, (s, e) => UpdateAll(true));
            menu.Items.Add(new ToolStripSeparator());
            miAuto = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (s, e) =>
            {
                try { Setup.SetAutoStart(!Setup.AutoStartEnabled()); } catch (Exception ex) { MessageBox.Show(ex.Message, "AI 사용량"); }
                miAuto.Checked = Setup.AutoStartEnabled();
            });
            miAuto.Checked = Setup.AutoStartEnabled();
            menu.Items.Add(miAuto);
            miClaude = new ToolStripMenuItem("Claude 연동 설정...", null, (s, e) =>
            {
                string r;
                try { r = Setup.ConfigureClaude(Paths.ClaudeSettings, true); } catch (Exception ex) { r = "오류: " + ex.Message; }
                MessageBox.Show(r, "AI 사용량");
                UpdateAll(true);
            });
            menu.Items.Add(miClaude);
            menu.Items.Add("데이터 폴더 열기", null, (s, e) => { try { Directory.CreateDirectory(Paths.AppDir); Process.Start(Paths.AppDir); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (s, e) => ExitWidget());
            menu.Opening += (s, e) => { miAuto.Checked = Setup.AutoStartEnabled(); miClaude.Checked = Setup.IsConfigured(); };

            tray.Text = "AI 사용량";
            tray.ContextMenuStrip = menu;
            form.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWidget(); };
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(Paths.WidgetSettings)) return;
                object s = Json.Parse(File.ReadAllText(Paths.WidgetSettings, Encoding.UTF8));
                object x = Json.Get(s, "X"), y = Json.Get(s, "Y");
                if (x != null && y != null)
                {
                    var pt = new Point((int)Json.Long(x), (int)Json.Long(y));
                    if (Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(pt))) form.Location = pt;
                }
                pinned = Json.Get(s, "Pinned") is bool && (bool)Json.Get(s, "Pinned");
                setupDone = Json.Get(s, "SetupDone") is bool && (bool)Json.Get(s, "SetupDone");
                miPin.Checked = pinned;
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Paths.AppDir);
                var d = new Dictionary<string, object> { { "X", form.Left }, { "Y", form.Top }, { "Pinned", pinned }, { "SetupDone", setupDone } };
                File.WriteAllText(Paths.WidgetSettings, Json.Pretty(d), new UTF8Encoding(false));
            }
            catch { }
        }

        void ShowWidget()
        {
            try { UpdateAll(false); } catch { }
            form.Show();
            form.Activate();
        }

        void HideWidget()
        {
            if (!form.Visible) return;
            SaveSettings();
            hiddenAt = DateTime.Now;
            form.Hide();
        }

        void ToggleWidget()
        {
            if (form.Visible) HideWidget();
            // 창이 열린 상태에서 트레이 아이콘을 누르면 자동 숨김이 먼저 일어나므로 다시 열지 않는다
            else if ((DateTime.Now - hiddenAt).TotalMilliseconds > 400) ShowWidget();
        }

        void ExitWidget()
        {
            exiting = true;
            SaveSettings();
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            form.Close();
            ExitThread();
        }

        static Color PctColor(double? p)
        {
            if (p == null) return CIdle;
            if (p >= 90) return CBad;
            if (p >= 70) return CWarn;
            return CGood;
        }

        void SetRow(BarRow row, double? pct, DateTime? reset, bool stale)
        {
            if (stale)
            {
                row.Fill.Width = 0; row.Pct.Text = "—"; row.Pct.ForeColor = CSub; row.Reset.Text = "초기화 지남 · 다음 사용 때 갱신";
                return;
            }
            if (pct == null)
            {
                row.Fill.Width = 0; row.Pct.Text = "확인필요"; row.Pct.ForeColor = CSub; row.Reset.Text = "";
                return;
            }
            double p = Math.Max(0, Math.Min(100, pct.Value));
            row.Fill.Width = (int)(BarWidth * p / 100);
            row.Fill.BackColor = PctColor(pct);
            row.Pct.Text = pct.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
            row.Pct.ForeColor = CFg;
            row.Reset.Text = Util.FormatReset(reset);
        }

        static bool Stale(LimitResult r, DateTime? reset) { return r.Ok && reset != null && reset <= DateTime.Now; }

        void SetTrayIcon(double? pct)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var brush = new SolidBrush(PctColor(pct)))
                using (var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.FillEllipse(brush, 0, 0, 31, 31);
                    string txt = pct == null ? "AI" : pct >= 100 ? "!!" : pct.Value.ToString("0", CultureInfo.InvariantCulture);
                    g.DrawString(txt, font, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                }
                IntPtr h = bmp.GetHicon();
                IntPtr old = trayHandle;
                tray.Icon = Icon.FromHandle(h);
                trayHandle = h;
                if (old != IntPtr.Zero) DestroyIcon(old);
            }
        }

        void UpdateAll(bool force)
        {
            try { claude = reader.ClaudeLimits(); } catch (Exception ex) { claude = new LimitResult { Error = "오류: " + ex.Message }; }
            try { codex = reader.CodexLimits(); } catch (Exception ex) { codex = new LimitResult { Error = "오류: " + ex.Message }; }
            if (force || (DateTime.Now - tokLast).TotalSeconds >= 300)
            {
                tokLast = DateTime.Now;
                try { tokC = reader.ClaudeTodayTokens(); } catch { }
                try { tokX = reader.CodexTodayTokens(); } catch { }
            }
            Render();
        }

        static string Title(string s) { return string.IsNullOrEmpty(s) ? "" : char.ToUpper(s[0]) + s.Substring(1).ToLower(); }

        void Render()
        {
            DateTime now = DateTime.Now;
            bool c5s = Stale(claude, claude.FiveReset), cws = Stale(claude, claude.WeekReset);
            bool x5s = Stale(codex, codex.FiveReset), xws = Stale(codex, codex.WeekReset);

            if (claude.Error != "") { lblClaudeStat.Text = claude.Error; lblClaudeStat.ForeColor = CWarn; }
            // 데스크탑앱은 상태표시줄을 실행하지 않아 터미널 Claude Code가 마지막으로 받은 값이다
            else { lblClaudeStat.Text = "터미널 Claude · 마지막 사용 " + claude.SnapshotAt.Value.ToString("M'/'d HH:mm", Util.Ko) + " 기준"; lblClaudeStat.ForeColor = CSub; }
            SetRow(rowClaude5, claude.FiveHour, claude.FiveReset, c5s);
            SetRow(rowClaudeW, claude.Week, claude.WeekReset, cws);

            lblCodexHead.Text = "Codex" + (codex.Plan != "" ? " · " + Title(codex.Plan) : "");
            if (codex.Error != "") { lblCodexStat.Text = codex.Error; lblCodexStat.ForeColor = CWarn; }
            else { lblCodexStat.Text = "마지막 사용 " + codex.SnapshotAt.Value.ToString("M'/'d HH:mm", Util.Ko) + " 기준"; lblCodexStat.ForeColor = CSub; }
            SetRow(rowCodex5, codex.FiveHour, codex.FiveReset, x5s);
            SetRow(rowCodexW, codex.Week, codex.WeekReset, xws);

            if (tokC != null)
                lblTokClaude.Text = string.Format("Claude   입력 {0} (캐시 {1}) · 출력 {2} · 요청 {3}회",
                    Util.FormatTokens(tokC.Input), Util.FormatTokens(tokC.Cached), Util.FormatTokens(tokC.Output), tokC.Count);
            if (tokX != null)
                lblTokCodex.Text = string.Format("Codex    입력 {0} (캐시 {1}) · 출력 {2} · 세션 {3}개",
                    Util.FormatTokens(tokX.Input), Util.FormatTokens(tokX.Cached), Util.FormatTokens(tokX.Output), tokX.Count);
            lblUpdated.Text = "화면 갱신 " + now.ToString("HH:mm:ss") + " · 1분마다 자동";

            // 트레이: 두 도구 5시간 사용률 중 높은 값
            var vals = new List<double>();
            if (claude.FiveHour != null && !c5s) vals.Add(claude.FiveHour.Value);
            if (codex.FiveHour != null && !x5s) vals.Add(codex.FiveHour.Value);
            SetTrayIcon(vals.Count > 0 ? (double?)vals.Max() : null);

            Func<double?, bool, string> fmt = (v, st) => st ? "-" : v == null ? "?" : v.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
            string tip = string.Format("Claude {0}/{1} · Codex {2}/{3} (5시간/주간)",
                fmt(claude.FiveHour, c5s), fmt(claude.Week, cws), fmt(codex.FiveHour, x5s), fmt(codex.Week, xws));
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string a = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (a == "--statusline") return Statusline.Run();
            if (a == "--dump") return Dump();
            if (a == "--configure-test" && args.Length > 1)
            {
                Util.WriteStdout(Setup.ConfigureClaude(args[1], false));
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\AIUsageWidgetExe", out created))
            {
                if (!created)
                {
                    MessageBox.Show("이미 실행 중입니다. 작업 표시줄 오른쪽 트레이 아이콘을 확인하세요.", "AI 사용량");
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetApp());
            }
            return 0;
        }

        static int Dump()
        {
            var r = new UsageReader();
            var sw = Stopwatch.StartNew();
            var c = r.ClaudeTodayTokens();
            var x = r.CodexTodayTokens();
            var d = new Dictionary<string, object> {
                { "Claude", r.ClaudeLimits().ToDict() },
                { "Codex", r.CodexLimits().ToDict() },
                { "ClaudeTokensToday", new Dictionary<string, object> { { "Input", c.Input }, { "Cached", c.Cached }, { "Output", c.Output }, { "Requests", c.Count } } },
                { "CodexTokensToday", new Dictionary<string, object> { { "Input", x.Input }, { "Cached", x.Cached }, { "Output", x.Output }, { "Sessions", x.Count } } },
                { "StatuslineConfigured", Setup.IsConfigured() },
                { "ElapsedSec", Math.Round(sw.Elapsed.TotalSeconds, 1) } };
            Util.WriteStdout(Json.Pretty(d));
            return 0;
        }
    }
}
