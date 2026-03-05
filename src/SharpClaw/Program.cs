using System.Text;
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
    // ── ANSI helpers ────────────────────────────────────────────────

    private const string Rst = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string Italic = "\x1b[3m";
    private const string FgCyan = "\x1b[36m";
    private const string FgGreen = "\x1b[32m";
    private const string FgYellow = "\x1b[33m";
    private const string FgRed = "\x1b[31m";
    private const string FgMagenta = "\x1b[35m";
    private const string FgBlue = "\x1b[34m";
    private const string FgWhite = "\x1b[97m";
    private const string FgGray = "\x1b[90m";
    private const string FgBrCyan = "\x1b[96m";
    private const string FgBrGreen = "\x1b[92m";
    private const string FgBrYellow = "\x1b[93m";
    private const string FgBrMagenta = "\x1b[95m";
    private const string EraseLine = "\x1b[K";

    private static string Rgb(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";

    private static string Gradient(string text, (int r, int g, int b) from, (int r, int g, int b) to)
    {
        if (text.Length <= 1) return $"{Rgb(from.r, from.g, from.b)}{text}{Rst}";
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            float t = (float)i / (text.Length - 1);
            int r = (int)(from.r + (to.r - from.r) * t);
            int g = (int)(from.g + (to.g - from.g) * t);
            int b = (int)(from.b + (to.b - from.b) * t);
            sb.Append($"{Rgb(r, g, b)}{text[i]}");
        }
        sb.Append(Rst);
        return sb.ToString();
    }

    // ── Spinner ─────────────────────────────────────────────────────

    private static readonly string[] SpinFrames =
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private static CancellationTokenSource? _spinnerCts;
    private static Task? _spinnerTask;

    private static void StartSpinner()
    {
        _spinnerCts = new CancellationTokenSource();
        var ct = _spinnerCts.Token;
        _spinnerTask = Task.Run(async () =>
        {
            var i = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Console.Write($"\r  {FgCyan}{SpinFrames[i++ % SpinFrames.Length]}{Rst} {Dim}thinking...{Rst}{EraseLine}");
                    await Task.Delay(80, ct);
                }
            }
            catch (OperationCanceledException) { }
            Console.Write($"\r{EraseLine}");
        }, ct);
    }

    private static void StopSpinner()
    {
        if (_spinnerCts == null) return;
        _spinnerCts.Cancel();
        try { _spinnerTask?.Wait(200); } catch { }
        _spinnerCts.Dispose();
        _spinnerCts = null;
        _spinnerTask = null;
    }

    // ── Prompt ──────────────────────────────────────────────────────

    private const string PromptChar = "❯";
    private const int PromptDisplayWidth = 2; // "❯ "

    private static void WritePrompt()
    {
        Console.Write($"\n{FgBrGreen}{PromptChar}{Rst} ");
    }

    private static void RedrawLine(StringBuilder buffer, int cursor)
    {
        Console.Write($"\r{FgBrGreen}{PromptChar}{Rst} ");
        Console.Write(buffer.ToString());
        Console.Write(EraseLine);
        var back = buffer.Length - cursor;
        if (back > 0)
            Console.Write(new string('\b', back));
    }

    // ── Token display helpers ───────────────────────────────────────

    private static void WriteToolCall(string token)
    {
        // Parse: "\n[Tool: name(args)]\n"
        var inner = token.Trim().TrimStart('[').TrimEnd(']');
        if (inner.StartsWith("Tool: "))
        {
            inner = inner["Tool: ".Length..];
            var parenIdx = inner.IndexOf('(');
            if (parenIdx > 0)
            {
                var name = inner[..parenIdx];
                var args = inner[(parenIdx + 1)..].TrimEnd(')');
                Console.Write($"\n  {FgGray}┊{Rst} {FgCyan}⚙ {name}{Rst}");
                if (args.Length > 0 && args != "{}")
                {
                    var displayArgs = args.Length > 120 ? args[..117] + "..." : args;
                    Console.Write($" {FgGray}{displayArgs}{Rst}");
                }
                Console.WriteLine();
                return;
            }
        }
        Console.Write(token);
    }

    private static void WriteToolResult(string token)
    {
        // Parse: "[Result: text]\n"
        var inner = token.Trim().TrimStart('[').TrimEnd(']');
        if (inner.StartsWith("Result: "))
        {
            var text = inner["Result: ".Length..];
            Console.Write($"  {FgGray}┊ {FgGreen}↳{Rst} {FgGray}{text}{Rst}");
            Console.WriteLine();
            return;
        }
        Console.Write(token);
    }

    private static void WriteSystemMessage(string token)
    {
        var text = token.Trim().Trim('[', ']');
        Console.Write($"\n  {FgYellow}▸ {text}{Rst}\n");
    }

    // ── Banner ──────────────────────────────────────────────────────

    private static void PrintBanner(SharpClawConfig config, Mailbox mailbox, AgentScheduler scheduler, ScreenAnalyzer analyzer, bool screenEnabled)
    {
        Console.WriteLine();

        var gradientName = Gradient("SharpClaw", (0, 220, 255), (200, 100, 255));
        var bar = $"{Dim}─────────────────────────────────{Rst}";

        Console.Write($"  {gradientName} {FgGray}v0.1{Rst}\n");
        Console.Write($"  {bar}\n");
        Console.Write($"  {FgGray}local ai agent · llama.cpp{Rst}\n");
        Console.WriteLine();

        Console.Write($"  {FgGray}model{Rst}     {FgWhite}{config.Model}{Rst}\n");
        Console.Write($"  {FgGray}server{Rst}    {config.LlamaCppUrl}\n");
        Console.Write($"  {FgGray}workspace{Rst} {config.ResolvedWorkspace}\n");

        var screenStatus = screenEnabled ? $"{FgGreen}on{Rst}" : $"{FgGray}off{Rst}";
        Console.Write($"  {FgGray}screen{Rst}    {screenStatus}");
        if (analyzer.ObservationCount > 0)
            Console.Write($" {FgGray}· {analyzer.ObservationCount} observations{Rst}");
        Console.WriteLine();

        var agentList = scheduler.ListAgents();
        var runningCount = agentList.Count(a => a.Status == AgentStatus.Running);
        if (agentList.Count > 0)
            Console.Write($"  {FgGray}agents{Rst}    {FgBrCyan}{runningCount}{Rst} running{FgGray}, {agentList.Count} total{Rst}\n");

        if (mailbox.TotalCount > 0)
        {
            var unread = mailbox.UnreadCount;
            if (unread > 0)
                Console.Write($"  {FgGray}mailbox{Rst}   {FgBrMagenta}{unread} unread{Rst}{FgGray} / {mailbox.TotalCount} total{Rst}\n");
            else
                Console.Write($"  {FgGray}mailbox{Rst}   {mailbox.TotalCount} messages\n");
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
            await RunRepl(config, runtime, sessionManager, screenCapture, screenAnalyzer, toolRegistry, mailbox, agentScheduler, notifications);
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

    private static async Task RunRepl(
        SharpClawConfig config,
        AgentRuntime runtime,
        SessionManager sessionManager,
        ScreenCaptureService screenCapture,
        ScreenAnalyzer screenAnalyzer,
        ToolRegistry toolRegistry,
        Mailbox mailbox,
        AgentScheduler agentScheduler,
        NotificationBus notifications)
    {
        var historyPath = Path.Combine(config.ResolvedConfigDir, "prompt-history.txt");
        var promptHistory = new PromptHistory(historyPath);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            StopSpinner();
            cts.Cancel();
        };

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
            catch (OperationCanceledException)
            {
                break;
            }

            if (input is null) break;
            input = input.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            if (input.StartsWith('/'))
            {
                var handled = await HandleCommand(input, config, runtime, sessionManager,
                    screenCapture, screenAnalyzer, toolRegistry, mailbox, agentScheduler);
                if (!handled) break;
                continue;
            }

            // Start thinking spinner
            Console.Write($"\n{FgBrCyan}◆{Rst} ");
            StartSpinner();

            var firstToken = true;

            try
            {
                await runtime.ProcessAsync(input, token =>
                {
                    if (firstToken)
                    {
                        StopSpinner();
                        firstToken = false;
                    }

                    if (token.Contains("[Tool:"))
                    {
                        WriteToolCall(token);
                    }
                    else if (token.Contains("[Result:"))
                    {
                        WriteToolResult(token);
                    }
                    else if (token.Contains("[Iteration limit") || token.Contains("[Connection") || token.Contains("[Cancelled"))
                    {
                        WriteSystemMessage(token);
                    }
                    else
                    {
                        Console.Write(token);
                    }
                }, cts.Token);

                Console.WriteLine();
            }
            catch (HttpRequestException ex)
            {
                StopSpinner();
                Console.Write($"\n  {FgRed}✗ Connection error:{Rst} {ex.Message}\n");
                Console.Write($"  {FgGray}Make sure llama-server is running at {config.LlamaCppUrl}{Rst}\n");
            }
            catch (OperationCanceledException)
            {
                StopSpinner();
                Console.Write($"\n  {FgYellow}▸ Cancelled{Rst}\n");
                break;
            }
            catch (Exception ex)
            {
                StopSpinner();
                Console.Write($"\n  {FgRed}✗ Error:{Rst} {ex.Message}\n");
            }
        }
    }

    // ── Input with notifications ────────────────────────────────────

    private static string? ReadLineWithNotifications(
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
                        if (cursor > 0)
                        {
                            buffer.Remove(cursor - 1, 1);
                            cursor--;
                            RedrawLine(buffer, cursor);
                        }
                        break;

                    case ConsoleKey.Delete:
                        if (cursor < buffer.Length)
                        {
                            buffer.Remove(cursor, 1);
                            RedrawLine(buffer, cursor);
                        }
                        break;

                    case ConsoleKey.LeftArrow:
                        if (cursor > 0)
                        {
                            cursor--;
                            Console.Write("\b");
                        }
                        break;

                    case ConsoleKey.RightArrow:
                        if (cursor < buffer.Length)
                        {
                            Console.Write(buffer[cursor]);
                            cursor++;
                        }
                        break;

                    case ConsoleKey.Home:
                        if (cursor > 0)
                        {
                            Console.Write(new string('\b', cursor));
                            cursor = 0;
                        }
                        break;

                    case ConsoleKey.End:
                        if (cursor < buffer.Length)
                        {
                            Console.Write(buffer.ToString()[cursor..]);
                            cursor = buffer.Length;
                        }
                        break;

                    case ConsoleKey.UpArrow:
                    {
                        var prev = history.Previous(buffer.ToString());
                        if (prev != null)
                        {
                            buffer.Clear();
                            buffer.Append(prev);
                            cursor = buffer.Length;
                            RedrawLine(buffer, cursor);
                        }
                        break;
                    }

                    case ConsoleKey.DownArrow:
                    {
                        var next = history.Next() ?? "";
                        buffer.Clear();
                        buffer.Append(next);
                        cursor = buffer.Length;
                        RedrawLine(buffer, cursor);
                        break;
                    }

                    default:
                        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            Console.WriteLine();
                            return null;
                        }
                        if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            Console.WriteLine();
                            return null;
                        }
                        if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            buffer.Clear();
                            cursor = 0;
                            RedrawLine(buffer, cursor);
                            break;
                        }
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
                        {
                            cursor = 0;
                            RedrawLine(buffer, cursor);
                            break;
                        }
                        if (key.Key == ConsoleKey.E && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            cursor = buffer.Length;
                            RedrawLine(buffer, cursor);
                            break;
                        }
                        if (key.Key == ConsoleKey.K && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            if (cursor < buffer.Length)
                            {
                                buffer.Remove(cursor, buffer.Length - cursor);
                                RedrawLine(buffer, cursor);
                            }
                            break;
                        }
                        if (!char.IsControl(key.KeyChar))
                        {
                            buffer.Insert(cursor, key.KeyChar);
                            cursor++;
                            if (cursor == buffer.Length)
                                Console.Write(key.KeyChar);
                            else
                                RedrawLine(buffer, cursor);
                        }
                        break;
                }
            }
            else if (buffer.Length == 0 && notifications.TryDequeue(out var notification))
            {
                Console.Write($"\r{EraseLine}");
                Console.Write($"  {FgMagenta}●{Rst} {FgGray}{notification!.Timestamp:HH:mm:ss}{Rst} {FgBrMagenta}{notification.AgentName}{Rst}{FgGray}: {notification.Message}{Rst}\n");
                Console.Write($"{FgBrGreen}{PromptChar}{Rst} ");
            }
            else
            {
                Thread.Sleep(50);
            }
        }

        return null;
    }

    // ── Commands ────────────────────────────────────────────────────

    private static async Task<bool> HandleCommand(
        string input,
        SharpClawConfig config,
        AgentRuntime runtime,
        SessionManager sessionManager,
        ScreenCaptureService screenCapture,
        ScreenAnalyzer screenAnalyzer,
        ToolRegistry toolRegistry,
        Mailbox mailbox,
        AgentScheduler agentScheduler)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "/quit" or "/exit" or "/q":
                Console.Write($"  {FgGray}goodbye ✦{Rst}\n");
                return false;

            case "/new":
                sessionManager.NewSession();
                var systemPrompt = BootstrapLoader.BuildSystemPrompt(config.ResolvedWorkspace);
                runtime.UpdateSystemPrompt(systemPrompt);
                sessionManager.SetSystemPrompt(systemPrompt);
                Console.Write($"  {FgGreen}✓{Rst} New session: {FgWhite}{sessionManager.SessionId}{Rst}\n");
                return true;

            case "/sessions":
            {
                var sessions = sessionManager.ListSessions();
                if (sessions.Count == 0)
                {
                    Console.Write($"  {FgGray}No saved sessions.{Rst}\n");
                }
                else
                {
                    Console.Write($"  {FgWhite}Sessions{Rst} {FgGray}({sessions.Count}){Rst}\n");
                    foreach (var s in sessions.Take(20))
                        Console.Write($"  {FgGray}│{Rst} {s}\n");
                    if (sessions.Count > 20)
                        Console.Write($"  {FgGray}│ ... and {sessions.Count - 20} more{Rst}\n");
                }
                return true;
            }

            case "/load":
                if (parts.Length < 2)
                {
                    Console.Write($"  {FgYellow}Usage:{Rst} /load <session-id>\n");
                    return true;
                }
                sessionManager.LoadSession(parts[1]);
                Console.Write($"  {FgGreen}✓{Rst} Loaded: {FgWhite}{parts[1]}{Rst} {FgGray}({sessionManager.Messages.Count} messages){Rst}\n");
                return true;

            case "/screen":
            {
                var subCmd = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
                switch (subCmd)
                {
                    case "on":
                        screenCapture.Resume();
                        if (!screenCapture.IsRunning)
                            await screenCapture.StartAsync();
                        Console.Write($"  {FgGreen}✓{Rst} Screen monitoring resumed\n");
                        break;
                    case "off":
                        screenCapture.Pause();
                        Console.Write($"  {FgYellow}▸{Rst} Screen monitoring paused\n");
                        break;
                    case "status":
                        var state = screenCapture.IsPaused ? $"{FgYellow}paused{Rst}" : $"{FgGreen}active{Rst}";
                        Console.Write($"  {FgGray}screen{Rst}        {state}\n");
                        Console.Write($"  {FgGray}observations{Rst}  {screenAnalyzer.ObservationCount}\n");
                        var latest = screenAnalyzer.GetLatest();
                        if (latest is not null)
                            Console.Write($"  {FgGray}latest{Rst}        {FgGray}[{latest.Timestamp:HH:mm:ss}]{Rst} {latest.Description[..Math.Min(latest.Description.Length, 80)]}\n");
                        break;
                    default:
                        Console.Write($"  {FgYellow}Usage:{Rst} /screen [on|off|status]\n");
                        break;
                }
                return true;
            }

            case "/history":
            {
                var count = 20;
                if (parts.Length > 1 && int.TryParse(parts[1], out var n))
                    count = Math.Clamp(n, 1, 200);

                var observations = screenAnalyzer.GetObservations(minutesBack: 60 * 24 * 365);
                if (observations.Count == 0)
                {
                    Console.Write($"  {FgGray}No screen observations recorded.{Rst}\n");
                    return true;
                }

                var shown = observations.TakeLast(count).ToList();
                Console.Write($"  {FgWhite}Screen History{Rst} {FgGray}(last {shown.Count} of {observations.Count}){Rst}\n");
                var lastDate = "";
                foreach (var obs in shown)
                {
                    var date = obs.Timestamp.ToString("yyyy-MM-dd");
                    if (date != lastDate)
                    {
                        Console.Write($"\n  {FgCyan}{date}{Rst}\n");
                        lastDate = date;
                    }
                    Console.Write($"  {FgGray}│ {obs.Timestamp:HH:mm:ss}{Rst}  {obs.Description}\n");
                }
                return true;
            }

            case "/tools":
            {
                Console.Write($"\n  {FgWhite}Available Tools{Rst}\n");
                Console.Write($"  {Dim}───────────────────────────────────────{Rst}\n");
                foreach (var tool in toolRegistry.All)
                {
                    var desc = tool.Description;
                    var firstLine = desc.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? desc;
                    if (firstLine.Length > 70)
                        firstLine = firstLine[..67] + "...";
                    Console.Write($"  {FgCyan}{tool.Name,-22}{Rst} {FgGray}{firstLine}{Rst}\n");
                }
                Console.Write($"\n  {FgGray}Invoke directly: /tool <name> {{\"param\":\"value\"}}{Rst}\n");
                return true;
            }

            case "/tool":
            {
                if (parts.Length < 2)
                {
                    Console.Write($"  {FgYellow}Usage:{Rst} /tool <name> [json-args]\n");
                    Console.Write($"  {FgGray}Examples:{Rst}\n");
                    Console.Write($"  {FgGray}│{Rst} /tool read_file {{\"path\":\"SOUL.md\"}}\n");
                    Console.Write($"  {FgGray}│{Rst} /tool memory {{\"action\":\"read\"}}\n");
                    Console.Write($"  {FgGray}│{Rst} /tool shell_exec {{\"command\":\"uname -a\"}}\n");
                    return true;
                }

                var rest = parts[1];
                var spaceIdx = rest.IndexOf(' ');
                string toolName;
                string toolArgs;

                if (spaceIdx < 0)
                {
                    toolName = rest.Trim();
                    toolArgs = "{}";
                }
                else
                {
                    toolName = rest[..spaceIdx].Trim();
                    toolArgs = rest[(spaceIdx + 1)..].Trim();
                    if (string.IsNullOrEmpty(toolArgs)) toolArgs = "{}";
                }

                Console.Write($"  {FgGray}┊{Rst} {FgCyan}⚙ {toolName}{Rst} {FgGray}{(toolArgs != "{}" ? toolArgs : "")}{Rst}\n");
                var result = await toolRegistry.ExecuteAsync(toolName, toolArgs);
                Console.WriteLine(result);
                return true;
            }

            case "/agents":
            {
                var agents = agentScheduler.ListAgents();
                if (agents.Count == 0)
                {
                    Console.Write($"  {FgGray}No agents. Chat with SharpClaw to spawn some.{Rst}\n");
                    return true;
                }
                Console.Write($"\n  {FgWhite}Agents{Rst} {FgGray}({agents.Count}){Rst}\n");
                Console.Write($"  {Dim}───────────────────────────────────────{Rst}\n");
                foreach (var a in agents)
                {
                    var (statusIcon, statusColor) = a.Status switch
                    {
                        AgentStatus.Running => ("●", FgBrGreen),
                        AgentStatus.Paused => ("◐", FgBrYellow),
                        AgentStatus.Completed => ("✓", FgCyan),
                        AgentStatus.Failed => ("✗", FgRed),
                        _ => ("○", FgGray)
                    };
                    var kind = a.Kind == AgentKind.LongRunning ? "agent" : "task";
                    Console.Write($"  {statusColor}{statusIcon}{Rst} {FgWhite}{a.Name}{Rst} {FgGray}({kind}){Rst}\n");
                    Console.Write($"    {FgGray}id: {a.Id}{Rst}\n");
                    var purpose = a.Purpose.ReplaceLineEndings(" ");
                    if (purpose.Length > 70) purpose = purpose[..67] + "...";
                    Console.Write($"    {FgGray}{purpose}{Rst}\n");
                    var runInfo = $"runs: {a.RunCount}";
                    if (a.Schedule != null) runInfo = $"schedule: {a.Schedule}, {runInfo}";
                    if (a.LastRunAt.HasValue) runInfo += $", last: {a.LastRunAt.Value:HH:mm:ss}";
                    Console.Write($"    {FgGray}{runInfo}{Rst}\n");
                    if (a.LastError != null)
                        Console.Write($"    {FgRed}error: {a.LastError[..Math.Min(a.LastError.Length, 80)]}{Rst}\n");
                }
                return true;
            }

            case "/mailbox":
            {
                var messages = mailbox.GetAll(limit: 20);
                if (messages.Count == 0)
                {
                    Console.Write($"  {FgGray}Mailbox is empty.{Rst}\n");
                    return true;
                }
                var unread = mailbox.UnreadCount;
                Console.Write($"\n  {FgWhite}Mailbox{Rst} {FgGray}({unread} unread, {mailbox.TotalCount} total){Rst}\n");
                Console.Write($"  {Dim}───────────────────────────────────────{Rst}\n");
                foreach (var msg in messages)
                {
                    var marker = msg.Read ? $"{FgGray}○{Rst}" : $"{FgBrMagenta}●{Rst}";
                    var nameColor = msg.Read ? FgGray : FgWhite;
                    Console.Write($"  {marker} {nameColor}{msg.FromName}{Rst} {FgGray}· {msg.Timestamp:MM-dd HH:mm} · {msg.Id}{Rst}\n");
                    var subjectColor = msg.Read ? FgGray : Rst;
                    Console.Write($"    {subjectColor}{msg.Subject}{Rst}\n");
                    var body = msg.Body.ReplaceLineEndings(" ");
                    if (body.Length > 100) body = body[..97] + "...";
                    if (!string.IsNullOrWhiteSpace(body))
                        Console.Write($"    {FgGray}{body}{Rst}\n");
                }
                return true;
            }

            case "/help" or "/?":
                PrintHelp();
                return true;

            case "/info":
            {
                Console.Write($"\n  {FgWhite}Dashboard{Rst}\n");
                Console.Write($"  {Dim}───────────────────────────────────────{Rst}\n");
                Console.Write($"  {FgGray}session{Rst}     {FgWhite}{sessionManager.SessionId}{Rst}\n");
                Console.Write($"  {FgGray}messages{Rst}    {sessionManager.Messages.Count}\n");
                Console.Write($"  {FgGray}model{Rst}       {FgWhite}{config.Model}{Rst}\n");
                Console.Write($"  {FgGray}server{Rst}      {config.LlamaCppUrl}\n");
                Console.Write($"  {FgGray}workspace{Rst}   {config.ResolvedWorkspace}\n");

                var scrState = screenCapture.IsPaused ? $"{FgYellow}paused{Rst}" : $"{FgGreen}active{Rst}";
                Console.Write($"  {FgGray}screen{Rst}      {scrState} {FgGray}({screenAnalyzer.ObservationCount} observations){Rst}\n");

                var agentList2 = agentScheduler.ListAgents();
                var running = agentList2.Count(a => a.Status == AgentStatus.Running);
                Console.Write($"  {FgGray}agents{Rst}      {FgBrCyan}{running}{Rst} running{FgGray}, {agentList2.Count} total{Rst}\n");
                Console.Write($"  {FgGray}mailbox{Rst}     ");
                if (mailbox.UnreadCount > 0)
                    Console.Write($"{FgBrMagenta}{mailbox.UnreadCount} unread{Rst}{FgGray} / {mailbox.TotalCount} total{Rst}\n");
                else
                    Console.Write($"{FgGray}{mailbox.TotalCount} messages{Rst}\n");

                return true;
            }

            default:
                Console.Write($"  {FgYellow}?{Rst} Unknown command: {command}. Type {FgWhite}/help{Rst} for commands.\n");
                return true;
        }
    }

    // ── Help ────────────────────────────────────────────────────────

    private static void PrintHelp()
    {
        void Cmd(string name, string desc) =>
            Console.Write($"    {FgBrCyan}{name,-20}{Rst} {FgGray}{desc}{Rst}\n");

        void Section(string title) =>
            Console.Write($"\n  {FgWhite}{title}{Rst} {Dim}{"".PadRight(35 - title.Length, '─')}{Rst}\n");

        Section("session");
        Cmd("/new", "start a fresh session");
        Cmd("/sessions", "list saved sessions");
        Cmd("/load <id>", "restore a session");

        Section("monitor");
        Cmd("/screen [on|off]", "toggle screen watching");
        Cmd("/history [n]", "screen observation log");

        Section("tools & agents");
        Cmd("/tools", "list available tools");
        Cmd("/tool <name> [args]", "invoke a tool directly");
        Cmd("/agents", "list sub-agents");
        Cmd("/mailbox", "read agent mailbox");

        Section("other");
        Cmd("/info", "session dashboard");
        Cmd("/help", "this help");
        Cmd("/quit", "exit");

        Console.Write($"\n  {FgGray}Or just type a message to chat.{Rst}\n");
    }
}

// ── Prompt history ──────────────────────────────────────────────────

internal class PromptHistory
{
    private readonly string _path;
    private readonly List<string> _entries = [];
    private int _index;
    private string? _savedCurrent;
    private const int MaxEntries = 1000;

    public PromptHistory(string path)
    {
        _path = path;
        Load();
        _index = _entries.Count;
    }

    public void BeginSession()
    {
        _index = _entries.Count;
        _savedCurrent = null;
    }

    public void Commit(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_entries.Count > 0 && _entries[^1] == line)
        {
            _index = _entries.Count;
            return;
        }
        _entries.Add(line);
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        _index = _entries.Count;
        Append(line);
    }

    public string? Previous(string currentBuffer)
    {
        if (_entries.Count == 0) return null;
        if (_index == _entries.Count)
            _savedCurrent = currentBuffer;
        if (_index > 0)
        {
            _index--;
            return _entries[_index];
        }
        return null;
    }

    public string? Next()
    {
        if (_index >= _entries.Count - 1)
        {
            _index = _entries.Count;
            return _savedCurrent ?? "";
        }
        _index++;
        return _entries[_index];
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var lines = File.ReadAllLines(_path);
            foreach (var line in lines.TakeLast(MaxEntries))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _entries.Add(line);
            }
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
                    var fileLines = File.ReadAllLines(_path);
                    if (fileLines.Length > MaxEntries * 2)
                    {
                        var keep = fileLines.TakeLast(MaxEntries).ToArray();
                        File.WriteAllLines(_path, keep);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
