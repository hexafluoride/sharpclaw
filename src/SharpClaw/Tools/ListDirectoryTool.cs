using System.Text;
using System.Text.Json;

namespace SharpClaw.Tools;

public class ListDirectoryTool : ITool
{
    private readonly string _workspaceDir;

    public ListDirectoryTool(string workspaceDir) => _workspaceDir = workspaceDir;

    public string Name => "list_directory";
    public string Description => "List files and directories (with sizes). Path defaults to workspace root. Set recursive=true to include subdirectories.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Directory path (default: workspace root)" },
            recursive = new { type = "boolean", description = "List recursively (default: false)" }
        }
    });

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var path = ".";
        if (arguments.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
            path = pathProp.GetString() ?? ".";

        var recursive = false;
        if (arguments.TryGetProperty("recursive", out var recProp) && recProp.ValueKind == JsonValueKind.True)
            recursive = true;

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
            return Task.FromResult($"Error: Directory not found: {path}");

        var sb = new StringBuilder();
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = new List<string>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(resolved, "*", option))
                entries.Add(Path.GetRelativePath(resolved, dir) + "/");

            foreach (var file in Directory.EnumerateFiles(resolved, "*", option))
            {
                var rel = Path.GetRelativePath(resolved, file);
                var info = new FileInfo(file);
                entries.Add($"{rel} ({FormatSize(info.Length)})");
            }
        }
        catch (UnauthorizedAccessException)
        {
            entries.Add("[some entries inaccessible due to permissions]");
        }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        if (entries.Count == 0)
            return Task.FromResult("Directory is empty.");

        const int maxEntries = 500;
        if (entries.Count > maxEntries)
        {
            sb.AppendLine($"[Showing first {maxEntries} of {entries.Count} entries]");
            entries = entries.Take(maxEntries).ToList();
        }

        foreach (var entry in entries)
            sb.AppendLine(entry);

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(_workspaceDir, path));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1}GB"
    };
}
