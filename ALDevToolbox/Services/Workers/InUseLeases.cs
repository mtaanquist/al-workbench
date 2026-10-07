namespace ALDevToolbox.Services.Workers;

/// <summary>
/// What jobs running side by side are using right now, counted per key, so a tidy-up
/// never deletes something from under one of them (#1137): an AL compiler a build is
/// running, a cached artifact set a build is reading. In memory only, which is enough
/// because the app runs as one instance.
///
/// <para>
/// Taking a hold and a tidy-up both run under one lock (<see cref="TryHold"/> and
/// <see cref="Locked"/>), so a hold either finds the thing gone or keeps it.
/// </para>
/// </summary>
public sealed class InUseLeases<TKey> where TKey : notnull
{
    private readonly object _lock = new();
    private readonly Dictionary<TKey, int> _held;

    public InUseLeases(IEqualityComparer<TKey>? comparer = null) => _held = new(comparer);

    /// <summary>Holds <paramref name="key"/> until the result is disposed.</summary>
    public InUseLease<TValue> Hold<TValue>(TKey key, TValue value, Action? discard = null)
    {
        lock (_lock) Add(key);
        return new InUseLease<TValue>(value, () => Release(key), discard);
    }

    /// <summary>
    /// Holds <paramref name="key"/> only if <paramref name="available"/> says it is still
    /// there, asked under the lock a tidy-up takes. Null when it is gone.
    /// </summary>
    public InUseLease<TValue>? TryHold<TValue>(TKey key, Func<bool> available, TValue value, Action? discard = null)
    {
        lock (_lock)
        {
            if (!available()) return null;
            Add(key);
        }
        return new InUseLease<TValue>(value, () => Release(key), discard);
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the lock holds are taken with, passing it whether
    /// a key is held: for a tidy-up that must skip whatever is in use.
    /// </summary>
    public void Locked(Action<Func<TKey, bool>> work)
    {
        lock (_lock) work(_held.ContainsKey);
    }

    /// <summary>Lets go of one hold on <paramref name="key"/>. A key nobody holds is left as it is.</summary>
    public void Release(TKey key)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(key, out var count)) return;
            if (count <= 1) _held.Remove(key);
            else _held[key] = count - 1;
        }
    }

    private void Add(TKey key) => _held[key] = _held.GetValueOrDefault(key) + 1;
}

/// <summary>
/// A job's hold on something <see cref="InUseLeases{TKey}"/> keeps from being tidied
/// away: <see cref="Value"/> stays usable until the lease is disposed.
/// </summary>
public sealed class InUseLease<T> : IDisposable
{
    private Action? _release;
    private readonly Action? _discard;

    /// <param name="release">Runs once, on the first <see cref="Dispose"/> or <see cref="Discard"/>.</param>
    /// <param name="discard">Runs instead of <paramref name="release"/> on <see cref="Discard"/>; null means the same.</param>
    public InUseLease(T value, Action release, Action? discard = null)
    {
        Value = value;
        _release = release;
        _discard = discard;
    }

    public T Value { get; }

    /// <summary>
    /// Lets go and asks for the thing to be dropped, because it could not be used: a
    /// corrupt download the next job must fetch again rather than fail on too.
    /// </summary>
    public void Discard()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        (_discard ?? release)();
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
