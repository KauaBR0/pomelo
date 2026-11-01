using System.Diagnostics;
using Pomelo.Core.IO;
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

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

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
    [InlineData(TransferMode.Copy, false)]
    [InlineData(TransferMode.Move, true)]
    public void Copies_keep_modified_time_and_the_downloaded_from_internet_mark(TransferMode mode, bool otherVolume)
    {
        string file = Write(@"source\album\setup.exe", "x");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);

        var result = FileTransfer.Run([Path.Combine(Source, "album")], Target, mode, default, otherVolume).Single();

        string copy = Path.Combine(result.Destination!, "setup.exe");
        Assert.Equal(written, File.GetLastWriteTimeUtc(copy));
        Assert.Contains("ZoneId=3", File.ReadAllText(copy + ":Zone.Identifier"));
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

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public void Drive_roots_cannot_be_transferred_into_their_own_descendants(TransferMode mode)
    {
        var result = FileTransfer.Run([Path.GetPathRoot(_root)!], Target, mode).Single();
        Assert.Null(result.Destination);
        Assert.NotNull(result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public void A_drive_root_gets_a_valid_visible_name()
    {
        string name = FileTransfer.RootName(Path.GetPathRoot(_root)!);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.Contains(Path.GetPathRoot(_root)![0].ToString(), name);
    }

    [Fact]
    public void Root_prefix_comparison_keeps_component_boundaries()
    {
        Assert.True(RealPath.IsSameOrInside(@"E:\Backup", @"E:\"));
        Assert.False(RealPath.IsSameOrInside(@"E:\Backup2", @"E:\Backup"));
    }

    [Fact]
    public void A_junction_cannot_hide_a_destination_inside_the_source()
    {
        Write(@"source\project\a.txt", "keep");
        string project = Path.Combine(Source, "project");
        string inside = Directory.CreateDirectory(Path.Combine(project, "backup")).FullName;
        string alias = Path.Combine(_root, "alias");
        Junction(alias, project);
        try
        {
            var result = FileTransfer.Run([project], Path.Combine(alias, "backup"), TransferMode.Copy).Single();
            Assert.Null(result.Destination);
            Assert.Empty(Directory.GetFileSystemEntries(inside));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(project, "a.txt")));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void Cross_volume_move_copies_readonly_files_then_removes_the_source()
    {
        string file = Write(@"source\repo\objects\a.txt", "object");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var result = FileTransfer.Run([Path.Combine(Source, "repo")], Target, TransferMode.Move,
            default, assumeDifferentVolumes: true).Single();
        Assert.Null(result.Error);
        Assert.Equal("object", File.ReadAllText(Path.Combine(result.Destination!, "objects", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(Source, "repo")));
    }

    [Fact]
    public void A_failed_copy_cleans_readonly_parts_and_preserves_the_original_error()
    {
        string first = Write(@"source\album\a.txt", "readonly");
        string locked = Write(@"source\album\z.txt", "locked");
        File.SetAttributes(first, FileAttributes.ReadOnly);
        using var handle = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = FileTransfer.Run([Path.Combine(Source, "album")], Target, TransferMode.Copy).Single();

        Assert.Null(result.Destination);
        Assert.Contains("z.txt", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Target));
        Assert.Equal("readonly", File.ReadAllText(first));
    }

    [Fact]
    public void A_move_that_cannot_delete_source_reports_the_complete_destination_with_warning()
    {
        string file = Write(@"source\album\a.txt", "keep both");
        using var handle = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = FileTransfer.Run([Path.Combine(Source, "album")], Target, TransferMode.Move,
            default, assumeDifferentVolumes: true).Single();
        Assert.Equal(Path.Combine(Target, "album"), result.Destination);
        Assert.NotNull(result.Error);
        Assert.Equal("keep both", File.ReadAllText(file));
        Assert.Equal("keep both", File.ReadAllText(Path.Combine(result.Destination!, "a.txt")));
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public void Cross_volume_folder_links_are_warned_or_refused_without_touching_the_target(TransferMode mode)
    {
        Write(@"source\album\a.txt", "a");
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "important.txt"), "untouched");
        string link = Path.Combine(Source, "album", "link");
        Junction(link, outside);
        try
        {
            var result = FileTransfer.Run([Path.Combine(Source, "album")], Target, mode,
                default, assumeDifferentVolumes: true).Single();
            Assert.NotNull(result.Error);
            Assert.True(Directory.Exists(link));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "important.txt")));
            if (mode == TransferMode.Move)
            {
                Assert.Null(result.Destination);
                Assert.Empty(Directory.GetFileSystemEntries(Target));
            }
            else
            {
                Assert.Equal("a", File.ReadAllText(Path.Combine(result.Destination!, "a.txt")));
                Assert.False(Directory.Exists(Path.Combine(result.Destination!, "link")));
            }
        }
        finally { Directory.Delete(link); }
    }

    private static void Junction(string alias, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.Arguments = $"/c mklink /J \"{alias}\" \"{target}\"";
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
