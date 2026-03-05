using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpClaw.Configuration;
using SharpClaw.LLM;
using SharpClaw.LLM.Models;
using SharpClaw.Sessions;
using SharpClaw.Tools;

namespace SharpClaw.Agent;

public class AgentRuntime
{
    private readonly SharpClawConfig _config;
    private readonly LlamaCppClient _llmClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly SessionManager _sessionManager;
    private string _systemPrompt;
    private int _estimatedTokens;

    public AgentRuntime(
        SharpClawConfig config,
        LlamaCppClient llmClient,
        ToolRegistry toolRegistry,
        SessionManager sessionManager,
        string systemPrompt)
    {
        _config = config;
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _sessionManager = sessionManager;
        _systemPrompt = systemPrompt;

        _sessionManager.SetSystemPrompt(systemPrompt);
    }

    public async Task<string> ProcessAsync(
        string userMessage,
        Action<string>? onToken = null,
        CancellationToken ct = default)
    {
        var userMsg = Message.User(userMessage);
        _sessionManager.AppendMessage(userMsg);
        _estimatedTokens += EstimateTokens(userMessage);

        TrimContextIfNeeded(onToken);

        var finalText = new StringBuilder();
        var iteration = 0;

        while (iteration < _config.MaxIterations)
        {
            iteration++;
            var toolDefs = _toolRegistry.GetDefinitions();

            var request = new ChatRequest
            {
                Model = _config.Model,
                Messages = _sessionManager.Messages.ToList(),
                Tools = toolDefs.Count > 0 ? toolDefs : null,
                MaxTokens = _config.MaxCompletionTokens,
                Stream = true
            };

            var responseText = new StringBuilder();
            List<ToolCall>? toolCalls = null;

            await foreach (var chunk in _llmClient.StreamAsync(request, ct))
            {
                if (chunk.IsText)
                {
                    responseText.Append(chunk.Text);
                    onToken?.Invoke(chunk.Text!);
                }
                if (chunk.IsToolCall)
                    toolCalls = chunk.ToolCalls;
            }

            _estimatedTokens += EstimateTokens(responseText.ToString());

            if (toolCalls is { Count: > 0 })
            {
                var assistantMsg = Message.AssistantWithToolCalls(toolCalls);
                if (responseText.Length > 0)
                    assistantMsg.Content = JsonSerializer.SerializeToElement(responseText.ToString());
                _sessionManager.AppendMessage(assistantMsg);

                foreach (var toolCall in toolCalls)
                {
                    var toolName = toolCall.ResolvedName;
                    var toolArgs = toolCall.ResolvedArguments;
                    var toolId = toolCall.Id ?? $"call_{Guid.NewGuid().ToString("N")[..8]}";

                    onToken?.Invoke($"\n[Tool: {toolName}({TruncateArgs(toolArgs)})]\n");

                    var toolSw = Stopwatch.StartNew();
                    var result = await _toolRegistry.ExecuteAsync(toolName, toolArgs, ct);
                    toolSw.Stop();

                    onToken?.Invoke($"[Result: {toolSw.Elapsed.TotalSeconds:F1}s | {TruncateForDisplay(result)}]\n");

                    var toolResultMsg = Message.Tool(toolId, toolName, result);
                    _sessionManager.AppendMessage(toolResultMsg);
                    _estimatedTokens += EstimateTokens(result);
                }

                TrimContextIfNeeded(onToken);
                continue;
            }

            var text = responseText.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                _sessionManager.AppendMessage(Message.Assistant(text));
                finalText.Append(text);
            }
            break;
        }

        if (iteration >= _config.MaxIterations)
        {
            onToken?.Invoke($"\n[Iteration limit reached ({_config.MaxIterations}). Requesting summary...]\n");

            var summaryPrompt = Message.User(
                "You have reached the iteration limit and cannot make any more tool calls. " +
                "Summarize what you have accomplished so far, any partial results, " +
                "and what remains to be done.");
            _sessionManager.AppendMessage(summaryPrompt);

            var summaryRequest = new ChatRequest
            {
                Model = _config.Model,
                Messages = _sessionManager.Messages.ToList(),
                Tools = null,
                Stream = true
            };

            var summaryText = new StringBuilder();
            await foreach (var chunk in _llmClient.StreamAsync(summaryRequest, ct))
            {
                if (chunk.IsText)
                {
                    summaryText.Append(chunk.Text);
                    onToken?.Invoke(chunk.Text!);
                }
            }

            var summary = summaryText.ToString();
            if (!string.IsNullOrEmpty(summary))
            {
                _sessionManager.AppendMessage(Message.Assistant(summary));
                finalText.Append(summary);
            }
        }

        return finalText.ToString();
    }

    public void UpdateSystemPrompt(string newPrompt)
    {
        _systemPrompt = newPrompt;
        _sessionManager.SetSystemPrompt(newPrompt);
    }

    private void TrimContextIfNeeded(Action<string>? onToken)
    {
        var threshold = (int)(_config.MaxContextTokens * 0.8);
        if (_estimatedTokens < threshold) return;

        var messages = _sessionManager.Messages;
        if (messages.Count < 4) return;

        var keepRecent = Math.Min(6, messages.Count / 2);
        var trimEnd = messages.Count - keepRecent;
        if (trimEnd <= 1) return;

        var toSummarize = new StringBuilder();
        for (int i = 1; i < trimEnd; i++)
        {
            var msg = messages[i];
            var text = msg.GetTextContent() ?? "";
            if (text.Length > 500) text = text[..500] + "...";
            toSummarize.AppendLine($"[{msg.Role}]: {text}");
        }

        var summaryText = $"[Context compacted: {trimEnd - 1} older messages summarized]\n" +
                          $"Previous conversation covered:\n{toSummarize}";

        _sessionManager.TrimMessages(1, trimEnd, Message.System(
            $"[Earlier conversation summary]\n{summaryText}"));

        _estimatedTokens = EstimateTokens(_systemPrompt);
        foreach (var m in _sessionManager.Messages)
            _estimatedTokens += EstimateTokens(m.GetTextContent() ?? "");

        onToken?.Invoke($"\n[Context trimmed: {trimEnd - 1} messages compacted to stay within context window]\n");
    }

    private static int EstimateTokens(string text) => text.Length / 4;

    private static string TruncateArgs(string args, int maxLen = 200)
        => args.Length <= maxLen ? args : args[..maxLen] + "...";

    private static string TruncateForDisplay(string text, int maxLen = 300)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLen ? singleLine : singleLine[..maxLen] + "...";
    }
}
