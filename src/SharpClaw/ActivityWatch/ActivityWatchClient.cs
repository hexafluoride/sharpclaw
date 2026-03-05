using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SharpClaw.Configuration;
using SharpClaw.Logging;

namespace SharpClaw.ActivityWatch;

public record AwBucket
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("client")] public string Client { get; init; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; init; } = "";
    [JsonPropertyName("created")] public DateTimeOffset? Created { get; init; }
    [JsonPropertyName("last_updated")] public DateTimeOffset? LastUpdated { get; init; }
}

public record AwEvent
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("duration")] public double Duration { get; init; }
    [JsonPropertyName("data")] public JsonElement Data { get; init; }
}

/// <summary>
/// Thin HTTP client for the ActivityWatch REST API (v0).
/// </summary>
public class ActivityWatchClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _log = Log.ForName("ActivityWatch");
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ActivityWatchClient(ActivityWatchConfig config)
    {
        _baseUrl = config.Url.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/0/info", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<Dictionary<string, AwBucket>> GetBucketsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/api/0/buckets/", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Dictionary<string, AwBucket>>(JsonOpts, ct)
               ?? new Dictionary<string, AwBucket>();
    }

    public async Task<List<AwEvent>> GetEventsAsync(
        string bucketId, int limit = 100,
        DateTimeOffset? start = null, DateTimeOffset? end = null,
        CancellationToken ct = default)
    {
        var url = $"/api/0/buckets/{Uri.EscapeDataString(bucketId)}/events?limit={limit}";
        if (start.HasValue) url += $"&start={start.Value:o}";
        if (end.HasValue) url += $"&end={end.Value:o}";

        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<AwEvent>>(JsonOpts, ct)
               ?? new List<AwEvent>();
    }

    /// <summary>
    /// Run an AW query over one or more time periods.
    /// Returns one result array per time period.
    /// </summary>
    public async Task<List<List<AwEvent>>> QueryAsync(
        string query, List<string> timePeriods, CancellationToken ct = default)
    {
        var body = new { timeperiods = timePeriods, query = new[] { query } };
        var resp = await _http.PostAsJsonAsync("/api/0/query/", body, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<List<AwEvent>>>(JsonOpts, ct)
               ?? new List<List<AwEvent>>();
    }

    /// <summary>
    /// Find the first bucket whose ID contains the given pattern.
    /// </summary>
    public async Task<string?> FindBucketAsync(string pattern, CancellationToken ct = default)
    {
        var buckets = await GetBucketsAsync(ct);
        return buckets.Keys.FirstOrDefault(k =>
            k.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// High-level: get a summary of active (not-afk) window usage for a time range,
    /// grouped by app, sorted by duration descending.
    /// </summary>
    public async Task<List<AwEvent>> GetActiveWindowSummaryAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        var windowBucket = await FindBucketAsync("aw-watcher-window", ct);
        var afkBucket = await FindBucketAsync("aw-watcher-afk", ct);

        if (windowBucket is null)
        {
            _log.LogWarning("No aw-watcher-window bucket found");
            return new List<AwEvent>();
        }

        var query = $"""
            events = query_bucket("{windowBucket}");
            {(afkBucket is not null
                ? $"""
                    afk_events = query_bucket("{afkBucket}");
                    not_afk = filter_keyvals(afk_events, "status", ["not-afk"]);
                    events = filter_period_intersect(events, not_afk);
                    """
                : "")}
            events = merge_events_by_keys(events, ["app", "title"]);
            RETURN = sort_by_duration(events);
            """;

        var period = $"{start:o}/{end:o}";
        try
        {
            var results = await QueryAsync(query, [period], ct);
            return results.Count > 0 ? results[0] : new List<AwEvent>();
        }
        catch (Exception ex)
        {
            _log.LogWarning("AW query failed: {Err}", ex.Message);
            return new List<AwEvent>();
        }
    }

    /// <summary>
    /// High-level: get recent web browsing activity sorted by duration.
    /// </summary>
    public async Task<List<AwEvent>> GetWebSummaryAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        var webBucket = await FindBucketAsync("aw-watcher-web", ct);
        if (webBucket is null)
        {
            _log.LogWarning("No aw-watcher-web bucket found");
            return new List<AwEvent>();
        }

        var query = $"""
            events = query_bucket("{webBucket}");
            events = merge_events_by_keys(events, ["url", "title"]);
            RETURN = sort_by_duration(events);
            """;

        var period = $"{start:o}/{end:o}";
        try
        {
            var results = await QueryAsync(query, [period], ct);
            return results.Count > 0 ? results[0] : new List<AwEvent>();
        }
        catch (Exception ex)
        {
            _log.LogWarning("AW web query failed: {Err}", ex.Message);
            return new List<AwEvent>();
        }
    }

    /// <summary>
    /// High-level: get the user's current AFK status.
    /// </summary>
    public async Task<(bool isAfk, double durationSeconds)?> GetAfkStatusAsync(CancellationToken ct = default)
    {
        var afkBucket = await FindBucketAsync("aw-watcher-afk", ct);
        if (afkBucket is null) return null;

        var events = await GetEventsAsync(afkBucket, limit: 1, ct: ct);
        if (events.Count == 0) return null;

        var ev = events[0];
        var status = ev.Data.TryGetProperty("status", out var s) ? s.GetString() : null;
        return (status == "afk", ev.Duration);
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
