using System.Collections.Concurrent;
using System.Text.Json;
using SharpClaw.Configuration;
using SharpClaw.LLM;
using SharpClaw.Tools;

namespace SharpClaw.Agents;

/// <summary>
/// Manages sub-agent lifecycles: persists configs, schedules long-running agents,
/// launches task agents, and coordinates LLM access via a semaphore so
/// sub-agents don't overwhelm a single llama.cpp instance.
/// </summary>
public class AgentScheduler : IAsyncDisposable
{
    private readonly SharpClawConfig _config;
    private readonly LlamaCppClient _llmClient;
    private readonly ToolRegistry _parentToolRegistry;
    private readonly Mailbox _mailbox;
    private readonly NotificationBus _notifications;
    private readonly string _agentsDir;

    private readonly ConcurrentDictionary<string, SubAgentConfig> _agents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _scheduledCts = new();
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new();
    private readonly SemaphoreSlim _llmSemaphore = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public AgentScheduler(
        SharpClawConfig config,
        LlamaCppClient llmClient,
        ToolRegistry parentToolRegistry,
        Mailbox mailbox,
        NotificationBus notifications)
    {
        _config = config;
        _llmClient = llmClient;
        _parentToolRegistry = parentToolRegistry;
        _mailbox = mailbox;
        _notifications = notifications;
        _agentsDir = Path.Combine(config.ResolvedConfigDir, "agents");
        Directory.CreateDirectory(_agentsDir);
    }

    /// <summary>
    /// Load persisted agent configs and resume scheduled long-running agents.
    /// </summary>
    public void Start()
    {
        LoadPersistedAgents();

        foreach (var agent in _agents.Values)
        {
            if (agent.Kind == AgentKind.LongRunning && agent.Status == AgentStatus.Running)
                ScheduleAgent(agent);
        }

        var scheduled = _agents.Values.Count(a =>
            a.Kind == AgentKind.LongRunning && a.Status == AgentStatus.Running);
        if (scheduled > 0)
            _notifications.Post("scheduler", $"Resumed {scheduled} long-running agent(s)");
    }

    /// <summary>
    /// Spawn a new long-running agent with a schedule.
    /// </summary>
    public SubAgentConfig SpawnAgent(string name, string purpose, string schedule,
        List<string>? tools = null, int maxIterations = 15)
    {
        var parsed = ParsedSchedule.Parse(schedule);

        var id = GenerateId(name);
        var agent = new SubAgentConfig
        {
            Id = id,
            Name = name,
            Kind = AgentKind.LongRunning,
            Purpose = purpose,
            Schedule = schedule,
            Tools = tools,
            MaxIterations = maxIterations,
            Status = AgentStatus.Running,
            CreatedAt = DateTimeOffset.Now
        };

        _agents[id] = agent;
        SaveAgentConfig(agent);
        ScheduleAgent(agent);

        _notifications.Post(name, $"Spawned (schedule: {schedule})");
        return agent;
    }

    /// <summary>
    /// Spawn a short-lived task agent that runs once and posts results to the mailbox.
    /// Returns the agent config immediately; the task runs in the background.
    /// </summary>
    public SubAgentConfig SpawnTask(string goal, List<string>? tools = null,
        int maxIterations = 10, string? name = null)
    {
        var taskName = name ?? $"task-{DateTime.Now:HHmmss}";
        var id = GenerateId(taskName);
        var agent = new SubAgentConfig
        {
            Id = id,
            Name = taskName,
            Kind = AgentKind.Task,
            Purpose = goal,
            Tools = tools,
            MaxIterations = maxIterations,
            Status = AgentStatus.Running,
            CreatedAt = DateTimeOffset.Now
        };

        _agents[id] = agent;
        SaveAgentConfig(agent);

        var task = Task.Run(async () =>
        {
            try
            {
                await RunAgentOnceAsync(agent);
            }
            catch (Exception ex)
            {
                _notifications.Post(taskName, $"Failed: {ex.Message}");
            }
        });
        _runningTasks[id] = task;

        _notifications.Post(taskName, "Task spawned");
        return agent;
    }

    public bool StopAgent(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return false;

        agent.Status = AgentStatus.Paused;
        SaveAgentConfig(agent);

        if (_scheduledCts.TryRemove(agentId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        _notifications.Post(agent.Name, "Stopped");
        return true;
    }

    public bool ResumeAgent(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return false;
        if (agent.Kind != AgentKind.LongRunning)
            return false;

        agent.Status = AgentStatus.Running;
        SaveAgentConfig(agent);
        ScheduleAgent(agent);

        _notifications.Post(agent.Name, "Resumed");
        return true;
    }

    public bool RemoveAgent(string agentId)
    {
        StopAgent(agentId);
        _agents.TryRemove(agentId, out _);
        _runningTasks.TryRemove(agentId, out _);

        var dir = Path.Combine(_agentsDir, agentId);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
        return true;
    }

    public SubAgentConfig? GetAgent(string agentId)
    {
        _agents.TryGetValue(agentId, out var agent);
        return agent;
    }

    public List<SubAgentConfig> ListAgents()
    {
        return _agents.Values.OrderByDescending(a => a.CreatedAt).ToList();
    }

    public string? ReadAgentMemory(string agentId)
    {
        var memPath = Path.Combine(_agentsDir, agentId, "memory.md");
        if (!File.Exists(memPath)) return null;
        return File.ReadAllText(memPath);
    }

    public bool WriteAgentMemory(string agentId, string content)
    {
        if (!_agents.ContainsKey(agentId)) return false;
        var dir = Path.Combine(_agentsDir, agentId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "memory.md"), content + "\n");
        return true;
    }

    private void ScheduleAgent(SubAgentConfig agent)
    {
        if (agent.Schedule == null) return;

        // Cancel any existing schedule
        if (_scheduledCts.TryRemove(agent.Id, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var parsed = ParsedSchedule.Parse(agent.Schedule);
        var cts = new CancellationTokenSource();
        _scheduledCts[agent.Id] = cts;

        var task = Task.Run(async () => await ScheduleLoopAsync(agent, parsed, cts.Token));
        _runningTasks[agent.Id] = task;
    }

    private async Task ScheduleLoopAsync(SubAgentConfig agent, ParsedSchedule schedule, CancellationToken ct)
    {
        try
        {
            // Initial delay
            var firstDelay = schedule.GetNextDelay();
            if (firstDelay > TimeSpan.Zero && !schedule.RunOnce)
            {
                // For newly created agents, do a first run soon
                if (agent.RunCount == 0)
                    firstDelay = TimeSpan.FromSeconds(5);

                await Task.Delay(firstDelay, ct);
            }

            while (!ct.IsCancellationRequested)
            {
                await RunAgentOnceAsync(agent, ct);

                if (schedule.RunOnce)
                    break;

                var nextDelay = schedule.GetNextDelay();
                await Task.Delay(nextDelay, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Post(agent.Name, $"Schedule error: {ex.Message}");
        }
    }

    private async Task RunAgentOnceAsync(SubAgentConfig agent, CancellationToken ct = default)
    {
        var agentDir = Path.Combine(_agentsDir, agent.Id);
        var runner = new SubAgentRunner(_config, _llmClient, _parentToolRegistry, agentDir);

        await _llmSemaphore.WaitAsync(ct);
        AgentRunResult result;
        try
        {
            result = await runner.RunAsync(agent,
                onNotification: msg => _notifications.Post(agent.Name, msg),
                ct: ct);
        }
        finally
        {
            _llmSemaphore.Release();
        }

        agent.LastRunAt = DateTimeOffset.Now;
        agent.RunCount++;

        if (result.Success)
        {
            agent.LastError = null;
            if (agent.Kind == AgentKind.Task)
                agent.Status = AgentStatus.Completed;
        }
        else
        {
            agent.LastError = result.Error;
            if (agent.Kind == AgentKind.Task)
                agent.Status = AgentStatus.Failed;
        }

        SaveAgentConfig(agent);

        _mailbox.Post(new MailMessage
        {
            From = agent.Id,
            FromName = agent.Name,
            Subject = result.Success
                ? $"[{agent.Name}] Run #{agent.RunCount} completed"
                : $"[{agent.Name}] Run #{agent.RunCount} failed: {result.Error}",
            Body = result.Response
        });

        _notifications.Post(agent.Name,
            $"New mail: Run #{agent.RunCount} {(result.Success ? "completed" : "failed")}");
    }

    private void LoadPersistedAgents()
    {
        if (!Directory.Exists(_agentsDir)) return;

        foreach (var dir in Directory.EnumerateDirectories(_agentsDir))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;

            try
            {
                var json = File.ReadAllText(configPath);
                var agent = JsonSerializer.Deserialize<SubAgentConfig>(json, JsonOpts);
                if (agent != null)
                {
                    // Completed/failed tasks don't need to be loaded as active
                    if (agent.Kind == AgentKind.Task &&
                        agent.Status is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Cancelled)
                        continue;

                    _agents[agent.Id] = agent;
                }
            }
            catch (Exception ex)
            {
                _notifications.Post("scheduler", $"Failed to load agent from {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
    }

    private void SaveAgentConfig(SubAgentConfig agent)
    {
        var dir = Path.Combine(_agentsDir, agent.Id);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        var json = JsonSerializer.Serialize(agent, JsonOpts);
        File.WriteAllText(configPath, json);
    }

    private static string GenerateId(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');
        if (slug.Length > 20) slug = slug[..20];
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{slug}-{suffix}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _scheduledCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _scheduledCts.Clear();

        // Wait briefly for running tasks to wind down
        var tasks = _runningTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
        _runningTasks.Clear();

        _llmSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
