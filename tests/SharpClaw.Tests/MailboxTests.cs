using SharpClaw.Agents;

namespace SharpClaw.Tests;

public class MailboxTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _mailboxPath;

    public MailboxTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sharpclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _mailboxPath = Path.Combine(_tempDir, "mailbox.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void NewMailbox_IsEmpty()
    {
        var mb = new Mailbox(_mailboxPath);
        Assert.Equal(0, mb.TotalCount);
        Assert.Equal(0, mb.UnreadCount);
    }

    [Fact]
    public void Post_IncrementsCount()
    {
        var mb = new Mailbox(_mailboxPath);
        mb.Post(new MailMessage { From = "a", Subject = "hi" });
        Assert.Equal(1, mb.TotalCount);
        Assert.Equal(1, mb.UnreadCount);
    }

    [Fact]
    public void GetAll_ReturnsMessages()
    {
        var mb = new Mailbox(_mailboxPath);
        mb.Post(new MailMessage { From = "a", Subject = "first" });
        mb.Post(new MailMessage { From = "b", Subject = "second" });

        var all = mb.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetAll_FilterByAgentId()
    {
        var mb = new Mailbox(_mailboxPath);
        mb.Post(new MailMessage { From = "agent-1", FromName = "Agent One", Subject = "hi" });
        mb.Post(new MailMessage { From = "agent-2", FromName = "Agent Two", Subject = "hey" });

        var filtered = mb.GetAll(fromAgentId: "agent-1");
        Assert.Single(filtered);
        Assert.Equal("hi", filtered[0].Subject);
    }

    [Fact]
    public void GetAll_FilterByPartialName()
    {
        var mb = new Mailbox(_mailboxPath);
        mb.Post(new MailMessage { From = "id-1", FromName = "Research Agent", Subject = "s1" });
        mb.Post(new MailMessage { From = "id-2", FromName = "Monitor Agent", Subject = "s2" });

        var filtered = mb.GetAll(fromAgentId: "Research");
        Assert.Single(filtered);
        Assert.Equal("s1", filtered[0].Subject);
    }

    [Fact]
    public void GetById_ReturnsCorrectMessage()
    {
        var mb = new Mailbox(_mailboxPath);
        var msg = new MailMessage { From = "a", Subject = "target" };
        mb.Post(msg);

        var found = mb.GetById(msg.Id);
        Assert.NotNull(found);
        Assert.Equal("target", found.Subject);
    }

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        var mb = new Mailbox(_mailboxPath);
        Assert.Null(mb.GetById("nonexistent"));
    }

    [Fact]
    public void MarkRead_SingleMessage()
    {
        var mb = new Mailbox(_mailboxPath);
        var msg = new MailMessage { From = "a", Subject = "hi" };
        mb.Post(msg);
        Assert.Equal(1, mb.UnreadCount);

        mb.MarkRead(msg.Id);
        Assert.Equal(0, mb.UnreadCount);
    }

    [Fact]
    public void MarkRead_AllMessages()
    {
        var mb = new Mailbox(_mailboxPath);
        mb.Post(new MailMessage { From = "a", Subject = "1" });
        mb.Post(new MailMessage { From = "b", Subject = "2" });
        Assert.Equal(2, mb.UnreadCount);

        mb.MarkRead();
        Assert.Equal(0, mb.UnreadCount);
    }

    [Fact]
    public void Dismiss_RemovesReadMessages()
    {
        var mb = new Mailbox(_mailboxPath);
        mb.Post(new MailMessage { From = "a", Subject = "1" });
        var msg2 = new MailMessage { From = "b", Subject = "2" };
        mb.Post(msg2);

        mb.MarkRead();
        mb.Dismiss();
        Assert.Equal(0, mb.TotalCount);
    }

    [Fact]
    public void Dismiss_ById_RemovesSpecificMessage()
    {
        var mb = new Mailbox(_mailboxPath);
        var msg = new MailMessage { From = "a", Subject = "delete me" };
        mb.Post(msg);
        mb.Post(new MailMessage { From = "b", Subject = "keep me" });

        mb.Dismiss(msg.Id);
        Assert.Equal(1, mb.TotalCount);
        Assert.Null(mb.GetById(msg.Id));
    }

    [Fact]
    public void Persistence_SurvivesReload()
    {
        var mb1 = new Mailbox(_mailboxPath);
        mb1.Post(new MailMessage { From = "a", Subject = "persisted" });
        mb1.Post(new MailMessage { From = "b", Subject = "also persisted" });

        var mb2 = new Mailbox(_mailboxPath);
        Assert.Equal(2, mb2.TotalCount);
        var all = mb2.GetAll();
        Assert.Contains(all, m => m.Subject == "persisted");
        Assert.Contains(all, m => m.Subject == "also persisted");
    }

    [Fact]
    public void Persistence_MarkReadSurvivesReload()
    {
        var mb1 = new Mailbox(_mailboxPath);
        var msg = new MailMessage { From = "a", Subject = "hi" };
        mb1.Post(msg);
        mb1.MarkRead(msg.Id);

        var mb2 = new Mailbox(_mailboxPath);
        Assert.Equal(0, mb2.UnreadCount);
    }

    [Fact]
    public void GetAll_RespectsLimit()
    {
        var mb = new Mailbox(_mailboxPath);
        for (int i = 0; i < 10; i++)
            mb.Post(new MailMessage { From = "a", Subject = $"msg-{i}" });

        var limited = mb.GetAll(limit: 3);
        Assert.Equal(3, limited.Count);
    }
}
