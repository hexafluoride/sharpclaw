using System.Diagnostics;
using System.Text.Json;

namespace SharpClaw.Tools;

public class ShellExecTool : ITool
{
    private readonly string _workspaceDir;

    public ShellExecTool(string workspaceDir) => _workspaceDir = workspaceDir;

    public string Name => "shell_exec";
    public string Description =>
        "Execute a shell command in the workspace directory and return stdout/stderr. " +
        "Default timeout is 30 seconds. Avoid slow commands like 'find' on large directories - " +
        "use list_directory instead, or scope find to specific subdirectories. " +
        "Increase timeout_seconds for long-running commands.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "command" },
        properties = new
        {
            command = new { type = "string", description = "The shell command to execute" },
            timeout_seconds = new { type = "integer", description = "Timeout in seconds (default: 30)" }
        }
    });

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var command = arguments.GetProperty("command").GetString()
            ?? throw new ArgumentException("Missing 'command' parameter");

        var timeoutSeconds = 30;
        if (arguments.TryGetProperty("timeout_seconds", out var timeoutProp))
            timeoutSeconds = timeoutProp.GetInt32();

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = _workspaceDir,
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
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var result = $"Exit code: {process.ExitCode}";
            if (!string.IsNullOrEmpty(stdout))
                result += $"\n\nStdout:\n{Truncate(stdout)}";
            if (!string.IsNullOrEmpty(stderr))
                result += $"\n\nStderr:\n{Truncate(stderr)}";

            return result;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return $"Error: Command timed out after {timeoutSeconds} seconds. " +
                   "Try a more targeted command, or increase timeout_seconds.";
        }
    }

    private static string Truncate(string text, int maxLength = 50_000)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + $"\n[Truncated: {text.Length} chars total]";
    }

}
