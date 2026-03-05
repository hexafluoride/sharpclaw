using System.Text.Json;
using SharpClaw.Tools;

namespace SharpClaw.Tests;

public class FileToolSecurityTests : IDisposable
{
    private readonly string _workspace;

    public FileToolSecurityTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"sharpclaw-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        File.WriteAllText(Path.Combine(_workspace, "allowed.txt"), "safe content");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    // ── ReadFileTool ────────────────────────────────────────────────

    [Fact]
    public async Task ReadFile_RelativePath_Works()
    {
        var tool = new ReadFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "allowed.txt" }));
        Assert.Equal("safe content", result);
    }

    [Fact]
    public async Task ReadFile_AbsolutePathOutsideWorkspace_Blocked()
    {
        var tool = new ReadFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "/etc/hostname" }));
        Assert.Contains("must be within workspace", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_DotDotTraversal_Blocked()
    {
        var tool = new ReadFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "../../../etc/passwd" }));
        Assert.Contains("must be within workspace", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_RelativePathWithinWorkspace_Works()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "sub"));
        File.WriteAllText(Path.Combine(_workspace, "sub", "nested.txt"), "nested");
        var tool = new ReadFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "sub/nested.txt" }));
        Assert.Equal("nested", result);
    }

    [Fact]
    public async Task ReadFile_DotDotStillInsideWorkspace_Works()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "a", "b"));
        File.WriteAllText(Path.Combine(_workspace, "a", "target.txt"), "ok");
        var tool = new ReadFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "a/b/../target.txt" }));
        Assert.Equal("ok", result);
    }

    // ── WriteFileTool ───────────────────────────────────────────────

    [Fact]
    public async Task WriteFile_RelativePath_Works()
    {
        var tool = new WriteFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "new.txt", content = "hello" }));
        Assert.Contains("Successfully wrote", result);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_workspace, "new.txt")));
    }

    [Fact]
    public async Task WriteFile_AbsolutePathOutsideWorkspace_Blocked()
    {
        var tool = new WriteFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "/tmp/evil.txt", content = "pwned" }));
        Assert.Contains("must be within workspace", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteFile_DotDotTraversal_Blocked()
    {
        var tool = new WriteFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "../../evil.txt", content = "pwned" }));
        Assert.Contains("must be within workspace", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteFile_CreatesSubdirectories()
    {
        var tool = new WriteFileTool(_workspace);
        var result = await tool.ExecuteAsync(Args(new { path = "deep/nested/file.txt", content = "deep" }));
        Assert.Contains("Successfully wrote", result);
        Assert.True(File.Exists(Path.Combine(_workspace, "deep", "nested", "file.txt")));
    }
}
