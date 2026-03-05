using System.Text.Json.Serialization;

namespace SharpClaw.Screen;

public record ScreenObservation
{
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("hash")]
    public required string ImageHash { get; init; }

    [JsonPropertyName("activeApp")]
    public string? ActiveApp { get; init; }

    [JsonPropertyName("activeTitle")]
    public string? ActiveTitle { get; init; }

    [JsonPropertyName("activeUrl")]
    public string? ActiveUrl { get; init; }

    [JsonPropertyName("afk")]
    public bool? IsAfk { get; init; }
}
