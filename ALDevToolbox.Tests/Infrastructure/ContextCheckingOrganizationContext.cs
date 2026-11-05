using ALDevToolbox.Services;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Passes every read through to an inner <see cref="IOrganizationContext"/>, noting
/// each one made with no synchronization context: a continuation that left the
/// caller's. Run a service under <c>Dispatcher.CreateDefault().InvokeAsync</c> with
/// this as its context (and as the tenant filter's, via
/// <see cref="TestDb.NewContext(IOrganizationContext)"/>) to check it reads the
/// signed-in user where a server render would (#1724, #1729).
/// </summary>
internal sealed class ContextCheckingOrganizationContext(IOrganizationContext inner) : IOrganizationContext
{
    private int _reads;
    public int Reads => _reads;
    public System.Collections.Concurrent.ConcurrentQueue<string> OffContextReads { get; } = new();

    private T Read<T>(T value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        Interlocked.Increment(ref _reads);
        if (SynchronizationContext.Current is null) OffContextReads.Enqueue(name);
        return value;
    }

    public int? CurrentOrganizationId => Read(inner.CurrentOrganizationId);
    public int? CurrentUserId => Read(inner.CurrentUserId);
    public bool IsSiteAdmin => Read(inner.IsSiteAdmin);
    public bool IsSystemOrganization => Read(inner.IsSystemOrganization);
    public int OrganizationIdForFilter => Read(inner.OrganizationIdForFilter);
}
