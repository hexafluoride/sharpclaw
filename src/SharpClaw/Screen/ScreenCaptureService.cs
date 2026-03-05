using System.Diagnostics;
using System.Security.Cryptography;
using SharpClaw.Configuration;

namespace SharpClaw.Screen;

/// <summary>
/// Background service that periodically captures screenshots using system tools.
/// Detects display server: tries grim (Wayland) first, falls back to scrot (X11).
/// Captured frames are emitted via a callback for analysis.
/// </summary>
public class ScreenCaptureService : IDisposable
{
    private readonly ScreenConfig _config;
    private readonly Func<byte[], string, Task> _onCapture;
    private CancellationTokenSource? _cts;
    private Task? _captureLoop;
    private string? _captureCommand;
    private volatile bool _paused;

    public bool IsRunning => _captureLoop is not null && !_captureLoop.IsCompleted;
    public bool IsPaused => _paused;

    public ScreenCaptureService(ScreenConfig config, Func<byte[], string, Task> onCapture)
    {
        _config = config;
        _onCapture = onCapture;
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        _captureCommand = await DetectCaptureCommand();
        if (_captureCommand is null)
        {
            Console.Error.WriteLine("[screen] No screenshot tool found. Install grim (Wayland) or scrot (X11).");
            return;
        }

        Console.Error.WriteLine($"[screen] Using capture command: {_captureCommand}");
        _cts = new CancellationTokenSource();
        _captureLoop = RunCaptureLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    /// <summary>
    /// Take a single screenshot on demand. Returns the image bytes and a hash,
    /// or (null, "") if capture failed. Ensures the capture command has been detected.
    /// </summary>
    public async Task<(byte[]? data, string hash)> CaptureOnceAsync(CancellationToken ct = default)
    {
        _captureCommand ??= await DetectCaptureCommand();
        if (_captureCommand is null)
            return (null, "");
        return await CaptureScreenshot(ct);
    }

    private async Task RunCaptureLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _config.CaptureIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                if (_paused) continue;

                var (imageData, hash) = await CaptureScreenshot(ct);
                if (imageData is not null)
                {
                    await _onCapture(imageData, hash);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screen] Capture error: {ex.Message}");
            }
        }
    }

    private async Task<(byte[]? data, string hash)> CaptureScreenshot(CancellationToken ct)
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"sharpclaw-capture-{Guid.NewGuid():N}.png");
        try
        {
            var command = _captureCommand!.Replace("{output}", tmpFile);
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0 || !File.Exists(tmpFile))
                return (null, "");

            var data = await File.ReadAllBytesAsync(tmpFile, ct);
            var hash = Convert.ToHexString(SHA256.HashData(data))[..16];
            return (data, hash);
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    private async Task<string?> DetectCaptureCommand()
    {
        if (_config.CaptureCommand != "auto" && _config.CaptureCommand != "grim" && _config.CaptureCommand != "scrot")
            return _config.CaptureCommand.Replace("{}", "{output}");

        if (_config.CaptureCommand == "grim" || _config.CaptureCommand == "auto")
        {
            if (await CommandExists("grim"))
                return "grim {output}";
        }

        if (_config.CaptureCommand == "scrot" || _config.CaptureCommand == "auto")
        {
            if (await CommandExists("scrot"))
                return "scrot -o {output}";
        }

        if (_config.CaptureCommand == "auto" && await CommandExists("import"))
            return "import -window root {output}";

        return null;
    }

    private static async Task<bool> CommandExists(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
