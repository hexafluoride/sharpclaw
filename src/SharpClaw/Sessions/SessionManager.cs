using System.Text.Json;
using SharpClaw.LLM.Models;

namespace SharpClaw.Sessions;

/// <summary>
/// Manages conversation persistence as JSONL files, one per session.
/// Mirrors OpenClaw's session transcript storage pattern.
/// </summary>
public class SessionManager
{
    private readonly string _sessionsDir;
    private string _sessionId;
    private readonly List<Message> _messages = [];

    public string SessionId => _sessionId;
    public IReadOnlyList<Message> Messages => _messages;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SessionManager(string sessionsDir, string? sessionId = null)
    {
        _sessionsDir = sessionsDir;
        Directory.CreateDirectory(sessionsDir);
        _sessionId = sessionId ?? GenerateSessionId();
    }

    public void LoadSession(string sessionId)
    {
        _sessionId = sessionId;
        _messages.Clear();

        var path = GetSessionPath(sessionId);
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<SessionEntry>(line, JsonOpts);
                if (entry?.Message is not null)
                    _messages.Add(entry.Message);
            }
            catch (JsonException)
            {
                // Skip malformed lines
            }
        }
    }

    public void NewSession()
    {
        _sessionId = GenerateSessionId();
        _messages.Clear();
    }

    public void AppendMessage(Message message)
    {
        _messages.Add(message);
        PersistEntry(new SessionEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Message = message
        });
    }

    public void SetSystemPrompt(string systemPrompt)
    {
        var sysMsg = Message.System(systemPrompt);
        if (_messages.Count > 0 && _messages[0].Role == "system")
            _messages[0] = sysMsg;
        else
            _messages.Insert(0, sysMsg);
    }

    public List<string> ListSessions()
    {
        if (!Directory.Exists(_sessionsDir))
            return [];

        return Directory.EnumerateFiles(_sessionsDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderByDescending(n => n)
            .ToList();
    }

    private void PersistEntry(SessionEntry entry)
    {
        var path = GetSessionPath(_sessionId);
        var line = JsonSerializer.Serialize(entry, JsonOpts);
        File.AppendAllText(path, line + "\n");
    }

    private string GetSessionPath(string sessionId) =>
        Path.Combine(_sessionsDir, $"{sessionId}.jsonl");

    private static string GenerateSessionId() =>
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

    private class SessionEntry
    {
        public DateTimeOffset Timestamp { get; set; }
        public Message? Message { get; set; }
    }
}
