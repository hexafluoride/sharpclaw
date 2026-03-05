using SharpClaw.LLM.Models;
using SharpClaw.Sessions;

namespace SharpClaw.Tests;

public class SessionManagerTests : IDisposable
{
    private readonly string _tempDir;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sharpclaw-sess-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void NewSession_GeneratesId()
    {
        var sm = new SessionManager(_tempDir);
        Assert.False(string.IsNullOrEmpty(sm.SessionId));
    }

    [Fact]
    public void AppendMessage_AddsToList()
    {
        var sm = new SessionManager(_tempDir);
        sm.AppendMessage(Message.User("hello"));
        Assert.Single(sm.Messages);
        Assert.Equal("user", sm.Messages[0].Role);
    }

    [Fact]
    public void SetSystemPrompt_InsertsAtFront()
    {
        var sm = new SessionManager(_tempDir);
        sm.SetSystemPrompt("you are a cat");
        Assert.Single(sm.Messages);
        Assert.Equal("system", sm.Messages[0].Role);
    }

    [Fact]
    public void SetSystemPrompt_ReplacesExisting()
    {
        var sm = new SessionManager(_tempDir);
        sm.SetSystemPrompt("version 1");
        sm.SetSystemPrompt("version 2");
        Assert.Single(sm.Messages);
        Assert.Equal("version 2", sm.Messages[0].GetTextContent());
    }

    [Fact]
    public void NewSession_ClearsMessages()
    {
        var sm = new SessionManager(_tempDir);
        sm.AppendMessage(Message.User("hi"));
        Assert.Single(sm.Messages);

        sm.NewSession();
        Assert.Empty(sm.Messages);
    }

    [Fact]
    public void Persistence_RoundTrip()
    {
        var sm1 = new SessionManager(_tempDir);
        var sessionId = sm1.SessionId;
        sm1.AppendMessage(Message.User("hello"));
        sm1.AppendMessage(Message.Assistant("world"));

        var sm2 = new SessionManager(_tempDir);
        sm2.LoadSession(sessionId);
        Assert.Equal(2, sm2.Messages.Count);
        Assert.Equal("user", sm2.Messages[0].Role);
        Assert.Equal("assistant", sm2.Messages[1].Role);
    }

    [Fact]
    public void ListSessions_ReturnsPersistedSessions()
    {
        var sm = new SessionManager(_tempDir);
        var id1 = sm.SessionId;
        sm.AppendMessage(Message.User("test"));

        sm.NewSession();
        var id2 = sm.SessionId;
        sm.AppendMessage(Message.User("test2"));

        var sessions = sm.ListSessions();
        Assert.Equal(2, sessions.Count);
        Assert.Contains(id1, sessions);
        Assert.Contains(id2, sessions);
    }

    [Fact]
    public void LoadSession_NonExistent_ClearsMessages()
    {
        var sm = new SessionManager(_tempDir);
        sm.AppendMessage(Message.User("old"));
        sm.LoadSession("nonexistent-session-id");
        Assert.Empty(sm.Messages);
    }
}
