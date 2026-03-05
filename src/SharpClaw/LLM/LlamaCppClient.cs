using System.Net;
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
    private readonly int _retryCount;
    private readonly int _retryBaseDelayMs;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<HttpStatusCode> RetriableStatusCodes =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    public LlamaCppClient(string baseUrl, int retryCount = 3, int retryBaseDelayMs = 1000)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _retryCount = retryCount;
        _retryBaseDelayMs = retryBaseDelayMs;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        request.Stream = false;
        return await WithRetryAsync(async () =>
        {
            var url = $"{_baseUrl}/v1/chat/completions";
            var json = JsonSerializer.Serialize(request, JsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
            return result ?? throw new InvalidOperationException("Empty response from llama.cpp server");
        }, ct);
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        request.Stream = true;
        var url = $"{_baseUrl}/v1/chat/completions";
        var json = JsonSerializer.Serialize(request, JsonOpts);

        HttpResponseMessage? response = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                break;
            }
            catch (HttpRequestException ex) when (attempt < _retryCount && IsRetriable(ex))
            {
                response?.Dispose();
                var delay = _retryBaseDelayMs * (1 << attempt);
                await Task.Delay(delay, ct);
            }
        }

        using var _ = response!;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolCallAccumulator = new Dictionary<int, AccumulatingToolCall>();
        string? finishReason = null;
        bool receivedDone = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") { receivedDone = true; break; }

            ChatResponse? chunk;
            try { chunk = JsonSerializer.Deserialize<ChatResponse>(data, JsonOpts); }
            catch (JsonException) { continue; }

            if (chunk?.Choices is not { Count: > 0 }) continue;
            var choice = chunk.Choices[0];
            var delta = choice.Delta;

            if (choice.FinishReason is not null)
                finishReason = choice.FinishReason;

            if (delta?.Content is { } contentElement)
            {
                var text = contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString() : null;
                if (text is not null)
                    yield return new StreamChunk { Text = text };
            }

            if (delta?.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in delta.ToolCalls)
                {
                    var idx = tc.Index ?? toolCallAccumulator.Count;
                    var name = tc.ResolvedName;
                    var args = tc.ResolvedArguments;

                    if (toolCallAccumulator.TryGetValue(idx, out var existing))
                    {
                        if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(existing.Name))
                            existing.Name = name;
                        if (!string.IsNullOrEmpty(tc.Id))
                            existing.Id = tc.Id;
                        if (!string.IsNullOrEmpty(args))
                            existing.ArgumentsBuilder.Append(args);
                    }
                    else
                    {
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

        if (!receivedDone && finishReason == null && toolCallAccumulator.Count == 0)
            yield return new StreamChunk { Text = "\n[Warning: stream ended without completion signal]\n", FinishReason = "incomplete" };

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

    private async Task<T> WithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (HttpRequestException ex) when (attempt < _retryCount && IsRetriable(ex))
            {
                var delay = _retryBaseDelayMs * (1 << attempt);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsRetriable(HttpRequestException ex)
    {
        if (ex.StatusCode.HasValue && RetriableStatusCodes.Contains(ex.StatusCode.Value))
            return true;
        if (ex.InnerException is System.IO.IOException or System.Net.Sockets.SocketException)
            return true;
        return false;
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
