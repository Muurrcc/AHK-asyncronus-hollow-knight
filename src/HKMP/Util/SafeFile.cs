using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Hkmp.Util;

/// <summary>
/// File helpers that avoid leaving half-written or lost files behind (atomic writes, rolling backups). No game
/// dependencies, covered by unit tests.
/// </summary>
internal static class SafeFile {
    /// <summary>
    /// Suffix of the temporary file used while writing.
    /// </summary>
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Write text to a file atomically: the content goes to a temporary file next to the target first, which then
    /// replaces the target. If the process dies (or the disk is full) half way, the previous file stays intact
    /// instead of ending up truncated or empty.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="contents">The text to write.</param>
    public static void WriteAllTextAtomic(string path, string contents) {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + TempSuffix;

        try {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                var bytes = new UTF8Encoding(false).GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(path)) {
                File.Replace(tempPath, path, null);
            } else {
                File.Move(tempPath, path);
            }
        } catch {
            try {
                if (File.Exists(tempPath)) {
                    File.Delete(tempPath);
                }
            } catch (Exception) {
                // Ignore, the temp file is harmless
            }

            throw;
        }
    }

    /// <summary>
    /// Copy a file to a timestamped backup next to it (<c>name.hkmp-backup-yyyyMMdd-HHmmss</c>), unless its content
    /// is identical to the newest existing backup. Only the newest <paramref name="keep"/> backups are kept.
    /// </summary>
    /// <param name="path">The file to back up.</param>
    /// <param name="keep">How many backups to keep.</param>
    /// <param name="now">The time used for the name (defaults to now).</param>
    /// <returns>The path of the created backup, or null if nothing was created.</returns>
    public static string BackupFile(string path, int keep = 3, DateTime? now = null) {
        if (!File.Exists(path)) {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var prefix = Path.GetFileName(path) + ".hkmp-backup-";
        var existing = Directory.GetFiles(directory, prefix + "*").OrderBy(f => f, StringComparer.Ordinal).ToList();

        if (existing.Count > 0 && FilesEqual(path, existing[existing.Count - 1])) {
            return null;
        }

        var backupPath = Path.Combine(directory, prefix + (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss"));
        File.Copy(path, backupPath, true);

        existing = Directory.GetFiles(directory, prefix + "*").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var old in existing.Take(System.Math.Max(0, existing.Count - keep))) {
            File.Delete(old);
        }

        return backupPath;
    }

    private static bool FilesEqual(string a, string b) {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        return infoA.Length == infoB.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
    }
}
