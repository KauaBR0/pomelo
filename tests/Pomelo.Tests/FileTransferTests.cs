using Pomelo.Core.Actions;
using Pomelo.Core.Conversion;

namespace Pomelo.Tests;

public sealed class FileTransferTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pomelo-transfer-").FullName;
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    public FileTransferTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Copy_keeps_the_original_and_never_replaces_existing_names()
    {
        string file = Write(@"source\notes.txt", "new");
        Write(@"target\notes.txt", "old");

        var result = FileTransfer.Run([file], Target, TransferMode.Copy).Single();

        Assert.Equal(Path.Combine(Target, "notes (1).txt"), result.Destination);
        Assert.Equal("new", File.ReadAllText(result.Destination!));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "notes.txt")));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Move_removes_the_original()
    {
        string file = Write(@"source\photo.jpg", "pixels");

        var result = FileTransfer.Run([file], Target, TransferMode.Move).Single();

        Assert.Equal(Path.Combine(Target, "photo.jpg"), result.Destination);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public void Folders_transfer_with_their_whole_tree(TransferMode mode)
    {
        Write(@"source\album\a.txt", "a");
        Write(@"source\album\sub\b.txt", "b");
        Directory.CreateDirectory(Path.Combine(Target, "album")); // Name taken: becomes "album (1)".

        var result = FileTransfer.Run([Path.Combine(Source, "album")], Target, mode).Single();

        Assert.Equal(Path.Combine(Target, "album (1)"), result.Destination);
        Assert.Equal("b", File.ReadAllText(Path.Combine(result.Destination!, "sub", "b.txt")));
        Assert.Equal(mode == TransferMode.Copy, Directory.Exists(Path.Combine(Source, "album")));
    }

    [Fact]
    public void A_folder_cannot_go_inside_itself_and_nothing_is_created()
    {
        Write(@"source\project\file.txt", "x");
        string inside = Directory.CreateDirectory(Path.Combine(Source, "project", "backup")).FullName;

        var result = FileTransfer.Run([Path.Combine(Source, "project")], inside, TransferMode.Copy).Single();

        Assert.Null(result.Destination);
        Assert.Empty(Directory.EnumerateFileSystemEntries(inside));
    }

    [Fact]
    public void Moving_into_the_same_folder_is_reported_not_renamed()
    {
        string file = Write(@"source\a.txt", "x");

        var result = FileTransfer.Run([file], Source, TransferMode.Move).Single();

        Assert.NotNull(result.Error);
        Assert.Equal([file], Directory.GetFiles(Source));
    }

    [Fact]
    public void One_failing_item_does_not_stop_the_others()
    {
        string good = Write(@"source\good.txt", "ok");
        string gone = Path.Combine(Source, "gone.txt");

        var results = FileTransfer.Run([gone, good], Target, TransferMode.Copy);

        Assert.NotNull(results[0].Error);
        Assert.Equal(Path.Combine(Target, "good.txt"), results[1].Destination);
    }

    [Fact]
    public void Missing_destination_folder_fails_up_front()
    {
        string file = Write(@"source\a.txt", "x");

        Assert.Throws<ConversionException>(() =>
            FileTransfer.Run([file], Path.Combine(_root, "nowhere"), TransferMode.Move));
        Assert.True(File.Exists(file));
    }
}
