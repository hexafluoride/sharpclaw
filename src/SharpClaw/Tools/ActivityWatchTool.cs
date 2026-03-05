using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpClaw.ActivityWatch;

namespace SharpClaw.Tools;

public class ActivityWatchTool : ITool
{
    private readonly ActivityWatchClient _aw;

    public ActivityWatchTool(ActivityWatchClient aw) => _aw = aw;

    public string Name => "activity_watch";

    public string Description =>
        "Query ActivityWatch for detailed user activity data (apps, windows, websites, AFK status).\n" +
        "Actions:\n" +
        "  summary   — Active window usage grouped by app+title for a time range (default: today)\n" +
        "  web       — Web browsing history grouped by url+title for a time range (default: today)\n" +
        "  afk       — Current AFK status\n" +
        "  buckets   — List all available AW data buckets\n" +
        "  events    — Raw events from a specific bucket\n" +
        "  query     — Run a custom AW query language expression";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "action" },
        properties = new
        {
            action = new
            {
                type = "string",
                description = "One of: summary, web, afk, buckets, events, query",
                @enum = new[] { "summary", "web", "afk", "buckets", "events", "query" }
            },
            hours_back = new
            {
                type = "number",
                description = "How many hours of history (default: 24). Used by summary, web."
            },
            date_from = new
            {
                type = "string",
                description = "Start date as YYYY-MM-DD or YYYY-MM-DD HH:MM. Overrides hours_back."
            },
            date_to = new
            {
                type = "string",
                description = "End date as YYYY-MM-DD or YYYY-MM-DD HH:MM (default: now)."
            },
            bucket_id = new
            {
                type = "string",
                description = "Bucket ID for 'events' action."
            },
            limit = new
            {
                type = "integer",
                description = "Max results to return (default: 50)."
            },
            aw_query = new
            {
                type = "string",
                description = "Custom AW query for 'query' action. Use AW query language."
            }
        }
    });

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var action = args.TryGetProperty("action", out var aProp) ? aProp.GetString() ?? "" : "";

        try
        {
            return action switch
            {
                "summary" => await Summary(args, ct),
                "web" => await Web(args, ct),
                "afk" => await Afk(ct),
                "buckets" => await Buckets(ct),
                "events" => await Events(args, ct),
                "query" => await Query(args, ct),
                _ => "Unknown action. Use: summary, web, afk, buckets, events, query"
            };
        }
        catch (HttpRequestException ex)
        {
            return $"ActivityWatch connection error: {ex.Message}. Is aw-server running on the configured URL?";
        }
    }

    private (DateTimeOffset start, DateTimeOffset end) ParseTimeRange(JsonElement args)
    {
        DateTimeOffset start, end;

        if (args.TryGetProperty("date_from", out var dfProp) && dfProp.ValueKind == JsonValueKind.String
            && TryParseDate(dfProp.GetString()!, out start))
        {
            end = DateTimeOffset.Now;
            if (args.TryGetProperty("date_to", out var dtProp) && dtProp.ValueKind == JsonValueKind.String
                && TryParseDate(dtProp.GetString()!, out var parsedEnd))
                end = parsedEnd;
        }
        else
        {
            var hoursBack = 24.0;
            if (args.TryGetProperty("hours_back", out var hbProp) && hbProp.ValueKind == JsonValueKind.Number)
                hoursBack = hbProp.GetDouble();
            end = DateTimeOffset.Now;
            start = end.AddHours(-hoursBack);
        }
        return (start, end);
    }

    private async Task<string> Summary(JsonElement args, CancellationToken ct)
    {
        var (start, end) = ParseTimeRange(args);
        var limit = GetLimit(args, 50);
        var events = await _aw.GetActiveWindowSummaryAsync(start, end, ct);

        if (events.Count == 0)
            return $"No active window data from {start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}. " +
                   "The aw-watcher-window may not be running.";

        var sb = new StringBuilder();
        sb.AppendLine($"Active window usage ({start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}):");
        sb.AppendLine();

        var totalSec = events.Sum(e => e.Duration);
        foreach (var ev in events.Take(limit))
        {
            var app = ev.Data.TryGetProperty("app", out var a) ? a.GetString() : "?";
            var title = ev.Data.TryGetProperty("title", out var t) ? t.GetString() : "";
            if (title is { Length: > 80 }) title = title[..77] + "...";
            var dur = FormatDuration(ev.Duration);
            var pct = totalSec > 0 ? ev.Duration / totalSec * 100 : 0;
            sb.AppendLine($"  {dur,8}  {pct,4:0}%  {app} — {title}");
        }

        sb.AppendLine();
        sb.AppendLine($"Total active: {FormatDuration(totalSec)}  ({events.Count} entries, showing top {Math.Min(limit, events.Count)})");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> Web(JsonElement args, CancellationToken ct)
    {
        var (start, end) = ParseTimeRange(args);
        var limit = GetLimit(args, 50);
        var events = await _aw.GetWebSummaryAsync(start, end, ct);

        if (events.Count == 0)
            return $"No web browsing data from {start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}. " +
                   "The aw-watcher-web browser extension may not be running.";

        var sb = new StringBuilder();
        sb.AppendLine($"Web browsing ({start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}):");
        sb.AppendLine();

        var totalSec = events.Sum(e => e.Duration);
        foreach (var ev in events.Take(limit))
        {
            var title = ev.Data.TryGetProperty("title", out var t) ? t.GetString() : "?";
            var url = ev.Data.TryGetProperty("url", out var u) ? u.GetString() : "";
            if (title is { Length: > 60 }) title = title[..57] + "...";
            if (url is { Length: > 60 }) url = url[..57] + "...";
            var dur = FormatDuration(ev.Duration);
            sb.AppendLine($"  {dur,8}  {title}");
            sb.AppendLine($"           {url}");
        }

        sb.AppendLine();
        sb.AppendLine($"Total browsing: {FormatDuration(totalSec)}  ({events.Count} entries, showing top {Math.Min(limit, events.Count)})");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> Afk(CancellationToken ct)
    {
        var status = await _aw.GetAfkStatusAsync(ct);
        if (status is null)
            return "No AFK data available. The aw-watcher-afk may not be running.";

        var (isAfk, duration) = status.Value;
        var state = isAfk ? "AFK (away)" : "Active (not AFK)";
        return $"Current status: {state} for {FormatDuration(duration)}";
    }

    private async Task<string> Buckets(CancellationToken ct)
    {
        var buckets = await _aw.GetBucketsAsync(ct);
        if (buckets.Count == 0)
            return "No ActivityWatch buckets found.";

        var sb = new StringBuilder();
        sb.AppendLine($"ActivityWatch buckets ({buckets.Count}):");
        sb.AppendLine();
        foreach (var (id, b) in buckets.OrderBy(kv => kv.Key))
        {
            var updated = b.LastUpdated?.ToString("yyyy-MM-dd HH:mm") ?? "never";
            sb.AppendLine($"  {id}");
            sb.AppendLine($"    type: {b.Type}  client: {b.Client}  last_updated: {updated}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> Events(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("bucket_id", out var bProp) || bProp.ValueKind != JsonValueKind.String)
            return "Error: 'bucket_id' is required for the events action. Use action=buckets to list available buckets.";

        var bucketId = bProp.GetString()!;
        var limit = GetLimit(args, 20);
        var (start, end) = ParseTimeRange(args);

        var events = await _aw.GetEventsAsync(bucketId, limit, start, end, ct);
        if (events.Count == 0)
            return $"No events in bucket '{bucketId}' for the requested time range.";

        var sb = new StringBuilder();
        sb.AppendLine($"Events from '{bucketId}' ({events.Count} results):");
        sb.AppendLine();
        foreach (var ev in events)
        {
            var dur = FormatDuration(ev.Duration);
            var data = ev.Data.ValueKind == JsonValueKind.Object
                ? string.Join(", ", ev.Data.EnumerateObject()
                    .Select(p => $"{p.Name}={Truncate(p.Value.ToString(), 50)}"))
                : ev.Data.ToString();
            sb.AppendLine($"  [{ev.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {dur,8}  {data}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> Query(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("aw_query", out var qProp) || qProp.ValueKind != JsonValueKind.String)
            return "Error: 'aw_query' is required for the query action. Write an AW query expression.\n" +
                   "Example: events = query_bucket(\"aw-watcher-web-firefox\");\nRETURN = sort_by_duration(events);";

        var query = qProp.GetString()!;
        var (start, end) = ParseTimeRange(args);
        var limit = GetLimit(args, 50);
        var period = $"{start:o}/{end:o}";

        var results = await _aw.QueryAsync(query, [period], ct);
        if (results.Count == 0 || results[0].Count == 0)
            return "Query returned no results.";

        var events = results[0];
        var sb = new StringBuilder();
        sb.AppendLine($"Query results ({events.Count} events, showing top {Math.Min(limit, events.Count)}):");
        sb.AppendLine();
        foreach (var ev in events.Take(limit))
        {
            var dur = FormatDuration(ev.Duration);
            var data = ev.Data.ValueKind == JsonValueKind.Object
                ? string.Join(", ", ev.Data.EnumerateObject()
                    .Select(p => $"{p.Name}={Truncate(p.Value.ToString(), 50)}"))
                : ev.Data.ToString();
            sb.AppendLine($"  {dur,8}  {data}");
        }
        return sb.ToString().TrimEnd();
    }

    private static int GetLimit(JsonElement args, int defaultLimit)
    {
        if (args.TryGetProperty("limit", out var lProp) && lProp.ValueKind == JsonValueKind.Number)
            return Math.Clamp(lProp.GetInt32(), 1, 500);
        return defaultLimit;
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:0}s";
        if (seconds < 3600) return $"{seconds / 60:0.0}m";
        return $"{seconds / 3600:0.0}h";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";

    private static bool TryParseDate(string input, out DateTimeOffset result)
    {
        string[] formats = [
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd",
        ];
        return DateTimeOffset.TryParseExact(input.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out result);
    }
}
