using System.Text.Json;

namespace SharpClaw.Tools;

public class WriteFileTool : ITool
{
    private readonly string _workspaceDir;

    public WriteFileTool(string workspaceDir) => _workspaceDir = workspaceDir;

    public string Name => "write_file";
    public string Description => "Write content to a file (creates parent directories if needed). Path is relative to workspace or absolute.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "path", "content" },
        properties = new
        {
            path = new { type = "string", description = "File path (relative to workspace or absolute)" },
            content = new { type = "string", description = "Content to write to the file" }
        }
    });

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var path = arguments.GetProperty("path").GetString()
            ?? throw new ArgumentException("Missing 'path' parameter");
        var content = arguments.GetProperty("content").GetString()
            ?? throw new ArgumentException("Missing 'content' parameter");

        var resolved = ResolveSafePath(path);
        if (resolved == null)
            return $"Error: Path must be within workspace ({_workspaceDir})";

        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(resolved, content, ct);
        return $"Successfully wrote {content.Length} characters to {path}";
    }

    private string? ResolveSafePath(string path)
    {
        var resolved = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_workspaceDir, path));
        var root = Path.GetFullPath(_workspaceDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(root) && resolved != root.TrimEnd(Path.DirectorySeparatorChar))
            return null;
        return resolved;
    }
}
