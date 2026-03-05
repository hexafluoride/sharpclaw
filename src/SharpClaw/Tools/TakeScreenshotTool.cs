using System.Text.Json;
using SharpClaw.Screen;

namespace SharpClaw.Tools;

/// <summary>
/// Takes a fresh screenshot right now, sends it to the vision model for analysis,
/// and returns the description. Unlike query_screen_activity which returns past
/// observations, this captures what's on screen at the moment the tool is called.
/// </summary>
public class TakeScreenshotTool : ITool
{
    private readonly ScreenCaptureService _captureService;
    private readonly ScreenAnalyzer _analyzer;

    public TakeScreenshotTool(ScreenCaptureService captureService, ScreenAnalyzer analyzer)
    {
        _captureService = captureService;
        _analyzer = analyzer;
    }

    public string Name => "take_screenshot";
    public string Description => "Take a screenshot of the user's screen right now and describe what is visible. Use this when the user asks what's on their screen, what they're doing, or when you need to see the current screen state.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            question = new { type = "string", description = "Optional specific question about what's on screen (e.g. 'what browser tabs are open?')" }
        }
    });

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        string? question = null;
        if (arguments.TryGetProperty("question", out var qProp) && qProp.ValueKind == JsonValueKind.String)
            question = qProp.GetString();

        var (imageData, hash) = await _captureService.CaptureOnceAsync(ct);
        if (imageData is null)
            return "Error: Failed to capture screenshot. Make sure grim (Wayland) or scrot (X11) is installed.";

        var description = await _analyzer.AnalyzeImageAndStore(imageData, hash);

        if (string.IsNullOrWhiteSpace(description))
            return "Screenshot was captured but the vision model returned no description. The vision model may not be running or may not support image input.";

        if (question is not null)
            return $"Screenshot taken at {DateTimeOffset.Now:HH:mm:ss}.\nUser question: {question}\nScreen description: {description}";

        return $"Screenshot taken at {DateTimeOffset.Now:HH:mm:ss}.\nScreen description: {description}";
    }
}
