using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Agents;

public class MailMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("fromName")]
    public string FromName { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("read")]
    public bool Read { get; set; }
}

/// <summary>
/// Persistent message store backed by a JSONL file.
/// Sub-agents post results here; the main agent reads them.
/// </summary>
public class Mailbox
{
    private readonly string _path;
    private readonly List<MailMessage> _messages = [];
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Mailbox(string path)
    {
        _path = path;
        Load();
    }

    public void Post(MailMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
            Append(message);
        }
    }

    public List<MailMessage> GetUnread()
    {
        lock (_lock)
        {
            return _messages.Where(m => !m.Read).OrderBy(m => m.Timestamp).ToList();
        }
    }

    public List<MailMessage> GetAll(string? fromAgentId = null, int limit = 50)
    {
        lock (_lock)
        {
            IEnumerable<MailMessage> query = _messages.OrderByDescending(m => m.Timestamp);
            if (fromAgentId != null)
                query = query.Where(m =>
                    m.From == fromAgentId ||
                    m.FromName.Contains(fromAgentId, StringComparison.OrdinalIgnoreCase));
            return query.Take(limit).Reverse().ToList();
        }
    }

    public MailMessage? GetById(string messageId)
    {
        lock (_lock)
        {
            return _messages.FirstOrDefault(m => m.Id == messageId);
        }
    }

    public int UnreadCount
    {
        get { lock (_lock) { return _messages.Count(m => !m.Read); } }
    }

    public int TotalCount
    {
        get { lock (_lock) { return _messages.Count; } }
    }

    public void MarkRead(string? messageId = null)
    {
        lock (_lock)
        {
            if (messageId != null)
            {
                var msg = _messages.FirstOrDefault(m => m.Id == messageId);
                if (msg != null) msg.Read = true;
            }
            else
            {
                foreach (var msg in _messages)
                    msg.Read = true;
            }
            Flush();
        }
    }

    public void Dismiss(string? messageId = null)
    {
        lock (_lock)
        {
            if (messageId != null)
                _messages.RemoveAll(m => m.Id == messageId);
            else
                _messages.RemoveAll(m => m.Read);
            Flush();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var msg = JsonSerializer.Deserialize<MailMessage>(line, JsonOpts);
                    if (msg != null) _messages.Add(msg);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mailbox] Failed to load: {ex.Message}");
        }
    }

    private void Append(MailMessage message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(message, JsonOpts);
            File.AppendAllText(_path, json + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mailbox] Failed to append: {ex.Message}");
        }
    }

    private void Flush()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null) Directory.CreateDirectory(dir);
            var lines = _messages.Select(m => JsonSerializer.Serialize(m, JsonOpts));
            File.WriteAllText(_path, string.Join("\n", lines) + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mailbox] Failed to flush: {ex.Message}");
        }
    }
}
