using System.Text;
using System.Text.Json;
using SharpClaw.Configuration;
using SharpClaw.LLM;
using SharpClaw.LLM.Models;
using SharpClaw.Tools;

namespace SharpClaw.Agents;

/// <summary>
/// Executes a sub-agent's reasoning loop: builds context from its purpose and
/// working memory, calls the LLM with tool definitions, executes tool calls,
/// and loops until a final response or iteration limit.
/// Runs independently of the main AgentRuntime.
/// </summary>
public class SubAgentRunner
{
    private readonly SharpClawConfig _config;
    private readonly LlamaCppClient _llmClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly string _agentDir;

    public SubAgentRunner(
        SharpClawConfig config,
        LlamaCppClient llmClient,
        ToolRegistry parentToolRegistry,
        string agentDir)
    {
        _config = config;
        _llmClient = llmClient;
        _toolRegistry = parentToolRegistry;
        _agentDir = agentDir;
        Directory.CreateDirectory(_agentDir);
    }

    /// <summary>
    /// Run one execution cycle for an agent: inject purpose + memory as system prompt,
    /// optionally inject a goal message, loop through the LLM reasoning cycle.
    /// </summary>
    public async Task<AgentRunResult> RunAsync(
        SubAgentConfig agentConfig,
        string? goalOverride = null,
        Action<string>? onProgress = null,
        Action<string>? onNotification = null,
        CancellationToken ct = default)
    {
        var toolRegistry = BuildToolRegistry(agentConfig);
        var memoryPath = Path.Combine(_agentDir, "memory.md");
        var systemPrompt = BuildSystemPrompt(agentConfig, memoryPath);

        var messages = new List<Message> { Message.System(systemPrompt) };

        var history = LoadRecentHistory();
        messages.AddRange(history);

        var goal = goalOverride ?? BuildGoalMessage(agentConfig);
        messages.Add(Message.User(goal));

        var iterations = 0;
        var finalResponse = new StringBuilder();

        onNotification?.Invoke($"Starting (iteration limit: {agentConfig.MaxIterations})");

        try
        {
            while (iterations < agentConfig.MaxIterations)
            {
                iterations++;
                ct.ThrowIfCancellationRequested();

                var toolDefs = toolRegistry.GetDefinitions();
                var request = new ChatRequest
                {
                    Model = _config.Model,
                    Messages = messages.ToList(),
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
                        onProgress?.Invoke(chunk.Text!);
                    }
                    if (chunk.IsToolCall)
                        toolCalls = chunk.ToolCalls;
                }

                if (toolCalls is { Count: > 0 })
                {
                    var assistantMsg = Message.AssistantWithToolCalls(toolCalls);
                    if (responseText.Length > 0)
                        assistantMsg.Content = JsonSerializer.SerializeToElement(responseText.ToString());
                    messages.Add(assistantMsg);

                    foreach (var tc in toolCalls)
                    {
                        var name = tc.ResolvedName;
                        var args = tc.ResolvedArguments;
                        var id = tc.Id ?? $"call_{Guid.NewGuid().ToString("N")[..8]}";

                        onNotification?.Invoke($"Tool: {name}({Truncate(args, 80)})");
                        onProgress?.Invoke($"\n  [{agentConfig.Name}] Tool: {name}({Truncate(args, 120)})\n");

                        var result = await toolRegistry.ExecuteAsync(name, args, ct);
                        onProgress?.Invoke($"  [{agentConfig.Name}] Result: {Truncate(result, 200)}\n");

                        messages.Add(Message.Tool(id, name, result));
                    }
                    continue;
                }

                var text = responseText.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    messages.Add(Message.Assistant(text));
                    finalResponse.Append(text);
                }
                break;
            }

            // If we have no captured response, prompt for a summary.
            // This covers: iteration limit hit, agent finished via tool calls
            // without a text-only response, or model returned empty final text.
            if (finalResponse.Length == 0)
            {
                var hitLimit = iterations >= agentConfig.MaxIterations;
                onNotification?.Invoke(hitLimit
                    ? "Iteration limit reached, summarizing..."
                    : "Summarizing results...");

                var prompt = hitLimit
                    ? "You have reached the iteration limit and cannot make any more tool calls. " +
                      "Summarize what you accomplished, any partial results, and what remains to be done."
                    : "Provide a concise summary of what you did and the results. " +
                      "Include any key findings, files created, or actions taken.";
                messages.Add(Message.User(prompt));

                var summaryRequest = new ChatRequest
                {
                    Model = _config.Model,
                    Messages = messages.ToList(),
                    Tools = null,
                    Stream = true
                };

                var summaryText = new StringBuilder();
                await foreach (var chunk in _llmClient.StreamAsync(summaryRequest, ct))
                {
                    if (chunk.IsText)
                    {
                        summaryText.Append(chunk.Text);
                        onProgress?.Invoke(chunk.Text!);
                    }
                }

                var summary = summaryText.ToString();
                if (!string.IsNullOrEmpty(summary))
                {
                    messages.Add(Message.Assistant(summary));
                    finalResponse.Append(summary);
                }
            }

            var response = finalResponse.ToString();

            SaveHistory(messages);
            await UpdateMemoryAfterRun(agentConfig, response, toolRegistry, ct);

            onNotification?.Invoke($"Completed ({iterations} iterations)");

            return new AgentRunResult
            {
                AgentId = agentConfig.Id,
                AgentName = agentConfig.Name,
                Success = true,
                Response = response,
                IterationsUsed = iterations,
                CompletedAt = DateTimeOffset.Now
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SaveHistory(messages);
            onNotification?.Invoke($"Failed: {Truncate(ex.Message, 100)}");

            return new AgentRunResult
            {
                AgentId = agentConfig.Id,
                AgentName = agentConfig.Name,
                Success = false,
                Response = finalResponse.ToString(),
                Error = ex.Message,
                IterationsUsed = iterations,
                CompletedAt = DateTimeOffset.Now
            };
        }
    }

    private string BuildSystemPrompt(SubAgentConfig agent, string memoryPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are '{agent.Name}', a sub-agent of SharpClaw.");
        sb.AppendLine($"Your purpose: {agent.Purpose}");
        sb.AppendLine();

        if (agent.Kind == AgentKind.LongRunning)
        {
            sb.AppendLine("You are a long-running agent with persistent memory. You run on a schedule.");
            sb.AppendLine("Use your working memory to track state across runs.");
            sb.AppendLine("When you finish your work for this cycle, produce a concise summary of what you did and any findings.");
            sb.AppendLine("Your summary will be delivered to the main SharpClaw agent's mailbox.");
        }
        else
        {
            sb.AppendLine("You are a task agent. Complete your assigned goal and provide a clear result.");
            sb.AppendLine("Be efficient - you have limited iterations.");
        }

        sb.AppendLine();
        sb.AppendLine("## Working Memory");
        if (File.Exists(memoryPath))
        {
            var memory = File.ReadAllText(memoryPath);
            if (!string.IsNullOrWhiteSpace(memory))
            {
                sb.AppendLine(memory);
            }
            else
            {
                sb.AppendLine("(empty - use memory tool to store persistent notes)");
            }
        }
        else
        {
            sb.AppendLine("(empty - use memory tool to store persistent notes)");
        }

        return sb.ToString();
    }

    private static string BuildGoalMessage(SubAgentConfig agent)
    {
        if (agent.Kind == AgentKind.LongRunning)
        {
            return $"Execute your scheduled run. Your purpose: {agent.Purpose}\n" +
                   $"This is run #{agent.RunCount + 1}. " +
                   (agent.LastRunAt.HasValue
                       ? $"Last run was at {agent.LastRunAt.Value:yyyy-MM-dd HH:mm:ss}."
                       : "This is your first run.") +
                   "\nDo your work, update your memory with anything worth remembering, " +
                   "then provide a summary.";
        }

        return agent.Purpose;
    }

    /// <summary>
    /// Build a ToolRegistry scoped to what this agent is allowed to use.
    /// Always includes a dedicated memory tool for the agent's own working memory.
    /// </summary>
    private ToolRegistry BuildToolRegistry(SubAgentConfig agent)
    {
        var registry = new ToolRegistry();
        var memoryPath = Path.Combine(_agentDir, "memory.md");

        registry.Register(new AgentMemoryTool(memoryPath));

        if (agent.Tools is { Count: > 0 })
        {
            foreach (var toolName in agent.Tools)
            {
                var tool = _toolRegistry.Get(toolName);
                if (tool != null)
                    registry.Register(tool);
            }
        }
        else
        {
            foreach (var tool in _toolRegistry.All)
            {
                if (tool.Name != "agents")
                    registry.Register(tool);
            }
        }

        return registry;
    }

    /// <summary>
    /// After a run, if the agent is long-running and the memory file is getting large,
    /// ask the LLM to compact it.
    /// </summary>
    private async Task UpdateMemoryAfterRun(
        SubAgentConfig agent,
        string response,
        ToolRegistry toolRegistry,
        CancellationToken ct)
    {
        if (agent.Kind != AgentKind.LongRunning) return;

        var memoryPath = Path.Combine(_agentDir, "memory.md");
        if (!File.Exists(memoryPath)) return;

        var memory = File.ReadAllText(memoryPath);
        if (memory.Length <= 4000) return;

        try
        {
            var compactRequest = new ChatRequest
            {
                Model = _config.Model,
                Messages =
                [
                    Message.System(
                        "You are compacting an agent's working memory. " +
                        "Preserve all important facts, state, and context. " +
                        "Remove redundancy and outdated information. " +
                        "Output ONLY the compacted memory as markdown bullet points."),
                    Message.User($"Compact this working memory:\n\n{memory}")
                ],
                Stream = false
            };

            var compactResponse = await _llmClient.CompleteAsync(compactRequest, ct);
            var compacted = compactResponse.Choices?.FirstOrDefault()?.Message?.GetTextContent();
            if (!string.IsNullOrWhiteSpace(compacted) && compacted.Length < memory.Length)
            {
                File.WriteAllText(memoryPath, compacted);
                Console.Error.WriteLine($"[agent:{agent.Id}] Compacted memory: {memory.Length} -> {compacted.Length} chars");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent:{agent.Id}] Memory compaction failed: {ex.Message}");
        }
    }

    private List<Message> LoadRecentHistory()
    {
        var historyPath = Path.Combine(_agentDir, "history.jsonl");
        if (!File.Exists(historyPath)) return [];

        try
        {
            var lines = File.ReadAllLines(historyPath);
            var messages = new List<Message>();
            foreach (var line in lines.TakeLast(20))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var msg = JsonSerializer.Deserialize<Message>(line);
                    if (msg != null && msg.Role != "system")
                        messages.Add(msg);
                }
                catch (JsonException) { }
            }
            return messages;
        }
        catch { return []; }
    }

    private void SaveHistory(List<Message> messages)
    {
        var historyPath = Path.Combine(_agentDir, "history.jsonl");
        try
        {
            var nonSystem = messages.Where(m => m.Role != "system");
            var lines = nonSystem.Select(m => JsonSerializer.Serialize(m));
            File.WriteAllText(historyPath, string.Join("\n", lines) + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] Failed to save history: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max)
    {
        var oneLine = s.ReplaceLineEndings(" ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "...";
    }
}

/// <summary>
/// Lightweight memory tool scoped to a single sub-agent's working memory file.
/// </summary>
internal class AgentMemoryTool : ITool
{
    private readonly string _memoryPath;

    public AgentMemoryTool(string memoryPath) => _memoryPath = memoryPath;

    public string Name => "memory";
    public string Description =>
        "Read or write your working memory. Actions: 'read', 'append' (add text), 'replace' (overwrite).";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "action" },
        properties = new
        {
            action = new { type = "string", @enum = new[] { "read", "append", "replace" } },
            content = new { type = "string", description = "Content for append/replace" }
        }
    });

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var action = args.GetProperty("action").GetString()!;
        return Task.FromResult(action switch
        {
            "read" => ReadMemory(),
            "append" => AppendMemory(args),
            "replace" => ReplaceMemory(args),
            _ => $"Unknown action: {action}"
        });
    }

    private string ReadMemory()
    {
        if (!File.Exists(_memoryPath)) return "(empty)";
        var content = File.ReadAllText(_memoryPath);
        return string.IsNullOrWhiteSpace(content) ? "(empty)" : content;
    }

    private string AppendMemory(JsonElement args)
    {
        var content = args.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(content)) return "Error: content is required for append";
        var dir = Path.GetDirectoryName(_memoryPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.AppendAllText(_memoryPath, content + "\n");
        return "Appended to memory.";
    }

    private string ReplaceMemory(JsonElement args)
    {
        var content = args.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(content)) return "Error: content is required for replace";
        var dir = Path.GetDirectoryName(_memoryPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(_memoryPath, content + "\n");
        return "Memory replaced.";
    }
}
