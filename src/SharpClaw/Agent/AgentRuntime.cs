using System.Text;
using System.Text.Json;
using SharpClaw.Configuration;
using SharpClaw.LLM;
using SharpClaw.LLM.Models;
using SharpClaw.Sessions;
using SharpClaw.Tools;

namespace SharpClaw.Agent;

/// <summary>
/// The agentic reasoning loop, modeled after OpenClaw's pi-embedded-runner.
/// Load context -> call LLM -> parse tool calls -> execute -> append results -> loop.
/// Terminates when the LLM produces a final text response with no tool calls,
/// or when the iteration guard is hit.
/// </summary>
public class AgentRuntime
{
    private readonly SharpClawConfig _config;
    private readonly LlamaCppClient _llmClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly SessionManager _sessionManager;
    private readonly string _systemPrompt;

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

    /// <summary>
    /// Process a user message through the reasoning loop.
    /// Streams text tokens to the provided callback as they arrive.
    /// Returns the final assistant text response.
    /// </summary>
    public async Task<string> ProcessAsync(
        string userMessage,
        Action<string>? onToken = null,
        CancellationToken ct = default)
    {
        var userMsg = Message.User(userMessage);
        _sessionManager.AppendMessage(userMsg);

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
                {
                    toolCalls = chunk.ToolCalls;
                }
            }

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

                    var result = await _toolRegistry.ExecuteAsync(toolName, toolArgs, ct);

                    onToken?.Invoke($"[Result: {TruncateForDisplay(result)}]\n");

                    var toolResultMsg = Message.Tool(toolId, toolName, result);
                    _sessionManager.AppendMessage(toolResultMsg);
                }

                continue;
            }

            // No tool calls: this is the final response
            var text = responseText.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                var assistantMsg = Message.Assistant(text);
                _sessionManager.AppendMessage(assistantMsg);
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
        _sessionManager.SetSystemPrompt(newPrompt);
    }

    private static string TruncateArgs(string args, int maxLen = 200)
    {
        if (args.Length <= maxLen) return args;
        return args[..maxLen] + "...";
    }

    private static string TruncateForDisplay(string text, int maxLen = 300)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        if (singleLine.Length <= maxLen) return singleLine;
        return singleLine[..maxLen] + "...";
    }
}
