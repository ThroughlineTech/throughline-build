using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.JudgmentSlots;

namespace ThroughlineBuild.Commands;

public sealed class DeferCommand : ITicketCommand
{
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly IGitClient _git;
    private readonly ReasonTranslator _translator;
    private readonly WorktreeDecrufter _decrufter;
    private readonly string _mainWorktreePath;

    public DeferCommand(
        ITicketing ticketing,
        IEventSink events,
        IGitClient git,
        ReasonTranslator translator,
        WorktreeDecrufter decrufter,
        string mainWorktreePath)
    {
        _ticketing = ticketing;
        _events = events;
        _git = git;
        _translator = translator;
        _decrufter = decrufter;
        _mainWorktreePath = mainWorktreePath;
    }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        // (a) reason required
        if (!ctx.Args.TryGetValue("reason", out var reason) || string.IsNullOrWhiteSpace(reason))
            return new CommandResult(false, "reason is required");

        // (b) reject terminal states
        var ticket = await _ticketing.GetAsync(ctx.TicketId, ct).ConfigureAwait(false);
        if (ticket.State == TicketState.Done || ticket.State == TicketState.Cancelled)
            return new CommandResult(false, "already terminal");

        // (c) warn on unmerged branches matching the ticket pattern (fail-soft)
        var pattern = $"ticket/{ctx.TicketId.ToLowerInvariant()}-*";
        var unmerged = await _git.GetBranchesNotMergedAsync(pattern, "origin/main", ct).ConfigureAwait(false);
        if (unmerged.Count > 0)
        {
            Console.Error.WriteLine(
                $"WARNING: {unmerged.Count} unmerged branch(es) matching {pattern}: {string.Join(", ", unmerged)} - branch may be needed for reopen, leaving in place");
        }

        // (d) translate reason to English
        var translated = await _translator.TranslateAsync(reason, ct).ConfigureAwait(false);

        // (e) post deferred comment - marker string is load-bearing
        var body = $"<p><strong>deferred:</strong> {translated}</p>";
        await _ticketing.CreateCommentAsync(ctx.TicketId, body, ct).ConfigureAwait(false);
        await _events.EmitAsync(new WorkflowEvent(
            string.Empty,
            DateTimeOffset.UtcNow,
            EventKind.TicketWrite,
            ctx.TicketId,
            Phase.Command,
            new Dictionary<string, object>
            {
                ["action"] = "create_comment",
                ["detail"] = "deferred"
            }), ct).ConfigureAwait(false);

        // (f) transition to Cancelled
        await _ticketing.TransitionAsync(ctx.TicketId, TicketState.Cancelled, ct).ConfigureAwait(false);
        await _events.EmitAsync(new WorkflowEvent(
            string.Empty,
            DateTimeOffset.UtcNow,
            EventKind.StateTransition,
            ctx.TicketId,
            Phase.Command,
            new Dictionary<string, object>
            {
                ["from"] = ticket.State.ToString(),
                ["to"] = "Cancelled"
            }), ct).ConfigureAwait(false);

        // (g) attempt parent rollup (fail-soft)
        string rollupDetail = "success";
        try
        {
            await _ticketing.RollupParentAsync(ctx.TicketId, ct).ConfigureAwait(false);
        }
        catch
        {
            rollupDetail = "failure";
        }
        await _events.EmitAsync(new WorkflowEvent(
            string.Empty,
            DateTimeOffset.UtcNow,
            EventKind.TicketWrite,
            ctx.TicketId,
            Phase.Command,
            new Dictionary<string, object>
            {
                ["action"] = "rollup_attempt",
                ["detail"] = rollupDetail
            }), ct).ConfigureAwait(false);

        // (h) decruft worktree if present
        var worktreePath = Path.Combine(
            _mainWorktreePath,
            ".worktrees",
            $"ticket-{ctx.TicketId.ToLowerInvariant()}");
        if (Directory.Exists(worktreePath))
        {
            await _decrufter.DecruftAsync(worktreePath, _mainWorktreePath, ct).ConfigureAwait(false);
        }
        // TODO v1.1: rebuild rollup-preview for parent when defer removes this ticket from a feature wave

        // (i) success
        return new CommandResult(true, null);
    }
}
