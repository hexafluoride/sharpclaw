using System.Text.Json;
using SharpClaw.Tools;

namespace SharpClaw.Tests;

public class ToolRegistryTests
{
    private class FakeTool : ITool
    {
        public string Name { get; init; } = "fake";
        public string Description => "A fake tool";
        public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { input = new { type = "string" } }
        });

        public Func<JsonElement, Task<string>>? Handler { get; init; }

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
            => Handler != null ? Handler(arguments) : Task.FromResult("ok");
    }

    [Fact]
    public void Register_And_Get_ReturnsTool()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool { Name = "test_tool" };
        registry.Register(tool);

        var retrieved = registry.Get("test_tool");
        Assert.NotNull(retrieved);
        Assert.Same(tool, retrieved);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool { Name = "MyTool" });

        Assert.NotNull(registry.Get("mytool"));
        Assert.NotNull(registry.Get("MYTOOL"));
        Assert.NotNull(registry.Get("MyTool"));
    }

    [Fact]
    public void Get_UnknownTool_ReturnsNull()
    {
        var registry = new ToolRegistry();
        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsError()
    {
        var registry = new ToolRegistry();
        var result = await registry.ExecuteAsync("nonexistent", "{}");
        Assert.StartsWith("Error: Unknown tool", result);
        Assert.Contains("nonexistent", result);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool());

        var result = await registry.ExecuteAsync("fake", "not json");
        Assert.StartsWith("Error: Invalid JSON", result);
    }

    [Fact]
    public async Task ExecuteAsync_ToolThrows_ReturnsError()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool
        {
            Handler = _ => throw new InvalidOperationException("boom")
        });

        var result = await registry.ExecuteAsync("fake", "{}");
        Assert.StartsWith("Error executing tool", result);
        Assert.Contains("boom", result);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArgs_TreatedAsEmptyObject()
    {
        var registry = new ToolRegistry();
        var received = "";
        registry.Register(new FakeTool
        {
            Handler = args => { received = args.ToString(); return Task.FromResult("ok"); }
        });

        await registry.ExecuteAsync("fake", "  ");
        Assert.Equal("{}", received);
    }

    [Fact]
    public void GetDefinitions_ReturnsAllRegistered()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool { Name = "a" });
        registry.Register(new FakeTool { Name = "b" });

        var defs = registry.GetDefinitions();
        Assert.Equal(2, defs.Count);
    }

    [Fact]
    public void All_EnumeratesRegisteredTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool { Name = "x" });
        registry.Register(new FakeTool { Name = "y" });

        Assert.Equal(2, registry.All.Count());
    }
}
