using System;
using System.Collections;
using System.IO;
using System.Linq;
using GlobalEnums;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Client;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <summary>
/// Development test for teleporting to another player (inactive unless <c>-hkmp-test-tp p1|p2</c> is given, needs
/// <c>-hkmp-connect</c> too). p1 is the target and walks through the scenes, p2 teleports to it: same scene (/tp),
/// another scene (/tp), an interior room (pause menu button), leaving and entering the room again and quitting to
/// the menu and joining again. After each step p2 checks the game state, the screen brightness and whether the hero
/// really moves when a direction is held. Lines in the ModLog start with <c>HKMP_TP</c>; both instances synchronise
/// through flag files in <c>-hkmp-test-dir</c> (default E:/HKMP-Enemies/testrun-tp).
/// </summary>
internal static class DevTeleportTest {
    private const string StartScene = "Tutorial_01";
    private const string OtherScene = "Town";
    private const string OtherGate = "left1";
    private const string RoomScene = "Room_Ouiji";
    private const string RoomGate = "left1";

    private static UiManager _uiManager;
    private static NetClient _netClient;
    private static string _role;
    private static string _dir;
    private static int _pass;
    private static int _fail;

    /// <summary>
    /// Direction injected into the hero input (-1, 0 or 1), as if the player held left or right.
    /// </summary>
    private static float _injectMove;

    /// <summary>
    /// Whether to press attack once (cleared when the attack happened).
    /// </summary>
    private static bool _injectAttack;

    public static void TryStart(UiManager uiManager, NetClient netClient) {
        _role = GetOption("-hkmp-test-tp")?.ToLowerInvariant();
        if (_role != "p1" && _role != "p2") {
            return;
        }

        _uiManager = uiManager;
        _netClient = netClient;
        _dir = GetOption("-hkmp-test-dir") ?? "E:/HKMP-Enemies/testrun-tp";
        Directory.CreateDirectory(_dir);

        // Same condition as HeroController.LookForInput: the value only counts if real input would be read too
        On.HeroController.LookForInput += (orig, self) => {
            orig(self);
            if (_injectMove != 0f && self.acceptingInput && !global::GameManager.instance.isPaused) {
                self.move_input = _injectMove;
            }
        };

        // Attack exactly as HeroController.LookForQueueInput does when the attack button is pressed
        On.HeroController.LookForQueueInput += (orig, self) => {
            orig(self);
            if (!_injectAttack || !self.acceptingInput || global::GameManager.instance.isPaused) {
                return;
            }

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            if ((bool) typeof(HeroController).GetMethod("CanAttack", flags)!.Invoke(self, null)) {
                typeof(HeroController).GetMethod("DoAttack", flags)!.Invoke(self, null);
                _injectAttack = false;
            }
        };

        // Who pauses the game (to tell test artefacts from real pauses)
        On.GameManager.PauseGameToggle += (orig, self) => {
            Log($"PauseGameToggle called (gs={self.gameState}) from: {Environment.StackTrace.Replace("\n", " | ")}");
            return orig(self);
        };

        Log($"start role={_role} dir={_dir}");
        MonoBehaviourUtil.Instance.StartCoroutine(_role == "p1" ? Target() : Teleporter());
    }

    private static void Log(string message) => Logger.Info($"HKMP_TP [{_role}] {message}");

    private static void Check(string name, bool ok, string detail) {
        if (ok) {
            _pass++;
        } else {
            _fail++;
        }

        Logger.Info($"HKMP_TP {(ok ? "PASS" : "FAIL")} [{_role}] {name}: {detail}");
    }

    private static string CurrentScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

    private static void Signal(string key, string value = "1") {
        File.WriteAllText(Path.Combine(_dir, key), value);
        Log($"signal {key}={value}");
    }

    private static string _signalValue;

    private static IEnumerator WaitSignal(string key, float timeout = 300f) {
        _signalValue = null;
        var path = Path.Combine(_dir, key);
        var waited = 0f;
        while (!File.Exists(path)) {
            if (waited > timeout) {
                Log($"timeout waiting for signal {key}");
                yield break;
            }

            waited += 0.25f;
            yield return new WaitForSecondsRealtime(0.25f);
        }

        yield return new WaitForSecondsRealtime(0.1f);
        _signalValue = File.ReadAllText(path);
    }

    private static IEnumerator WaitUntil(Func<bool> condition, float timeout) {
        var waited = 0f;
        while (waited < timeout) {
            bool done;
            try {
                done = condition();
            } catch (Exception) {
                done = false;
            }

            if (done) {
                yield break;
            }

            waited += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private static string State() {
        try {
            var gm = global::GameManager.instance;
            var hc = HeroController.instance;
            var ih = InputHandler.Instance;
            var em = EntityManager.Instance;
            return $"scene={CurrentScene} gs={gm.gameState} gmPaused={gm.isPaused} ts={Time.timeScale} " +
                   $"inTrans={gm.IsInSceneTransition} entryGate='{gm.entryGateName}' " +
                   $"ui={UIManager.instance.uiState} ihAccept={(ih != null && ih.acceptingInput)} " +
                   (hc == null
                       ? "hero=null"
                       : $"hcAccept={hc.acceptingInput} relinq={hc.controlReqlinquished} heroState={hc.hero_state} " +
                         $"trans={hc.transitionState} cTrans={hc.cState.transitioning} hcPaused={hc.cState.isPaused} " +
                         $"hazardResp={hc.cState.hazardRespawning} dead={hc.cState.dead} " +
                         $"renderer={hc.GetComponent<MeshRenderer>()?.enabled} pos={hc.transform.position}") +
                   $" hostDetermined={em?.IsSceneHostDetermined}";
        } catch (Exception e) {
            return $"state error {e.Message}";
        }
    }

    private static float _brightness;

    /// <summary>
    /// Measure the average brightness of the screen (0 black, 1 white) and save a screenshot.
    /// </summary>
    private static IEnumerator Measure(string step) {
        yield return new WaitForEndOfFrame();
        _brightness = -1f;
        try {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var pixels = tex.GetPixels32();
            double sum = 0;
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 37) {
                var p = pixels[i];
                sum += (0.299 * p.r + 0.587 * p.g + 0.114 * p.b) / 255.0;
                count++;
            }

            _brightness = count > 0 ? (float) (sum / count) : -1f;
            File.WriteAllBytes(Path.Combine(_dir, $"{_role}-{step}.png"), tex.EncodeToPNG());
            Object.Destroy(tex);
        } catch (Exception e) {
            Log($"measure failed: {e.Message}");
        }
    }

    private static float _moved;

    /// <summary>
    /// Hold right (then left if blocked) and measure how far the hero moved.
    /// </summary>
    private static IEnumerator TryMove(float seconds = 0.8f) {
        _moved = 0f;
        var hc = HeroController.instance;
        if (hc == null) {
            yield break;
        }

        foreach (var dir in new[] { 1f, -1f }) {
            var start = hc.transform.position;
            _injectMove = dir;
            yield return new WaitForSecondsRealtime(seconds);
            _injectMove = 0f;
            yield return new WaitForSecondsRealtime(0.2f);
            _moved = Mathf.Max(_moved, Mathf.Abs(hc.transform.position.x - start.x));
            if (_moved > 1f) {
                yield break;
            }
        }
    }

    private static bool _attacked;

    /// <summary>
    /// Press attack and check that the hero swings the nail.
    /// </summary>
    private static IEnumerator TryAttack() {
        _attacked = false;
        var hc = HeroController.instance;
        if (hc == null) {
            yield break;
        }

        _injectAttack = true;
        var waited = 0f;
        while (waited < 1f && !_attacked) {
            _attacked = hc.cState.attacking;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        _injectAttack = false;
    }

    private static IEnumerator GoTo(string scene, string gate) {
        Log($"transition {CurrentScene} -> {scene}:{gate}");
        yield return WaitUntil(() => !global::GameManager.instance.IsInSceneTransition, 30f);
        global::GameManager.instance.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = gate,
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = global::GameManager.SceneLoadVisualizations.Default,
            AlwaysUnloadUnusedAssets = false
        });
        yield return WaitUntil(() => CurrentScene == scene && !global::GameManager.instance.IsInSceneTransition, 60f);
        yield return WaitUntil(() => HeroController.instance.acceptingInput, 10f);
        yield return new WaitForSecondsRealtime(1.5f);
        var gates = Object.FindObjectsOfType<TransitionPoint>().Select(t => $"{t.name}->{t.targetScene}[{t.entryPoint}]");
        Log($"entered {CurrentScene}; gates: {string.Join(" ", gates)}; {State()}");
    }

    private static IEnumerator WaitInGame() {
        yield return WaitUntil(
            () => HeroController.instance != null && _netClient.IsConnected &&
                  global::GameManager.instance.gameState == GameState.PLAYING &&
                  HeroController.instance.acceptingInput && !global::GameManager.instance.IsInSceneTransition,
            600f
        );
        yield return new WaitForSecondsRealtime(3f);
        Log($"in game: {State()}");
    }

    private static void WritePosition(string key) {
        var pos = HeroController.instance.transform.position;
        Signal(key, $"{CurrentScene};{pos.x};{pos.y}");
    }

    // ---------------------------------------------------------------- target (p1)

    private static IEnumerator Target() {
        yield return WaitInGame();
        PlayerData.instance.isInvincible = true;
        Signal("ready-p1");
        yield return WaitSignal("ready-p2");

        // 1. Same scene: walk away from the start so p2 has some distance to cover
        yield return TryMove(1.5f);
        yield return new WaitForSecondsRealtime(1f);
        WritePosition("t1-target");
        yield return TargetSideChecks("t1");

        // 2. Another scene
        yield return GoTo(OtherScene, OtherGate);
        yield return new WaitForSecondsRealtime(1f);
        WritePosition("t2-target");
        yield return TargetSideChecks("t2");

        // 3. Interior room
        yield return GoTo(RoomScene, RoomGate);
        yield return new WaitForSecondsRealtime(1f);
        WritePosition("t3-target");
        yield return TargetSideChecks("t3");

        // 6. p2 teleports again from Town while its game is paused (the teleport itself must unpause)
        yield return WaitSignal("t6-ready", 600f);
        WritePosition("t6-target");
        yield return TargetSideChecks("t6");

        // Stay until p2 finished
        yield return WaitSignal("finished-p2", 900f);
        Log($"SUMMARY pass={_pass} fail={_fail}");
        Signal("finished-p1", $"{_pass} {_fail}");
    }

    /// <summary>
    /// On the target: after p2 arrived, check that p2's remote player is shown near its position and follows its
    /// movement, and that the target itself still moves and attacks.
    /// </summary>
    private static IEnumerator TargetSideChecks(string step) {
        yield return WaitSignal($"{step}-arrived");
        var parts = (_signalValue ?? "").Split(';');
        var remote = TeleportManager.Instance.DevPlayers.FirstOrDefault(p => p.Username == "P2");
        if (parts.Length >= 3 && parts[0] == CurrentScene) {
            yield return new WaitForSecondsRealtime(1f);
            var reported = new Vector2(float.Parse(parts[1]), float.Parse(parts[2]));
            var shown = remote?.PlayerContainer != null ? (Vector2) remote.PlayerContainer.transform.position : Vector2.zero;
            var active = remote != null && remote.IsInLocalScene && remote.PlayerContainer != null &&
                         remote.PlayerContainer.activeInHierarchy;
            Check($"{step} target: remote P2 shown", active, $"inLocalScene={remote?.IsInLocalScene} active={remote?.PlayerContainer?.activeInHierarchy}");
            Check($"{step} target: remote P2 near its position", active && Vector2.Distance(shown, reported) < 3f,
                $"shown={shown} reported={reported}");
            Signal($"{step}-p1-sampled");
            yield return WaitSignal($"{step}-p2-checked");
            yield return new WaitForSecondsRealtime(0.5f);
            var after = remote?.PlayerContainer != null ? (Vector2) remote.PlayerContainer.transform.position : Vector2.zero;
            Check($"{step} target: remote P2 follows its movement", active && Vector2.Distance(after, shown) > 1f,
                $"before={shown} after={after}");
        } else {
            Log($"{step}: p2 not in our scene ({_signalValue}), skipping remote checks");
            Signal($"{step}-p1-sampled");
            yield return WaitSignal($"{step}-p2-checked");
        }

        yield return CheckControl(step + "-target", "target after teleport", null);
        Signal($"{step}-p1-checked");
        yield return WaitSignal($"{step}-done");
    }

    // ---------------------------------------------------------------- teleporter (p2)

    private static IEnumerator Teleporter() {
        yield return WaitInGame();
        PlayerData.instance.isInvincible = true;
        Signal("ready-p2");
        yield return WaitSignal("ready-p1");

        yield return TeleportStep("t1", "same scene /tp", 0);
        Signal("t1-done");
        yield return TeleportStep("t2", "other scene /tp", 0);
        Signal("t2-done");
        yield return TeleportStep("t3", "interior room via pause button", 1);
        Signal("t3-done");

        // 4. Leave the room through its gate (the game's own transition) and enter it again
        var exitGate = Object.FindObjectsOfType<TransitionPoint>().FirstOrDefault(t => t.name == RoomGate);
        if (exitGate != null) {
            Log($"walking into gate {exitGate.name} at {exitGate.transform.position}");
            HeroController.instance.transform.SetPosition2D(exitGate.transform.position);
            yield return WaitUntil(() => CurrentScene == OtherScene && !global::GameManager.instance.IsInSceneTransition, 15f);
        }

        if (CurrentScene != OtherScene) {
            Log($"gate did not trigger ({State()}), using a scene transition");
            yield return GoTo(OtherScene, "door_jiji");
        } else {
            yield return WaitUntil(() => HeroController.instance.acceptingInput, 10f);
            yield return new WaitForSecondsRealtime(1.5f);
        }

        yield return CheckControl("t4a", "after leaving the room", OtherScene);
        yield return GoTo(RoomScene, RoomGate);
        yield return CheckControl("t4b", "after entering the room again", RoomScene);

        // 5. Quit to the menu (with save) and join again
        Log("quit to menu with save");
        UIManager.instance.TogglePauseGame();
        yield return new WaitForSecondsRealtime(2f);
        Log($"paused for quitting: {State()}");
        UIManager.instance.UIReturnToMainMenu();
        yield return WaitUntil(() => global::GameManager.instance.gameState == GameState.MAIN_MENU &&
                                     UIManager.instance.menuState == MainMenuState.MAIN_MENU, 90f);
        yield return new WaitForSecondsRealtime(3f);
        Log($"at main menu: {State()}");
        yield return DevAutoConnect.Reconnect();
        yield return WaitInGame();
        yield return CheckControl("t5", "after quitting to the menu and joining again", null);

        // 6. Teleport request answered while the game is paused (pause menu open)
        if (CurrentScene == RoomScene) {
            yield return GoTo(OtherScene, "door_jiji");
        }

        Signal("t6-ready");
        yield return TeleportStep("t6", "other scene while paused", 2);
        Signal("t6-done");

        // 7. Teleport to a scene that does not exist: cancelled, control untouched
        Log($"t7 invalid scene: before: {State()}");
        TeleportManager.Instance.OnPlayerTeleport(new Hkmp.Networking.Packet.Data.PlayerTeleport {
            SceneName = "Not_A_Scene", X = 1f, Y = 1f
        });
        yield return new WaitForSecondsRealtime(3f);
        yield return CheckControl("t7", "after a teleport to an invalid scene", CurrentScene);

        Log($"SUMMARY pass={_pass} fail={_fail}");
        Signal("finished-p2", $"{_pass} {_fail}");
    }

    /// <param name="mode">0: /tp, 1: pause menu button, 2: /tp answered while paused.</param>
    private static IEnumerator TeleportStep(string step, string label, int mode) {
        yield return WaitSignal($"{step}-target");
        var parts = (_signalValue ?? "").Split(';');
        if (parts.Length < 3) {
            Check($"{step} {label}", false, "no target position");
            yield break;
        }

        var targetScene = parts[0];
        var target = new Vector2(float.Parse(parts[1]), float.Parse(parts[2]));
        Log($"{step} {label}: target {targetScene} {target}; before: {State()}");

        if (mode == 2) {
            UIManager.instance.TogglePauseGame();
            yield return new WaitForSecondsRealtime(1.5f);
            Log($"{step} paused: {State()}");
            TeleportManager.Instance.Request("P1");
        } else if (mode == 1) {
            // Pause like the player does (Esc / menu), through the hooked UIManager.TogglePauseGame
            UIManager.instance.TogglePauseGame();
            yield return new WaitForSecondsRealtime(1.5f);
            Log($"{step} paused: {State()}");
            _uiManager.DevPressTeleport();
        } else {
            TeleportManager.Instance.Request("P1");
        }

        // Log the state for a while (every 0.5 s) to see how the teleport goes
        for (var i = 0; i < 16; i++) {
            yield return new WaitForSecondsRealtime(0.5f);
            Log($"{step} t+{(i + 1) * 0.5f:0.0}s: {State()}");
        }

        var hc = HeroController.instance;
        var distance = hc != null ? Vector2.Distance(hc.transform.position, target) : -1f;
        Check($"{step} {label}: in target scene", CurrentScene == targetScene, $"scene={CurrentScene} target={targetScene}");
        Check($"{step} {label}: near target", distance >= 0f && distance < 4f, $"distance={distance:0.00}");
        var pos = hc != null ? hc.transform.position : Vector3.zero;
        Signal($"{step}-arrived", $"{CurrentScene};{pos.x};{pos.y}");
        // Stand still until the target looked at our remote player, then move (the target checks it follows)
        yield return WaitSignal($"{step}-p1-sampled", 30f);
        yield return CheckControl(step, label, targetScene);
        Signal($"{step}-p2-checked");
        yield return WaitSignal($"{step}-p1-checked");
    }

    private static IEnumerator CheckControl(string step, string label, string expectedScene) {
        yield return Measure(step);
        var stateBefore = State();
        yield return TryMove();
        var gm = global::GameManager.instance;
        var hc = HeroController.instance;
        Log($"{step} {label}: brightness={_brightness:0.000} moved={_moved:0.00} {stateBefore}");
        if (expectedScene != null) {
            Check($"{step} {label}: scene", CurrentScene == expectedScene, $"scene={CurrentScene}");
        }

        Check($"{step} {label}: screen not black", _brightness > 0.04f, $"brightness={_brightness:0.000}");
        Check(
            $"{step} {label}: game state",
            gm.gameState == GameState.PLAYING && !gm.isPaused && Time.timeScale > 0.5f && !gm.IsInSceneTransition,
            $"gs={gm.gameState} paused={gm.isPaused} ts={Time.timeScale}"
        );
        Check(
            $"{step} {label}: hero control",
            hc != null && hc.acceptingInput && !hc.controlReqlinquished && !hc.cState.transitioning &&
            InputHandler.Instance.acceptingInput,
            stateBefore
        );
        Check($"{step} {label}: hero moves", _moved > 1f, $"moved={_moved:0.00}");
        yield return TryAttack();
        Check($"{step} {label}: hero attacks", _attacked, $"attacked={_attacked}");
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
