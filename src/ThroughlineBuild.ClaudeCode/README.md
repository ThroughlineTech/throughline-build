# ThroughlineBuild.ClaudeCode

Reusable facade for running Claude Code from .NET without the legacy
`claude --print` path. The library delegates to the existing
`ThroughlineBuild.Workers.ClaudeCode` transport: Windows runs under ConPTY,
Unix-like hosts run under a PTY, and completion is recovered from Claude Code's
persisted transcript.

```csharp
using ThroughlineBuild.ClaudeCode;

var client = new ClaudeCodeClient(new ClaudeCodeClientOptions
{
    Transport = ClaudeCodeTransport.InteractiveHook,
    ExecutablePath = "claude",
});

var result = await client.RunAsync(
    "Update the repository README with the new setup instructions.",
    workingDirectory,
    new ClaudeCodeRunOptions
    {
        Timeout = TimeSpan.FromMinutes(20),
        AllowedTools = ["Read", "Edit", "Write", "Bash"],
    },
    cancellationToken);
```

The string overload appends a `WORKER_RESULT` final-output contract when the
instruction does not already include one. Advanced callers can pass a complete
`Brief` directly.

## Interactive host notes

`ClaudeCodeClientOptions` defaults to `Transport = InteractiveHook` and
`EnableStopHook = false`. This is intentional for embedded library use:
transcript-based turn detection is the completion path, while Claude's Stop hook
is only a best-effort fast path. Hosts that want the hook can expose
`ClaudeStopHookBridge.RunAsync` from their own executable and set
`StopHookCommandPrefix`.

The interactive transport requires Claude Code `>= 2.1.177` on Windows, Linux,
or macOS. Call `CheckAsync` during startup to report capability problems before a
run creates worktree state.
