using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using GlobalEnums;
using Hkmp.Game.Client.Entity;
using Hkmp.Networking.Client;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <summary>
/// Development test for grub jar synchronisation (inactive unless <c>-hkmp-test-grub p1|p2</c> is given, needs
/// <c>-hkmp-connect</c> too). Both players enter a scene with a grub jar, p1 breaks it and p2 checks that the jar
/// is broken live, that the counters were synced and that the jar is still broken after re-entering the scene.
/// Lines in the ModLog start with <c>HKMP_GRUB</c>; the instances synchronise through flag files in
/// <c>-hkmp-test-dir</c>.
/// </summary>
internal static class DevGrubTest {
    private static NetClient _netClient;
    private static string _role;
    private static string _dir;
    private static string _scene;
    private static string _gate;
    private static int _pass;
    private static int _fail;

    public static void TryStart(NetClient netClient) {
        _role = GetOption("-hkmp-test-grub")?.ToLowerInvariant();
        if (_role != "p1" && _role != "p2") {
            return;
        }

        _netClient = netClient;
        _dir = GetOption("-hkmp-test-dir") ?? "E:/HKMP-Enemies/testrun-grub";
        _scene = GetOption("-hkmp-test-grub-scene") ?? "Crossroads_48";
        _gate = GetOption("-hkmp-test-grub-gate") ?? "left1";
        Directory.CreateDirectory(_dir);
        Log($"start role={_role} dir={_dir} scene={_scene}:{_gate}");
        MonoBehaviourUtil.Instance.StartCoroutine(Run());
    }

    private static void Log(string message) => Logger.Info($"HKMP_GRUB [{_role}] {message}");

    private static void Check(string name, bool ok, string detail) {
        if (ok) {
            _pass++;
        } else {
            _fail++;
        }

        Logger.Info($"HKMP_GRUB {(ok ? "PASS" : "FAIL")} [{_role}] {name}: {detail}");
    }

    private static string CurrentScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

    private static string _signalValue;

    private static void Signal(string key, string value = "1") {
        File.WriteAllText(Path.Combine(_dir, key), value);
        Log($"signal {key}={value}");
    }

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
        yield return new WaitForSecondsRealtime(2f);
        var gates = Object.FindObjectsOfType<TransitionPoint>().Select(t => $"{t.name}->{t.targetScene}[{t.entryPoint}]");
        Log($"entered {CurrentScene}; gates: {string.Join(" ", gates)}");
    }

    private static IEnumerator WaitInGame() {
        yield return WaitUntil(
            () => HeroController.instance != null && _netClient.IsConnected &&
                  global::GameManager.instance.gameState == GameState.PLAYING &&
                  HeroController.instance.acceptingInput && !global::GameManager.instance.IsInSceneTransition,
            600f
        );
        yield return new WaitForSecondsRealtime(3f);
        Log($"in game scene={CurrentScene}");
    }

    private static string ObjPath(GameObject o) {
        var sb = new StringBuilder(o.name);
        for (var t = o.transform.parent; t != null; t = t.parent) {
            sb.Insert(0, t.name + "/");
        }

        return sb.ToString();
    }

    private static string Fields(object a) {
        var sb = new StringBuilder("{");
        foreach (var f in a.GetType().GetFields()) {
            try {
                var v = f.GetValue(a);
                if (v == null) continue;
                var str = v.ToString();
                if (str.Length > 60) str = str.Substring(0, 60);
                sb.Append($"{f.Name}={str} ");
            } catch (Exception) { }
        }

        return sb.Append("}").ToString();
    }

    /// <summary>
    /// Dump every object whose name contains "Grub" (components, FSMs, states, variables).
    /// </summary>
    private static void Dump(string label) {
        foreach (var go in Object.FindObjectsOfType<GameObject>().Where(g => g.name.Contains("Grub") || g.name.Contains("Bottle"))) {
            var sb = new StringBuilder();
            sb.Append($"{label} obj {ObjPath(go)} active={go.activeInHierarchy} pos={go.transform.position} comps=");
            sb.Append(string.Join(",", go.GetComponents<UnityEngine.Component>().Select(c => c == null ? "null" : c.GetType().Name)));
            Log(sb.ToString());
            foreach (var fsm in go.GetComponents<PlayMakerFSM>()) {
                var vars = string.Join(" ", fsm.FsmVariables.BoolVariables.Select(b => $"{b.Name}={b.Value}"));
                Log($"{label}   fsm {fsm.FsmName} state={fsm.ActiveStateName} bools: {vars}");
                foreach (var st in fsm.FsmStates) {
                    foreach (var a in st.Actions.OfType<HutongGames.PlayMaker.Actions.SendMessage>()) {
                        Log($"{label}     {fsm.FsmName}/{st.Name}: SendMessage {a.functionCall?.FunctionName} param={a.functionCall?.ParameterType}");
                    }

                    foreach (var a in st.Actions.OfType<HutongGames.PlayMaker.Actions.BoolTest>()) {
                        Log($"{label}     {fsm.FsmName}/{st.Name}: BoolTest var={a.boolVariable?.Name} true={a.isTrue?.Name} false={a.isFalse?.Name}");
                    }
                }
            }
        }
    }

    private static IEnumerator Run() {
        yield return WaitInGame();
        Signal($"ready-{_role}");
        yield return WaitSignal(_role == "p1" ? "ready-p2" : "ready-p1");
        yield return GoTo(_scene, _gate);
        Signal($"in-scene-{_role}");
        yield return WaitSignal(_role == "p1" ? "in-scene-p2" : "in-scene-p1");
        Dump("initial");
        var pd = PlayerData.instance;
        Log($"counters grubsCollected={pd.grubsCollected} scenesGrubRescued=[{string.Join(",", pd.scenesGrubRescued)}]");
        yield return Scenario();
    }

    private static string Summary() {
        var sb = new StringBuilder();
        foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.FsmName == "Bottle Control")) {
            var go = fsm.gameObject;
            var grub = go.transform.Find("Grub");
            var sr = go.GetComponent<SpriteRenderer>();
            var col = go.GetComponent<BoxCollider2D>();
            sb.Append($"[{go.name} state={fsm.ActiveStateName} Activated={fsm.FsmVariables.GetFsmBool("Activated").Value} " +
                      $"sprite={(sr != null ? sr.enabled : (bool?) null)} collider={(col != null ? col.enabled : (bool?) null)} " +
                      $"grub={(grub == null ? "gone" : grub.gameObject.activeSelf.ToString())}]");
        }

        var pd = PlayerData.instance;
        sb.Append($" grubsCollected={pd.grubsCollected} scenesGrubRescued=[{string.Join(",", pd.scenesGrubRescued)}]");
        return sb.ToString();
    }

    private static void CheckState(string step, bool expectedScene) {
        var fsm = Object.FindObjectsOfType<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Bottle Control");
        var pd = PlayerData.instance;
        Check($"{step} jar broken", fsm != null && fsm.gameObject.name == "Collected" && fsm.FsmVariables.GetFsmBool("Activated").Value,
            fsm == null ? "no jar" : $"name={fsm.gameObject.name} state={fsm.ActiveStateName}");
        Check($"{step} grub freed", fsm != null && fsm.transform.Find("Grub") == null, "grub child gone");
        Check($"{step} grubsCollected == 1", pd.grubsCollected == 1, $"grubsCollected={pd.grubsCollected}");
        Check($"{step} scene in scenesGrubRescued once", pd.scenesGrubRescued.Count(x => x == _scene) == 1,
            $"[{string.Join(",", pd.scenesGrubRescued)}]");
    }

    private static IEnumerator Scenario() {
        Log($"before break: {Summary()}");
        var breaker = GetOption("-hkmp-test-grub-breaker") ?? "p1";
        var isBreaker = _role == breaker;
        if (isBreaker) {
            var fsm = Object.FindObjectsOfType<PlayMakerFSM>().First(f => f.FsmName == "Bottle Control");
            HeroController.instance.transform.position = fsm.transform.position + new Vector3(-1.5f, 0f, 0f);
            yield return new WaitForSecondsRealtime(1f);
            Log("the breaker breaks the jar (NAIL HIT)");
            fsm.SendEvent("NAIL HIT");
            Signal("broken");
        } else {
            yield return WaitSignal("broken");
        }

        foreach (var t in new[] { 0.5f, 2f, 5f }) {
            yield return new WaitForSecondsRealtime(t);
            Log($"after break +{t}s: {Summary()}");
        }

        CheckState("live", expectedScene: true);

        // Leave and enter again
        yield return GoTo("Crossroads_14", "right1");
        yield return GoTo(_scene, _gate);
        Log($"after re-entering: {Summary()}");
        CheckState("re-entered", expectedScene: true);
        Signal($"done-{_role}");
        yield return WaitSignal(_role == "p1" ? "done-p2" : "done-p1");
        Log($"SUMMARY pass={_pass} fail={_fail}");
        yield return new WaitForSecondsRealtime(600f);
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
