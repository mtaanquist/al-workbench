namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Orders a set of apps dependencies-first: an item comes after every item in the set it
/// (transitively) depends on. A dependency outside the set (a Microsoft or third-party
/// app) is no constraint. Cycle-safe (best-effort) and stable in input order, so two
/// unrelated apps keep the order they were given in. Used by the project build to
/// compile siblings in order and by the upload dialog to install several apps in order.
/// </summary>
public static class DependencyOrder
{
    /// <param name="id">An item's own id; null or empty for an item that cannot be identified, which then only keeps its place.</param>
    /// <param name="dependencies">The ids an item depends on.</param>
    public static IReadOnlyList<T> Sort<T>(
        IReadOnlyList<T> items,
        Func<T, string?> id,
        Func<T, IEnumerable<string>> dependencies) where T : class
    {
        var byId = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = id(item);
            if (!string.IsNullOrEmpty(key)) byId[key] = item;
        }

        var ordered = new List<T>(items.Count);
        // 0 = unvisited, 1 = on the stack (visiting), 2 = emitted.
        var state = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);

        void Visit(T item)
        {
            if (state.TryGetValue(item, out var s) && s != 0) return; // visiting (cycle) or done
            state[item] = 1;
            foreach (var dep in dependencies(item))
            {
                if (byId.TryGetValue(dep, out var depItem) && !ReferenceEquals(depItem, item))
                {
                    Visit(depItem);
                }
            }
            state[item] = 2;
            ordered.Add(item);
        }

        foreach (var item in items) Visit(item);
        return ordered;
    }
}
