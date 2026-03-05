using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Configuration;

public class ScreenConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("captureIntervalSeconds")]
    public int CaptureIntervalSeconds { get; set; } = 10;

    [JsonPropertyName("maxObservations")]
    public int MaxObservations { get; set; } = 50;

    [JsonPropertyName("captureCommand")]
    public string CaptureCommand { get; set; } = "auto";
}

public class ActivityWatchConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("url")]
    public string Url { get; set; } = "http://localhost:5600";

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 10;
}

public class BrowserConfig
{
    [JsonPropertyName("headless")]
    public bool Headless { get; set; } = true;

    [JsonPropertyName("viewportWidth")]
    public int ViewportWidth { get; set; } = 1280;

    [JsonPropertyName("viewportHeight")]
    public int ViewportHeight { get; set; } = 720;
}

public class SharpClawConfig
{
    [JsonPropertyName("llamaCppUrl")]
    public string LlamaCppUrl { get; set; } = "http://10.0.0.218:4683";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "qwen2.5";

    [JsonPropertyName("visionModel")]
    public string VisionModel { get; set; } = "qwen2.5-vl";

    [JsonPropertyName("visionEndpoint")]
    public string VisionEndpoint { get; set; } = "http://10.0.0.218:4683";

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "~/.sharpclaw/workspace";

    [JsonPropertyName("maxIterations")]
    public int MaxIterations { get; set; } = 25;

    [JsonPropertyName("maxContextTokens")]
    public int MaxContextTokens { get; set; } = 120000;

    [JsonPropertyName("maxCompletionTokens")]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("shellTimeoutSeconds")]
    public int ShellTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("memoryCompactionThreshold")]
    public int MemoryCompactionThreshold { get; set; } = 4000;

    [JsonPropertyName("subAgentHistoryMessages")]
    public int SubAgentHistoryMessages { get; set; } = 20;

    [JsonPropertyName("maxMailboxMessages")]
    public int MaxMailboxMessages { get; set; } = 500;

    [JsonPropertyName("maxNotifications")]
    public int MaxNotifications { get; set; } = 200;

    [JsonPropertyName("llmRetryCount")]
    public int LlmRetryCount { get; set; } = 3;

    [JsonPropertyName("llmRetryBaseDelayMs")]
    public int LlmRetryBaseDelayMs { get; set; } = 1000;

    [JsonPropertyName("screen")]
    public ScreenConfig Screen { get; set; } = new();

    [JsonPropertyName("activityWatch")]
    public ActivityWatchConfig ActivityWatch { get; set; } = new();

    [JsonPropertyName("browser")]
    public BrowserConfig Browser { get; set; } = new();

    public string ResolvedWorkspace => ExpandTilde(Workspace);
    public string ResolvedConfigDir => ExpandTilde("~/.sharpclaw");
    public string ResolvedSessionsDir => Path.Combine(ResolvedConfigDir, "sessions");
    public string ResolvedArchiveDir => Path.Combine(ResolvedConfigDir, "archive");
    public string ResolvedBrowserDataDir => Path.Combine(ResolvedConfigDir, "browser-data");

    private static string ExpandTilde(string path)
    {
        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }
        return path;
    }

    private static readonly string ConfigPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sharpclaw", "sharpclaw.json");

    public static SharpClawConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new SharpClawConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<SharpClawConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? new SharpClawConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ConfigPath, json);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ResolvedWorkspace);
        Directory.CreateDirectory(ResolvedSessionsDir);
        Directory.CreateDirectory(ResolvedArchiveDir);
        Directory.CreateDirectory(ResolvedBrowserDataDir);
    }
}
