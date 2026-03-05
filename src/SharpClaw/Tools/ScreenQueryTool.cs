using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpClaw.Screen;

namespace SharpClaw.Tools;

public class ScreenQueryTool : ITool
{
    private readonly ScreenAnalyzer _analyzer;

    public ScreenQueryTool(ScreenAnalyzer analyzer) => _analyzer = analyzer;

    public string Name => "query_screen_activity";
    public string Description =>
        "Query the user's screen activity observations. Two modes:\n" +
        "  Recent: set minutes_back (default: 10) to search the last N minutes.\n" +
        "  Historical: set date_from (and optionally date_to) to search the full archive.\n" +
        "If date_from is set, minutes_back is IGNORED. Use 'query' to filter by keywords.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Optional keyword filter (space-separated, any match)" },
            minutes_back = new { type = "integer", description = "How many minutes of recent history to search (default: 10). IGNORED when date_from is set." },
            date_from = new { type = "string", description = "Historical mode: start date as YYYY-MM-DD or YYYY-MM-DD HH:MM. When set, searches full archive instead of recent buffer." },
            date_to = new { type = "string", description = "Historical mode: end date as YYYY-MM-DD or YYYY-MM-DD HH:MM (default: now)" }
        }
    });

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        string? query = null;
        if (arguments.TryGetProperty("query", out var qProp) && qProp.ValueKind == JsonValueKind.String)
            query = qProp.GetString();

        // Check if this is a historical (archive) query or a recent (ring buffer) query
        string? dateFromStr = null;
        if (arguments.TryGetProperty("date_from", out var dfProp) && dfProp.ValueKind == JsonValueKind.String)
            dateFromStr = dfProp.GetString();

        if (dateFromStr is not null)
            return Task.FromResult(SearchArchive(dateFromStr, arguments, query));

        return Task.FromResult(SearchRecent(arguments, query));
    }

    private string SearchRecent(JsonElement arguments, string? query)
    {
        var minutesBack = 10;
        if (arguments.TryGetProperty("minutes_back", out var mbProp) && mbProp.ValueKind == JsonValueKind.Number)
            minutesBack = mbProp.GetInt32();

        var observations = _analyzer.GetObservations(minutesBack);

        if (observations.Count == 0)
            return "No screen observations available for the requested time period. Screen monitoring may not be active.";

        if (query is not null)
            observations = FilterByKeywords(observations, query);

        if (observations.Count == 0)
            return $"No screen observations matching '{query}' in the last {minutesBack} minutes.";

        var sb = new StringBuilder();
        sb.AppendLine($"Screen activity (last {minutesBack} minutes, {observations.Count} observations):");
        sb.AppendLine();
        foreach (var obs in observations)
        {
            sb.Append($"[{obs.Timestamp:yyyy-MM-dd HH:mm:ss}] {obs.Description}");
            AppendAwContext(sb, obs);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string SearchArchive(string dateFromStr, JsonElement arguments, string? query)
    {
        if (!TryParseDate(dateFromStr, out var from))
            return $"Could not parse date_from: '{dateFromStr}'. Use YYYY-MM-DD or YYYY-MM-DD HH:MM.";

        var to = DateTimeOffset.Now;
        if (arguments.TryGetProperty("date_to", out var dtProp) && dtProp.ValueKind == JsonValueKind.String)
        {
            var dateToStr = dtProp.GetString();
            if (dateToStr is not null && !TryParseDate(dateToStr, out to))
                return $"Could not parse date_to: '{dateToStr}'. Use YYYY-MM-DD or YYYY-MM-DD HH:MM.";
        }

        // If date_to is just a date with no time, extend to end of day
        if (to.TimeOfDay == TimeSpan.Zero && to.Date == to.DateTime)
            to = to.AddDays(1).AddSeconds(-1);

        var observations = _analyzer.SearchArchive(from, to, query);

        if (observations.Count == 0)
        {
            var rangeDesc = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
            var extra = query is not null ? $" matching '{query}'" : "";
            return $"No screen observations found{extra} from {rangeDesc}.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Screen activity from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} ({observations.Count} observations):");
        sb.AppendLine();

        foreach (var dayGroup in observations.GroupBy(o => o.Timestamp.Date))
        {
            sb.AppendLine($"  {dayGroup.Key:yyyy-MM-dd} ({dayGroup.Count()} observations):");
            foreach (var obs in dayGroup)
            {
                sb.Append($"    [{obs.Timestamp:HH:mm:ss}] {obs.Description}");
                AppendAwContext(sb, obs);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static List<ScreenObservation> FilterByKeywords(List<ScreenObservation> observations, string query)
    {
        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return observations
            .Where(o => keywords.Any(k => o.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static void AppendAwContext(StringBuilder sb, ScreenObservation obs)
    {
        var parts = new List<string>();
        if (obs.ActiveApp is not null) parts.Add($"app={obs.ActiveApp}");
        if (obs.ActiveUrl is not null) parts.Add($"url={obs.ActiveUrl}");
        else if (obs.ActiveTitle is not null) parts.Add($"title={obs.ActiveTitle}");
        if (obs.IsAfk == true) parts.Add("afk");
        if (parts.Count > 0) sb.Append($" [{string.Join(", ", parts)}]");
    }

    private static bool TryParseDate(string input, out DateTimeOffset result)
    {
        string[] formats = [
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd",
            "MM/dd/yyyy",
            "dd/MM/yyyy"
        ];
        return DateTimeOffset.TryParseExact(input.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out result);
    }
}
