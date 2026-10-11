using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Asyncronus.Setup
{
    /// <summary>
    /// CLI del instalador (el MSI usa la misma logica desde Asyncronus.CA.dll).
    ///   AsyncronusSetup install   -Payload DIR [-GameDir DIR]
    ///   AsyncronusSetup uninstall -Payload DIR  -GameDir DIR
    ///   AsyncronusSetup status    -Payload DIR [-GameDir DIR]
    ///   AsyncronusSetup locate
    ///   AsyncronusSetup diff  VANILLA.dll PATCHED.dll OUT.patch     (solo build.ps1)
    ///   AsyncronusSetup apply VANILLA.dll IN.patch OUT.dll          (verificacion)
    /// </summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) { Usage(); return 2; }
                string cmd = args[0].ToLowerInvariant();
                string gameDir = Opt(args, "-GameDir"), payloadDir = Opt(args, "-Payload");
                switch (cmd)
                {
                    case "diff":
                        {
                            int rc = 0;
                            var t = new Thread(() =>
                            {
                                try
                                {
                                    var o = File.ReadAllBytes(args[1]); var n = File.ReadAllBytes(args[2]);
                                    Delta.Create(o, n, args[3]);
                                    Console.WriteLine("patch: " + new FileInfo(args[3]).Length + " bytes");
                                }
                                catch (Exception e) { Console.Error.WriteLine(e); rc = 1; }
                            }, 512 * 1024 * 1024);
                            t.Start(); t.Join(); return rc;
                        }
                    case "apply":
                        File.WriteAllBytes(args[3], Delta.Apply(File.ReadAllBytes(args[1]), args[2]));
                        Console.WriteLine("sha256 " + Delta.Sha256Hex(args[3]));
                        return 0;
                    case "locate":
                        Console.WriteLine(GameLocator.AutoDetect() ?? "(no encontrado)");
                        return 0;
                }
                if (payloadDir == null) payloadDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload");
                var payload = Payload.Load(payloadDir);
                gameDir = GameLocator.Normalize(gameDir) ?? (gameDir == null ? GameLocator.AutoDetect() : null);
                if (gameDir == null) { Console.Error.WriteLine("Hollow Knight no encontrado. Usa -GameDir."); return 3; }
                Console.WriteLine("Juego: " + gameDir);
                var inst = new ModInstaller(gameDir, payload, Console.WriteLine);
                switch (cmd)
                {
                    case "install": inst.Install(); return 0;
                    case "uninstall": inst.Uninstall(); return 0;
                    case "status":
                        Console.WriteLine("Assembly-CSharp.dll: " + ModInstaller.GetState(GameLocator.ManagedOf(gameDir), payload));
                        return 0;
                    default: Usage(); return 2;
                }
            }
            catch (SetupException e) { Console.Error.WriteLine("ERROR: " + e.Message); return 1; }
            catch (Exception e) { Console.Error.WriteLine("ERROR inesperado: " + e); return 1; }
        }

        static string Opt(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++) if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }
        static void Usage() { Console.WriteLine("Uso: AsyncronusSetup install|uninstall|status|locate|diff|apply ..."); }
    }
}
