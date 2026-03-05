using System.Text.Json;
using SharpClaw.LLM.Models;

namespace SharpClaw.Tools;

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public ITool? Get(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public List<ToolDefinition> GetDefinitions()
    {
        return _tools.Values.Select(t => t.ToDefinition()).ToList();
    }

    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        var tool = Get(name.Trim());
        if (tool is null)
            return $"Error: Unknown tool '{name}'. Available tools: {string.Join(", ", _tools.Keys)}";

        try
        {
            // Treat empty/whitespace arguments as empty object
            var jsonStr = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim();
            var args = JsonDocument.Parse(jsonStr).RootElement;
            return await tool.ExecuteAsync(args, ct);
        }
        catch (JsonException)
        {
            return $"Error: Invalid JSON arguments for tool '{name}': {argumentsJson}";
        }
        catch (Exception ex)
        {
            return $"Error executing tool '{name}': {ex.Message}";
        }
    }

    public IReadOnlyCollection<string> ToolNames => _tools.Keys;
    public IEnumerable<ITool> All => _tools.Values;
}
