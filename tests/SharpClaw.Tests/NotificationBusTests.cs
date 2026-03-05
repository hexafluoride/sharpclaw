using SharpClaw.Agents;

namespace SharpClaw.Tests;

public class NotificationBusTests
{
    [Fact]
    public void Post_And_Dequeue()
    {
        var bus = new NotificationBus();
        bus.Post("agent1", "hello");

        Assert.True(bus.HasPending);
        Assert.True(bus.TryDequeue(out var n));
        Assert.Equal("agent1", n!.AgentName);
        Assert.Equal("hello", n.Message);
        Assert.False(bus.HasPending);
    }

    [Fact]
    public void TryDequeue_Empty_ReturnsFalse()
    {
        var bus = new NotificationBus();
        Assert.False(bus.TryDequeue(out _));
    }

    [Fact]
    public void Preserves_Order()
    {
        var bus = new NotificationBus();
        bus.Post("a", "first");
        bus.Post("b", "second");
        bus.Post("c", "third");

        bus.TryDequeue(out var n1);
        bus.TryDequeue(out var n2);
        bus.TryDequeue(out var n3);

        Assert.Equal("first", n1!.Message);
        Assert.Equal("second", n2!.Message);
        Assert.Equal("third", n3!.Message);
    }

    [Fact]
    public async Task ThreadSafe_ConcurrentPostAndDrain()
    {
        var bus = new NotificationBus();
        var count = 1000;
        var drained = 0;

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
                bus.Post("agent", $"msg-{i}");
        });

        var consumer = Task.Run(() =>
        {
            while (Interlocked.CompareExchange(ref drained, 0, 0) < count)
            {
                if (bus.TryDequeue(out _))
                    Interlocked.Increment(ref drained);
                else
                    Thread.SpinWait(10);
            }
        });

        await Task.WhenAll(producer, consumer);
        Assert.Equal(count, drained);
    }

    [Fact]
    public void BoundedSize_DropsOldest()
    {
        var bus = new NotificationBus(maxSize: 5);
        for (int i = 0; i < 10; i++)
            bus.Post("a", $"msg-{i}");

        var messages = new List<string>();
        while (bus.TryDequeue(out var n))
            messages.Add(n!.Message);

        Assert.Equal(5, messages.Count);
        Assert.Equal("msg-5", messages[0]);
        Assert.Equal("msg-9", messages[4]);
    }
}
