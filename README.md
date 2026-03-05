# SharpClaw

A C# / .NET 8 autonomous AI agent inspired by [OpenClaw](https://github.com/openclaw/openclaw), connecting to a local **llama.cpp server** for inference with tool calling. Includes a **screen monitoring subsystem** that continuously captures and analyzes your screen, acting as a productivity assistant that watches over your shoulder and can do work for you.

## Prerequisites

- .NET 8.0 SDK or later
- [llama.cpp](https://github.com/ggml-org/llama.cpp) server running with `--jinja` flag for tool calling support
- A tool-calling capable model (Qwen2.5, Llama 3.x, Hermes, Mistral Nemo, etc.)
- For screen monitoring: `grim` (Wayland) or `scrot` (X11)
- For vision analysis: a multimodal model (Qwen2.5-VL, LLaVA, etc.)

## Quick Start

### 1. Start llama-server

```bash
llama-server --jinja -fa -hf bartowski/Qwen2.5-7B-Instruct-GGUF:Q4_K_M
```

### 2. Build and run SharpClaw

```bash
cd src/SharpClaw
dotnet run
```

### 3. Chat

```
you> What files are in the current directory?
sharpclaw> [Tool: list_directory(...)]
...

you> Write a hello world script in Python
sharpclaw> [Tool: write_file(...)]
...
```

## Configuration

SharpClaw uses `~/.sharpclaw/sharpclaw.json`:

```json
{
  "llamaCppUrl": "http://localhost:8080",
  "model": "qwen2.5",
  "visionModel": "qwen2.5-vl",
  "visionEndpoint": "http://localhost:8080",
  "workspace": "~/.sharpclaw/workspace",
  "maxIterations": 20,
  "screen": {
    "enabled": true,
    "captureIntervalSeconds": 10,
    "maxObservations": 50,
    "captureCommand": "auto"
  }
}
```

| Field | Description |
|-------|-------------|
| `llamaCppUrl` | Base URL for the llama.cpp server |
| `model` | Model name for text/tool calling |
| `visionModel` | Model name for screen analysis (vision) |
| `visionEndpoint` | Base URL for the vision model server |
| `workspace` | Agent workspace directory |
| `maxIterations` | Max reasoning loop iterations per turn |
| `screen.enabled` | Enable/disable screen monitoring |
| `screen.captureIntervalSeconds` | Screenshot interval |
| `screen.maxObservations` | Ring buffer size for observations |
| `screen.captureCommand` | `"auto"`, `"grim"`, `"scrot"`, or custom command |

## Commands

| Command | Description |
|---------|-------------|
| `/new` | Start a new conversation session |
| `/sessions` | List saved sessions |
| `/load <id>` | Load a saved session |
| `/screen on\|off` | Toggle screen monitoring |
| `/screen status` | Show screen monitoring status |
| `/info` | Show current session info |
| `/help` | Show available commands |
| `/quit` | Exit |

## Tools

SharpClaw provides these tools to the LLM:

| Tool | Description |
|------|-------------|
| `read_file` | Read file contents |
| `write_file` | Write content to a file |
| `shell_exec` | Execute a shell command |
| `list_directory` | List directory contents |
| `query_screen_activity` | Query recent screen observations |

## Bootstrap Files

Place these files in `~/.sharpclaw/workspace/` to customize agent behavior:

- `SOUL.md` — Agent personality and values
- `IDENTITY.md` — Agent name and description
- `USER.md` — User profile
- `TOOLS.md` — Tool usage conventions
- `AGENTS.md` — Operating instructions and memory

## Architecture

SharpClaw re-implements OpenClaw's core patterns in idiomatic C#:

- **Reasoning loop** — Calls LLM, parses tool calls, executes them, appends results, loops until final response
- **Bootstrap files** — Markdown files injected into system prompt on session start
- **Tool system** — Pluggable tools with JSON schema definitions
- **Session persistence** — JSONL conversation transcripts
- **Screen monitoring** — Background capture + vision model analysis + observation ring buffer

## License

MIT
