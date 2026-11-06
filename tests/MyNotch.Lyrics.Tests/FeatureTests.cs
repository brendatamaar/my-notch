using System.Text;
using System.Text.Json;
using MyNotch.Models;
using MyNotch.Services;

/// <summary>Pure-logic checks for the clipboard detectors, cron, ICS feeds, agent sessions, Claude Code hooks, CLI parsers and settings.</summary>
internal static class FeatureTests
{
    private static void Check(bool value, string name) { if (!value) throw new Exception("FeatureTests: " + name); }

    public static void Run()
    {
        Clipboard();
        Cron();
        Ics();
        Sessions();
        Hooks();
        CliParsers();
        Settings();
        Console.WriteLine("PASS: clipboard detectors (JWT exp, timestamp, UUID v4/v7, color, cron, URL, Base64), cron next-runs, ICS recurrence/exceptions/zones, agent sessions, Claude Code hook mapping and decisions, docker/git/gh parsers, settings persistence, cmd escaping, Run-key parsing");
    }

    /// <summary>Real HTTP round trips against the webhook server on a free loopback port.</summary>
    public static async Task RunHttpAsync()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var server = new WebhookServerService(port);
        var payloads = new List<AgentPayload>();
        var ended = new List<string>();
        server.PayloadReceived += (_, p) => { lock (payloads) payloads.Add(p); };
        server.SessionEnded += (_, key) => { lock (ended) ended.Add(key); };
        await server.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
            async Task<(int Status, string Body)> Post(string path, string json, string? origin = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json) };
                if (origin is not null) request.Headers.Add("Origin", origin);
                using var response = await http.SendAsync(request);
                return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            const string bash = """{"session_id":"s9","cwd":"C:\\src\\app","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"npm test"}}""";

            var none = await Post("claude/permission", bash);
            Check(none == (200, ""), "no handler -> empty decision");
            server.ApprovalHandler = r => Task.FromResult(r.Tool == "Bash" ? ApprovalDecision.Allow : ApprovalDecision.Deny);
            var allowed = await Post("claude/permission", bash);
            using (var doc = JsonDocument.Parse(allowed.Body))
                Check(allowed.Status == 200 && doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString() == "allow", "permission allowed over HTTP");
            var denied = await Post("claude/permission", bash.Replace("\"Bash\"", "\"Write\""));
            Check(denied.Body.Contains("\"deny\""), "permission denied over HTTP");

            var hook = await Post("claude/hook", """{"hook_event_name":"UserPromptSubmit","session_id":"s9","cwd":"C:\\src\\app","prompt":"go"}""");
            Check(hook == (202, ""), "hook accepted with empty stdout");
            var end = await Post("claude/hook", """{"hook_event_name":"SessionEnd","session_id":"s9"}""");
            var forged = await Post("agent", """{"agent":"evil","status":"awaiting_approval"}""", origin: "https://example.com");
            Check(forged.Status == 403, "browser-origin POST rejected");
            var legacy = await Post("agent", """{"agent":"build","status":"done","message":"ok"}""");
            Check(legacy.Status == 202, "plain /agent still works");
            Check((await Post("claude/hook", "{ nope")).Status == 400, "bad JSON rejected");

            await Task.Delay(100);
            lock (payloads)
            {
                Check(payloads.Count == 2 && payloads[0] is { Agent: "Claude Code", Status: AgentStatus.Running, Session: "s9" } && payloads[1].Agent == "build", "hook and agent payloads raised");
                Check(payloads[0].Pid is > 0, "sending process resolved from the TCP connection");
            }
            lock (ended) Check(ended.Count == 1 && end.Status == 202, "session end raised");
        }
        finally { await server.StopAsync(); }

        // A subscriber that throws must still get the client an answer, not a hung connection.
        var throwing = new WebhookServerService(port);
        throwing.PayloadReceived += (_, _) => throw new InvalidOperationException("subscriber failed");
        await throwing.StartAsync();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.PostAsync($"http://127.0.0.1:{port}/agent", new StringContent("""{"agent":"build","status":"done","message":"ok"}"""));
            Check((int)response.StatusCode == 500, "throwing subscriber answered with 500 instead of hanging");
        }
        finally { await throwing.StopAsync(); }
        Console.WriteLine("PASS: webhook HTTP: /claude/permission allow/deny/no-decision, /claude/hook mapping with empty stdout, session end, sender PID lookup, Origin rejection, legacy /agent");
    }

    private static void Clipboard()
    {
        var now = new DateTimeOffset(2025, 11, 5, 9, 0, 0, TimeSpan.Zero);
        ClipboardKind Kind(string text) => ClipboardAnalyzer.Analyze(text, now).Kind;

        Check(Kind("{\"a\":1}") == ClipboardKind.Json, "JSON still detected");
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = ClipboardAnalyzer.Analyze($"{B64("{\"alg\":\"HS256\"}")}.{B64("{\"sub\":\"1\",\"exp\":1762340400}")}.sig", now);
        Check(jwt.Kind == ClipboardKind.Jwt && jwt.Detail.StartsWith("Expires in 2 hours", StringComparison.Ordinal), "JWT expiry shown: " + jwt.Detail);
        var expired = ClipboardAnalyzer.Analyze($"{B64("{\"alg\":\"HS256\"}")}.{B64("{\"exp\":1700000000}")}.sig", now);
        Check(expired.Detail.StartsWith("Expired", StringComparison.Ordinal), "expired JWT flagged");

        Check(Kind("1730784000") == ClipboardKind.Timestamp, "unix seconds");
        Check(Kind("1730784000123") == ClipboardKind.Timestamp, "unix milliseconds");
        Check(Kind("0812345678") == ClipboardKind.None, "phone-like number with leading zero isn't a timestamp");
        Check(Kind("123456") == ClipboardKind.None, "short number ignored");
        Check(Kind("9999999999") == ClipboardKind.None, "out-of-range number ignored");

        var v4 = ClipboardAnalyzer.Analyze("f47ac10b-58cc-4372-a567-0e02b2c3d479", now);
        Check(v4.Kind == ClipboardKind.Uuid && v4.Detail.Contains("v4") && v4.Detail.Contains("RFC 9562"), "UUID v4");
        var v7 = ClipboardAnalyzer.Analyze("{01932C07-A7AC-7E3A-8F4B-4C2A3E1B5D6F}", now);
        Check(v7.Kind == ClipboardKind.Uuid && v7.Detail.Contains("v7") && v7.Detail.Contains("2024"), "UUID v7 timestamp: " + v7.Detail);
        Check(Kind("f47ac10b58cc4372a5670e02b2c3d479") != ClipboardKind.Uuid, "dashless hex (hash) isn't treated as a UUID");

        Check(ClipboardAnalyzer.Analyze("#F0892E", now).ColorArgb == 0xFFF0892E, "hex color");
        Check(ClipboardAnalyzer.Analyze("#fff", now).ColorArgb == 0xFFFFFFFF, "short hex color");
        Check(ClipboardAnalyzer.Analyze("rgba(255, 0, 0, 0.5)", now).ColorArgb == 0x80FF0000, "rgba color");
        Check(ClipboardAnalyzer.Analyze("#F0892E", now).Detail.Contains("hsl(28, 87%, 56%)"), "HSL conversion");
        Check(Kind("F0892E") != ClipboardKind.Color, "hex without # isn't a color");

        var cron = ClipboardAnalyzer.Analyze("0 9 * * 1-5", now);
        Check(cron.Kind == ClipboardKind.Cron && cron.Detail == "At 09:00 on Monday through Friday", "cron description: " + cron.Detail);
        Check(Kind("*/15 9-17 * * MON-FRI") == ClipboardKind.Cron, "cron with names and steps");
        Check(Kind("@daily") == ClipboardKind.Cron, "cron macro");
        Check(Kind("1 2 3 4 5") == ClipboardKind.None, "five plain numbers aren't cron");

        var url = ClipboardAnalyzer.Analyze("https://example.com/search?q=hello%20world&lang=en", now);
        Check(url.Kind == ClipboardKind.Url && url.Formatted.Contains("q = hello world") && url.Formatted.Contains("lang = en"), "URL parameters decoded");
        Check(Kind("https://example.com/") == ClipboardKind.None, "plain link ignored");
        Check(Kind("name%3Dvalue%26x") == ClipboardKind.Url, "percent-encoded text");

        var b64 = ClipboardAnalyzer.Analyze("aGVsbG8gd29ybGQgZnJvbSBteW5vdGNo", now);
        Check(b64.Kind == ClipboardKind.Base64 && b64.Formatted == "hello world from mynotch", "Base64 text");
        Check(ClipboardAnalyzer.Analyze(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"ok\":true,\"n\":1}")), now).Detail.Contains("JSON"), "Base64 JSON");
        Check(Kind("HelloWorldExample") == ClipboardKind.None, "plain word not Base64");
        Check(Kind("3q2+7wAAAAAAAAAAAAAAAA==") == ClipboardKind.None, "binary Base64 ignored");
        Check(Kind("just some text") == ClipboardKind.None, "plain text ignored");
    }

    private static void Cron()
    {
        Check(CronSchedule.TryParse("0 9 * * 1-5", out var weekdays) && weekdays!.Next(new DateTime(2025, 11, 8, 10, 0, 0)) == new DateTime(2025, 11, 10, 9, 0, 0), "weekday 09:00 skips the weekend");
        Check(CronSchedule.TryParse("0 0 13 * 5", out var either) && either!.Next(new DateTime(2025, 11, 1)) == new DateTime(2025, 11, 7), "day-of-month OR day-of-week");
        Check(CronSchedule.TryParse("0 0 30 2 *", out var never) && never!.Next(new DateTime(2025, 1, 1)) is null, "impossible date returns no run");
        Check(CronSchedule.TryParse("*/20 * * * *", out var every) && every!.NextRuns(new DateTime(2025, 1, 1, 10, 5, 0), 3).SequenceEqual(new[] { new DateTime(2025, 1, 1, 10, 20, 0), new DateTime(2025, 1, 1, 10, 40, 0), new DateTime(2025, 1, 1, 11, 0, 0) }), "step minutes");
        Check(CronSchedule.TryParse("0 0 * * 7", out var sunday) && sunday!.Next(new DateTime(2025, 11, 5))!.Value.DayOfWeek == DayOfWeek.Sunday, "7 means Sunday");
        Check(!CronSchedule.TryParse("61 * * * *", out _) && !CronSchedule.TryParse("* * * *", out _) && !CronSchedule.TryParse("0 0 1 FOO *", out _), "invalid cron rejected");
        Check(CronSchedule.TryParse("30 8 1 JAN,JUL *", out var names) && names!.Describe() == "At 08:30, on day 1 of the month, in January, July", "month names: " + names!.Describe());
    }

    private static void Ics()
    {
        var utc = TimeZoneInfo.Utc;
        string Calendar(string body) => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + body + "END:VCALENDAR\r\n";

        var zoned = IcsCalendar.Parse(Calendar("BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Standup\r\nDTSTART;TZID=SE Asia Standard Time:20251105T093000\r\nDURATION:PT15M\r\nEND:VEVENT\r\n"), utc);
        Check(zoned.Count == 1 && zoned[0].Start.UtcDateTime == new DateTime(2025, 11, 5, 2, 30, 0) && zoned[0].End - zoned[0].Start == TimeSpan.FromMinutes(15), "Windows TZID and DURATION");
        var iana = IcsCalendar.Parse(Calendar("BEGIN:VEVENT\r\nUID:b\r\nDTSTART;TZID=Asia/Jakarta:20251105T093000\r\nDTEND;TZID=Asia/Jakarta:20251105T100000\r\nEND:VEVENT\r\n"), utc);
        Check(iana[0].Start.UtcDateTime == new DateTime(2025, 11, 5, 2, 30, 0), "IANA TZID resolved");
        var custom = IcsCalendar.Parse(Calendar("BEGIN:VTIMEZONE\r\nTZID:Office\r\nBEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:+0800\r\nTZOFFSETTO:+0800\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:c\r\nDTSTART;TZID=Office:20251105T090000\r\nEND:VEVENT\r\n"), utc);
        Check(custom[0].Start.UtcDateTime == new DateTime(2025, 11, 5, 1, 0, 0), "VTIMEZONE offset fallback");

        string weekly = Calendar(
            "BEGIN:VEVENT\r\nUID:w\r\nSUMMARY:Sync\r\nDTSTART:20251103T090000Z\r\nDTEND:20251103T093000Z\r\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=6\r\nEXDATE:20251105T090000Z\r\n" +
            "DESCRIPTION:Join: https://meet.google.com/abc-\r\n defg-hij now\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:w\r\nSUMMARY:Sync (moved)\r\nRECURRENCE-ID:20251107T090000Z\r\nDTSTART:20251107T150000Z\r\nDTEND:20251107T153000Z\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:w\r\nRECURRENCE-ID:20251110T090000Z\r\nDTSTART:20251110T090000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:allday\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20251106\r\nEND:VEVENT\r\n");
        var events = IcsCalendar.Parse(weekly, utc);
        var meetings = IcsCalendar.Occurrences(events, new DateTimeOffset(2025, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var starts = meetings.Select(m => m.Start.UtcDateTime).ToArray();
        Check(starts.SequenceEqual(new[] { new DateTime(2025, 11, 3, 9, 0, 0), new DateTime(2025, 11, 7, 15, 0, 0), new DateTime(2025, 11, 12, 9, 0, 0), new DateTime(2025, 11, 14, 9, 0, 0) }),
            "COUNT, EXDATE, moved and cancelled instances, all-day excluded: " + string.Join(", ", starts));
        Check(meetings[1].Title == "Sync (moved)" && meetings[0].JoinUrl == "https://meet.google.com/abc-defg-hij", "override title and folded join link");

        var lastFriday = IcsCalendar.Parse(Calendar("BEGIN:VEVENT\r\nUID:m\r\nDTSTART:20251031T100000Z\r\nRRULE:FREQ=MONTHLY;BYDAY=-1FR;UNTIL=20251231T235959Z\r\nEND:VEVENT\r\n"), utc);
        var fridays = IcsCalendar.Occurrences(lastFriday, new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)).Select(m => m.Start.Day).ToArray();
        Check(fridays.SequenceEqual(new[] { 31, 28, 26 }), "last Friday monthly with UNTIL: " + string.Join(",", fridays));

        var impossible = IcsCalendar.Parse(Calendar("BEGIN:VEVENT\r\nUID:x\r\nDTSTART:20250101T100000Z\r\nRRULE:FREQ=MONTHLY;BYMONTHDAY=32\r\nEND:VEVENT\r\n"), utc);
        Check(IcsCalendar.Occurrences(impossible, DateTimeOffset.UnixEpoch, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)).Count == 0, "never-matching rule terminates");

        var daily = IcsCalendar.Parse(Calendar("BEGIN:VEVENT\r\nUID:d\r\nDTSTART:20200101T080000Z\r\nRRULE:FREQ=DAILY;INTERVAL=2\r\nEND:VEVENT\r\n"), utc);
        var window = IcsCalendar.Occurrences(daily, new DateTimeOffset(2025, 11, 5, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 11, 9, 0, 0, 0, TimeSpan.Zero));
        Check(window.Count == 2 && window.All(m => (m.Start.UtcDateTime - new DateTime(2020, 1, 1, 8, 0, 0)).Days % 2 == 0), "open-ended series from years ago");

        Check(IcsCalendar.JoinLink("", "Room 4", "https://us02web.zoom.us/j/123456?pwd=abc).") == "https://us02web.zoom.us/j/123456?pwd=abc", "Zoom link trimmed");
        Check(IcsCalendar.TryDuration("P1DT2H30M", out var d) && d == new TimeSpan(1, 2, 30, 0), "ISO duration");
    }

    private static void Sessions()
    {
        var now = DateTimeOffset.Now;
        AgentPayload P(string session, AgentStatus status, long? hwnd = null, DateTimeOffset? at = null) =>
            new() { Agent = "Claude Code", Session = session, Status = status, Hwnd = hwnd, Cwd = @"C:\src\" + session, ReceivedAt = at ?? now };
        var sessions = AgentSessions.Apply(Array.Empty<AgentSession>(), P("a", AgentStatus.Running, hwnd: 42), now);
        sessions = AgentSessions.Apply(sessions, P("b", AgentStatus.AwaitingApproval), now);
        sessions = AgentSessions.Apply(sessions, new AgentPayload { Agent = "Claude Code", Session = "a", Status = AgentStatus.Done, ReceivedAt = now }, now);
        Check(sessions.Count == 2 && sessions[0].Key.EndsWith("a") && sessions[0].Latest.Hwnd == 42 && sessions[0].Project == "a", "same session updated, window and folder kept");
        Check(AgentSessions.Attention(sessions)?.Key.EndsWith("b") == true, "waiting session gets attention");
        var later = AgentSessions.Prune(sessions, now + TimeSpan.FromMinutes(31));
        Check(later.Count == 1 && later[0].Status == AgentStatus.AwaitingApproval, "finished sessions age out, waiting ones stay");
        Check(AgentSessions.Remove(sessions, sessions[1].Key).Count == 1, "session removed");
        var many = Enumerable.Range(0, 10).Aggregate((IReadOnlyList<AgentSession>)Array.Empty<AgentSession>(), (list, i) => AgentSessions.Apply(list, P("s" + i, AgentStatus.Running), now));
        Check(many.Count == AgentSessions.Max && many[0].Key.EndsWith("s9"), "bounded, newest first");
        Check(AgentSessions.KeyOf(new AgentPayload { Agent = "x", Pid = 7 }) != AgentSessions.KeyOf(new AgentPayload { Agent = "x", Pid = 8 }), "pid separates sessions");
    }

    private static void Hooks()
    {
        var now = DateTimeOffset.Now;
        ClaudeHookEvent Map(string json) { using var doc = JsonDocument.Parse(json); return ClaudeHooks.Map(doc.RootElement, now); }
        Check(Map("""{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"C:\\src\\app","prompt":"fix the login bug\nand tests"}""")
            is ClaudeHookEvent.Update { Payload: { Status: AgentStatus.Running, Message: "Working on: fix the login bug", Session: "s1", Cwd: @"C:\src\app" } }, "prompt submit -> running");
        Check(Map("""{"hook_event_name":"Notification","session_id":"s1","notification_type":"permission_prompt","message":"Claude needs your permission"}""")
            is ClaudeHookEvent.Update { Payload.Status: AgentStatus.AwaitingApproval }, "permission notification -> awaiting");
        Check(Map("""{"hook_event_name":"Notification","notification_type":"idle_prompt","message":"waiting"}""") is ClaudeHookEvent.Update { Payload.Status: AgentStatus.Done }, "idle -> done");
        Check(Map("""{"hook_event_name":"Stop","session_id":"s1","last_assistant_message":"## Refactor complete\nDetails..."}""")
            is ClaudeHookEvent.Update { Payload: { Status: AgentStatus.Done, Message: "Refactor complete" } }, "stop -> done with summary");
        var end = Map("""{"hook_event_name":"SessionEnd","session_id":"s1"}""");
        Check(end is ClaudeHookEvent.End e && e.SessionKey == AgentSessions.KeyOf(new AgentPayload { Agent = ClaudeHooks.AgentName, Session = "s1" }), "session end key matches");
        Check(Map("""{"hook_event_name":"PreCompact"}""") is ClaudeHookEvent.Ignore && Map("[]") is ClaudeHookEvent.Ignore, "unknown events ignored");

        using var permission = JsonDocument.Parse("""{"session_id":"s1","cwd":"C:\\src\\app","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf node_modules","description":"Remove"}}""");
        var request = ClaudeHooks.ParsePermission(permission.RootElement, now);
        Check(request is { Tool: "Bash", Detail: "rm -rf node_modules", Project: "app" }, "permission request parsed");
        using var mcp = JsonDocument.Parse("""{"tool_name":"mcp__db__query","tool_input":{"sql":"select 1"}}""");
        Check(ClaudeHooks.ParsePermission(mcp.RootElement, now)!.Detail.Contains("select 1"), "unknown tool input shown as JSON");

        using var allow = JsonDocument.Parse(ClaudeHooks.Response(ApprovalDecision.Allow));
        var decision = allow.RootElement.GetProperty("hookSpecificOutput");
        Check(decision.GetProperty("hookEventName").GetString() == "PermissionRequest" && decision.GetProperty("decision").GetProperty("behavior").GetString() == "allow", "allow output");
        using var deny = JsonDocument.Parse(ClaudeHooks.Response(ApprovalDecision.Deny));
        Check(deny.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString() == "deny"
            && deny.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").TryGetProperty("message", out _), "deny output with message");
        Check(ClaudeHooks.Response(ApprovalDecision.None) == "", "no decision -> empty stdout");

        using var setup = JsonDocument.Parse(ClaudeHookSetup.SettingsJson);
        var hooks = setup.RootElement.GetProperty("hooks");
        Check(hooks.TryGetProperty("PermissionRequest", out _) && hooks.TryGetProperty("Stop", out _), "hooks snippet is valid JSON");
    }

    private static void CliParsers()
    {
        var containers = DockerService.Parse("""
            {"ID":"0123456789abcdef","Names":"pg","Image":"postgres:16","Ports":"0.0.0.0:5432->5432/tcp, :::5432->5432/tcp","State":"running"}
            {"ID":"fedcba9876543210","Names":"worker","Image":"app","Ports":"","State":"running"}
            not json
            """);
        Check(containers.Count == 2 && containers[0].HostPorts.SequenceEqual(new[] { 5432 }) && containers[1].HostPorts.Count == 0, "docker ps json lines");

        var status = GitStatusService.ParseStatus("# branch.oid abc\n# branch.head feature/x\n# branch.upstream origin/feature/x\n# branch.ab +2 -1\n1 .M N... 100644 100644 100644 a b src/a.cs\n? new.txt\n");
        Check(status == new GitStatus("feature/x", "origin/feature/x", 2, 1, 2), "git porcelain v2");
        var run = GitStatusService.ParseRuns("""[{"status":"completed","conclusion":"failure","workflowName":"CI","displayTitle":"Fix","url":"https://github.com/o/r/actions/runs/1"}]""");
        Check(run is { Failed: true, InProgress: false, Url: "https://github.com/o/r/actions/runs/1" }, "gh run list json");
        Check(GitStatusService.ParseRuns("[]") is null && GitStatusService.ParseRuns("oops") is null, "no runs / bad output");

        Check(PortMonitorService.EscapeForCmd("node a.js & del x") == "node a.js ^& del x", "cmd metacharacters escaped");
        Check(PortMonitorService.EscapeForCmd("node -e \"a&&b|c\"") == "node -e \"a&&b|c\"", "quoted text untouched");

        if (OperatingSystem.IsWindows())
        {
            Check(StartupRegistration.ExecutableFromCommand("\"C:\\A B\\MyNotch.exe\" --flag") == @"C:\A B\MyNotch.exe", "quoted Run command");
            Check(StartupRegistration.ExecutableFromCommand(@"C:\x\MyNotch.exe -a") == @"C:\x\MyNotch.exe", "unquoted Run command");
            Check(StartupRegistration.SamePath(@"C:\X\..\X\MyNotch.exe", @"c:\x\mynotch.exe"), "path comparison normalizes");
        }
    }

    private static void Settings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mynotch-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var service = new SettingsService(path);
            Check(service.Current.FocusMinutes == 25 && !File.Exists(path), "defaults without a file");
            Check(service.Update(s => s with { FocusMinutes = 500, Display = "follow", DisabledUsageProviders = ["Muse"] }), "update saves");
            var reloaded = new SettingsService(path);
            Check(reloaded.Current.FocusMinutes == 180 && reloaded.Current.Display == "follow" && reloaded.Current.DisabledUsageProviders.SequenceEqual(new[] { "Muse" }), "clamped and reloaded");
            int changes = 0;
            reloaded.Changed += (_, _) => changes++;
            Check(reloaded.Update(s => s with { DisabledUsageProviders = ["Muse"] }) && changes == 0, "no-op update doesn't fire or write");
            File.WriteAllText(path, "{ broken");
            var corrupt = new SettingsService(path);
            Check(!corrupt.Update(s => s with { FocusMinutes = 30 }) && File.ReadAllText(path) == "{ broken", "corrupt file is never overwritten");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
