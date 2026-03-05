using System.Collections.Concurrent;

namespace SharpClaw.Agents;

/// <summary>
/// Thread-safe bounded notification queue for background agent updates.
/// Agents and the scheduler post messages here; the REPL drains
/// and displays them when the user is idle (empty input buffer).
/// </summary>
public class NotificationBus
{
    private readonly ConcurrentQueue<Notification> _queue = new();
    private readonly int _maxSize;

    public NotificationBus(int maxSize = 200)
    {
        _maxSize = maxSize;
    }

    public void Post(string agentName, string message)
    {
        _queue.Enqueue(new Notification
        {
            Timestamp = DateTimeOffset.Now,
            AgentName = agentName,
            Message = message
        });

        while (_queue.Count > _maxSize)
            _queue.TryDequeue(out _);
    }

    public bool TryDequeue(out Notification? notification)
    {
        return _queue.TryDequeue(out notification);
    }

    public bool HasPending => !_queue.IsEmpty;
}

public class Notification
{
    public DateTimeOffset Timestamp { get; init; }
    public string AgentName { get; init; } = "";
    public string Message { get; init; } = "";

    public string Format()
    {
        return $"[{Timestamp:HH:mm:ss}] {AgentName}: {Message}";
    }
}
