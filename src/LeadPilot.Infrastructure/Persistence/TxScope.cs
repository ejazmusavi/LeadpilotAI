using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>
/// A transaction that joins one already open on the same context. Services that need atomicity use it instead of BeginTransaction, so a caller (such as the
/// industry pack importer) can wrap several of them in ONE transaction: the inner commit does nothing, the outer owner commits or rolls back everything.
/// </summary>
internal sealed class TxScope : IAsyncDisposable
{
    private readonly IDbContextTransaction? _own;
    private TxScope(IDbContextTransaction? own) => _own = own;

    public static async Task<TxScope> BeginAsync(AppDbContext db, CancellationToken ct) =>
        new(db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null);

    public Task CommitAsync(CancellationToken ct = default) => _own?.CommitAsync(ct) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => _own?.DisposeAsync() ?? ValueTask.CompletedTask;
}
