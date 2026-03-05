using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.LLM.Models;

public class Message
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    public static Message System(string content) => new()
    {
        Role = "system",
        Content = JsonSerializer.SerializeToElement(content)
    };

    public static Message User(string content) => new()
    {
        Role = "user",
        Content = JsonSerializer.SerializeToElement(content)
    };

    public static Message UserWithImage(string text, string base64Image, string mimeType = "image/png") => new()
    {
        Role = "user",
        Content = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text },
            new { type = "image_url", image_url = new { url = $"data:{mimeType};base64,{base64Image}" } }
        })
    };

    public static Message Assistant(string content) => new()
    {
        Role = "assistant",
        Content = JsonSerializer.SerializeToElement(content)
    };

    public static Message AssistantWithToolCalls(List<ToolCall> toolCalls) => new()
    {
        Role = "assistant",
        ToolCalls = toolCalls
    };

    public static Message Tool(string toolCallId, string name, string content) => new()
    {
        Role = "tool",
        ToolCallId = toolCallId,
        Name = name,
        Content = JsonSerializer.SerializeToElement(content)
    };

    public string? GetTextContent()
    {
        if (Content is null) return null;
        if (Content.Value.ValueKind == JsonValueKind.String)
            return Content.Value.GetString();
        if (Content.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in Content.Value.EnumerateArray())
            {
                if (element.TryGetProperty("type", out var typeProp) &&
                    typeProp.GetString() == "text" &&
                    element.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString();
                }
            }
        }
        return null;
    }
}
