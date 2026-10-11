using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Asyncronus.Setup
{
    /// <summary>Error with a message already written for the end user.</summary>
    public class SetupException : Exception { public SetupException(string m) : base(m) { } }

    public enum AssemblyState { Missing, Vanilla, Patched, Other }

    /// <summary>Datos del paquete (payload\payload.ini, generado por build.ps1).</summary>
    public class Payload
    {
        public string Dir;
        public string ModVersion, GameBuild, VanillaSha256, PatchedSha256;
        public Dictionary<string, long> HookSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static Payload Load(string dir)
        {
            var p = new Payload { Dir = dir };
            var ini = Path.Combine(dir, "payload.ini");
            if (!File.Exists(ini)) throw new SetupException("El instalador esta incompleto (falta payload.ini). Vuelve a descargarlo.");
            foreach (var line in File.ReadAllLines(ini))
            {
                int i = line.IndexOf('=');
                if (i <= 0 || line.StartsWith("#")) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                switch (k)
                {
                    case "ModVersion": p.ModVersion = v; break;
                    case "GameBuild": p.GameBuild = v; break;
                    case "VanillaSha256": p.VanillaSha256 = v.ToLowerInvariant(); break;
                    case "PatchedSha256": p.PatchedSha256 = v.ToLowerInvariant(); break;
                    default:
                        if (k.StartsWith("HookSize.")) p.HookSizes[k.Substring(9)] = long.Parse(v);
                        break;
                }
            }
            return p;
        }
        public string PatchFile { get { return Path.Combine(Dir, "Assembly-CSharp.patch"); } }
        public string ApiDir { get { return Path.Combine(Dir, "api"); } }
        public string ModDir { get { return Path.Combine(Dir, "mod"); } }
        public string ToolsDir { get { return Path.Combine(Dir, "tools"); } }
    }

    public class ModInstaller
    {
        public const string ModFolderName = "Asyncronus";
        const string BackupFolderName = "Managed-vanilla-backup";
        const string ManifestName = "asyncronus-backup.txt";
        static readonly string[] HookNames = { "MMHOOK_Assembly-CSharp.dll", "MMHOOK_PlayMaker.dll", "MMHOOK_TeamCherry.TK2D.dll", "MMHOOK_TeamCherry.Cinematics.dll" };

        readonly string game, managed, data, backup, manifestPath;
        readonly Payload payload;
        readonly Action<string> log;

        public ModInstaller(string gameDir, Payload payload, Action<string> log)
        {
            game = gameDir; this.payload = payload; this.log = log ?? (s => { });
            data = Path.Combine(game, "hollow_knight_Data");
            managed = Path.Combine(data, "Managed");
            backup = Path.Combine(data, BackupFolderName);
            manifestPath = Path.Combine(backup, ManifestName);
        }

        public static AssemblyState GetState(string managedDir, Payload p)
        {
            var f = Path.Combine(managedDir, "Assembly-CSharp.dll");
            if (!File.Exists(f)) return AssemblyState.Missing;
            var h = Delta.Sha256Hex(f);
            if (h == p.VanillaSha256) return AssemblyState.Vanilla;
            if (h == p.PatchedSha256) return AssemblyState.Patched;
            return AssemblyState.Other;
        }

        public const string OtherAssemblyMessage =
            "Tu Hollow Knight no es la version que necesita Asyncronus (build 1.5.12459, Unity 6), o ya tiene otra Modding API u otros cambios en Assembly-CSharp.dll.\r\n\r\n" +
            "Que hacer: en Steam, clic derecho sobre Hollow Knight > Propiedades > Archivos instalados > Verificar integridad de los archivos del juego, " +
            "y en la pestana Betas elige \"Ninguna\". Cuando Steam termine, vuelve a ejecutar este instalador.\r\n\r\n" +
            "Aviso: si usas Lumafly u otro gestor con su propia Modding API, esta instalacion no es compatible con ella.";

        public static void EnsureClosed(string gameDir)
        {
            if (GameLocator.IsGameRunning(gameDir))
                throw new SetupException("Hollow Knight esta abierto. Cierralo del todo y vuelve a intentarlo.");
        }

        // ======================================================== INSTALAR / ACTUALIZAR
        public void Install()
        {
            if (!File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")))
                throw new SetupException("La carpeta elegida no parece Hollow Knight (falta " + managed + "\\Assembly-CSharp.dll).");
            EnsureClosed(game);

            var state = GetState(managed, payload);
            log("Estado de Assembly-CSharp.dll: " + state);
            if (state == AssemblyState.Other) throw new SetupException(OtherAssemblyMessage);

            // Working folder in %TEMP%: everything costly or risky happens here BEFORE the game is touched.
            var work = Path.Combine(Path.GetTempPath(), "Asyncronus-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            try
            {
                var plan = new List<KeyValuePair<string, string>>(); // nombre en Managed -> origen
                string patchedAsm = null;
                if (state == AssemblyState.Vanilla)
                {
                    log("Aplicando el parche a Assembly-CSharp.dll...");
                    var vanilla = File.ReadAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"));
                    var patched = Delta.Apply(vanilla, payload.PatchFile); // verifica hash de entrada y de salida
                    patchedAsm = Path.Combine(work, "patched-Assembly-CSharp.dll");
                    File.WriteAllBytes(patchedAsm, patched);
                    plan.Add(new KeyValuePair<string, string>("Assembly-CSharp.dll", patchedAsm));
                }

                foreach (var f in Directory.GetFiles(payload.ApiDir))
                    plan.Add(new KeyValuePair<string, string>(Path.GetFileName(f), f));

                // Hooks MMHOOK_*: se generan aqui con HookGen (ver README: no se distribuyen).
                bool allHooks = true;
                foreach (var h in HookNames) if (!File.Exists(Path.Combine(managed, h))) allHooks = false;
                string vanillaForHooks = null;
                if (state == AssemblyState.Vanilla) vanillaForHooks = Path.Combine(managed, "Assembly-CSharp.dll");
                else
                {
                    var b = Path.Combine(backup, "Assembly-CSharp.dll");
                    if (File.Exists(b) && Delta.Sha256Hex(b) == payload.VanillaSha256) vanillaForHooks = b;
                }
                if (state == AssemblyState.Vanilla || !allHooks)
                {
                    if (vanillaForHooks == null)
                        throw new SetupException("Faltan ficheros de la Modding API y no hay copia del Assembly-CSharp.dll original para regenerarlos. En Steam: Verificar integridad de los archivos del juego, y vuelve a ejecutar el instalador.");
                    var hooks = GenerateHooks(work, vanillaForHooks);
                    foreach (var kv in hooks) plan.Add(kv);
                }

                // ---- from here on the game is modified ----
                Directory.CreateDirectory(backup);
                var man = Manifest.Load(manifestPath);
                // Copia vieja (instalar.ps1): copia completa de Managed sin manifiesto.
                bool legacyBackup = !File.Exists(manifestPath) && File.Exists(Path.Combine(backup, "Assembly-CSharp.dll"));

                bool createdMods = !Directory.Exists(Path.Combine(managed, "Mods"));
                RegisterBackup(man, "Assembly-CSharp.dll", state, legacyBackup); // siempre: es el fichero critico
                foreach (var kv in plan) RegisterBackup(man, kv.Key, state, legacyBackup);
                if (legacyBackup) // hooks de una instalacion antigua que no regeneramos: tambien son nuestros y se quitaran al desinstalar
                    foreach (var h in HookNames) if (!plan.Exists(k => k.Key == h)) RegisterBackup(man, h, state, true);
                if (createdMods && !man.Has("C", "Mods")) man.Add("C", "Mods", "");
                man.Save(manifestPath); // diario escrito ANTES de modificar: si algo falla, Uninstall() puede revertir

                try
                {
                    foreach (var kv in plan)
                    {
                        log("Copiando " + kv.Key);
                        AtomicCopy(kv.Value, Path.Combine(managed, kv.Key));
                    }
                    InstallModFolder();
                }
                catch
                {
                    log("Error: revirtiendo cambios...");
                    try { new ModInstaller(game, payload, log).Uninstall(); } catch (Exception e2) { log("Fallo al revertir: " + e2.Message); }
                    throw;
                }

                if (Delta.Sha256Hex(Path.Combine(managed, "Assembly-CSharp.dll")) != payload.PatchedSha256)
                    throw new SetupException("La comprobacion final de Assembly-CSharp.dll ha fallado.");
                log("Instalacion terminada.");
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        void RegisterBackup(Manifest man, string name, AssemblyState state, bool legacy)
        {
            if (man.HasFile(name)) return; // ya registrado por una instalacion anterior: conserva el estado ORIGINAL
            var target = Path.Combine(managed, name);
            var bfile = Path.Combine(backup, name);
            if (!File.Exists(target)) { man.Add("A", name, ""); return; }
            if (name.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(bfile) && Delta.Sha256Hex(bfile) == payload.VanillaSha256) man.Add("R", name, "old");
                else if (state == AssemblyState.Vanilla) { File.Copy(target, bfile, true); man.Add("R", name, "new"); }
                else man.Add("K", name, ""); // ya parcheado y sin copia original: no se puede restaurar
                return;
            }
            if (File.Exists(bfile)) man.Add("R", name, "old");
            else if (legacy) man.Add("A", name, "");
            else { File.Copy(target, bfile, true); man.Add("R", name, "new"); }
        }

        void InstallModFolder()
        {
            var mods = Path.Combine(managed, "Mods");
            Directory.CreateDirectory(mods);
            // Carpeta Mods\HKMP antigua de este mismo mod: se aparta (nunca se tocan otros mods).
            var oldHkmp = Path.Combine(mods, "HKMP");
            if (File.Exists(Path.Combine(oldHkmp, "HKMP.dll")))
            {
                var dst = Path.Combine(backup, "Mods-HKMP-anterior");
                if (Directory.Exists(dst)) dst += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                log("Apartando el mod HKMP antiguo a " + dst);
                Directory.Move(oldHkmp, dst);
            }
            var target = Path.Combine(mods, ModFolderName);
            Directory.CreateDirectory(target);
            // replaces the contents of OUR folder (removes files from previous versions)
            foreach (var f in Directory.GetFiles(target)) File.Delete(f);
            foreach (var f in Directory.GetFiles(payload.ModDir))
            {
                log("Mod: " + Path.GetFileName(f));
                AtomicCopy(f, Path.Combine(target, Path.GetFileName(f)));
            }
        }

        static void AtomicCopy(string src, string dst)
        {
            var tmp = dst + ".asy-tmp";
            File.Copy(src, tmp, true);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        /// <summary>True for a well-formed PE file without a CLR header (a native DLL; not an assembly, so not an error).</summary>
        static bool IsNativeDll(string path)
        {
            try
            {
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    if (br.ReadUInt16() != 0x5A4D) return false;
                    br.BaseStream.Position = 0x3C;
                    var pe = br.ReadInt32();
                    br.BaseStream.Position = pe;
                    if (br.ReadUInt32() != 0x00004550) return false;
                    br.BaseStream.Position = pe + 24;
                    var magic = br.ReadUInt16();
                    var dirOffset = pe + 24 + (magic == 0x20B ? 112 : 96) + 14 * 8; // data directory 14 = CLR runtime header
                    br.BaseStream.Position = dirOffset;
                    return br.ReadUInt32() == 0;
                }
            }
            catch { return false; }
        }

        // -------------------------------------------------------- HookGen
        List<KeyValuePair<string, string>> GenerateHooks(string work, string vanillaAsm)
        {
            var wd = Path.Combine(work, "hooks");
            Directory.CreateDirectory(wd);
            // HookGen reads every DLL of Managed; one corrupt or truncated file kills it with an unhelpful exception.
            // Check them first so the user is told which file is damaged.
            var damaged = new List<string>();
            foreach (var f in Directory.GetFiles(managed, "*.dll"))
            {
                try { System.Reflection.AssemblyName.GetAssemblyName(f); }
                catch (BadImageFormatException) { if (new FileInfo(f).Length > 0 && !IsNativeDll(f)) damaged.Add(Path.GetFileName(f)); else if (new FileInfo(f).Length == 0) damaged.Add(Path.GetFileName(f)); }
                catch (IOException) { damaged.Add(Path.GetFileName(f)); }
            }
            if (damaged.Count > 0)
                throw new SetupException("Estos ficheros de tu juego estan danados o incompletos: " + string.Join(", ", damaged.ToArray())
                    + ". En Steam: clic derecho en Hollow Knight > Propiedades > Archivos instalados > Verificar integridad, y vuelve a ejecutar el instalador.");

            log("Preparando generacion de hooks...");
            foreach (var f in Directory.GetFiles(managed, "*.dll")) File.Copy(f, Path.Combine(wd, Path.GetFileName(f)), true);
            File.Copy(vanillaAsm, Path.Combine(wd, "Assembly-CSharp.dll"), true); // siempre el original
            foreach (var f in Directory.GetFiles(wd, "MMHOOK_*.dll")) File.Delete(f);
            foreach (var f in Directory.GetFiles(payload.ToolsDir)) File.Copy(f, Path.Combine(wd, Path.GetFileName(f)), true);
            foreach (var f in Directory.GetFiles(payload.ApiDir)) // Mono.Cecil / MonoMod.* que necesita HookGen
                if (Path.GetFileName(f).StartsWith("Mono", StringComparison.OrdinalIgnoreCase)) File.Copy(f, Path.Combine(wd, Path.GetFileName(f)), true);

            // 1) PrePatcher sobre el original (igual que el build de la Modding API)
            Run(wd, "PrePatcher.exe", "Assembly-CSharp.dll Assembly-CSharp-prepatched.dll");
            File.Delete(Path.Combine(wd, "Assembly-CSharp.dll"));
            File.Move(Path.Combine(wd, "Assembly-CSharp-prepatched.dll"), Path.Combine(wd, "Assembly-CSharp.dll"));
            // 2) HookGen
            foreach (var asm in new[] { "Assembly-CSharp", "PlayMaker", "TeamCherry.TK2D", "TeamCherry.Cinematics" })
                Run(wd, "MonoMod.RuntimeDetour.HookGen.exe", "--private " + asm + ".dll");
            // 3) reenvio de tipos movidos a TeamCherry.* hacia MMHOOK_Assembly-CSharp
            Run(wd, "PrePatcher.exe", "--mmhook MMHOOK_Assembly-CSharp.dll MMHOOK_TeamCherry.TK2D.dll");
            Run(wd, "PrePatcher.exe", "--mmhook MMHOOK_Assembly-CSharp.dll MMHOOK_TeamCherry.Cinematics.dll");

            var res = new List<KeyValuePair<string, string>>();
            foreach (var h in HookNames)
            {
                var f = Path.Combine(wd, h);
                if (!File.Exists(f)) throw new SetupException("No se pudo generar " + h + ".");
                long expected;
                long len = new FileInfo(f).Length;
                if (payload.HookSizes.TryGetValue(h, out expected) && Math.Abs(len - expected) > expected / 20)
                    throw new SetupException("El fichero generado " + h + " tiene un tamano inesperado (" + len + " bytes). Verifica la integridad del juego en Steam.");
                res.Add(new KeyValuePair<string, string>(h, f));
            }
            return res;
        }

        void Run(string wd, string exe, string args)
        {
            log("> " + exe + " " + args);
            var psi = new ProcessStartInfo(Path.Combine(wd, exe), args)
            {
                WorkingDirectory = wd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } throw new SetupException("Tiempo agotado ejecutando " + exe + "."); }
                var text = (so.Result + se.Result).Trim();
                if (p.ExitCode != 0)
                {
                    // Full output to a file the user can send us (the MSI message box truncates long text).
                    var NL = Environment.NewLine;
                    string logPath = null;
                    try
                    {
                        logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "asyncronus-error-log.txt");
                        File.WriteAllText(logPath, "Asyncronus installer error" + NL + "Command: " + exe + " " + args + NL + "Exit code: " + p.ExitCode
                            + NL + "OS: " + Environment.OSVersion + " / CLR " + Environment.Version + NL + "Work folder: " + wd + NL + NL + text);
                    }
                    catch { logPath = null; }
                    // Put the exception line first: that is the useful part.
                    string key = null;
                    foreach (var line in text.Split(new[] { Convert.ToChar(10) }))
                        if (line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("Excepci", StringComparison.OrdinalIgnoreCase) >= 0) { key = line.Trim(); break; }
                    var head = key ?? (text.Length > 300 ? text.Substring(0, 300) : text);
                    if (head.Length > 400) head = head.Substring(0, 400);
                    throw new SetupException("Fallo " + exe + " (codigo " + p.ExitCode + "). " + head
                        + (logPath != null ? NL + NL + "Se ha guardado el detalle completo en: " + logPath + " (enviamelo)." : ""));
                }
            }
        }

        // ======================================================== DESINSTALAR
        /// <summary>Ficheros que no se pudieron restaurar (no habia copia original); se rellena en Uninstall().</summary>
        public readonly List<string> NotRestored = new List<string>();

        public void Uninstall()
        {
            if (!Directory.Exists(managed)) { log("La carpeta del juego ya no existe; nada que restaurar."); return; }
            EnsureClosed(game);
            var man = Manifest.Load(manifestPath);

            foreach (var e in man.Entries)
            {
                var target = Path.Combine(managed, e.Name);
                try
                {
                    if (e.Kind == "R")
                    {
                        var src = Path.Combine(backup, e.Name);
                        if (File.Exists(src)) { log("Restaurando " + e.Name); AtomicCopy(src, target); }
                        else log("AVISO: falta la copia de " + e.Name + "; se deja como esta. Usa Verificar integridad en Steam.");
                    }
                    else if (e.Kind == "A" && File.Exists(target)) { log("Quitando " + e.Name); File.Delete(target); }
                }
                catch (Exception ex) { throw new SetupException("No se pudo restaurar " + e.Name + ": " + ex.Message); }
            }
            foreach (var e in man.Entries)
                if (e.Kind == "K") { NotRestored.Add(e.Name); log("AVISO: " + e.Name + " no se pudo restaurar (no habia copia original). Usa Verificar integridad en Steam para dejarlo como el original."); }

            // Our mod folder (only that one).
            var modDir = Path.Combine(managed, "Mods", ModFolderName);
            if (Directory.Exists(modDir)) { log("Quitando Mods\\" + ModFolderName); Directory.Delete(modDir, true); }
            var modsDir = Path.Combine(managed, "Mods");
            if (man.Has("C", "Mods") && Directory.Exists(modsDir) && Directory.GetFileSystemEntries(modsDir).Length == 0) Directory.Delete(modsDir);

            // Copias de seguridad que creamos nosotros.
            foreach (var e in man.Entries)
                if (e.Kind == "R" && e.Extra == "new") { try { File.Delete(Path.Combine(backup, e.Name)); } catch { } }
            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }
            try { if (Directory.Exists(backup) && Directory.GetFileSystemEntries(backup).Length == 0) Directory.Delete(backup); } catch { }
            log("Desinstalacion terminada.");
        }
    }

    class Manifest
    {
        public class Entry { public string Kind, Name, Extra; }
        public List<Entry> Entries = new List<Entry>();

        public static Manifest Load(string path)
        {
            var m = new Manifest();
            if (!File.Exists(path)) return m;
            foreach (var l in File.ReadAllLines(path))
            {
                var p = l.Split('\t');
                if (p.Length >= 2 && p[0].Length == 1) m.Entries.Add(new Entry { Kind = p[0], Name = p[1], Extra = p.Length > 2 ? p[2] : "" });
            }
            return m;
        }
        public bool HasFile(string name) { return Entries.Exists(e => e.Kind != "C" && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        public bool Has(string kind, string name) { return Entries.Exists(e => e.Kind == kind && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        public void Add(string kind, string name, string extra) { Entries.Add(new Entry { Kind = kind, Name = name, Extra = extra }); }
        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Asyncronus: registro de ficheros modificados (R=restaurar de la copia, A=anadido por nosotros, K=sin copia, C=carpeta creada)");
            foreach (var e in Entries) sb.AppendLine(e.Kind + "\t" + e.Name + "\t" + e.Extra);
            File.WriteAllText(path, sb.ToString());
        }
    }
}
