using Microsoft.Extensions.Logging;
using SharpClaw.ActivityWatch;
using SharpClaw.Configuration;
using SharpClaw.Logging;
using SharpClaw.Screen;

namespace SharpClaw.Daemon;

/// <summary>
/// Headless screen capture daemon. Runs the capture service and analyzer
/// without any TUI, agent runtime, or tools. Writes observations to the
/// shared screen-history.jsonl so the TUI and agents can read them.
/// When ActivityWatch is available, enriches each observation with the
/// current app, window title, URL, and AFK status from AW.
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

        ActivityWatchClient? awClient = null;
        if (config.ActivityWatch.Enabled)
        {
            awClient = new ActivityWatchClient(config.ActivityWatch);
            if (await awClient.IsAvailableAsync())
                log.LogInformation("ActivityWatch connected at {Url}", config.ActivityWatch.Url);
            else
            {
                log.LogWarning("ActivityWatch not reachable at {Url}, continuing without enrichment",
                    config.ActivityWatch.Url);
                awClient.Dispose();
                awClient = null;
            }
        }

        var analyzer = new ScreenAnalyzer(config);

        async Task OnCapture(byte[] imageData, string hash)
        {
            string? app = null, title = null, url = null;
            bool? isAfk = null;

            if (awClient is not null)
            {
                try
                {
                    var windowBucket = await awClient.FindBucketAsync("aw-watcher-window");
                    if (windowBucket is not null)
                    {
                        var events = await awClient.GetEventsAsync(windowBucket, limit: 1);
                        if (events.Count > 0)
                        {
                            var d = events[0].Data;
                            app = d.TryGetProperty("app", out var a) ? a.GetString() : null;
                            title = d.TryGetProperty("title", out var t) ? t.GetString() : null;
                        }
                    }

                    var webBucket = await awClient.FindBucketAsync("aw-watcher-web");
                    if (webBucket is not null)
                    {
                        var events = await awClient.GetEventsAsync(webBucket, limit: 1);
                        if (events.Count > 0)
                        {
                            var d = events[0].Data;
                            url = d.TryGetProperty("url", out var u) ? u.GetString() : null;
                        }
                    }

                    var afkStatus = await awClient.GetAfkStatusAsync();
                    if (afkStatus.HasValue)
                        isAfk = afkStatus.Value.isAfk;
                }
                catch (Exception ex)
                {
                    log.LogDebug("AW enrichment failed: {Err}", ex.Message);
                }
            }

            await analyzer.OnScreenCaptured(imageData, hash, app, title, url, isAfk);
        }

        var capture = new ScreenCaptureService(config.Screen, OnCapture);

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
        awClient?.Dispose();
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
