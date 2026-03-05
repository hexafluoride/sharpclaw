using System.Text.Json;

namespace SharpClaw.Tools;

public class ReadFileTool : ITool
{
    private readonly string _workspaceDir;

    public ReadFileTool(string workspaceDir) => _workspaceDir = workspaceDir;

    public string Name => "read_file";
    public string Description => "Read the full contents of a file (max 100K chars). Path is relative to workspace or absolute.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "path" },
        properties = new
        {
            path = new { type = "string", description = "File path (relative to workspace or absolute)" }
        }
    });

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var path = arguments.GetProperty("path").GetString()
            ?? throw new ArgumentException("Missing 'path' parameter");

        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
            return $"Error: File not found: {path}";

        var content = await File.ReadAllTextAsync(resolved, ct);
        const int maxChars = 100_000;
        if (content.Length > maxChars)
            return content[..maxChars] + $"\n\n[Truncated: file is {content.Length} chars, showing first {maxChars}]";

        return content;
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(_workspaceDir, path));
    }
}
