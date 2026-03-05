using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpClaw.Agents;

public enum AgentKind { LongRunning, Task }

public enum AgentStatus { Running, Paused, Completed, Failed, Cancelled }

/// <summary>
/// Persisted configuration and state for a sub-agent.
/// Long-running agents have a schedule and survive restarts.
/// Task agents run once to completion.
/// </summary>
public class SubAgentConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentKind Kind { get; set; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = "";

    [JsonPropertyName("schedule")]
    public string? Schedule { get; set; }

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }

    [JsonPropertyName("maxIterations")]
    public int MaxIterations { get; set; } = 15;

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentStatus Status { get; set; } = AgentStatus.Running;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; set; }

    [JsonPropertyName("runCount")]
    public int RunCount { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

/// <summary>
/// Parsed representation of a schedule string.
/// Supports: "every 5m", "every 2h", "daily 09:00", "once"
/// </summary>
public class ParsedSchedule
{
    public TimeSpan? Interval { get; init; }
    public TimeOnly? DailyAt { get; init; }
    public bool RunOnce { get; init; }

    public static ParsedSchedule Parse(string schedule)
    {
        var s = schedule.Trim().ToLowerInvariant();

        if (s == "once")
            return new ParsedSchedule { RunOnce = true };

        var intervalMatch = Regex.Match(s, @"^every\s+(\d+)\s*(m|min|minutes?|h|hr|hours?)$");
        if (intervalMatch.Success)
        {
            var amount = int.Parse(intervalMatch.Groups[1].Value);
            var unit = intervalMatch.Groups[2].Value;
            var span = unit.StartsWith('h')
                ? TimeSpan.FromHours(amount)
                : TimeSpan.FromMinutes(amount);
            return new ParsedSchedule { Interval = span };
        }

        var dailyMatch = Regex.Match(s, @"^daily\s+(\d{1,2}):(\d{2})$");
        if (dailyMatch.Success)
        {
            var hour = int.Parse(dailyMatch.Groups[1].Value);
            var minute = int.Parse(dailyMatch.Groups[2].Value);
            return new ParsedSchedule { DailyAt = new TimeOnly(hour, minute) };
        }

        throw new FormatException(
            $"Unrecognized schedule: '{schedule}'. " +
            "Use: 'every 5m', 'every 2h', 'daily 09:00', or 'once'");
    }

    public TimeSpan GetNextDelay()
    {
        if (RunOnce)
            return TimeSpan.Zero;

        if (Interval.HasValue)
            return Interval.Value;

        if (DailyAt.HasValue)
        {
            var now = DateTime.Now;
            var target = now.Date.Add(DailyAt.Value.ToTimeSpan());
            if (target <= now)
                target = target.AddDays(1);
            return target - now;
        }

        return TimeSpan.FromHours(1);
    }
}

/// <summary>
/// Result from a single sub-agent run.
/// </summary>
public class AgentRunResult
{
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public bool Success { get; init; }
    public string Response { get; init; } = "";
    public string? Error { get; init; }
    public int IterationsUsed { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.Now;
}
