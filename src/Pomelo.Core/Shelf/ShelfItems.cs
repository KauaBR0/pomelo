namespace Pomelo.Core.Shelf;

/// <summary>
/// What the shelf holds: paths only, never copies. Keeps the order items arrived in and
/// ignores a path already on the shelf (letter case and "..\" spellings count as the same).
/// </summary>
public sealed class ShelfItems
{
    private readonly List<string> _paths = [];

    public IReadOnlyList<string> Paths => _paths;

    public int Count => _paths.Count;

    /// <returns>How many paths were new.</returns>
    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); // Keeps "E:\" intact.
            if (IndexOf(full) >= 0)
                continue;
            _paths.Add(full);
            added++;
        }
        return added;
    }

    public bool Remove(string path)
    {
        int index = IndexOf(path);
        if (index < 0)
            return false;
        _paths.RemoveAt(index);
        return true;
    }

    public void Clear() => _paths.Clear();

    /// <summary>Items that still exist, in shelf order; moved or deleted ones are left out.</summary>
    public IReadOnlyList<string> Available() => _paths.Where(Path.Exists).ToList();

    private int IndexOf(string path) =>
        _paths.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}
