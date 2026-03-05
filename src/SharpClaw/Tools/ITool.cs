using System.Text.Json;
using SharpClaw.LLM.Models;

namespace SharpClaw.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default);

    ToolDefinition ToDefinition() => new()
    {
        Type = "function",
        Function = new FunctionDefinition
        {
            Name = Name,
            Description = Description,
            Parameters = ParameterSchema
        }
    };
}
