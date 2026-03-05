using System.Text;

namespace SharpClaw.Agent;

/// <summary>
/// Loads bootstrap markdown files from the workspace directory and builds a system prompt.
/// Mirrors OpenClaw's resolveBootstrapContextForRun() behavior: each file is injected
/// into the system prompt, missing files are skipped with a note, and large files are trimmed.
/// </summary>
public static class BootstrapLoader
{
    private static readonly string[] BootstrapFiles =
    [
        "SOUL.md",
        "IDENTITY.md",
        "USER.md",
        "TOOLS.md",
        "AGENTS.md",
        "MEMORY.md"
    ];

    private const int MaxFileChars = 8000;

    public static string BuildSystemPrompt(string workspaceDir)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are SharpClaw, an autonomous AI agent inspired by OpenClaw.");
        sb.AppendLine("You have access to tools that let you read/write files, execute shell commands, list directories, take screenshots, and query screen activity.");
        sb.AppendLine("Use tools to accomplish tasks. You can make multiple tool calls in sequence.");
        sb.AppendLine("When you have completed a task or answered a question, respond with your final answer.");
        sb.AppendLine();
        sb.AppendLine("## Long-term memory");
        sb.AppendLine("You have a persistent memory that survives across sessions and restarts.");
        sb.AppendLine("Use the 'memory' tool to save important facts you learn about the user: their name,");
        sb.AppendLine("preferences, projects they're working on, habits, tools they use, or anything else");
        sb.AppendLine("that would help you assist them better in future conversations.");
        sb.AppendLine("Your current memory contents (if any) are included below in MEMORY.md.");
        sb.AppendLine("Proactively save things you learn — don't wait to be asked.");
        sb.AppendLine();
        sb.AppendLine("## Screen monitoring");
        sb.AppendLine("You can see the user's screen. Use 'take_screenshot' to look at their screen right now,");
        sb.AppendLine("or 'query_screen_activity' to review past screen observations.");
        sb.AppendLine("Screen observations persist across restarts so you can review past activity.");
        sb.AppendLine();

        var loadedAny = false;
        foreach (var filename in BootstrapFiles)
        {
            var path = Path.Combine(workspaceDir, filename);
            if (!File.Exists(path)) continue;

            var content = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(content)) continue;

            loadedAny = true;
            if (content.Length > MaxFileChars)
            {
                content = content[..MaxFileChars] + "\n[... truncated, read the file for full content]";
            }

            sb.AppendLine($"--- {filename} ---");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        if (!loadedAny)
        {
            sb.AppendLine("No bootstrap files found in workspace. You can create SOUL.md, IDENTITY.md, USER.md, TOOLS.md, or AGENTS.md in the workspace to customize agent behavior.");
        }

        var runtimeInfo = BuildRuntimeInfo();
        sb.AppendLine(runtimeInfo);

        return sb.ToString().TrimEnd();
    }

    private static string BuildRuntimeInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Runtime Info ---");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine($"User: {Environment.UserName}");
        sb.AppendLine($"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Working directory: {Environment.CurrentDirectory}");
        return sb.ToString();
    }

    public static void CreateDefaultBootstrapFiles(string workspaceDir)
    {
        Directory.CreateDirectory(workspaceDir);

        var soulPath = Path.Combine(workspaceDir, "SOUL.md");
        if (!File.Exists(soulPath))
        {
            File.WriteAllText(soulPath, """
                # Soul

                You are a helpful, thoughtful AI productivity assistant. You watch over the user's shoulder,
                understand what they're working on, and help them be more productive.

                ## Core values
                - Be genuinely helpful, not just compliant
                - Respect the user's privacy and autonomy
                - Be concise and actionable
                - Proactively offer relevant assistance when you notice the user could benefit

                ## Communication style
                - Direct and clear
                - Use technical language when appropriate
                - Keep responses focused and avoid unnecessary verbosity
                """);
        }

        var identityPath = Path.Combine(workspaceDir, "IDENTITY.md");
        if (!File.Exists(identityPath))
        {
            File.WriteAllText(identityPath, """
                # Identity

                Name: SharpClaw
                Description: A local AI productivity assistant that monitors your screen and helps you work.
                """);
        }
    }
}
