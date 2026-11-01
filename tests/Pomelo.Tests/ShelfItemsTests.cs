using Pomelo.Core.Shelf;

namespace Pomelo.Tests;

public sealed class ShelfItemsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-shelf-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Items_keep_arrival_order_and_the_same_place_is_added_once()
    {
        var shelf = new ShelfItems();
        string a = Touch("a.txt"), b = Touch("b.txt");

        Assert.Equal(2, shelf.Add([b, a]));
        // Other spellings of the same paths: letter case, "..\" segments, trailing separator on a folder.
        Assert.Equal(0, shelf.Add([a.ToUpperInvariant(), Path.Combine(_dir, "sub", "..", "b.txt"), b]));
        Assert.Equal(1, shelf.Add([_dir + Path.DirectorySeparatorChar]));
        Assert.Equal(0, shelf.Add([_dir]));

        Assert.Equal([b, a, _dir], shelf.Paths);
    }

    [Fact]
    public void Drive_roots_keep_their_separator()
    {
        var shelf = new ShelfItems();
        string root = Path.GetPathRoot(_dir)!;

        shelf.Add([root]);

        Assert.Equal([root], shelf.Paths);
        Assert.Equal([root], shelf.Available());
    }

    [Fact]
    public void Moved_or_deleted_items_stay_listed_but_are_not_available()
    {
        var shelf = new ShelfItems();
        string kept = Touch("kept.txt"), gone = Touch("gone.txt");
        shelf.Add([gone, kept]);

        File.Delete(gone);

        Assert.Equal([gone, kept], shelf.Paths);
        Assert.Equal([kept], shelf.Available());
        Assert.True(File.Exists(kept)); // The shelf never touches files.
    }

    [Fact]
    public void Removing_takes_the_item_off_the_shelf_only()
    {
        var shelf = new ShelfItems();
        string a = Touch("a.txt"), b = Touch("b.txt");
        shelf.Add([a, b]);

        Assert.True(shelf.Remove(a.ToUpperInvariant()));
        Assert.False(shelf.Remove(a));

        Assert.Equal([b], shelf.Paths);
        Assert.True(File.Exists(a));
    }
}
