using System;
using System.Collections;
using System.IO;
using GlobalEnums;
using Hkmp.Networking.Client;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui;

/// <summary>
/// Development test for saving a hosted multiplayer game and leaving the world (inactive unless
/// <c>-hkmp-test-save host|client</c> is present). Lines in the ModLog start with <c>HKMP_SAVE</c>.
/// <list type="bullet">
/// <item>host: hosts on <c>-hkmp-test-save-slot N</c> (default 3, new game if empty) on
/// <c>-hkmp-test-save-port</c> (default 26960), changes its geo and a world flag, waits for the client, quits to
/// the menu through the pause menu (with save), continues the same slot as host again and checks what came back.</item>
/// <item>client: (with <c>-hkmp-connect 127.0.0.1:PORT</c>) changes its geo and a world flag, quits to the menu
/// through the pause menu (with save), reconnects and checks its data; is then kicked when the host quits and
/// reconnects once more to check the data the host loaded from its save.</item>
/// </list>
/// Both instances synchronise through flag files in <c>-hkmp-test-dir</c> and copy their save files to
/// <c>dir/ROLE-LABEL</c> at each step.
/// </summary>
internal static class DevSaveTest {
    private const int HostGeo = 1111;
    private const int ClientGeo = 2222;
    private const string HostWorldFlag = "openedGreenpath";
    private const string ClientWorldFlag = "openedCrossroads";

    private static UiManager _uiManager;
    private static NetClient _netClient;
    private static string _dir;
    private static string _role;
    private static int _slot;
    private static int _port;
    private static int _fails;

    public static void TryStart(UiManager uiManager, NetClient netClient) {
        _role = GetOption("-hkmp-test-save")?.ToLowerInvariant();
        if (_role != "host" && _role != "client") {
            return;
        }

        _uiManager = uiManager;
        _netClient = netClient;
        _dir = GetOption("-hkmp-test-dir") ?? "E:/HKMP-Enemies/testrun";
        _slot = int.TryParse(GetOption("-hkmp-test-save-slot"), out var slot) ? slot : 3;
        _port = int.TryParse(GetOption("-hkmp-test-save-port"), out var port) ? port : 26960;
        Directory.CreateDirectory(_dir);

        Log($"start role={_role} slot={_slot} port={_port} dir={_dir} data={Application.persistentDataPath}");
        MonoBehaviourUtil.Instance.StartCoroutine(_role == "host" ? Host() : Client());
    }

    private static IEnumerator Host() {
        yield return WaitMainMenu();
        Snapshot("h0-start");

        // 1. Host the slot (new game if empty) and change our own data and the world
        yield return StartHost();
        yield return WaitInGame();
        var pd = PlayerData.instance;
        pd.SetInt("geo", HostGeo);
        pd.SetBool(HostWorldFlag, true);
        Log($"host changed geo={HostGeo} {HostWorldFlag}=true, profileID={GameManager.instance.profileID}");
        Flag("host-ingame");

        // 2. The client changes its data, quits with save and reconnects
        yield return WaitFlag("client-quit-done");
        yield return new WaitForSecondsRealtime(3f);

        // 3. Quit to the menu as host through the pause menu (with save)
        yield return QuitToMenuWithSave();
        Snapshot("h1-after-quit");

        // 4. Continue the same slot as host again
        yield return StartHost();
        yield return WaitInGame();
        pd = PlayerData.instance;
        Check("host geo after reload", pd.GetInt("geo") == HostGeo, $"geo={pd.GetInt("geo")}");
        Check($"host {HostWorldFlag} after reload", pd.GetBool(HostWorldFlag), "");
        Check($"host sees client {ClientWorldFlag} after reload", pd.GetBool(ClientWorldFlag), "");
        Flag("host-reloaded");

        // 5. Wait for the client to check its own data and quit once more
        yield return WaitFlag("client-verified");
        yield return new WaitForSecondsRealtime(2f);
        yield return QuitToMenuWithSave();
        Snapshot("h2-after-quit");

        Finish();
    }

    private static IEnumerator Client() {
        // DevAutoConnect connects and enters the game
        yield return WaitInGame();
        Snapshot("c0-ingame");
        var pd = PlayerData.instance;
        pd.SetInt("geo", ClientGeo);
        pd.SetBool(ClientWorldFlag, true);
        Log($"client changed geo={ClientGeo} {ClientWorldFlag}=true, profileID={GameManager.instance.profileID}");
        yield return new WaitForSecondsRealtime(5f);

        // 1. Quit to the menu as client through the pause menu (with save), then join again
        yield return QuitToMenuWithSave();
        Snapshot("c1-after-quit");
        yield return DevAutoConnect.Reconnect();
        yield return WaitInGame();
        pd = PlayerData.instance;
        Check("client geo after own quit+rejoin", pd.GetInt("geo") == ClientGeo, $"geo={pd.GetInt("geo")}");
        Check($"client {HostWorldFlag} after own quit+rejoin", pd.GetBool(HostWorldFlag), "");
        Flag("client-quit-done");

        // 2. The host quits (we get kicked to the menu without saving) and continues the slot as host again
        yield return new WaitUntil(() => !_netClient.IsConnected);
        Log("kicked by the host leaving");
        yield return WaitMainMenu();
        Snapshot("c2-kicked");
        yield return WaitFlag("host-reloaded");
        yield return DevAutoConnect.Reconnect();
        yield return WaitInGame();
        pd = PlayerData.instance;
        Check("client geo after host reload", pd.GetInt("geo") == ClientGeo, $"geo={pd.GetInt("geo")}");
        Check($"client {ClientWorldFlag} after host reload", pd.GetBool(ClientWorldFlag), "");
        Check($"client {HostWorldFlag} after host reload", pd.GetBool(HostWorldFlag), "");
        Flag("client-verified");

        yield return new WaitUntil(() => !_netClient.IsConnected);
        yield return WaitMainMenu();
        Snapshot("c3-kicked");
        Finish();
    }

    private static IEnumerator StartHost() {
        yield return WaitMainMenu();
        yield return _uiManager.OpenMultiplayerMenu();
        yield return new WaitForSecondsRealtime(1f);

        var hasSave = File.Exists(SlotPath(_slot));
        Log($"hosting slot {_slot} ({(hasSave ? "continue" : "new game")})");
        _uiManager.DevStartHost("P1", _port, _slot, hasSave);
    }

    private static IEnumerator QuitToMenuWithSave() {
        Log($"quit to menu with save (connected={_netClient.IsConnected}, profileID={GameManager.instance.profileID})");
        var gm = GameManager.instance;
        gm.StartCoroutine(gm.PauseGameToggle());
        yield return new WaitForSecondsRealtime(2f);
        UIManager.instance.UIReturnToMainMenu();
        yield return WaitMainMenu();
        // Let the game finish writing
        yield return new WaitForSecondsRealtime(2f);
        Log("back in main menu");
    }

    private static IEnumerator WaitMainMenu() {
        yield return new WaitUntil(() =>
            GameManager.instance != null &&
            GameManager.instance.gameState == GameState.MAIN_MENU &&
            UIManager.instance != null &&
            UIManager.instance.menuState == MainMenuState.MAIN_MENU &&
            InputHandler.Instance != null &&
            InputHandler.Instance.acceptingInput
        );
        yield return new WaitForSecondsRealtime(1f);
    }

    private static IEnumerator WaitInGame() {
        var waited = 0f;
        var logged = 0f;
        yield return new WaitUntil(() => {
            waited += Time.unscaledDeltaTime;
            var gm = GameManager.instance;
            var hc = HeroController.instance;
            if (waited - logged > 15f) {
                logged = waited;
                Log(
                    $"waiting in game: state={(gm ? gm.gameState.ToString() : "null")} hero={(hc != null)} " +
                    $"input={(hc != null && hc.acceptingInput)} connected={_netClient.IsConnected} timeScale={Time.timeScale}"
                );
            }

            return gm != null && gm.gameState == GameState.PLAYING && hc != null && _netClient.IsConnected &&
                   (hc.acceptingInput || waited > 60f);
        });
        yield return new WaitForSecondsRealtime(4f);
        Log($"in game, scene={GameManager.instance.sceneName}, profileID={GameManager.instance.profileID}");
    }

    private static string SlotPath(int slot) =>
        Path.Combine(Application.persistentDataPath, slot == 0 ? "user.dat" : $"user{slot}.dat");

    private static void Snapshot(string label) {
        try {
            var target = Path.Combine(_dir, $"{_role}-{label}");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(Application.persistentDataPath, "user*")) {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }

            Log($"snapshot {label} -> {target}");
        } catch (Exception e) {
            Log($"snapshot {label} failed: {e.Message}");
        }
    }

    private static void Check(string name, bool ok, string detail) {
        if (!ok) {
            _fails++;
        }

        Log($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    private static void Finish() {
        Log($"SUMMARY role={_role} fails={_fails}");
        Flag($"finished-{_role}");
    }

    private static void Flag(string name) {
        File.WriteAllText(Path.Combine(_dir, name), DateTime.Now.ToString("O"));
        Log($"flag {name}");
    }

    private static IEnumerator WaitFlag(string name) {
        Log($"waiting for flag {name}");
        yield return new WaitUntil(() => File.Exists(Path.Combine(_dir, name)));
    }

    private static void Log(string message) {
        Logger.Info($"HKMP_SAVE: {message}");
    }

    private static string GetOption(string option) {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++) {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase)) {
                return args[i + 1];
            }
        }

        return null;
    }
}
