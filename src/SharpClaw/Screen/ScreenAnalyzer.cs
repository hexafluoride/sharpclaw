using System.Text.Json;
using SharpClaw.Configuration;
using SharpClaw.LLM;
using SharpClaw.LLM.Models;

namespace SharpClaw.Screen;

/// <summary>
/// Analyzes screenshots by sending them to a vision-capable LLM.
/// Maintains a ring buffer of observations with deduplication via image hashing.
/// Observations are persisted to a JSONL file on disk and reloaded on startup.
/// </summary>
public class ScreenAnalyzer : IDisposable
{
    private readonly SharpClawConfig _config;
    private readonly LlamaCppClient _visionClient;
    private readonly LinkedList<ScreenObservation> _observations = new();
    private readonly object _lock = new();
    private readonly string _historyPath;
    private string? _lastImageHash;
    private int _writesSinceCompactionCheck;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _archiveDir;

    public ScreenAnalyzer(SharpClawConfig config)
    {
        _config = config;
        _visionClient = new LlamaCppClient(config.VisionEndpoint);
        _historyPath = Path.Combine(config.ResolvedConfigDir, "screen-history.jsonl");
        _archiveDir = config.ResolvedArchiveDir;
        Directory.CreateDirectory(_archiveDir);
        LoadHistory();
    }

    /// <summary>
    /// Called by ScreenCaptureService when a new screenshot is captured.
    /// Skips analysis if the image hash matches the previous capture (dedup).
    /// </summary>
    public async Task OnScreenCaptured(byte[] imageData, string imageHash)
    {
        await OnScreenCaptured(imageData, imageHash, null, null, null, null);
    }

    public async Task OnScreenCaptured(
        byte[] imageData, string imageHash,
        string? activeApp, string? activeTitle, string? activeUrl, bool? isAfk)
    {
        if (imageHash == _lastImageHash)
            return;
        _lastImageHash = imageHash;

        try
        {
            var description = await AnalyzeImage(imageData);
            if (string.IsNullOrWhiteSpace(description)) return;

            AddObservation(new ScreenObservation
            {
                Timestamp = DateTimeOffset.Now,
                Description = description.Trim(),
                ImageHash = imageHash,
                ActiveApp = activeApp,
                ActiveTitle = activeTitle,
                ActiveUrl = activeUrl,
                IsAfk = isAfk
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screen-analyzer] Analysis error: {ex.Message}");
        }
    }

    /// <summary>
    /// Analyze a screenshot image and return a text description.
    /// Also stores the result in the observation ring buffer and persists to disk.
    /// </summary>
    public async Task<string> AnalyzeImageAndStore(byte[] imageData, string imageHash)
    {
        var description = await AnalyzeImage(imageData);
        if (!string.IsNullOrWhiteSpace(description))
        {
            AddObservation(new ScreenObservation
            {
                Timestamp = DateTimeOffset.Now,
                Description = description.Trim(),
                ImageHash = imageHash
            });
        }
        return description;
    }

    public List<ScreenObservation> GetObservations(int minutesBack = 60)
    {
        var cutoff = DateTimeOffset.Now.AddMinutes(-minutesBack);
        lock (_lock)
        {
            return _observations
                .Where(o => o.Timestamp >= cutoff)
                .ToList();
        }
    }

    public ScreenObservation? GetLatest()
    {
        lock (_lock) { return _observations.Last?.Value; }
    }

    public int ObservationCount
    {
        get { lock (_lock) { return _observations.Count; } }
    }

    private void AddObservation(ScreenObservation observation)
    {
        lock (_lock)
        {
            _observations.AddLast(observation);
            while (_observations.Count > _config.Screen.MaxObservations)
                _observations.RemoveFirst();
        }
        PersistObservation(observation);
    }

    private void PersistObservation(ScreenObservation observation)
    {
        try
        {
            var line = JsonSerializer.Serialize(observation, JsonOpts);
            File.AppendAllText(_historyPath, line + "\n");
            _writesSinceCompactionCheck++;
            // Periodically compact during runtime (not just on startup)
            if (_writesSinceCompactionCheck >= 50)
            {
                _writesSinceCompactionCheck = 0;
                MaybeCompactHistory();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screen-analyzer] Failed to persist observation: {ex.Message}");
        }
    }

    private void LoadHistory()
    {
        if (!File.Exists(_historyPath)) return;

        try
        {
            var lines = File.ReadAllLines(_historyPath);
            var loaded = new List<ScreenObservation>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var obs = JsonSerializer.Deserialize<ScreenObservation>(line, JsonOpts);
                    if (obs is not null)
                        loaded.Add(obs);
                }
                catch (JsonException) { }
            }

            // Only keep the most recent maxObservations entries
            var recent = loaded
                .OrderBy(o => o.Timestamp)
                .TakeLast(_config.Screen.MaxObservations)
                .ToList();

            lock (_lock)
            {
                _observations.Clear();
                foreach (var obs in recent)
                    _observations.AddLast(obs);
            }

            if (_observations.Count > 0)
            {
                _lastImageHash = _observations.Last?.Value.ImageHash;
                Console.Error.WriteLine($"[screen-analyzer] Loaded {_observations.Count} observations from history");
            }

            // Compact the file if it grew much larger than maxObservations
            if (lines.Length > _config.Screen.MaxObservations * 2)
                CompactHistory();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screen-analyzer] Failed to load history: {ex.Message}");
        }
    }

    private void CompactHistory()
    {
        try
        {
            // Archive the full uncompacted file before truncating
            ArchiveFile(_historyPath, "screen-history");

            List<ScreenObservation> current;
            lock (_lock)
            {
                current = _observations.ToList();
            }
            var lines = current.Select(o => JsonSerializer.Serialize(o, JsonOpts));
            File.WriteAllText(_historyPath, string.Join("\n", lines) + "\n");
            Console.Error.WriteLine($"[screen-analyzer] Compacted history (archived old copy)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screen-analyzer] Failed to compact history: {ex.Message}");
        }
    }

    private void MaybeCompactHistory()
    {
        try
        {
            if (!File.Exists(_historyPath))
                return;
            var lineCount = File.ReadLines(_historyPath).Count();
            if (lineCount > _config.Screen.MaxObservations * 2)
                CompactHistory();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screen-analyzer] Failed during periodic compaction check: {ex.Message}");
        }
    }

    private void ArchiveFile(string sourcePath, string prefix)
    {
        if (!File.Exists(sourcePath)) return;
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var ext = Path.GetExtension(sourcePath);
        var archivePath = Path.Combine(_archiveDir, $"{prefix}-{timestamp}{ext}");
        var suffix = 0;
        while (File.Exists(archivePath))
        {
            suffix++;
            archivePath = Path.Combine(_archiveDir, $"{prefix}-{timestamp}-{suffix}{ext}");
        }
        File.Copy(sourcePath, archivePath, overwrite: false);
    }

    /// <summary>
    /// Search archived screen history files for observations in a date range.
    /// This allows backwards-looking queries beyond the in-memory ring buffer.
    /// </summary>
    public List<ScreenObservation> SearchArchive(DateTimeOffset from, DateTimeOffset to, string? keyword = null)
    {
        var results = new List<ScreenObservation>();

        // Search the current live file first
        results.AddRange(SearchJsonlFile(_historyPath, from, to, keyword));

        // Then search all archive files
        if (Directory.Exists(_archiveDir))
        {
            foreach (var file in Directory.EnumerateFiles(_archiveDir, "screen-history-*.jsonl"))
            {
                results.AddRange(SearchJsonlFile(file, from, to, keyword));
            }
        }

        // Deduplicate by timestamp + hash and sort
        return results
            .DistinctBy(o => (o.Timestamp, o.ImageHash))
            .OrderBy(o => o.Timestamp)
            .ToList();
    }

    private static List<ScreenObservation> SearchJsonlFile(
        string path, DateTimeOffset from, DateTimeOffset to, string? keyword)
    {
        var results = new List<ScreenObservation>();
        if (!File.Exists(path)) return results;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var obs = JsonSerializer.Deserialize<ScreenObservation>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (obs is null) continue;
                    if (obs.Timestamp < from || obs.Timestamp > to) continue;
                    if (keyword is not null &&
                        !obs.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        continue;
                    results.Add(obs);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception) { }

        return results;
    }

    private async Task<string> AnalyzeImage(byte[] imageData)
    {
        var base64 = Convert.ToBase64String(imageData);

        var request = new ChatRequest
        {
            Model = _config.VisionModel,
            Messages =
            [
                Message.System("You are a screen activity observer. Describe what the user is doing on their screen concisely. Note visible applications, documents, websites, or tasks. Keep your description to 2-3 sentences."),
                Message.UserWithImage(
                    "Describe what the user is currently doing on their screen.",
                    base64)
            ],
            Stream = false
        };

        var response = await _visionClient.CompleteAsync(request);

        if (response.Choices is { Count: > 0 })
        {
            var message = response.Choices[0].Message;
            return message?.GetTextContent() ?? "";
        }

        return "";
    }

    public void Dispose()
    {
        _visionClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
