using Microsoft.Extensions.Logging;
using SharpClaw.Configuration;
using SharpClaw.Logging;
using SharpClaw.Screen;

namespace SharpClaw.Daemon;

/// <summary>
/// Headless screen capture daemon. Runs the capture service and analyzer
/// without any TUI, agent runtime, or tools. Writes observations to the
/// shared screen-history.jsonl so the TUI and agents can read them.
/// Managed as a systemd user service.
/// </summary>
public static class ScreenDaemon
{
    private static readonly string PidPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sharpclaw", "screen-daemon.pid");

    public static bool IsRunning()
    {
        if (!File.Exists(PidPath)) return false;
        try
        {
            var pidStr = File.ReadAllText(PidPath).Trim();
            if (!int.TryParse(pidStr, out var pid)) return false;
            var procDir = $"/proc/{pid}";
            if (!Directory.Exists(procDir)) { TryCleanPid(); return false; }
            var cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline"));
            if (cmdline.Contains("SharpClaw", StringComparison.OrdinalIgnoreCase) &&
                cmdline.Contains("daemon", StringComparison.OrdinalIgnoreCase))
                return true;
            TryCleanPid();
            return false;
        }
        catch { return false; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var log = Log.ForName("ScreenDaemon");
        var config = SharpClawConfig.Load();
        config.EnsureDirectories();

        WritePid();

        log.LogInformation("Screen daemon starting (interval: {Interval}s, model: {Model})",
            config.Screen.CaptureIntervalSeconds, config.VisionModel);

        var analyzer = new ScreenAnalyzer(config);
        var capture = new ScreenCaptureService(config.Screen, analyzer.OnScreenCaptured);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        await capture.StartAsync();
        if (!capture.IsRunning)
        {
            log.LogError("No screenshot tool available. Install grim (Wayland) or scrot (X11).");
            TryCleanPid();
            return 1;
        }

        log.LogInformation("Screen daemon running (pid {Pid})", Environment.ProcessId);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }

        log.LogInformation("Screen daemon shutting down");
        capture.Stop();
        capture.Dispose();
        analyzer.Dispose();
        TryCleanPid();
        return 0;
    }

    private static void WritePid()
    {
        try
        {
            var dir = Path.GetDirectoryName(PidPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(PidPath, Environment.ProcessId.ToString());
        }
        catch { }
    }

    private static void TryCleanPid()
    {
        try { File.Delete(PidPath); } catch { }
    }
}
