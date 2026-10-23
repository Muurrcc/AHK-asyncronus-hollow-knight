using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GlobalEnums;
using Hkmp.Game.Client;
using Hkmp.Networking.Client;
using Hkmp.Util;
using HutongGames.PlayMaker;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <summary>
/// Development test for dream scenes and dream bosses (inactive unless <c>-hkmp-test-dream p1|p2</c> is given, needs
/// <c>-hkmp-connect</c> too). Lines in the ModLog start with <c>HKMP_DREAM</c>; the instances synchronise through
/// flag files in <c>-hkmp-test-dir</c>.
/// <list type="bullet">
/// <item><c>-hkmp-test-dream-dump Scene:gate,...</c> (p1): visit the scenes and log every FSM action and component
/// that reads or writes a dream/boss related PlayerData field (to see the vanilla conditions).</item>
/// <item>Otherwise: p1 enters a dream first, p2 joins the game late (the launcher starts it after the
/// <c>p1-in-dream</c> signal), then they check that they see each other in the dream and back in the world, and that
/// a dream boss defeat stored in the dream reaches the server and the other player.</item>
/// </list>
/// </summary>
internal static class DevDreamTest {
    private const string DreamScene = "Dream_01_False_Knight";
    private const string WorldScene = "Crossroads_10";

    private static NetClient _netClient;
    private static string _role;
    private static string _dir;
    private static int _pass;
    private static int _fail;
    private static string _signalValue;

    private static bool IsP1 => _role == "p1";

    private static string CurrentScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

    public static void TryStart(NetClient netClient) {
        _role = GetOption("-hkmp-test-dream")?.ToLowerInvariant();
        if (_role != "p1" && _role != "p2") {
            return;
        }

        AudioListener.volume = 0f;
        _netClient = netClient;
        _dir = GetOption("-hkmp-test-dir") ?? "E:/HKMP-Enemies/testrun-dream";
        Directory.CreateDirectory(_dir);
        Log($"start role={_role} dir={_dir}");
        MonoBehaviourUtil.Instance.StartCoroutine(Run());
    }

    private static void Log(string message) => Logger.Info($"HKMP_DREAM [{_role}] {message}");

    private static void Check(string name, bool ok, string detail) {
        if (ok) {
            _pass++;
        } else {
            _fail++;
        }

        Logger.Info($"HKMP_DREAM {(ok ? "PASS" : "FAIL")} [{_role}] {name}: {detail}");
    }

    private static void Signal(string key, string value = "1") {
        File.WriteAllText(System.IO.Path.Combine(_dir, key), value);
        Log($"signal {key}={value}");
    }

    private static IEnumerator WaitSignal(string key, float timeout = 400f) {
        _signalValue = null;
        var path = System.IO.Path.Combine(_dir, key);
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

    private static bool HeroReady() =>
        HeroController.instance != null && _netClient.IsConnected &&
        global::GameManager.instance.gameState == GameState.PLAYING &&
        !global::GameManager.instance.IsInSceneTransition;

    private static IEnumerator WaitInGame() {
        var waited = 0f;
        while (!(HeroReady() && HeroController.instance.acceptingInput) && waited < 600f) {
            var hc = HeroController.instance;
            if (waited >= 30f && HeroReady() && !hc.acceptingInput && !hc.controlReqlinquished) {
                hc.AcceptInput();
            }

            yield return new WaitForSecondsRealtime(1f);
            waited += 1f;
        }

        yield return new WaitForSecondsRealtime(3f);
        Log($"in game scene={CurrentScene}");
    }

    private static IEnumerator GoTo(string scene, string gate,
        global::GameManager.SceneLoadVisualizations visualization = global::GameManager.SceneLoadVisualizations.Default) {
        Log($"transition {CurrentScene} -> {scene}:{gate} ({visualization})");
        yield return WaitUntil(() => !global::GameManager.instance.IsInSceneTransition, 30f);
        global::GameManager.instance.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = gate,
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = visualization,
            AlwaysUnloadUnusedAssets = false
        });
        yield return WaitUntil(() => CurrentScene == scene && HeroReady(), 60f);
        yield return new WaitForSecondsRealtime(2f);
        var gates = Object.FindObjectsOfType<TransitionPoint>().Select(t => $"{t.name}->{t.targetScene}[{t.entryPoint}]");
        Log($"entered {CurrentScene}; gates: {string.Join(" ", gates)}");
    }

    private static ClientPlayerData Remote(string username) =>
        TeleportManager.Instance?.DevPlayers.FirstOrDefault(p => p.Username == username);

    private static bool RemoteShown(string username, out string detail) {
        var remote = Remote(username);
        var shown = remote != null && remote.IsInLocalScene && remote.PlayerContainer != null &&
                    remote.PlayerContainer.activeInHierarchy;
        detail = $"scene={CurrentScene} known={remote != null} inLocalScene={remote?.IsInLocalScene} " +
                 $"active={remote?.PlayerContainer?.activeInHierarchy} " +
                 $"pos={(remote?.PlayerContainer != null ? (Vector2) remote.PlayerContainer.transform.position : Vector2.zero)}";
        return shown;
    }

    private static IEnumerator CheckRemoteShown(string name, string username, float timeout = 8f) {
        yield return WaitUntil(() => RemoteShown(username, out _), timeout);
        Check(name, RemoteShown(username, out var detail), detail + " " + RenderInfo(username));
        Screenshot(Regex.Replace(name, "[^A-Za-z0-9]+", "-"));
        yield return new WaitForSecondsRealtime(0.5f);
    }

    private static string RenderInfo(string username) {
        var remote = Remote(username);
        if (remote?.PlayerObject == null) {
            return "render=none";
        }

        var go = remote.PlayerObject;
        var renderer = go.GetComponent<MeshRenderer>();
        var cam = GameCameras.instance ? GameCameras.instance.mainCamera : null;
        var viewport = cam ? cam.WorldToViewportPoint(go.transform.position) : Vector3.zero;
        var hero = HeroController.instance ? HeroController.instance.transform.position : Vector3.zero;
        return $"render: active={go.activeInHierarchy} renderer={(renderer ? renderer.enabled.ToString() : "-")} " +
               $"layer={go.layer} z={go.transform.position.z} heroZ={hero.z} viewport={viewport} " +
               $"sortingLayer={(renderer ? renderer.sortingLayerName : "-")}";
    }

    private static void Screenshot(string label) {
        try {
            var path = System.IO.Path.Combine(_dir, $"{_role}-{label}.png");
            ScreenCapture.CaptureScreenshot(path);
        } catch (Exception e) {
            Log($"screenshot failed: {e.Message}");
        }
    }

    private static string Snap() {
        var pd = PlayerData.instance;
        return $"scene={CurrentScene} hasDreamNail={pd.hasDreamNail} falseKnightDefeated={pd.falseKnightDefeated} " +
               $"falseKnightDreamDefeated={pd.falseKnightDreamDefeated} xeroDefeated={pd.xeroDefeated} " +
               $"dreamReturnScene={pd.dreamReturnScene} mapZone={pd.mapZone} respawnScene={pd.respawnScene}";
    }

    private static IEnumerator Run() {
        yield return WaitInGame();
        AudioListener.volume = 0f;
        PlayerData.instance.isInvincible = true;

        var dump = GetOption("-hkmp-test-dream-dump");
        if (IsP1 && !string.IsNullOrEmpty(dump)) {
            PlayerData.instance.hasDreamNail = true;
            PlayerData.instance.falseKnightDefeated = true;
            foreach (var entry in dump.Split(',')) {
                var parts = entry.Split(':');
                yield return GoTo(parts[0], parts.Length > 1 ? parts[1] : "x");
                yield return new WaitForSecondsRealtime(2f);
                DumpScene();
                if (CurrentScene == WarriorScene) {
                    LogWarrior("dump");
                    yield return new WaitForSecondsRealtime(3f);
                }
            }

            Log($"SUMMARY dump done");
            Signal("finished-p1", "dump");
            yield break;
        }

        if (IsP1) {
            yield return P1();
        } else {
            yield return P2();
        }

        Log($"SUMMARY pass={_pass} fail={_fail} [{Snap()}]");
        Signal($"finished-{_role}", $"{_pass} {_fail}");
    }

    // ---------------------------------------------------------------- p1: in the dream first

    private static IEnumerator P1() {
        var pd = PlayerData.instance;
        pd.hasDreamNail = true;
        pd.falseKnightDefeated = true;
        Log($"baseline [{Snap()}]");

        // 1. p1 enters the dream before p2 is in the game
        yield return GoTo(DreamScene, "door1", global::GameManager.SceneLoadVisualizations.Dream);
        Signal("p1-in-dream", CurrentScene);

        // 2. p2 joins the game late and enters the same dream: both must see each other
        yield return WaitSignal("p2-in-dream", 600f);
        yield return CheckRemoteShown("dream: p1 sees late joiner p2 in the same dream", "P2");
        Signal("p1-checked-dream");
        yield return WaitSignal("p2-checked-dream");

        // 3. p2 leaves the dream back to the world; p1 must not see it anymore (no ghost player)
        yield return WaitSignal("p2-in-world");
        yield return new WaitForSecondsRealtime(2f);
        Check("dream: p2 is gone from p1's dream after it left", !RemoteShown("P2", out var detail), detail);

        // 4. p1 defeats the dream boss in the dream (the dream is not shared with anybody now)
        Log($"before dream defeat [{Snap()}]");
        pd.falseKnightDreamDefeated = true;
        yield return new WaitForSecondsRealtime(2f);
        Signal("p1-defeated-dream-boss", Snap());

        // 5. p1 returns from the dream to the world scene where p2 is
        yield return GoTo(WorldScene, "door_dreamReturn", global::GameManager.SceneLoadVisualizations.Dream);
        yield return CheckRemoteShown("world: p1 sees p2 after returning from the dream", "P2");
        if (GetOption("-hkmp-test-dream-dump-world") != null) {
            DumpScene(true);
        }
        Signal("p1-in-world");
        yield return WaitSignal("p2-checked-world");

        // 6. p1 goes elsewhere and warps back with its Dream Gate (entry gate "dreamGate") next to p2
        yield return GoTo("Crossroads_21", "left1");
        yield return WaitSignal("p2-position");
        var parts = (_signalValue ?? "").Split(';');
        pd.dreamGateScene = parts[0];
        pd.dreamGateX = float.Parse(parts[1]);
        pd.dreamGateY = float.Parse(parts[2]) + 0.5f;
        Log($"warping to dream gate {pd.dreamGateScene} ({pd.dreamGateX}, {pd.dreamGateY})");
        global::GameManager.instance.WarpToDreamGate();
        yield return WaitUntil(() => CurrentScene == parts[0] && HeroReady(), 60f);
        yield return new WaitForSecondsRealtime(3f);
        Log($"after dream gate warp [{Snap()}] accepting={HeroController.instance.acceptingInput}");
        yield return CheckRemoteShown("dream gate: p1 sees p2 after warping with the Dream Gate", "P2");
        Signal("p1-warped");
        yield return WaitSignal("p2-checked-warp");

        // 7. Dream warrior (Xero, RestingGrounds_02): p1 enters first (scene host), p2 follows (scene client)
        yield return GoTo(WarriorScene, WarriorGate);
        LogWarrior("p1 host arrived");
        yield return new WaitForSecondsRealtime(4f);
        Check("warrior: not awake on the scene host before it is challenged", !WarriorAwake(out var awakeDetail),
            awakeDetail);
        Signal("p1-at-warrior");
        yield return WaitSignal("p2-at-warrior");
        yield return new WaitForSecondsRealtime(2f);
        // The warrior is challenged on the scene host (the ghost NPC starts the fight): it must wake up for both
        var npc = UnityEngine.Resources.FindObjectsOfTypeAll<PlayMakerFSM>().FirstOrDefault(f =>
            f.gameObject.scene.IsValid() && f.gameObject.name == "Ghost Warrior NPC" &&
            f.FsmName == "Conversation Control");
        Log($"starting the fight from state '{npc?.ActiveStateName}'");
        npc?.SetState("Start Fight");
        yield return new WaitForSecondsRealtime(3f);
        Check("warrior: wakes up on the scene host when challenged", WarriorAwake(out var fightDetail), fightDetail);
        Signal("p1-fight-started");
        yield return WaitSignal("p2-fight-checked");
        LogWarrior("p1 with p2 in scene");
        // The warrior is defeated: the boss FSM runs on the scene host and stores xeroDefeated = 1
        pd.xeroDefeated = 1;
        yield return new WaitForSecondsRealtime(1f);
        Signal("p1-warrior-defeated");
        // p2 (not scene host) dream nails the defeated warrior and collects the essence: xeroDefeated = 2, and it
        // is the one that stores a dream boss defeat
        yield return WaitSignal("p2-warrior-collected");
        yield return new WaitForSecondsRealtime(2.5f);
        Check("warrior: essence collected by the non-host reaches the host (xeroDefeated 2)", pd.xeroDefeated == 2,
            Snap());
        Check("dream boss defeat stored by the non-host reaches the host", pd.infectedKnightDreamDefeated, Snap());
        LogWarrior("p1 end");
        Signal("p1-warrior-checked");
        yield return WaitSignal("p2-warrior-done");

        // 8. p1 dies inside a dream and wakes up in the world scene where p2 waits
        yield return GoTo(WorldScene, "left1");
        yield return WaitSignal("p2-back-in-world");
        pd.dreamReturnScene = WorldScene;
        yield return GoTo(DreamScene, "door1", global::GameManager.SceneLoadVisualizations.Dream);
        yield return new WaitForSecondsRealtime(2f);
        pd.isInvincible = false;
        Log($"dying in the dream [{Snap()}]");
        HeroController.instance.TakeDamage(HeroController.instance.gameObject, CollisionSide.top, 99, 1);
        yield return WaitUntil(() => CurrentScene == WorldScene && HeroReady(), 90f);
        yield return new WaitForSecondsRealtime(3f);
        pd.isInvincible = true;
        Log($"after dream death [{Snap()}] accepting={HeroController.instance.acceptingInput}");
        yield return CheckRemoteShown("dream death: p1 sees p2 after waking up from the dream", "P2");
        Signal("p1-woke-up");
        yield return WaitSignal("p2-checked-woke");
    }

    private const string WarriorScene = "RestingGrounds_02";
    private const string WarriorGate = "left1";

    private static bool WarriorAwake(out string detail) {
        var objects = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>()
            .Where(g => g.scene.IsValid() && g.scene.isLoaded && g.name.StartsWith("Ghost Warrior Xero"))
            .ToList();
        detail = string.Join(" ", objects.Select(g => $"{GoPath(g)}[{g.scene.name}]={g.activeInHierarchy}"));
        return objects.Any(g => g.activeInHierarchy);
    }

    private static void LogWarrior(string label) {
        if (HeroController.instance && CurrentScene == WarriorScene) {
            HeroController.instance.transform.position = new Vector3(92f, 12f, HeroController.instance.transform.position.z);
        }

        MonoBehaviourUtil.Instance.StartCoroutine(LogWarriorLater(label));
    }

    private static IEnumerator LogWarriorLater(string label) {
        yield return new WaitForSecondsRealtime(1.5f);
        var parts = new List<string>();
        foreach (var fsm in UnityEngine.Resources.FindObjectsOfTypeAll<PlayMakerFSM>()) {
            var go = fsm.gameObject;
            if (!go.scene.IsValid() || !go.scene.isLoaded || !go.name.Contains("Ghost Warrior")) {
                continue;
            }

            if (fsm.FsmName is "Appear" or "Conversation Control" or "Attacking" or "Movement" or "Control") {
                var renderer = go.GetComponent<MeshRenderer>();
                var sprite = go.GetComponent<tk2dSprite>();
                parts.Add($"{GoPath(go)}[{go.scene.name},{(go.activeInHierarchy ? "on" : "off")}," +
                          $"renderer={(renderer ? renderer.enabled.ToString() : "-")}," +
                          $"alpha={(sprite ? sprite.color.a.ToString("0.00") : "-")},pos={(Vector2) go.transform.position}," +
                          $"fsmEnabled={fsm.enabled}]:{fsm.FsmName}={fsm.ActiveStateName}");
            }
        }

        Log($"warrior {label}: xeroDefeated={PlayerData.instance.xeroDefeated} hasDreamNail={PlayerData.instance.hasDreamNail}; " +
            string.Join(" ", parts));
        Screenshot("warrior-" + Regex.Replace(label, "[^A-Za-z0-9]+", "-"));
    }

    // ---------------------------------------------------------------- p2: joins late

    private static IEnumerator P2() {
        var pd = PlayerData.instance;
        Log($"joined late [{Snap()}]");
        yield return WaitSignal("p1-in-dream");

        yield return GoTo(DreamScene, "door1", global::GameManager.SceneLoadVisualizations.Dream);
        Signal("p2-in-dream", CurrentScene);
        yield return CheckRemoteShown("dream: late joiner p2 sees p1 in the same dream", "P1");
        yield return WaitSignal("p1-checked-dream");
        Signal("p2-checked-dream");

        yield return GoTo(WorldScene, "door_dreamReturn", global::GameManager.SceneLoadVisualizations.Dream);
        Signal("p2-in-world", CurrentScene);

        yield return WaitSignal("p1-defeated-dream-boss");
        yield return new WaitForSecondsRealtime(2f);
        Check("dream boss defeated in p1's dream reaches p2", pd.falseKnightDreamDefeated, Snap());

        yield return WaitSignal("p1-in-world");
        yield return CheckRemoteShown("world: p2 sees p1 after p1 returned from the dream", "P1");
        Signal("p2-checked-world");

        var pos = HeroController.instance.transform.position;
        Signal("p2-position", $"{CurrentScene};{pos.x};{pos.y}");
        yield return WaitSignal("p1-warped");
        yield return CheckRemoteShown("dream gate: p2 sees p1 after p1 warped with the Dream Gate", "P1");
        Signal("p2-checked-warp");

        // 7. Dream warrior: p2 enters after p1 (scene client)
        yield return WaitSignal("p1-at-warrior");
        yield return GoTo(WarriorScene, WarriorGate);
        LogWarrior("p2 client arrived");
        yield return new WaitForSecondsRealtime(4f);
        Check("warrior: not shown on the scene client before it is challenged", !WarriorAwake(out var awakeDetail),
            awakeDetail);
        Signal("p2-at-warrior");
        yield return WaitSignal("p1-fight-started");
        yield return new WaitForSecondsRealtime(2f);
        Check("warrior: shown on the scene client when the host challenges it", WarriorAwake(out var fightDetail),
            fightDetail);
        Signal("p2-fight-checked");
        yield return WaitSignal("p1-warrior-defeated");
        yield return new WaitForSecondsRealtime(2.5f);
        Check("warrior: defeat by the host reaches the non-host (xeroDefeated 1)", pd.xeroDefeated == 1, Snap());
        pd.xeroDefeated = 2;
        pd.infectedKnightDreamDefeated = true;
        yield return new WaitForSecondsRealtime(1f);
        Signal("p2-warrior-collected");
        yield return WaitSignal("p1-warrior-checked");
        // Re-enter: the warrior must stay gone (inert) for the non-host as well
        yield return GoTo("RestingGrounds_04", "left1");
        yield return GoTo(WarriorScene, "right1");
        LogWarrior("p2 re-entered");
        Check("warrior: still collected for p2 after re-entering", pd.xeroDefeated == 2, Snap());
        Signal("p2-warrior-done");

        // 8. p1 dies inside a dream and wakes up here
        yield return GoTo(WorldScene, "left1");
        Signal("p2-back-in-world");
        yield return WaitSignal("p1-woke-up");
        yield return CheckRemoteShown("dream death: p2 sees p1 after it woke up from the dream", "P1");
        Signal("p2-checked-woke");
    }

    // ---------------------------------------------------------------- dump of vanilla conditions

    private static readonly Regex Interesting = new(
        "defeat|dream|ghost|encounter|killed|zote|whitedefender|orbs|warrior|pinned|essence|bretta",
        RegexOptions.IgnoreCase
    );

    private static void DumpScene(bool all = false) {
        var sb = new StringBuilder();
        var fsms = UnityEngine.Resources.FindObjectsOfTypeAll<PlayMakerFSM>()
            .Where(f => f.gameObject.scene.IsValid() && f.gameObject.scene.isLoaded &&
                        f.gameObject.scene.name != "DontDestroyOnLoad")
            .ToList();
        Log($"DUMP scene {CurrentScene}: {fsms.Count} fsms");
        foreach (var fsm in fsms) {
            try {
                var lines = new List<string>();
                var interesting = all && fsm.gameObject.scene.name != CurrentScene || Interesting.IsMatch(fsm.gameObject.name);
                foreach (var state in fsm.FsmStates) {
                    var actionTexts = new List<string>();
                    foreach (var action in state.Actions) {
                        var text = DescribeObject(action);
                        if (Interesting.IsMatch(text) && !(action is HutongGames.PlayMaker.Actions.Wait)) {
                            interesting = true;
                        }

                        actionTexts.Add(text);
                    }

                    var transitions = string.Join(", ", state.Transitions.Select(t => $"{t.EventName}->{t.ToState}"));
                    lines.Add($"    state '{state.Name}' [{transitions}]: {string.Join(" | ", actionTexts)}");
                }

                if (!interesting) {
                    continue;
                }

                sb.Clear();
                sb.Append($"DUMP fsm {GoPath(fsm.gameObject)} ({fsm.gameObject.scene.name}, " +
                          $"active={fsm.gameObject.activeInHierarchy}) '{fsm.FsmName}' start='{fsm.Fsm.StartState}' " +
                          $"current='{fsm.ActiveStateName}'\n");
                foreach (var line in lines) {
                    sb.Append(line).Append('\n');
                }

                Log(sb.ToString());
            } catch (Exception e) {
                Log($"DUMP fsm {GoPath(fsm.gameObject)} failed: {e.Message}");
            }
        }

        foreach (var behaviour in UnityEngine.Resources.FindObjectsOfTypeAll<MonoBehaviour>()) {
            if (behaviour is PlayMakerFSM || !behaviour.gameObject.scene.IsValid() ||
                !behaviour.gameObject.scene.isLoaded || behaviour.gameObject.scene.name == "DontDestroyOnLoad") {
                continue;
            }

            var ns = behaviour.GetType().Namespace ?? "";
            if (ns.StartsWith("Hkmp") || ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro")) {
                continue;
            }

            string text;
            try {
                text = DescribeObject(behaviour, true);
            } catch (Exception) {
                continue;
            }

            if (Interesting.IsMatch(text) || Interesting.IsMatch(behaviour.gameObject.name)) {
                Log($"DUMP comp {GoPath(behaviour.gameObject)} ({behaviour.gameObject.scene.name}, " +
                    $"active={behaviour.gameObject.activeInHierarchy} enabled={behaviour.enabled}): {text}");
            }
        }
    }

    private static string GoPath(GameObject go) {
        var path = go.name;
        var t = go.transform.parent;
        while (t != null) {
            path = t.name + "/" + path;
            t = t.parent;
        }

        return path;
    }

    private static string DescribeObject(object obj, bool privateFields = false) {
        var type = obj.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public;
        if (privateFields) {
            flags |= BindingFlags.NonPublic;
        }

        var parts = new List<string>();
        foreach (var field in type.GetFields(flags)) {
            if (field.DeclaringType == typeof(FsmStateAction) || field.DeclaringType == typeof(MonoBehaviour) ||
                field.DeclaringType == typeof(Behaviour) || field.DeclaringType == typeof(UnityEngine.Component)) {
                continue;
            }

            string value;
            try {
                value = DescribeValue(field.GetValue(obj), 0);
            } catch (Exception) {
                continue;
            }

            if (value != null) {
                parts.Add($"{field.Name}={value}");
            }
        }

        return $"{type.Name}({string.Join(", ", parts)})";
    }

    private static string DescribeValue(object value, int depth) {
        switch (value) {
            case null:
                return null;
            case string s:
                return string.IsNullOrEmpty(s) ? null : $"\"{s}\"";
            case bool or int or float or Enum:
                return value.ToString();
            case FsmString fs:
                return fs.UseVariable ? $"var:{fs.Name}" : $"\"{fs.Value}\"";
            case FsmBool fb:
                return fb.UseVariable ? $"var:{fb.Name}" : fb.Value.ToString();
            case FsmInt fi:
                return fi.UseVariable ? $"var:{fi.Name}" : fi.Value.ToString();
            case FsmFloat ff:
                return ff.UseVariable ? $"var:{ff.Name}" : ff.Value.ToString();
            case FsmEvent fe:
                return $"ev:{fe.Name}";
            case FsmGameObject fg:
                return fg.UseVariable ? $"var:{fg.Name}" : fg.Value ? fg.Value.name : null;
            case FsmOwnerDefault fo:
                return fo.OwnerOption == OwnerDefaultOption.UseOwner ? "owner" :
                    fo.GameObject?.Value ? fo.GameObject.Value.name : $"var:{fo.GameObject?.Name}";
            case GameObject go:
                return go.name;
            case NamedVariable nv:
                return $"var:{nv.Name}";
            case Object uo:
                return uo ? uo.name : null;
        }

        if (depth > 1) {
            return null;
        }

        if (value is System.Collections.IEnumerable enumerable) {
            var items = new List<string>();
            foreach (var item in enumerable) {
                items.Add(DescribeValue(item, depth + 1) ?? "-");
                if (items.Count > 12) {
                    break;
                }
            }

            return items.Count == 0 ? null : $"[{string.Join("; ", items)}]";
        }

        var type = value.GetType();
        if (type.IsValueType || type.IsSerializable) {
            var parts = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(f => {
                    try {
                        var v = DescribeValue(f.GetValue(value), depth + 1);
                        return v == null ? null : $"{f.Name}={v}";
                    } catch (Exception) {
                        return null;
                    }
                })
                .Where(p => p != null)
                .ToList();
            return parts.Count == 0 ? null : $"{{{string.Join(", ", parts)}}}";
        }

        return null;
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
