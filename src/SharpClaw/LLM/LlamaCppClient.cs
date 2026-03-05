using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SharpClaw.LLM.Models;

namespace SharpClaw.LLM;

public class LlamaCppClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public LlamaCppClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>
    /// Non-streaming completion. Returns the full response at once.
    /// </summary>
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        request.Stream = false;
        var url = $"{_baseUrl}/v1/chat/completions";
        var json = JsonSerializer.Serialize(request, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
        return result ?? throw new InvalidOperationException("Empty response from llama.cpp server");
    }

    /// <summary>
    /// Streaming completion. Yields partial chunks as SSE events arrive.
    /// Accumulates tool calls across chunks and yields a final synthetic chunk
    /// with the complete tool calls when finish_reason is "tool_calls".
    /// </summary>
    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        request.Stream = true;
        var url = $"{_baseUrl}/v1/chat/completions";
        var json = JsonSerializer.Serialize(request, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolCallAccumulator = new Dictionary<int, AccumulatingToolCall>();
        string? finishReason = null;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            ChatResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatResponse>(data, JsonOpts);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk?.Choices is not { Count: > 0 }) continue;
            var choice = chunk.Choices[0];
            var delta = choice.Delta;

            if (choice.FinishReason is not null)
                finishReason = choice.FinishReason;

            if (delta?.Content is { } contentElement)
            {
                var text = contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString()
                    : null;

                if (text is not null)
                {
                    yield return new StreamChunk { Text = text };
                }
            }

            if (delta?.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in delta.ToolCalls)
                {
                    // Use the explicit index from the delta if present,
                    // otherwise infer from position
                    var idx = tc.Index ?? toolCallAccumulator.Count;
                    var name = tc.ResolvedName;
                    var args = tc.ResolvedArguments;

                    if (toolCallAccumulator.TryGetValue(idx, out var existing))
                    {
                        // Append to existing tool call accumulator
                        if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(existing.Name))
                            existing.Name = name;
                        if (!string.IsNullOrEmpty(tc.Id))
                            existing.Id = tc.Id;
                        if (!string.IsNullOrEmpty(args))
                            existing.ArgumentsBuilder.Append(args);
                    }
                    else
                    {
                        // New tool call
                        toolCallAccumulator[idx] = new AccumulatingToolCall
                        {
                            Id = tc.Id ?? $"call_{idx}",
                            Name = name,
                            ArgumentsBuilder = new StringBuilder(args)
                        };
                    }
                }
            }
        }

        if (toolCallAccumulator.Count > 0 &&
            (finishReason == "tool_calls" || finishReason == "stop" || finishReason is null))
        {
            var toolCalls = toolCallAccumulator
                .OrderBy(kv => kv.Key)
                .Select(kv => new ToolCall
                {
                    Id = kv.Value.Id,
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = kv.Value.Name,
                        Arguments = kv.Value.ArgumentsBuilder.ToString()
                    }
                }).ToList();

            yield return new StreamChunk { ToolCalls = toolCalls, FinishReason = "tool_calls" };
        }
        else if (finishReason is not null)
        {
            yield return new StreamChunk { FinishReason = finishReason };
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    private class AccumulatingToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder ArgumentsBuilder { get; set; } = new();
    }
}

public class StreamChunk
{
    public string? Text { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? FinishReason { get; init; }
    public bool IsToolCall => ToolCalls is { Count: > 0 };
    public bool IsText => Text is not null;
}
