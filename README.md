# SharpClaw

An autonomous AI agent framework in C# / .NET 9, inspired by [OpenClaw](https://github.com/openclaw/openclaw). Runs against a local **llama.cpp** server with tool calling, continuously observes your screen and activity, manages persistent sub-agents, drives a headless browser, and integrates with **ActivityWatch** — all from a rich terminal UI.

## Features

- **Agentic reasoning loop** — iterates LLM → tool calls → results until done, with automatic context compaction
- **Screen monitoring** — periodic screenshot capture + vision model analysis, with AFK-aware dedup
- **Background daemon** — `systemd` user service captures screens even when the TUI is closed
- **ActivityWatch integration** — enriches observations with active app, window title, URL, AFK status; queryable by agents
- **Sub-agents** — spin off long-running scheduled agents or one-shot task agents, each with their own memory and tool access
- **Browser automation** — headless Chromium via Playwright with persistent login sessions
- **Persistent memory** — per-agent and global `MEMORY.md` with automatic compaction and archival
- **Mailbox** — inter-agent message passing with read/unread tracking
- **Rich TUI** — streaming markdown, gradient spinner, tab completion, prompt history, live notifications

## Prerequisites

- .NET 9.0 SDK
- [llama.cpp](https://github.com/ggml-org/llama.cpp) server running with `--jinja` for tool-call support
- A tool-calling model (Qwen 2.5, Llama 3.x, Mistral, etc.)
- For screen monitoring: `grim` (Wayland) or `scrot` (X11)
- For vision analysis: a multimodal model (Qwen2.5-VL, LLaVA, etc.)
- For browser tool: Chromium (auto-installed by Playwright on first use)
- Optional: [ActivityWatch](https://activitywatch.net/) for app/web/AFK tracking

## Quick Start

### 1. Start llama-server

```bash
llama-server --jinja -fa -hf bartowski/Qwen2.5-7B-Instruct-GGUF:Q4_K_M
```

### 2. Build and run

```bash
cd src/SharpClaw
dotnet run
```

### 3. Chat

```
you❯ What files are in the current directory?
◆ [list_directory] path="."
  ...

you❯ Research the latest news about AI and write a summary
◆ [browser] action="navigate" url="https://..."
  ...
```

## Screen Daemon

The screen daemon captures and analyzes screenshots in the background, even when the TUI isn't running. When ActivityWatch is available, it enriches each observation with the active app, window title, current URL, and AFK status. Captures are skipped entirely when you're AFK.

```bash
# Install as a systemd user service (starts on login)
bash contrib/install-daemon.sh

# Or run manually
dotnet run -- --daemon

# Management
systemctl --user status sharpclaw-screen
systemctl --user stop sharpclaw-screen
journalctl --user -u sharpclaw-screen -f
```

The TUI automatically detects a running daemon and avoids duplicate captures.

## Tools

| Tool | Description |
|------|-------------|
| `read_file` | Read file contents (workspace-sandboxed) |
| `write_file` | Write/create files (workspace-sandboxed) |
| `list_directory` | List directory tree, optional recursive |
| `shell_exec` | Execute shell commands with configurable timeout |
| `take_screenshot` | Capture and analyze the screen on demand |
| `query_screen_activity` | Query recent or archived screen observations |
| `activity_watch` | Query ActivityWatch — app usage, web history, AFK, custom queries |
| `memory` | Read/append/replace long-term agent memory |
| `browser` | Drive headless Chromium — navigate, click, type, JS, tabs, cookies |
| `agents` | Spawn/manage sub-agents, read mailbox |

## Sub-Agents

SharpClaw can spawn autonomous sub-agents that run independently:

**Long-running agents** have a schedule (`every 5m`, `every 2h`, `daily 09:00`), persistent working memory, and run indefinitely. They post results to the mailbox.

**Task agents** run once to completion with a focused purpose, then post their results to the mailbox.

Both types have scoped tool access, their own conversation history, and survive process restarts.

## Commands

| Command | Description |
|---------|-------------|
| `/new` | Start a new session |
| `/sessions` | List saved sessions |
| `/load <id>` | Load a session |
| `/screen [on\|off\|status]` | Toggle screen monitoring / check daemon |
| `/history [n]` | Screen observation log |
| `/activity [web] [hours]` | ActivityWatch summary |
| `/tools` | List available tools |
| `/tool <name> <json>` | Invoke a tool directly |
| `/agents` | List sub-agents and status |
| `/mailbox` | View mailbox messages |
| `/info` | Dashboard |
| `/help` | Help |
| `/quit` | Exit |

## Configuration

`~/.sharpclaw/sharpclaw.json`:

```json
{
  "llamaCppUrl": "http://localhost:8080",
  "model": "qwen2.5",
  "visionModel": "qwen2.5-vl",
  "visionEndpoint": "http://localhost:8080",
  "workspace": "~/.sharpclaw/workspace",
  "maxIterations": 25,
  "maxContextTokens": 120000,
  "screen": {
    "enabled": true,
    "captureIntervalSeconds": 10,
    "maxObservations": 50,
    "captureCommand": "auto"
  },
  "activityWatch": {
    "enabled": true,
    "url": "http://localhost:5600",
    "timeoutSeconds": 10
  },
  "browser": {
    "headless": true,
    "viewportWidth": 1280,
    "viewportHeight": 720
  }
}
```

<details>
<summary>Full configuration reference</summary>

| Field | Default | Description |
|-------|---------|-------------|
| `llamaCppUrl` | `http://localhost:8080` | llama.cpp server URL |
| `model` | `qwen2.5` | Text/tool-calling model |
| `visionModel` | `qwen2.5-vl` | Vision model for screenshots |
| `visionEndpoint` | `http://localhost:8080` | Vision model server URL |
| `workspace` | `~/.sharpclaw/workspace` | Agent workspace directory |
| `maxIterations` | `25` | Max reasoning loop iterations per turn |
| `maxContextTokens` | `120000` | Context window budget (triggers compaction at 80%) |
| `maxCompletionTokens` | `null` | Max tokens per completion (null = model default) |
| `shellTimeoutSeconds` | `30` | Default shell command timeout |
| `memoryCompactionThreshold` | `4000` | Memory size (chars) before LLM compaction |
| `subAgentHistoryMessages` | `20` | Messages to reload for sub-agent context |
| `maxMailboxMessages` | `500` | Mailbox cap (oldest evicted) |
| `maxNotifications` | `200` | Notification queue cap |
| `llmRetryCount` | `3` | Retry attempts for transient LLM failures |
| `llmRetryBaseDelayMs` | `1000` | Base delay for exponential backoff |
| `screen.enabled` | `true` | Enable screen monitoring |
| `screen.captureIntervalSeconds` | `10` | Seconds between captures |
| `screen.maxObservations` | `50` | In-memory ring buffer size |
| `screen.captureCommand` | `auto` | `auto`, `grim`, `scrot`, or custom `cmd {output}` |
| `activityWatch.enabled` | `true` | Enable ActivityWatch integration |
| `activityWatch.url` | `http://localhost:5600` | AW server URL |
| `activityWatch.timeoutSeconds` | `10` | AW request timeout |
| `browser.headless` | `true` | Run Chromium headless |
| `browser.viewportWidth` | `1280` | Browser viewport width |
| `browser.viewportHeight` | `720` | Browser viewport height |

</details>

## Bootstrap Files

Place these in `~/.sharpclaw/workspace/` to customize agent behavior:

| File | Purpose |
|------|---------|
| `SOUL.md` | Agent personality and values |
| `IDENTITY.md` | Agent name and role |
| `USER.md` | User profile and preferences |
| `TOOLS.md` | Tool usage conventions |
| `AGENTS.md` | Operating instructions |
| `MEMORY.md` | Persistent long-term memory (managed by agent) |

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                    TUI (Program.cs)                  │
│  MdStream · Spinner · Prompt History · Notifications │
└──────────────────────┬──────────────────────────────┘
                       │
              ┌────────▼────────┐
              │  AgentRuntime   │◄──── Bootstrap Files
              │  Reasoning Loop │      System Prompt
              └────────┬────────┘
                       │
              ┌────────▼────────┐
              │  LlamaCppClient │──── llama.cpp server
              │  Stream + Retry │     (OpenAI-compat API)
              └────────┬────────┘
                       │
              ┌────────▼────────┐
              │  ToolRegistry   │
              └────────┬────────┘
                       │
    ┌──────┬──────┬────┴────┬──────┬──────┬──────┐
    │ File │Shell │ Screen  │Memory│Browse│Agents│
    │Tools │ Exec │Query/Cap│ Tool │ Tool │ Tool │
    └──────┴──────┴────┬────┴──────┴──────┴───┬──┘
                       │                      │
              ┌────────▼────────┐    ┌────────▼────────┐
              │ ScreenAnalyzer  │    │ AgentScheduler   │
              │ + AW Enrichment │    │ SubAgentRunner   │
              └────────┬────────┘    │ Mailbox · Notifs │
                       │             └──────────────────┘
              ┌────────▼────────┐
              │  Screen Daemon  │
              │  (systemd svc)  │
              └─────────────────┘
```

## Data Layout

```
~/.sharpclaw/
├── sharpclaw.json           # Configuration
├── screen-history.jsonl     # Screen observations
├── screen-daemon.pid        # Daemon PID file
├── mailbox.jsonl            # Inter-agent messages
├── prompt-history.txt       # REPL input history
├── sessions/                # Conversation transcripts (JSONL)
├── archive/                 # Compacted history snapshots
├── agents/                  # Sub-agent state and history
├── browser-data/            # Persistent Chromium profile
└── workspace/               # Agent working directory
    ├── SOUL.md
    ├── IDENTITY.md
    ├── MEMORY.md
    └── ...
```

## Testing

```bash
dotnet test
```

73 tests covering tool registry, session persistence, mailbox, notifications, file security (path traversal), schedule parsing, and LLM DTOs.

## License

MIT
