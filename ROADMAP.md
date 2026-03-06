# SharpClaw Roadmap

## Current State (v0.1)

- Core agentic reasoning loop with tool calling against llama.cpp
- Rich TUI with streaming markdown, spinner, prompt history, tab completion
- Screen monitoring daemon with vision model analysis, AFK dedup, pixel similarity
- ActivityWatch integration for app/web/AFK enrichment
- Sub-agents (long-running scheduled + one-shot tasks) with mailbox
- Browser automation via Playwright with persistent sessions
- Persistent memory with compaction and archival
- Session persistence, context window management, retry logic
- 73 unit tests covering core components

---

## Near-Term

### Multi-Surface / Messaging Protocols

Expose SharpClaw beyond the TUI so it can be reached from anywhere.

- **Matrix / Element** — bridge SharpClaw as a Matrix bot. The user messages the agent in a room; tool results, observations, and sub-agent updates appear as messages. Leverages Matrix's E2EE and federation.
- **IRC** — lightweight adapter for IRC channels/DMs. Good for headless servers.
- **Discord** — bot integration for personal servers. Slash commands map to SharpClaw tools.
- **XMPP / Jabber** — for self-hosted setups.
- **Email** — periodic digest or on-demand query via IMAP/SMTP. Agent can check inbox, draft replies.
- **REST / WebSocket API** — expose the agent as an HTTP service with streaming responses, enabling custom frontends, mobile apps, or integration with other automation.
- **Unix socket / named pipe** — lightweight IPC for local scripts to query the agent.

Architecture: extract the core agent loop from `Program.cs` into a `SharpClawHost` that multiple frontends can attach to. Each surface adapter translates messages to/from the agent and renders tool calls appropriately for its medium.

### Ambient Agent Mode

Make the core agent loop proactive rather than purely reactive.

- **Periodic check-in** — the agent wakes on a schedule (e.g. every 15 minutes), reviews recent screen observations, ActivityWatch data, mailbox, and memory, then decides if there's anything useful to surface: reminders, summaries, suggestions, or warnings.
- **Trigger-based activation** — fire the agent when specific events occur:
  - Screen idle for N minutes → summarize what you were working on
  - New mailbox message from a sub-agent → synthesize and notify
  - ActivityWatch detects a context switch → offer relevant notes from memory
  - Time-of-day triggers → morning briefing, end-of-day summary
- **Proactive observations** — instead of waiting to be asked, the agent can:
  - Notice you've been on the same page for a long time and offer help
  - Detect repetitive actions and suggest automation
  - Cross-reference screen activity with calendar/tasks
  - Flag when you've been AFK for unusually long
- **Notification routing** — ambient insights delivered via the active surface (TUI notification, Matrix message, desktop notification via `notify-send`, etc.)
- **Attention budget** — configurable limit on how often the agent can interrupt. Priority system so it only surfaces genuinely useful things.

### Quality of Life

- **Model hot-swap** — switch models mid-session without restarting
- **Multi-model routing** — use different models for different tasks (fast model for simple tool calls, large model for complex reasoning, vision model for screenshots)
- **Token usage tracking** — actual token counts from server response headers instead of estimation
- **Conversation branching** — fork a session to explore alternatives
- **Export** — export sessions/memory/observations to markdown, HTML, or PDF

---

## Medium-Term

### Agent Ecosystem

- **Agent templates** — predefined agent configs for common roles (research assistant, code reviewer, news monitor, email drafter)
- **Agent-to-agent communication** — agents can message each other directly, not just via mailbox
- **Shared memory** — agents can read/write to shared knowledge bases
- **Agent marketplace** — share and import agent configurations

### Deeper Integrations

- **Calendar** — read/write Google Calendar or CalDAV. Time-aware context.
- **Task managers** — Todoist, Taskwarrior, org-mode. Agent can manage tasks.
- **Git** — aware of repo state, can create commits, PRs, review diffs
- **Clipboard** — monitor clipboard for context
- **Audio** — speech-to-text input, text-to-speech output
- **Notifications** — read/dismiss desktop notifications, react to them

### Infrastructure

- **Structured logging with OpenTelemetry** — traces for the full reasoning chain
- **Agent observability dashboard** — web UI showing agent activity, token usage, tool call patterns
- **Config hot-reload** — watch `sharpclaw.json` for changes
- **Plugin system** — load tools from external assemblies at runtime
- **Sandboxed execution** — run shell commands in containers

---

## Long-Term

### Distributed

- **Remote agents** — agents running on different machines coordinating via network
- **Federated memory** — shared knowledge across instances
- **Multi-user** — multiple users interacting with the same agent ecosystem with access control

### Intelligence

- **RAG** — vector store over memory, observations, and files for retrieval-augmented generation
- **Learning from corrections** — when the user corrects the agent, update behavior preferences
- **Plan-and-execute** — explicit planning step before complex multi-tool tasks
- **Self-reflection** — agent evaluates its own outputs and retries if quality is low

### Platform

- **Mobile companion** — lightweight app that talks to SharpClaw over the REST API
- **Desktop widget** — persistent overlay showing ambient observations and quick actions
- **Voice interface** — always-listening mode with wake word
