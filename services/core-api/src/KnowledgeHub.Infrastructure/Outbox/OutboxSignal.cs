using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using KnowledgeHub.Domain;
using System.Runtime.CompilerServices;

namespace KnowledgeHub.Infrastructure.Outbox;

/// <summary>An in-memory nudge so the worker runs right after a commit instead of waiting for its next poll.</summary>
public sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>Waits for a nudge or the timeout, whichever comes first.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);

    public void Dispose() => _semaphore.Dispose();
}

/// <summary>After a save that included outbox rows has committed, wake the worker.</summary>
public sealed class OutboxSignalInterceptor(OutboxSignal signal) : SaveChangesInterceptor
{
    private sealed class Flag { public bool Value; }
    private static readonly ConditionalWeakTable<DbContext, Flag> s_pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Mark(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Mark(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Fire(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Fire(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Clear(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Clear(eventData.Context);
        return Task.CompletedTask;
    }

    private static void Mark(DbContext? context)
    {
        if (context is null || !context.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added)) return;
        s_pending.GetOrCreateValue(context).Value = true;
    }

    private void Fire(DbContext? context)
    {
        if (context is null || !s_pending.TryGetValue(context, out var flag) || !flag.Value) return;
        flag.Value = false;
        // Inside an explicit transaction the rows are not committed yet; the worker's poll covers that gap, and the
        // nudge still helps for the common case of a plain save.
        signal.Notify();
    }

    private static void Clear(DbContext? context)
    {
        if (context is not null && s_pending.TryGetValue(context, out var flag)) flag.Value = false;
    }
}
