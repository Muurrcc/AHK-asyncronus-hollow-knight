using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Asyncronus.Setup;
using WixToolset.Dtf.WindowsInstaller;

namespace Asyncronus.CA
{
    public static class CustomActions
    {
        // ---------------------------------------------------------------- mensajes
        static MessageResult Msg(Session s, string text, MessageBoxButtons btn, MessageBoxIcon icon)
        {
            s.Log("MSG: " + text);
            var r = new Record(1);
            r.FormatString = "[1]";
            r[1] = text;
            return s.Message(InstallMessage.Error | (InstallMessage)((int)btn | (int)icon), r);
        }
        static void Fail(Session s, string text) { Msg(s, text, MessageBoxButtons.OK, MessageBoxIcon.Error); }

        static string PickFolder()
        {
            string result = null;
            var t = new Thread(() =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "No encuentro Hollow Knight automaticamente. Elige su carpeta (la que contiene hollow_knight.exe).";
                    d.ShowNewFolderButton = false;
                    if (d.ShowDialog() == DialogResult.OK) result = d.SelectedPath;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            return result;
        }

        static Payload HashesOnly(Session s)
        {
            return new Payload { VanillaSha256 = (s["VANILLA_SHA256"] ?? "").ToLowerInvariant(), PatchedSha256 = (s["PATCHED_SHA256"] ?? "").ToLowerInvariant() };
        }

        // ---------------------------------------------------------------- inmediata: localizar y validar
        [CustomAction]
        public static ActionResult LocateGame(Session session)
        {
            try
            {
                bool removing = string.Equals(session["REMOVE"], "ALL", StringComparison.OrdinalIgnoreCase);
                bool interactive = session.EvaluateCondition("UILevel >= 3");
                string gd = GameLocator.Normalize(session["GAMEDIR"]);
                if (gd == null && !string.IsNullOrEmpty(session["GAMEDIR"]))
                    session.Log("GAMEDIR indicado no valido: " + session["GAMEDIR"]);
                if (gd == null) gd = GameLocator.Normalize(session["GAMEDIR_PREV"]);

                if (removing)
                {
                    if (gd == null) { session.Log("Desinstalacion: carpeta del juego no localizable; no hay nada que restaurar."); return ActionResult.Success; }
                    // Acceso directo "Desinstalar Asyncronus" (msiexec /x ... CONFIRMUNINSTALL=1): pedir confirmacion una sola vez.
                    if (interactive && session["CONFIRMUNINSTALL"] == "1")
                    {
                        session["CONFIRMUNINSTALL"] = "0";
                        if (Msg(session, "Se va a desinstalar Asyncronus y se restauraran los ficheros originales de Hollow Knight.\r\nTus partidas guardadas y los demas mods no se tocan.\r\n\r\nQuieres continuar?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != MessageResult.Yes)
                            return ActionResult.UserExit;
                    }
                    while (GameLocator.IsGameRunning(gd))
                    {
                        if (Msg(session, "Hollow Knight esta abierto. Cierralo para poder desinstalar Asyncronus y restaurar los ficheros originales.", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != MessageResult.Retry)
                            return ActionResult.UserExit;
                    }
                    session["GAMEDIR"] = gd;
                    return ActionResult.Success;
                }

                if (gd == null) gd = GameLocator.AutoDetect();
                while (gd == null)
                {
                    if (!interactive)
                    {
                        Fail(session, "No se encontro Hollow Knight. Indica su carpeta con GAMEDIR=\"ruta\" en la linea de comandos.");
                        return ActionResult.Failure;
                    }
                    var pick = PickFolder();
                    if (pick == null) return ActionResult.UserExit;
                    gd = GameLocator.Normalize(pick);
                    if (gd == null)
                        Fail(session, "Esa carpeta no parece Hollow Knight (no contiene hollow_knight_Data\\Managed\\Assembly-CSharp.dll). Elige la carpeta que contiene hollow_knight.exe.");
                }
                session.Log("Juego: " + gd);

                var state = ModInstaller.GetState(GameLocator.ManagedOf(gd), HashesOnly(session));
                session.Log("Estado Assembly-CSharp.dll: " + state);
                if (state == AssemblyState.Other || state == AssemblyState.Missing)
                {
                    Fail(session, ModInstaller.OtherAssemblyMessage);
                    return ActionResult.Failure;
                }
                while (GameLocator.IsGameRunning(gd))
                {
                    if (Msg(session, "Hollow Knight esta abierto. Cierralo del todo y pulsa Reintentar.", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != MessageResult.Retry)
                        return ActionResult.UserExit;
                }
                session["GAMEDIR"] = gd;
                return ActionResult.Success;
            }
            catch (Exception e)
            {
                session.Log("LocateGame: " + e);
                Fail(session, "Error inesperado: " + e.Message);
                return ActionResult.Failure;
            }
        }

        // ---------------------------------------------------------------- diferidas
        [CustomAction]
        public static ActionResult InstallMod(Session session)
        {
            try
            {
                var d = session.CustomActionData;
                var payload = Payload.Load(Path.Combine(d["PAYLOAD"], "payload"));
                new ModInstaller(d["GAMEDIR"], payload, session.Log).Install();
                return ActionResult.Success;
            }
            catch (SetupException e) { session.Log("InstallMod: " + e.Message); Fail(session, e.Message); return ActionResult.Failure; }
            catch (UnauthorizedAccessException e)
            {
                session.Log("InstallMod: " + e);
                Fail(session, "No hay permiso para escribir en la carpeta del juego (" + e.Message + "). Si Hollow Knight esta en una carpeta protegida (p. ej. Archivos de programa), ejecuta el instalador desde una consola de administrador: msiexec /i Asyncronus.msi");
                return ActionResult.Failure;
            }
            catch (Exception e) { session.Log("InstallMod: " + e); Fail(session, "Error inesperado: " + e.Message); return ActionResult.Failure; }
        }

        [CustomAction]
        public static ActionResult RemoveMod(Session session)
        {
            try
            {
                var gd = GameLocator.Normalize(session.CustomActionData["GAMEDIR"]);
                if (gd == null) { session.Log("RemoveMod: carpeta del juego no valida; nada que hacer."); return ActionResult.Success; }
                var inst = new ModInstaller(gd, null, session.Log);
                inst.Uninstall();
                if (inst.NotRestored.Count == 0)
                    Msg(session, "Asyncronus se ha desinstalado.\r\nSe han restaurado los ficheros originales de Hollow Knight y se ha quitado el mod. Tus partidas guardadas y los demas mods no se han tocado.", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    Msg(session, "Asyncronus se ha desinstalado, pero no se pudo restaurar: " + string.Join(", ", inst.NotRestored) + " (no habia copia original).\r\nEn Steam: clic derecho en Hollow Knight > Propiedades > Archivos instalados > Verificar integridad. Tus partidas guardadas y los demas mods no se han tocado.", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return ActionResult.Success;
            }
            catch (Exception e)
            {
                // Nunca bloquear la desinstalacion del MSI por esto, pero avisar claramente.
                session.Log("RemoveMod: " + e);
                Msg(session, "No se pudieron restaurar todos los ficheros del juego: " + e.Message + "\r\nEn Steam: Verificar integridad de los archivos del juego.", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return ActionResult.Success;
            }
        }
    }
}
