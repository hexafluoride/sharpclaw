using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SharpClaw.Agent;
using SharpClaw.Agents;
using SharpClaw.Browser;
using SharpClaw.Configuration;
using SharpClaw.LLM;
using SharpClaw.Screen;
using SharpClaw.Sessions;
using SharpClaw.Tools;

namespace SharpClaw;

public static class Program
{
    // ── ANSI escape codes ───────────────────────────────────────────

    const string R   = "\x1b[0m";
    const string B   = "\x1b[1m";
    const string D   = "\x1b[2m";
    const string It  = "\x1b[3m";
    const string Ul  = "\x1b[4m";
    const string NoU = "\x1b[24m";

    const string Cyn  = "\x1b[36m";
    const string Grn  = "\x1b[32m";
    const string Ylw  = "\x1b[33m";
    const string Red  = "\x1b[31m";
    const string Mag  = "\x1b[35m";
    const string Blu  = "\x1b[34m";
    const string Wht  = "\x1b[97m";
    const string Gry  = "\x1b[90m";
    const string BCyn = "\x1b[96m";
    const string BGrn = "\x1b[92m";
    const string BYlw = "\x1b[93m";
    const string BMag = "\x1b[95m";
    const string EL   = "\x1b[K";

    static string Fg(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";
    static string Bg256(int n) => $"\x1b[48;5;{n}m";

    static string Grad(string text, (int r, int g, int b) a, (int r, int g, int b) z)
    {
        if (text.Length <= 1) return $"{Fg(a.r, a.g, a.b)}{text}{R}";
        var sb = new StringBuilder(text.Length * 20);
        for (int i = 0; i < text.Length; i++)
        {
            float t = (float)i / (text.Length - 1);
            sb.Append(Fg(
                (int)(a.r + (z.r - a.r) * t),
                (int)(a.g + (z.g - a.g) * t),
                (int)(a.b + (z.b - a.b) * t)));
            sb.Append(text[i]);
        }
        sb.Append(R);
        return sb.ToString();
    }

    static string GradBar(int len, (int r, int g, int b) a, (int r, int g, int b) z)
        => Grad(new string('━', len), a, z);

    // ── Spinner ─────────────────────────────────────────────────────

    static readonly string[] Spin = ["⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷"];
    static CancellationTokenSource? _spinCts;
    static Task? _spinTask;

    static void SpinStart()
    {
        _spinCts = new CancellationTokenSource();
        var ct = _spinCts.Token;
        _spinTask = Task.Run(async () =>
        {
            int i = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = Grad(Spin[i++ % Spin.Length], (0, 200, 255), (180, 80, 255));
                    Console.Write($"\r  {frame} {D}thinking...{R}{EL}");
                    await Task.Delay(80, ct);
                }
            }
            catch (OperationCanceledException) { }
            Console.Write($"\r{EL}");
        }, ct);
    }

    static void SpinStop()
    {
        if (_spinCts == null) return;
        _spinCts.Cancel();
        try { _spinTask?.Wait(200); } catch { }
        _spinCts.Dispose();
        _spinCts = null;
        _spinTask = null;
    }

    // ── Streaming Markdown Renderer ─────────────────────────────────

    sealed class MdStream
    {
        readonly StringBuilder _buf = new();
        bool _inCode;
        int _tokenCount;

        public int TokenCount => _tokenCount;

        public void Feed(string token)
        {
            _tokenCount++;
            _buf.Append(token);

            while (true)
            {
                var s = _buf.ToString();
                var nl = s.IndexOf('\n');
                if (nl < 0) break;
                RenderLine(s[..nl]);
                Console.WriteLine();
                _buf.Clear();
                _buf.Append(s[(nl + 1)..]);
            }
        }

        public void Flush()
        {
            if (_buf.Length > 0)
            {
                RenderInline(_buf.ToString());
                _buf.Clear();
            }
        }

        public void Reset()
        {
            _buf.Clear();
            _inCode = false;
            _tokenCount = 0;
        }

        void RenderLine(string line)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                if (_inCode)
                {
                    _inCode = false;
                    Console.Write($"  {D}╰{'─', 0}───────────────────────────{R}");
                }
                else
                {
                    _inCode = true;
                    var lang = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
                    var label = lang.Length > 0 ? $" {Cyn}{lang}{R} " : " ";
                    Console.Write($"  {D}╭──{R}{label}{D}─────────────────────────{R}");
                }
                return;
            }

            if (_inCode)
            {
                Console.Write($"  {D}│{R} {Grn}{line}{R}");
                return;
            }

            // Headers
            if (trimmed.StartsWith("#### "))
            {
                Console.Write($"{B}{Cyn}{trimmed[5..]}{R}");
                return;
            }
            if (trimmed.StartsWith("### "))
            {
                Console.Write($"{B}{Cyn}{trimmed[4..]}{R}");
                return;
            }
            if (trimmed.StartsWith("## "))
            {
                Console.Write($"{B}{BCyn}{trimmed[3..]}{R}");
                return;
            }
            if (trimmed.StartsWith("# "))
            {
                Console.Write($"{B}{Wht}{trimmed[2..]}{R}");
                return;
            }

            // Horizontal rule
            if (trimmed.Length >= 3 && trimmed.All(c => c is '-' or '=' or '*' or '_'))
            {
                Console.Write($"  {GradBar(36, (60, 60, 80), (120, 60, 140))}");
                return;
            }

            // Blockquote
            if (trimmed.StartsWith("> "))
            {
                Console.Write($"  {Mag}▎{R} {D}{It}");
                RenderInline(trimmed[2..]);
                Console.Write(R);
                return;
            }

            // Bullet list
            if (IsListItem(line, out var indent, out var content))
            {
                Console.Write($"{new string(' ', indent)}{Cyn}•{R} ");
                RenderInline(content);
                return;
            }

            // Numbered list
            var numMatch = Regex.Match(trimmed, @"^(\d+)\.\s(.*)");
            if (numMatch.Success)
            {
                var ind = line.Length - trimmed.Length;
                Console.Write($"{new string(' ', ind)}{Cyn}{numMatch.Groups[1].Value}.{R} ");
                RenderInline(numMatch.Groups[2].Value);
                return;
            }

            // Table separator
            if (trimmed.StartsWith('|') && Regex.IsMatch(trimmed, @"^\|[\s\-:│|]+\|?$"))
            {
                Console.Write($"  {D}{line}{R}");
                return;
            }

            // Table row
            if (trimmed.StartsWith('|'))
            {
                var cells = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries);
                Console.Write($"{D}│{R}");
                foreach (var cell in cells)
                {
                    Console.Write(' ');
                    RenderInline(cell.Trim());
                    Console.Write($" {D}│{R}");
                }
                return;
            }

            // Regular line
            RenderInline(line);
        }

        static void RenderInline(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                // Bold **text**
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        Console.Write($"{B}");
                        RenderInline(text[(i + 2)..end]);
                        Console.Write($"{R}");
                        i = end + 2;
                        continue;
                    }
                }

                // Inline code `text`
                if (text[i] == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > 0)
                    {
                        Console.Write($"{Bg256(236)}{Grn} {text[(i + 1)..end]} {R}");
                        i = end + 1;
                        continue;
                    }
                }

                // Link [text](url)
                if (text[i] == '[')
                {
                    var cb = text.IndexOf("](", i, StringComparison.Ordinal);
                    if (cb > 0)
                    {
                        var cp = text.IndexOf(')', cb + 2);
                        if (cp > 0)
                        {
                            var lt = text[(i + 1)..cb];
                            var url = text[(cb + 2)..cp];
                            Console.Write($"{B}{lt}{R} {Blu}{Ul}{url}{NoU}{R}");
                            i = cp + 1;
                            continue;
                        }
                    }
                }

                Console.Write(text[i]);
                i++;
            }
        }

        static bool IsListItem(string line, out int indent, out string content)
        {
            indent = 0; content = "";
            var trimmed = line.TrimStart();
            indent = line.Length - trimmed.Length;
            if (trimmed.StartsWith("- ")) { content = trimmed[2..]; return true; }
            if (trimmed.StartsWith("* ")) { content = trimmed[2..]; return true; }
            return false;
        }
    }

    // ── Prompt ──────────────────────────────────────────────────────

    static readonly string PromptGlyph = Grad("❯", (0, 230, 200), (100, 200, 255));

    static void WritePrompt() => Console.Write($"\n{PromptGlyph} ");

    static void RedrawLine(StringBuilder buffer, int cursor)
    {
        Console.Write($"\r{PromptGlyph} ");
        Console.Write(buffer.ToString());
        Console.Write(EL);
        var back = buffer.Length - cursor;
        if (back > 0)
            Console.Write(new string('\b', back));
    }

    // ── Token display helpers ───────────────────────────────────────

    static void WriteToolCall(string token)
    {
        var inner = token.Trim().TrimStart('[').TrimEnd(']');
        if (!inner.StartsWith("Tool: ")) { Console.Write(token); return; }
        inner = inner["Tool: ".Length..];
        var p = inner.IndexOf('(');
        if (p <= 0) { Console.Write(token); return; }
        var name = inner[..p];
        var args = inner[(p + 1)..].TrimEnd(')');
        Console.Write($"\n  {Gry}┊{R} {BCyn}⚙ {name}{R}");
        if (args.Length > 0 && args != "{}")
        {
            var display = args.Length > 100 ? args[..97] + "..." : args;
            Console.Write($" {Gry}{display}{R}");
        }
        Console.WriteLine();
    }

    static void WriteToolResult(string token)
    {
        var inner = token.Trim().TrimStart('[').TrimEnd(']');
        if (!inner.StartsWith("Result: ")) { Console.Write(token); return; }
        var body = inner["Result: ".Length..];

        // Parse optional timing prefix "1.2s | actual result..."
        string? timing = null;
        var pipeIdx = body.IndexOf(" | ", StringComparison.Ordinal);
        if (pipeIdx > 0 && pipeIdx < 8 && body[..pipeIdx].EndsWith('s'))
        {
            timing = body[..pipeIdx];
            body = body[(pipeIdx + 3)..];
        }

        Console.Write($"  {Gry}┊ {Grn}↳{R}");
        if (timing != null) Console.Write($" {D}{timing}{R}");
        Console.Write($" {Gry}{body}{R}\n");
    }

    static void WriteSysMsg(string token)
    {
        var text = token.Trim().Trim('[', ']');
        Console.Write($"\n  {BYlw}▸{R} {Ylw}{text}{R}\n");
    }

    // ── Tab completion ──────────────────────────────────────────────

    static readonly string[] SlashCmds =
        ["/new", "/sessions", "/load", "/screen", "/history", "/tools", "/tool",
         "/agents", "/mailbox", "/info", "/help", "/quit", "/exit"];

    static bool TryTabComplete(StringBuilder buffer, ref int cursor)
    {
        if (buffer.Length == 0 || buffer[0] != '/') return false;
        var partial = buffer.ToString();
        var matches = SlashCmds.Where(c => c.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            buffer.Clear();
            buffer.Append(matches[0]);
            cursor = buffer.Length;
            RedrawLine(buffer, cursor);
            return true;
        }
        if (matches.Count > 1)
        {
            Console.Write($"\n  {Gry}{string.Join("  ", matches)}{R}\n");
            RedrawLine(buffer, cursor);
            return true;
        }
        return false;
    }

    // ── Banner ──────────────────────────────────────────────────────

    static void PrintBanner(SharpClawConfig config, Mailbox mailbox, AgentScheduler scheduler,
        ScreenAnalyzer analyzer, bool screenEnabled)
    {
        Console.WriteLine();

        var c1 = (0, 210, 255);
        var c2 = (190, 80, 255);

        Console.Write($"  {GradBar(42, c1, c2)}\n");
        Console.WriteLine();
        Console.Write($"    {Grad("S H A R P C L A W", c1, c2)}  {D}v0.1{R}\n");
        Console.Write($"    {D}local ai agent · llama.cpp{R}\n");
        Console.WriteLine();
        Console.Write($"  {GradBar(42, c2, c1)}\n");
        Console.WriteLine();

        void Kv(string key, string val) => Console.Write($"  {Gry}{key,-10}{R} {val}\n");

        Kv("model", $"{Wht}{config.Model}{R}");
        Kv("server", config.LlamaCppUrl);
        Kv("workspace", config.ResolvedWorkspace);

        var ss = screenEnabled ? $"{Grn}on{R}" : $"{Gry}off{R}";
        var extra = analyzer.ObservationCount > 0 ? $" {Gry}· {analyzer.ObservationCount} obs{R}" : "";
        Kv("screen", $"{ss}{extra}");

        var agents = scheduler.ListAgents();
        var running = agents.Count(a => a.Status == AgentStatus.Running);
        if (agents.Count > 0)
            Kv("agents", $"{BCyn}{running}{R} running{Gry}, {agents.Count} total{R}");

        if (mailbox.TotalCount > 0)
        {
            var u = mailbox.UnreadCount;
            Kv("mailbox", u > 0
                ? $"{BMag}{u} unread{R}{Gry} / {mailbox.TotalCount} total{R}"
                : $"{Gry}{mailbox.TotalCount} messages{R}");
        }

        Console.WriteLine();
    }

    // ── Main ────────────────────────────────────────────────────────

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var config = SharpClawConfig.Load();
        config.EnsureDirectories();
        BootstrapLoader.CreateDefaultBootstrapFiles(config.ResolvedWorkspace);

        using var llmClient = new LlamaCppClient(config.LlamaCppUrl);
        var sessionManager = new SessionManager(config.ResolvedSessionsDir);
        var screenAnalyzer = new ScreenAnalyzer(config);
        var screenCapture = new ScreenCaptureService(config.Screen, screenAnalyzer.OnScreenCaptured);
        var browserService = new BrowserService(config);
        var mailbox = new Mailbox(Path.Combine(config.ResolvedConfigDir, "mailbox.jsonl"));

        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new ReadFileTool(config.ResolvedWorkspace));
        toolRegistry.Register(new WriteFileTool(config.ResolvedWorkspace));
        toolRegistry.Register(new ShellExecTool(config.ResolvedWorkspace));
        toolRegistry.Register(new ListDirectoryTool(config.ResolvedWorkspace));
        toolRegistry.Register(new ScreenQueryTool(screenAnalyzer));
        toolRegistry.Register(new TakeScreenshotTool(screenCapture, screenAnalyzer));
        toolRegistry.Register(new MemoryTool(config.ResolvedWorkspace, config.ResolvedArchiveDir));
        toolRegistry.Register(new BrowserTool(browserService, config));

        var notifications = new NotificationBus();
        var agentScheduler = new AgentScheduler(config, llmClient, toolRegistry, mailbox, notifications);
        toolRegistry.Register(new AgentTool(agentScheduler, mailbox));
        agentScheduler.Start();

        var systemPrompt = BootstrapLoader.BuildSystemPrompt(config.ResolvedWorkspace);
        var runtime = new AgentRuntime(config, llmClient, toolRegistry, sessionManager, systemPrompt);

        if (config.Screen.Enabled)
            await screenCapture.StartAsync();

        PrintBanner(config, mailbox, agentScheduler, screenAnalyzer, config.Screen.Enabled);
        PrintHelp();

        try
        {
            await RunRepl(config, runtime, sessionManager, screenCapture, screenAnalyzer,
                toolRegistry, mailbox, agentScheduler, notifications);
        }
        finally
        {
            await agentScheduler.DisposeAsync();
            await browserService.DisposeAsync();
            screenCapture?.Dispose();
            screenAnalyzer.Dispose();
        }
        return 0;
    }

    // ── REPL ────────────────────────────────────────────────────────

    static async Task RunRepl(
        SharpClawConfig config, AgentRuntime runtime, SessionManager sessionManager,
        ScreenCaptureService screenCapture, ScreenAnalyzer screenAnalyzer,
        ToolRegistry toolRegistry, Mailbox mailbox, AgentScheduler agentScheduler,
        NotificationBus notifications)
    {
        var historyPath = Path.Combine(config.ResolvedConfigDir, "prompt-history.txt");
        var promptHistory = new PromptHistory(historyPath);
        var md = new MdStream();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; SpinStop(); cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            WritePrompt();

            string? input;
            try
            {
                input = Console.IsInputRedirected
                    ? Console.ReadLine()
                    : ReadLineWithNotifications(notifications, promptHistory, cts.Token);
            }
            catch (OperationCanceledException) { break; }

            if (input is null) break;
            input = input.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            if (input.StartsWith('/'))
            {
                if (!await HandleCommand(input, config, runtime, sessionManager,
                        screenCapture, screenAnalyzer, toolRegistry, mailbox, agentScheduler))
                    break;
                continue;
            }

            var agentGlyph = Grad("◆", (0, 200, 255), (160, 100, 255));
            Console.Write($"\n{agentGlyph} ");
            SpinStart();

            var firstToken = true;
            var sw = Stopwatch.StartNew();
            md.Reset();

            try
            {
                await runtime.ProcessAsync(input, token =>
                {
                    if (firstToken) { SpinStop(); firstToken = false; }

                    if (token.Contains("[Tool:"))       { md.Flush(); WriteToolCall(token); }
                    else if (token.Contains("[Result:")) { WriteToolResult(token); }
                    else if (token.Contains("[Iteration limit") || token.Contains("[Cancelled"))
                    {
                        md.Flush();
                        WriteSysMsg(token);
                    }
                    else { md.Feed(token); }
                }, cts.Token);

                md.Flush();
                sw.Stop();

                var secs = sw.Elapsed.TotalSeconds;
                var toks = md.TokenCount;
                var tps = secs > 0 ? toks / secs : 0;
                Console.Write($"\n  {D}── {secs:F1}s · {toks} tokens · {tps:F0} tok/s ──{R}\n");
            }
            catch (HttpRequestException ex)
            {
                SpinStop(); md.Flush();
                Console.Write($"\n  {Red}✗ Connection error:{R} {ex.Message}\n");
                Console.Write($"  {Gry}Make sure llama-server is running at {config.LlamaCppUrl}{R}\n");
            }
            catch (OperationCanceledException)
            {
                SpinStop(); md.Flush();
                Console.Write($"\n  {Ylw}▸ Cancelled{R}\n");
                break;
            }
            catch (Exception ex)
            {
                SpinStop(); md.Flush();
                Console.Write($"\n  {Red}✗ Error:{R} {ex.Message}\n");
            }
        }
    }

    // ── Input ───────────────────────────────────────────────────────

    static string? ReadLineWithNotifications(
        NotificationBus notifications, PromptHistory history, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var cursor = 0;
        history.BeginSession();

        while (!ct.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        var line = buffer.ToString();
                        history.Commit(line);
                        return line;

                    case ConsoleKey.Backspace:
                        if (cursor > 0) { buffer.Remove(--cursor, 1); RedrawLine(buffer, cursor); }
                        break;

                    case ConsoleKey.Delete:
                        if (cursor < buffer.Length) { buffer.Remove(cursor, 1); RedrawLine(buffer, cursor); }
                        break;

                    case ConsoleKey.LeftArrow:
                        if (cursor > 0) { cursor--; Console.Write("\b"); }
                        break;

                    case ConsoleKey.RightArrow:
                        if (cursor < buffer.Length) { Console.Write(buffer[cursor]); cursor++; }
                        break;

                    case ConsoleKey.Home:
                        if (cursor > 0) { Console.Write(new string('\b', cursor)); cursor = 0; }
                        break;

                    case ConsoleKey.End:
                        if (cursor < buffer.Length) { Console.Write(buffer.ToString()[cursor..]); cursor = buffer.Length; }
                        break;

                    case ConsoleKey.UpArrow:
                    {
                        var prev = history.Previous(buffer.ToString());
                        if (prev != null) { buffer.Clear(); buffer.Append(prev); cursor = buffer.Length; RedrawLine(buffer, cursor); }
                        break;
                    }

                    case ConsoleKey.DownArrow:
                    {
                        var next = history.Next() ?? "";
                        buffer.Clear(); buffer.Append(next); cursor = buffer.Length; RedrawLine(buffer, cursor);
                        break;
                    }

                    case ConsoleKey.Tab:
                        TryTabComplete(buffer, ref cursor);
                        break;

                    default:
                        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { Console.WriteLine(); return null; }
                        if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { Console.WriteLine(); return null; }
                        if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { buffer.Clear(); cursor = 0; RedrawLine(buffer, cursor); break; }
                        if (key.Key == ConsoleKey.W && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            if (cursor > 0)
                            {
                                var end = cursor;
                                while (cursor > 0 && buffer[cursor - 1] == ' ') cursor--;
                                while (cursor > 0 && buffer[cursor - 1] != ' ') cursor--;
                                buffer.Remove(cursor, end - cursor);
                                RedrawLine(buffer, cursor);
                            }
                            break;
                        }
                        if (key.Key == ConsoleKey.A && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { cursor = 0; RedrawLine(buffer, cursor); break; }
                        if (key.Key == ConsoleKey.E && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { cursor = buffer.Length; RedrawLine(buffer, cursor); break; }
                        if (key.Key == ConsoleKey.K && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            if (cursor < buffer.Length) { buffer.Remove(cursor, buffer.Length - cursor); RedrawLine(buffer, cursor); }
                            break;
                        }
                        if (!char.IsControl(key.KeyChar))
                        {
                            buffer.Insert(cursor, key.KeyChar); cursor++;
                            if (cursor == buffer.Length) Console.Write(key.KeyChar);
                            else RedrawLine(buffer, cursor);
                        }
                        break;
                }
            }
            else if (buffer.Length == 0 && notifications.TryDequeue(out var n))
            {
                Console.Write($"\r{EL}");
                Console.Write($"  {Mag}●{R} {Gry}{n!.Timestamp:HH:mm:ss}{R} {BMag}{n.AgentName}{R}{Gry}: {n.Message}{R}\n");
                Console.Write($"{PromptGlyph} ");
            }
            else { Thread.Sleep(50); }
        }
        return null;
    }

    // ── Commands ────────────────────────────────────────────────────

    static async Task<bool> HandleCommand(
        string input, SharpClawConfig config, AgentRuntime runtime,
        SessionManager sessionManager, ScreenCaptureService screenCapture,
        ScreenAnalyzer screenAnalyzer, ToolRegistry toolRegistry,
        Mailbox mailbox, AgentScheduler agentScheduler)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        switch (cmd)
        {
            case "/quit" or "/exit" or "/q":
                Console.Write($"\n  {Gry}goodbye ✦{R}\n");
                return false;

            case "/new":
                sessionManager.NewSession();
                var sp = BootstrapLoader.BuildSystemPrompt(config.ResolvedWorkspace);
                runtime.UpdateSystemPrompt(sp);
                sessionManager.SetSystemPrompt(sp);
                Console.Write($"  {Grn}✓{R} New session: {Wht}{sessionManager.SessionId}{R}\n");
                return true;

            case "/sessions":
            {
                var sessions = sessionManager.ListSessions();
                if (sessions.Count == 0) { Console.Write($"  {Gry}No saved sessions.{R}\n"); return true; }
                Console.Write($"\n  {Wht}Sessions{R} {Gry}({sessions.Count}){R}\n");
                Console.Write($"  {D}{'─',0}───────────────────────────────────{R}\n");
                foreach (var s in sessions.Take(20))
                    Console.Write($"  {Gry}│{R} {s}\n");
                if (sessions.Count > 20)
                    Console.Write($"  {Gry}│ ... and {sessions.Count - 20} more{R}\n");
                return true;
            }

            case "/load":
                if (parts.Length < 2) { Console.Write($"  {Ylw}Usage:{R} /load <session-id>\n"); return true; }
                sessionManager.LoadSession(parts[1]);
                Console.Write($"  {Grn}✓{R} Loaded: {Wht}{parts[1]}{R} {Gry}({sessionManager.Messages.Count} messages){R}\n");
                return true;

            case "/screen":
            {
                var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
                switch (sub)
                {
                    case "on":
                        screenCapture.Resume();
                        if (!screenCapture.IsRunning) await screenCapture.StartAsync();
                        Console.Write($"  {Grn}✓{R} Screen monitoring resumed\n");
                        break;
                    case "off":
                        screenCapture.Pause();
                        Console.Write($"  {Ylw}▸{R} Screen monitoring paused\n");
                        break;
                    case "status":
                        var st = screenCapture.IsPaused ? $"{Ylw}paused{R}" : $"{Grn}active{R}";
                        Console.Write($"  {Gry}screen{R}        {st}\n");
                        Console.Write($"  {Gry}observations{R}  {screenAnalyzer.ObservationCount}\n");
                        var lat = screenAnalyzer.GetLatest();
                        if (lat is not null)
                            Console.Write($"  {Gry}latest{R}        {Gry}[{lat.Timestamp:HH:mm:ss}]{R} {lat.Description[..Math.Min(lat.Description.Length, 80)]}\n");
                        break;
                    default:
                        Console.Write($"  {Ylw}Usage:{R} /screen [on|off|status]\n"); break;
                }
                return true;
            }

            case "/history":
            {
                var count = 20;
                if (parts.Length > 1 && int.TryParse(parts[1], out var n2))
                    count = Math.Clamp(n2, 1, 200);
                var obs = screenAnalyzer.GetObservations(minutesBack: 60 * 24 * 365);
                if (obs.Count == 0) { Console.Write($"  {Gry}No screen observations.{R}\n"); return true; }
                var shown = obs.TakeLast(count).ToList();
                Console.Write($"\n  {Wht}Screen History{R} {Gry}(last {shown.Count} of {obs.Count}){R}\n");
                Console.Write($"  {D}{'─',0}───────────────────────────────────{R}\n");
                var lastDate = "";
                foreach (var o in shown)
                {
                    var d = o.Timestamp.ToString("yyyy-MM-dd");
                    if (d != lastDate) { Console.Write($"\n  {Cyn}{d}{R}\n"); lastDate = d; }
                    Console.Write($"  {Gry}│ {o.Timestamp:HH:mm:ss}{R}  {o.Description}\n");
                }
                return true;
            }

            case "/tools":
            {
                Console.Write($"\n  {Wht}Available Tools{R}\n");
                Console.Write($"  {D}{'─',0}───────────────────────────────────{R}\n");
                foreach (var tool in toolRegistry.All)
                {
                    var desc = tool.Description;
                    var fl = desc.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? desc;
                    if (fl.Length > 65) fl = fl[..62] + "...";
                    Console.Write($"  {BCyn}{tool.Name,-22}{R} {Gry}{fl}{R}\n");
                }
                Console.Write($"\n  {Gry}Invoke: /tool <name> {{\"param\":\"value\"}}{R}\n");
                return true;
            }

            case "/tool":
            {
                if (parts.Length < 2)
                {
                    Console.Write($"  {Ylw}Usage:{R} /tool <name> [json-args]\n");
                    Console.Write($"  {Gry}│{R} /tool read_file {{\"path\":\"SOUL.md\"}}\n");
                    Console.Write($"  {Gry}│{R} /tool memory {{\"action\":\"read\"}}\n");
                    return true;
                }
                var rest = parts[1];
                var si = rest.IndexOf(' ');
                var tn = si < 0 ? rest.Trim() : rest[..si].Trim();
                var ta = si < 0 ? "{}" : rest[(si + 1)..].Trim();
                if (string.IsNullOrEmpty(ta)) ta = "{}";
                Console.Write($"  {Gry}┊{R} {BCyn}⚙ {tn}{R} {Gry}{(ta != "{}" ? ta : "")}{R}\n");
                var sw2 = Stopwatch.StartNew();
                var result = await toolRegistry.ExecuteAsync(tn, ta);
                sw2.Stop();
                Console.WriteLine(result);
                Console.Write($"  {D}── {sw2.Elapsed.TotalSeconds:F1}s ──{R}\n");
                return true;
            }

            case "/agents":
            {
                var agents = agentScheduler.ListAgents();
                if (agents.Count == 0) { Console.Write($"  {Gry}No agents. Chat to spawn some.{R}\n"); return true; }
                Console.Write($"\n  {Wht}Agents{R} {Gry}({agents.Count}){R}\n");
                Console.Write($"  {D}{'─',0}───────────────────────────────────{R}\n");
                foreach (var a in agents)
                {
                    var (ico, col) = a.Status switch
                    {
                        AgentStatus.Running => ("●", BGrn),
                        AgentStatus.Paused => ("◐", BYlw),
                        AgentStatus.Completed => ("✓", Cyn),
                        AgentStatus.Failed => ("✗", Red),
                        _ => ("○", Gry)
                    };
                    var kind = a.Kind == AgentKind.LongRunning ? "agent" : "task";
                    Console.Write($"  {col}{ico}{R} {Wht}{a.Name}{R} {Gry}({kind}){R}\n");
                    Console.Write($"    {Gry}id: {a.Id}{R}\n");
                    var pur = a.Purpose.ReplaceLineEndings(" ");
                    if (pur.Length > 70) pur = pur[..67] + "...";
                    Console.Write($"    {Gry}{pur}{R}\n");
                    var ri = $"runs: {a.RunCount}";
                    if (a.Schedule != null) ri = $"schedule: {a.Schedule}, {ri}";
                    if (a.LastRunAt.HasValue) ri += $", last: {a.LastRunAt.Value:HH:mm:ss}";
                    Console.Write($"    {Gry}{ri}{R}\n");
                    if (a.LastError != null)
                        Console.Write($"    {Red}error: {a.LastError[..Math.Min(a.LastError.Length, 80)]}{R}\n");
                }
                return true;
            }

            case "/mailbox":
            {
                var msgs = mailbox.GetAll(limit: 20);
                if (msgs.Count == 0) { Console.Write($"  {Gry}Mailbox is empty.{R}\n"); return true; }
                var ur = mailbox.UnreadCount;
                Console.Write($"\n  {Wht}Mailbox{R} {Gry}({ur} unread, {mailbox.TotalCount} total){R}\n");
                Console.Write($"  {D}{'─',0}───────────────────────────────────{R}\n");
                foreach (var m in msgs)
                {
                    var mk = m.Read ? $"{Gry}○{R}" : $"{BMag}●{R}";
                    var nc = m.Read ? Gry : Wht;
                    Console.Write($"  {mk} {nc}{m.FromName}{R} {Gry}· {m.Timestamp:MM-dd HH:mm} · {m.Id}{R}\n");
                    var sc = m.Read ? Gry : R;
                    Console.Write($"    {sc}{m.Subject}{R}\n");
                    var body = m.Body.ReplaceLineEndings(" ");
                    if (body.Length > 100) body = body[..97] + "...";
                    if (!string.IsNullOrWhiteSpace(body))
                        Console.Write($"    {Gry}{body}{R}\n");
                }
                return true;
            }

            case "/help" or "/?":
                PrintHelp();
                return true;

            case "/info":
            {
                Console.Write($"\n  {Wht}Dashboard{R}\n");
                Console.Write($"  {D}{'─',0}───────────────────────────────────{R}\n");
                void Kv(string k, string v) => Console.Write($"  {Gry}{k,-12}{R} {v}\n");
                Kv("session", $"{Wht}{sessionManager.SessionId}{R}");
                Kv("messages", $"{sessionManager.Messages.Count}");
                Kv("model", $"{Wht}{config.Model}{R}");
                Kv("server", config.LlamaCppUrl);
                Kv("workspace", config.ResolvedWorkspace);
                var scrSt = screenCapture.IsPaused ? $"{Ylw}paused{R}" : $"{Grn}active{R}";
                Kv("screen", $"{scrSt} {Gry}({screenAnalyzer.ObservationCount} obs){R}");
                var al = agentScheduler.ListAgents();
                var rn = al.Count(a2 => a2.Status == AgentStatus.Running);
                Kv("agents", $"{BCyn}{rn}{R} running{Gry}, {al.Count} total{R}");
                Kv("mailbox", mailbox.UnreadCount > 0
                    ? $"{BMag}{mailbox.UnreadCount} unread{R}{Gry} / {mailbox.TotalCount} total{R}"
                    : $"{Gry}{mailbox.TotalCount} messages{R}");
                return true;
            }

            default:
                Console.Write($"  {Ylw}?{R} Unknown: {cmd}. Type {Wht}/help{R}\n");
                return true;
        }
    }

    // ── Help ────────────────────────────────────────────────────────

    static void PrintHelp()
    {
        var c1 = (0, 210, 255);
        var c2 = (190, 80, 255);

        void Sec(string title)
        {
            var bar = GradBar(36 - title.Length, c1, c2);
            Console.Write($"\n  {Wht}{title}{R} {bar}\n");
        }

        void Cmd(string name, string desc) =>
            Console.Write($"    {BCyn}{name,-20}{R} {Gry}{desc}{R}\n");

        Sec("session");
        Cmd("/new", "start a fresh session");
        Cmd("/sessions", "list saved sessions");
        Cmd("/load <id>", "restore a session");

        Sec("monitor");
        Cmd("/screen [on|off]", "toggle screen watching");
        Cmd("/history [n]", "screen observation log");

        Sec("tools & agents");
        Cmd("/tools", "list available tools");
        Cmd("/tool <name> [args]", "invoke a tool directly");
        Cmd("/agents", "list sub-agents");
        Cmd("/mailbox", "read agent mailbox");

        Sec("other");
        Cmd("/info", "session dashboard");
        Cmd("/help", "this help");
        Cmd("/quit", "exit");

        Console.Write($"\n  {Gry}Or just type a message to chat. {D}Tab completes commands.{R}\n");
    }
}

// ── Prompt History ──────────────────────────────────────────────────

internal class PromptHistory
{
    private readonly string _path;
    private readonly List<string> _entries = [];
    private int _index;
    private string? _savedCurrent;
    private const int MaxEntries = 1000;

    public PromptHistory(string path) { _path = path; Load(); _index = _entries.Count; }

    public void BeginSession() { _index = _entries.Count; _savedCurrent = null; }

    public void Commit(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_entries.Count > 0 && _entries[^1] == line) { _index = _entries.Count; return; }
        _entries.Add(line);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        _index = _entries.Count;
        Append(line);
    }

    public string? Previous(string currentBuffer)
    {
        if (_entries.Count == 0) return null;
        if (_index == _entries.Count) _savedCurrent = currentBuffer;
        if (_index > 0) { _index--; return _entries[_index]; }
        return null;
    }

    public string? Next()
    {
        if (_index >= _entries.Count - 1) { _index = _entries.Count; return _savedCurrent ?? ""; }
        _index++;
        return _entries[_index];
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadAllLines(_path).TakeLast(MaxEntries))
                if (!string.IsNullOrWhiteSpace(line)) _entries.Add(line);
        }
        catch { }
    }

    private void Append(string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.AppendAllText(_path, line + "\n");
            if (_entries.Count % 100 == 0)
            {
                try
                {
                    var fl = File.ReadAllLines(_path);
                    if (fl.Length > MaxEntries * 2) File.WriteAllLines(_path, fl.TakeLast(MaxEntries).ToArray());
                }
                catch { }
            }
        }
        catch { }
    }
}
