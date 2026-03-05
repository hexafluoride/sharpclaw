using System.Text;
using System.Text.Json;
using SharpClaw.Agents;

namespace SharpClaw.Tools;

/// <summary>
/// Tool for spawning and managing sub-agents, and reading the mailbox.
/// Long-running agents execute on a schedule with persistent memory.
/// Task agents run once to complete a specific goal.
/// All agent results are delivered to the mailbox.
/// </summary>
public class AgentTool : ITool
{
    private readonly AgentScheduler _scheduler;
    private readonly Mailbox _mailbox;

    public AgentTool(AgentScheduler scheduler, Mailbox mailbox)
    {
        _scheduler = scheduler;
        _mailbox = mailbox;
    }

    public string Name => "agents";

    public string Description =>
        "Manage sub-agents and their mailbox. Results from agents arrive in the mailbox.\n" +
        "Actions:\n" +
        "  spawn_agent(name, purpose, schedule) - create a scheduled long-running agent\n" +
        "  spawn_task(purpose, name?) - create a one-time background task\n" +
        "  list - show all agents with their IDs and status\n" +
        "  info(agent_id) - detailed info about one agent\n" +
        "  stop/resume/remove(agent_id) - control agent lifecycle\n" +
        "  mailbox(from_agent?, limit?) - list messages (shows preview)\n" +
        "  read_message(message_id) - read full message body\n" +
        "  mark_read(message_id?) / dismiss(message_id?) - manage read state\n" +
        "  agent_memory(agent_id, memory_action) - read/write agent's working memory\n" +
        "Note: agent_id is a slug like 'research-bot-a1b2c3', not the display name.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "action" },
        properties = new
        {
            action = new
            {
                type = "string",
                description = "Action to perform",
                @enum = new[]
                {
                    "spawn_agent", "spawn_task", "list", "info",
                    "stop", "resume", "remove",
                    "mailbox", "read_message", "mark_read", "dismiss",
                    "agent_memory"
                }
            },
            name = new
            {
                type = "string",
                description = "Agent name (for spawn_agent/spawn_task)"
            },
            purpose = new
            {
                type = "string",
                description = "Agent purpose/goal (for spawn_agent/spawn_task)"
            },
            schedule = new
            {
                type = "string",
                description = "Schedule for long-running agents: 'every 5m', 'every 2h', 'daily 09:00', 'once'"
            },
            tools = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Tool names the agent can use (default: all tools except 'agents')"
            },
            max_iterations = new
            {
                type = "integer",
                description = "Max reasoning iterations (default: 15 for agents, 10 for tasks)"
            },
            agent_id = new
            {
                type = "string",
                description = "Agent ID slug (e.g. 'research-bot-a1b2c3') for info/stop/resume/remove/agent_memory. Shown in list output and mailbox 'from' field."
            },
            from_agent = new
            {
                type = "string",
                description = "Filter mailbox by agent ID or name (partial match)"
            },
            message_id = new
            {
                type = "string",
                description = "Message ID (e.g. 'd35ca07f4d5c') for read_message/mark_read/dismiss"
            },
            memory_action = new
            {
                type = "string",
                description = "For agent_memory: 'read' or 'write'",
                @enum = new[] { "read", "write" }
            },
            content = new
            {
                type = "string",
                description = "Content for agent_memory write"
            },
            limit = new
            {
                type = "integer",
                description = "Max messages to return for mailbox (default: 20)"
            }
        }
    });

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var action = args.GetProperty("action").GetString()!;

        try
        {
            return Task.FromResult<string>(action switch
            {
                "spawn_agent" => SpawnAgent(args),
                "spawn_task" => SpawnTask(args),
                "list" => ListAgents(),
                "info" => AgentInfo(args),
                "stop" => StopAgent(args),
                "resume" => ResumeAgent(args),
                "remove" => RemoveAgent(args),
                "mailbox" => ReadMailbox(args),
                "read_message" => ReadSingleMessage(args),
                "mark_read" => MarkRead(args),
                "dismiss" => DismissMessages(args),
                "agent_memory" => AgentMemory(args),
                _ => $"Unknown action: {action}"
            });
        }
        catch (FormatException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    private string SpawnAgent(JsonElement args)
    {
        var name = Require(args, "name");
        var purpose = Require(args, "purpose");
        var schedule = Require(args, "schedule");
        var tools = GetStringList(args, "tools");
        var maxIter = Int(args, "max_iterations") ?? 15;

        var agent = _scheduler.SpawnAgent(name, purpose, schedule, tools, maxIter);

        return $"Spawned long-running agent:\n" +
               $"  ID: {agent.Id}\n" +
               $"  Name: {agent.Name}\n" +
               $"  Schedule: {agent.Schedule}\n" +
               $"  Tools: {(agent.Tools != null ? string.Join(", ", agent.Tools) : "all")}\n" +
               $"  Max iterations: {agent.MaxIterations}\n" +
               $"Results will be delivered to the mailbox.";
    }

    private string SpawnTask(JsonElement args)
    {
        var purpose = Require(args, "purpose");
        var name = Str(args, "name");
        var tools = GetStringList(args, "tools");
        var maxIter = Int(args, "max_iterations") ?? 10;

        var agent = _scheduler.SpawnTask(purpose, tools, maxIter, name);

        return $"Spawned task agent:\n" +
               $"  ID: {agent.Id}\n" +
               $"  Name: {agent.Name}\n" +
               $"  Goal: {Truncate(purpose, 200)}\n" +
               $"  Tools: {(agent.Tools != null ? string.Join(", ", agent.Tools) : "all")}\n" +
               $"  Max iterations: {agent.MaxIterations}\n" +
               $"Running in background. Result will be delivered to the mailbox.";
    }

    private string ListAgents()
    {
        var agents = _scheduler.ListAgents();
        if (agents.Count == 0) return "No agents. Use spawn_agent or spawn_task to create one.";

        var sb = new StringBuilder();
        sb.AppendLine($"Agents ({agents.Count}):");

        foreach (var a in agents)
        {
            var statusIcon = a.Status switch
            {
                AgentStatus.Running => "[running]",
                AgentStatus.Paused => "[paused]",
                AgentStatus.Completed => "[done]",
                AgentStatus.Failed => "[failed]",
                AgentStatus.Cancelled => "[cancelled]",
                _ => "[?]"
            };
            var kind = a.Kind == AgentKind.LongRunning ? "agent" : "task";
            sb.AppendLine($"  {a.Id} {statusIcon} ({kind})");
            sb.AppendLine($"    Name: {a.Name}");
            sb.AppendLine($"    Purpose: {Truncate(a.Purpose, 100)}");
            if (a.Schedule != null)
                sb.AppendLine($"    Schedule: {a.Schedule}");
            sb.AppendLine($"    Runs: {a.RunCount}" +
                (a.LastRunAt.HasValue ? $", last: {a.LastRunAt.Value:yyyy-MM-dd HH:mm:ss}" : ""));
            if (a.LastError != null)
                sb.AppendLine($"    Last error: {Truncate(a.LastError, 100)}");
        }

        return sb.ToString().TrimEnd();
    }

    private string AgentInfo(JsonElement args)
    {
        var id = Require(args, "agent_id");
        var agent = _scheduler.GetAgent(id);
        if (agent == null) return $"Agent not found: {id}";

        var sb = new StringBuilder();
        sb.AppendLine($"Agent: {agent.Name} ({agent.Id})");
        sb.AppendLine($"  Kind: {agent.Kind}");
        sb.AppendLine($"  Status: {agent.Status}");
        sb.AppendLine($"  Purpose: {agent.Purpose}");
        if (agent.Schedule != null)
            sb.AppendLine($"  Schedule: {agent.Schedule}");
        sb.AppendLine($"  Tools: {(agent.Tools != null ? string.Join(", ", agent.Tools) : "all")}");
        sb.AppendLine($"  Max iterations: {agent.MaxIterations}");
        sb.AppendLine($"  Created: {agent.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Runs: {agent.RunCount}");
        if (agent.LastRunAt.HasValue)
            sb.AppendLine($"  Last run: {agent.LastRunAt.Value:yyyy-MM-dd HH:mm:ss}");
        if (agent.LastError != null)
            sb.AppendLine($"  Last error: {agent.LastError}");

        var memory = _scheduler.ReadAgentMemory(id);
        if (memory != null)
        {
            sb.AppendLine();
            sb.AppendLine("Working Memory:");
            sb.AppendLine(memory.Length > 2000 ? memory[..2000] + "... (truncated)" : memory);
        }

        return sb.ToString().TrimEnd();
    }

    private string StopAgent(JsonElement args)
    {
        var id = Require(args, "agent_id");
        return _scheduler.StopAgent(id)
            ? $"Agent '{id}' stopped."
            : $"Agent not found: {id}";
    }

    private string ResumeAgent(JsonElement args)
    {
        var id = Require(args, "agent_id");
        if (_scheduler.ResumeAgent(id))
            return $"Agent '{id}' resumed.";

        var agent = _scheduler.GetAgent(id);
        if (agent == null)
            return $"Agent not found: {id}. Use action 'list' to see all agents with their IDs.";
        if (agent.Kind == AgentKind.Task)
            return $"Cannot resume '{id}': task agents run once and cannot be resumed. Use spawn_task to create a new task.";
        return $"Cannot resume '{id}': agent status is {agent.Status}. Only paused/stopped long-running agents can be resumed.";
    }

    private string RemoveAgent(JsonElement args)
    {
        var id = Require(args, "agent_id");
        return _scheduler.RemoveAgent(id)
            ? $"Agent '{id}' removed."
            : $"Agent not found: {id}";
    }

    private string ReadMailbox(JsonElement args)
    {
        var fromAgent = Str(args, "from_agent");
        var limit = Int(args, "limit") ?? 20;

        var messages = _mailbox.GetAll(fromAgent, limit);
        if (messages.Count == 0)
        {
            var unread = _mailbox.UnreadCount;
            return unread > 0
                ? $"No messages{(fromAgent != null ? $" from {fromAgent}" : "")} (but {unread} unread total)."
                : "Mailbox is empty.";
        }

        var sb = new StringBuilder();
        var unreadCount = _mailbox.UnreadCount;
        sb.AppendLine($"Mailbox ({messages.Count} shown, {unreadCount} unread, {_mailbox.TotalCount} total):");
        sb.AppendLine("Use read_message with a message_id to read full content.");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var marker = msg.Read ? " " : "*";
            sb.AppendLine($"  {marker} [{msg.Id}] {msg.Timestamp:MM-dd HH:mm} from {msg.FromName} (agent: {msg.From})");
            sb.AppendLine($"    {msg.Subject}");
            var preview = msg.Body.ReplaceLineEndings(" ");
            if (preview.Length > 150)
                preview = preview[..150] + "...";
            if (!string.IsNullOrWhiteSpace(preview))
                sb.AppendLine($"    Preview: {preview}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string ReadSingleMessage(JsonElement args)
    {
        var msgId = Require(args, "message_id");
        var msg = _mailbox.GetById(msgId);
        if (msg == null)
            return $"Message not found: {msgId}";

        _mailbox.MarkRead(msgId);

        var sb = new StringBuilder();
        sb.AppendLine($"From: {msg.FromName} (agent: {msg.From})");
        sb.AppendLine($"Date: {msg.Timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Subject: {msg.Subject}");
        sb.AppendLine($"Message ID: {msg.Id}");
        sb.AppendLine();
        sb.AppendLine(msg.Body);

        return sb.ToString().TrimEnd();
    }

    private string MarkRead(JsonElement args)
    {
        var msgId = Str(args, "message_id");
        _mailbox.MarkRead(msgId);
        return msgId != null
            ? $"Marked message '{msgId}' as read."
            : "Marked all messages as read.";
    }

    private string DismissMessages(JsonElement args)
    {
        var msgId = Str(args, "message_id");
        _mailbox.Dismiss(msgId);
        return msgId != null
            ? $"Dismissed message '{msgId}'."
            : "Dismissed all read messages.";
    }

    private string AgentMemory(JsonElement args)
    {
        var id = Require(args, "agent_id");
        var memAction = Str(args, "memory_action") ?? "read";

        if (memAction == "write")
        {
            var content = Str(args, "content") ?? "";
            if (string.IsNullOrEmpty(content))
                return "Error: content required for write";
            return _scheduler.WriteAgentMemory(id, content)
                ? $"Updated memory for agent '{id}'."
                : $"Agent not found: {id}";
        }

        var memory = _scheduler.ReadAgentMemory(id);
        if (memory == null)
            return _scheduler.GetAgent(id) != null ? "(empty memory)" : $"Agent not found: {id}";
        return string.IsNullOrWhiteSpace(memory) ? "(empty memory)" : memory;
    }

    private static string Require(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        throw new ArgumentException($"'{prop}' is required");
    }

    private static string? Str(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static int? Int(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return null;
    }

    private static List<string>? GetStringList(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString()!);
        }
        return list.Count > 0 ? list : null;
    }

    private static string Truncate(string s, int max)
    {
        var oneLine = s.ReplaceLineEndings(" ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "...";
    }
}
