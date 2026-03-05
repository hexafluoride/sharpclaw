using System.Net;
using System.Text;
using System.Text.Json;
using SharpClaw.LLM;
using SharpClaw.LLM.Models;

namespace SharpClaw.Tests;

public class LlamaCppClientTests
{
    [Fact]
    public void StreamChunk_IsText_WhenTextSet()
    {
        var chunk = new StreamChunk { Text = "hello" };
        Assert.True(chunk.IsText);
        Assert.False(chunk.IsToolCall);
    }

    [Fact]
    public void StreamChunk_IsToolCall_WhenToolCallsSet()
    {
        var chunk = new StreamChunk
        {
            ToolCalls = [new ToolCall { Function = new FunctionCall { Name = "test" } }]
        };
        Assert.True(chunk.IsToolCall);
        Assert.False(chunk.IsText);
    }

    [Fact]
    public void StreamChunk_Neither_WhenEmpty()
    {
        var chunk = new StreamChunk { };
        Assert.False(chunk.IsText);
        Assert.False(chunk.IsToolCall);
    }

    [Fact]
    public void ChatRequest_Serialization_OmitsNulls()
    {
        var request = new ChatRequest
        {
            Model = "test",
            Messages = [Message.User("hi")],
            Stream = true
        };

        var json = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("tools", json);
        Assert.DoesNotContain("temperature", json);
    }

    [Fact]
    public void ToolCall_ResolvedName_PrefersFunction()
    {
        var tc = new ToolCall
        {
            Function = new FunctionCall { Name = "from_function" },
            FlatName = "from_flat"
        };
        Assert.Equal("from_function", tc.ResolvedName);
    }

    [Fact]
    public void ToolCall_ResolvedName_FallsBackToFlat()
    {
        var tc = new ToolCall { FlatName = "flat_name" };
        Assert.Equal("flat_name", tc.ResolvedName);
    }

    [Fact]
    public void ToolCall_ResolvedArguments_PrefersFunction()
    {
        var tc = new ToolCall
        {
            Function = new FunctionCall { Arguments = "{\"a\":1}" },
            FlatArguments = "{\"b\":2}"
        };
        Assert.Equal("{\"a\":1}", tc.ResolvedArguments);
    }
}
