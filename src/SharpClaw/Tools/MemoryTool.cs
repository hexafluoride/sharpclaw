using System.Text.Json;

namespace SharpClaw.Tools;

/// <summary>
/// Lets the agent read and write its long-term memory file (MEMORY.md).
/// This file is loaded into the system prompt on every session start,
/// so anything saved here persists across sessions and restarts.
/// </summary>
public class MemoryTool : ITool
{
    private readonly string _memoryPath;
    private readonly string _archiveDir;
    private const int MaxMemoryCharsBeforeCompaction = 10000;
    private const int TargetMemoryCharsAfterCompaction = 6000;
    private const int KeepRecentEntries = 120;

    public MemoryTool(string workspaceDir, string archiveDir)
    {
        _memoryPath = Path.Combine(workspaceDir, "MEMORY.md");
        _archiveDir = archiveDir;
        Directory.CreateDirectory(_archiveDir);
    }

    public string Name => "memory";
    public string Description =>
        "Read or update your persistent long-term memory. " +
        "Use action 'read' to see what you currently remember. " +
        "Use action 'append' to add a new fact or observation. " +
        "Use action 'replace' to rewrite the entire memory (use sparingly). " +
        "Memory is compacted automatically when it grows too large, and full " +
        "uncompacted snapshots are archived with timestamps before compaction. " +
        "Memory persists across sessions and restarts. Use this proactively to " +
        "remember important facts about the user, their preferences, projects, and habits.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "action" },
        properties = new
        {
            action = new
            {
                type = "string",
                description = "One of: 'read', 'append', 'replace'",
                @enum = new[] { "read", "append", "replace" }
            },
            content = new
            {
                type = "string",
                description = "The text to append or replace with (required for append/replace)"
            }
        }
    });

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var action = arguments.GetProperty("action").GetString()?.ToLowerInvariant()
            ?? throw new ArgumentException("Missing 'action' parameter");

        string? content = null;
        if (arguments.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            content = contentProp.GetString();

        return Task.FromResult(action switch
        {
            "read" => ReadMemory(),
            "append" => AppendMemory(content),
            "replace" => ReplaceMemory(content),
            _ => $"Unknown action '{action}'. Use 'read', 'append', or 'replace'."
        });
    }

    private string ReadMemory()
    {
        if (!File.Exists(_memoryPath))
            return "Memory is empty. Use the 'append' action to start remembering things.";
        var text = File.ReadAllText(_memoryPath).Trim();
        return string.IsNullOrEmpty(text)
            ? "Memory is empty. Use the 'append' action to start remembering things."
            : text;
    }

    private string AppendMemory(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "Error: 'content' is required for append.";

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
        var entry = $"\n- [{timestamp}] {content.Trim()}\n";

        if (!File.Exists(_memoryPath))
            File.WriteAllText(_memoryPath, "# Memory\n\nPersistent observations and facts about the user.\n");

        File.AppendAllText(_memoryPath, entry);
        var compacted = CompactMemoryIfNeeded();
        return compacted
            ? $"Remembered: {content.Trim()} (memory compacted; full snapshot archived)"
            : $"Remembered: {content.Trim()}";
    }

    private string ReplaceMemory(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "Error: 'content' is required for replace.";

        // Archive the old memory before overwriting
        if (File.Exists(_memoryPath))
        {
            var archivePath = BuildArchivePath("memory-replace", ".md");
            File.Copy(_memoryPath, archivePath, overwrite: false);
        }

        File.WriteAllText(_memoryPath, content);
        var compacted = CompactMemoryIfNeeded();
        return compacted
            ? "Memory replaced (previous version archived; new memory compacted with uncompacted snapshot archived)."
            : "Memory replaced (previous version archived).";
    }

    private bool CompactMemoryIfNeeded()
    {
        if (!File.Exists(_memoryPath))
            return false;

        var current = File.ReadAllText(_memoryPath);
        if (current.Length <= MaxMemoryCharsBeforeCompaction)
            return false;

        // Always archive the full uncompacted context before rewriting.
        var uncompactedArchive = BuildArchivePath("memory-uncompacted", ".md");
        File.Copy(_memoryPath, uncompactedArchive, overwrite: false);

        var lines = current.Replace("\r\n", "\n").Split('\n');
        var bulletEntries = lines
            .Select(l => l.TrimEnd())
            .Where(l => l.TrimStart().StartsWith("- [", StringComparison.Ordinal))
            .ToList();

        var compacted = new List<string>
        {
            "# Memory",
            "",
            "Persistent observations and facts about the user.",
            "",
            $"_Compacted on {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}. Full uncompacted snapshot archived at `{uncompactedArchive}`._",
            ""
        };

        if (bulletEntries.Count > 0)
        {
            var selected = new List<string>();
            for (var i = bulletEntries.Count - 1; i >= 0; i--)
            {
                selected.Add(bulletEntries[i]);
                if (selected.Count >= KeepRecentEntries)
                    break;
                if (selected.Sum(s => s.Length + 1) >= TargetMemoryCharsAfterCompaction)
                    break;
            }
            selected.Reverse();
            compacted.AddRange(selected);
        }
        else
        {
            // If memory is not in bullet form, keep the trailing chunk.
            var tail = current.Length <= TargetMemoryCharsAfterCompaction
                ? current
                : current[^TargetMemoryCharsAfterCompaction..];
            compacted.Add(tail.Trim());
        }

        File.WriteAllText(_memoryPath, string.Join("\n", compacted).TrimEnd() + "\n");
        return true;
    }

    private string BuildArchivePath(string prefix, string extension)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(_archiveDir, $"{prefix}-{timestamp}{extension}");
    }
}
