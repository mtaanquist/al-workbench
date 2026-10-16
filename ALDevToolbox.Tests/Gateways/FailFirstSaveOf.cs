using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Fails the first save that adds a <typeparamref name="TEntity"/>, once: the save after a
/// gateway command was queued in the same transaction, which is where a failure leaves the
/// rolled-back command still tracked unless the service clears it.
/// </summary>
internal sealed class FailFirstSaveOf<TEntity> : SaveChangesInterceptor where TEntity : class
{
    public bool Failed { get; private set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!Failed && eventData.Context!.ChangeTracker.Entries<TEntity>().Any(e => e.State == EntityState.Added))
        {
            Failed = true;
            throw new InvalidOperationException("The database went away.");
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
