using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Asyncronus.Setup
{
    /// <summary>Localiza Hollow Knight de Steam (registro + libraryfolders.vdf) y detecta si esta en ejecucion.</summary>
    public static class GameLocator
    {
        const string AppId = "367520";

        public static string ManagedOf(string gameDir) { return Path.Combine(gameDir, "hollow_knight_Data", "Managed"); }

        /// <summary>Accepts the game folder, hollow_knight_Data or Managed and returns the game folder (or null).</summary>
        public static string Normalize(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try
            {
                dir = Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\');
                for (int i = 0; i < 3 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(ManagedOf(dir), "Assembly-CSharp.dll"))) return dir;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        public static List<string> FindSteamLibraries()
        {
            var libs = new List<string>();
            Action<string> add = p =>
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                p = p.Replace('/', '\\').TrimEnd('\\');
                if (!libs.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) libs.Add(p);
            };
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    try
                    {
                        using (var bk = RegistryKey.OpenBaseKey(hive, view))
                        using (var k = bk.OpenSubKey(@"SOFTWARE\Valve\Steam") ?? bk.OpenSubKey(@"Software\Valve\Steam"))
                        {
                            if (k == null) continue;
                            add(k.GetValue("InstallPath") as string);
                            add(k.GetValue("SteamPath") as string);
                        }
                    }
                    catch { }
                }
            }
            foreach (var l in libs.ToArray())
            {
                var vdf = Path.Combine(l, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }
            return libs;
        }

        public static string AutoDetect()
        {
            foreach (var lib in FindSteamLibraries())
            {
                string installDir = "Hollow Knight";
                try
                {
                    var acf = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                    if (File.Exists(acf))
                    {
                        var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (m.Success) installDir = m.Groups[1].Value;
                    }
                }
                catch { }
                var g = Normalize(Path.Combine(lib, "steamapps", "common", installDir));
                if (g != null) return g;
            }
            return null;
        }

        // ---- game process (checked by image path, so another copy of the game is not mistaken for it) ----
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder sb, ref int size);

        public static bool IsGameRunning(string gameDir)
        {
            var root = Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcessesByName("hollow_knight"))
            {
                try
                {
                    IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, p.Id);
                    if (h == IntPtr.Zero) return true; // no se puede comprobar: por seguridad se considera abierto
                    try
                    {
                        var sb = new System.Text.StringBuilder(1024); int size = sb.Capacity;
                        if (!QueryFullProcessImageName(h, 0, sb, ref size)) return true;
                        if (sb.ToString().StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    finally { CloseHandle(h); }
                }
                catch { return true; }
            }
            return false;
        }
    }
}
