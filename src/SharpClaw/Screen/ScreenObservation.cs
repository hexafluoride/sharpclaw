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
}
