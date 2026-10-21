using System;
using System.IO;
using System.Linq;
using Hkmp.Util;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

public class SafeFileTests : IDisposable {
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hkmp-safefile-" + Guid.NewGuid().ToString("N"));

    public SafeFileTests() {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() {
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void AtomicWrite_CreatesAndReplacesWithoutLeavingTempFile() {
        var path = Path.Combine(_dir, "save.json");

        SafeFile.WriteAllTextAtomic(path, "one");
        Assert.Equal("one", File.ReadAllText(path));

        SafeFile.WriteAllTextAtomic(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal(["save.json"], Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void AtomicWrite_FailureKeepsPreviousContent() {
        var path = Path.Combine(_dir, "save.json");
        SafeFile.WriteAllTextAtomic(path, "good");

        // A directory squatting on the temp file name makes the write fail before the target is touched
        Directory.CreateDirectory(path + ".tmp");
        Assert.ThrowsAny<Exception>(() => SafeFile.WriteAllTextAtomic(path, "bad"));

        Assert.Equal("good", File.ReadAllText(path));
    }

    [Fact]
    public void Backup_SkipsIdenticalContentAndKeepsNewest() {
        var path = Path.Combine(_dir, "user1.dat");
        var t = new DateTime(2026, 1, 1, 10, 0, 0);

        File.WriteAllText(path, "a");
        Assert.NotNull(SafeFile.BackupFile(path, 2, t));
        Assert.Null(SafeFile.BackupFile(path, 2, t.AddSeconds(1)));

        File.WriteAllText(path, "b");
        Assert.NotNull(SafeFile.BackupFile(path, 2, t.AddSeconds(2)));
        File.WriteAllText(path, "c");
        Assert.NotNull(SafeFile.BackupFile(path, 2, t.AddSeconds(3)));

        var backups = Directory.GetFiles(_dir, "user1.dat.hkmp-backup-*").OrderBy(f => f).ToArray();
        Assert.Equal(2, backups.Length);
        Assert.Equal("b", File.ReadAllText(backups[0]));
        Assert.Equal("c", File.ReadAllText(backups[1]));
    }

    [Fact]
    public void Backup_MissingFileReturnsNull() {
        Assert.Null(SafeFile.BackupFile(Path.Combine(_dir, "nope.dat")));
    }
}
