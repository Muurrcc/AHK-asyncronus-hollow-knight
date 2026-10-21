using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GlobalEnums;
using Hkmp.Game.Client;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;
using Object = UnityEngine.Object;
using T = Hkmp.Ui.DevTeleportTest;

namespace Hkmp.Ui;

/// <summary>
/// Development test of the teleport edge cases (inactive unless <c>-hkmp-test-tp p1|p2|p3</c> and
/// <c>-hkmp-test-tp-suite edge</c> are given). p1 is the target, p2 the teleporter, p3 (with
/// <c>-hkmp-test-tp-players 3</c>) a third player. Cases: target in the air above spikes and above a bottomless pit,
/// target inside a boss arena with closed gates, target in the middle of a scene change, repeated requests and double
/// click, two players teleporting to each other at once, teleporting in the air / paused / during an ability / focus /
/// bench / dialogue / dead, a stuck arrival (forced failure), blocked scenes (client and server), and three players
/// (cycling, /tp name). Lines in the ModLog start with <c>HKMP_TP</c>. With <c>-hkmp-test-tp-unsafe 1</c> also the
/// menu/cutscene scenes are tried (they break builds without the scene checks).
/// </summary>
internal static class DevTeleportEdgeTest {
    private const string StartScene = "Tutorial_01";
    private const string Town = "Town";
    private const string Crossroads = "Crossroads_01";
    private const string Arena = "Crossroads_10";

    /// <summary>
    /// Seconds between requests (longer than the request throttle of the client).
    /// </summary>
    private const float Gap = (float) TeleportRules.AnswerTimeout + 1f;

    private static string _role;
    private static UiManager _uiManager;
    private static NetClient _netClient;
    private static string _dir;
    private static int _players;
    private static bool _unsafe;

    /// <summary>
    /// Position the target hovers at (null: not hovering).
    /// </summary>
    private static Vector2? _hover;

    public static IEnumerator Run(string role, UiManager uiManager, NetClient netClient, string dir) {
        _role = role;
        _uiManager = uiManager;
        _netClient = netClient;
        _dir = dir;
        _players = int.TryParse(T.GetOption("-hkmp-test-tp-players"), out var players) ? players : 2;
        _unsafe = T.GetOption("-hkmp-test-tp-unsafe") == "1";
        MonoBehaviourUtil.Instance.StartCoroutine(HoverLoop());

        yield return T.WaitInGame();
        PlayerData.instance.isInvincible = true;
        yield return Barrier("ready");
        T.Log($"edge suite start, players={_players}, unsafe={_unsafe}, {T.State()}");

        switch (role) {
            case "p1":
                yield return Target();
                break;
            case "p2":
                yield return Teleporter();
                break;
            default:
                yield return Third();
                break;
        }

        T.Log($"SUMMARY edge");
        yield return Barrier("finished");
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> Roles => _players >= 3 ? new[] { "p1", "p2", "p3" } : new[] { "p1", "p2" };

    /// <summary>
    /// Wait until every role reached the barrier.
    /// </summary>
    private static IEnumerator Barrier(string name, float timeout = 600f, bool twoPlayers = false) {
        T.Signal($"{name}-{_role}");
        foreach (var role in twoPlayers ? new[] { "p1", "p2" } : Roles) {
            yield return T.WaitSignal($"{name}-{role}", timeout);
        }
    }

    private static IEnumerator HoverLoop() {
        while (true) {
            var hero = HeroController.instance;
            if (_hover.HasValue && hero != null) {
                hero.AffectedByGravity(false);
                hero.transform.SetPosition2D(_hover.Value);
                var body = hero.GetComponent<Rigidbody2D>();
                if (body) {
                    body.linearVelocity = Vector2.zero;
                }
            }

            yield return null;
        }
    }

    private static void StopHover() {
        if (_hover == null) {
            return;
        }

        _hover = null;
        HeroController.instance?.AffectedByGravity(true);
    }

    private static Vector2 HeroPos => HeroController.instance != null
        ? (Vector2) HeroController.instance.transform.position
        : Vector2.zero;

    private static void WriteTarget(string key) {
        var pos = HeroPos;
        T.Signal(key, $"{T.CurrentScene};{pos.x};{pos.y}");
    }

    private static bool ParseTarget(out string scene, out Vector2 pos) {
        var parts = (T._signalValue ?? "").Split(';');
        scene = parts.Length > 0 ? parts[0] : null;
        pos = Vector2.zero;
        if (parts.Length < 3) {
            return false;
        }

        pos = new Vector2(float.Parse(parts[1]), float.Parse(parts[2]));
        return true;
    }

    /// <summary>
    /// Go to a scene unless we are already there.
    /// </summary>
    private static IEnumerator Ensure(string scene, string gate) {
        if (T.CurrentScene != scene) {
            yield return T.GoTo(scene, gate);
        }
    }

    private static bool Settled() {
        var gm = global::GameManager.instance;
        var hc = HeroController.instance;
        return hc != null && !gm.IsInSceneTransition && gm.gameState == GameState.PLAYING && hc.acceptingInput &&
               !hc.controlReqlinquished && !gm.isPaused;
    }

    /// <summary>
    /// Wait for a teleport to be over: a moment for it to start, then until settled (or the timeout).
    /// </summary>
    private static IEnumerator WaitTeleport(float timeout = 20f) {
        yield return new WaitForSecondsRealtime(1.5f);
        yield return T.WaitUntil(Settled, timeout);
        yield return new WaitForSecondsRealtime(1.5f);
    }

    /// <summary>
    /// Whether the knight standing at the given position is touching a hazard (spikes, acid, lava, pit).
    /// </summary>
    private static string HazardAt(Vector2 position) {
        var hits = Physics2D.OverlapBoxAll(position, new Vector2(0.9f, 1.3f), 0f);
        foreach (var hit in hits) {
            var damage = hit != null ? hit.GetComponent<DamageHero>() ?? hit.GetComponentInParent<DamageHero>() : null;
            if (damage != null && damage.enabled && hit.enabled && damage.hazardType >= 2) {
                return $"{hit.name}(type {damage.hazardType})";
            }
        }

        return null;
    }

    // Arrival watch: hazard deaths and health changes after a teleport
    private static int _hazardDeaths;
    private static int _minHealth;
    private static bool _watching;
    private static int _watchNumber;

    private static IEnumerator Watch() {
        var number = ++_watchNumber;
        _hazardDeaths = 0;
        _minHealth = PlayerData.instance.health;
        _watching = true;
        var wasHazard = false;
        while (_watching && number == _watchNumber) {
            var hc = HeroController.instance;
            if (hc != null) {
                var hazard = hc.cState.hazardDeath;
                if (hazard && !wasHazard) {
                    _hazardDeaths++;
                    T.Log($"hazard death #{_hazardDeaths} at {hc.transform.position} ({T.State()})");
                }

                wasHazard = hazard;
                _minHealth = Mathf.Min(_minHealth, PlayerData.instance.health);
            }

            yield return null;
        }
    }

    /// <summary>
    /// p2 side of a teleport to p1: request, wait, check scene/position/health/soul, let p1 look at our remote player,
    /// then check control (screen, state, moving, attacking).
    /// </summary>
    private static IEnumerator TeleportAndCheck(
        string step,
        string label,
        Action request,
        string expectScene,
        Vector2? expectPos,
        float maxDistance,
        bool p1Checks
    ) {
        var pd = PlayerData.instance;
        var health = pd.health;
        var soul = pd.MPCharge;
        var started = TeleportManager.DevStartedCount;
        T.Log($"{step} {label}: before: {T.State()}");
        MonoBehaviourUtil.Instance.StartCoroutine(Watch());
        request();
        for (var i = 0; i < 4; i++) {
            yield return new WaitForSecondsRealtime(0.5f);
            T.Log($"{step} t+{(i + 1) * 0.5f:0.0}s: {T.State()}");
        }

        yield return WaitTeleport();
        // Keep watching a little for spike/acid loops after landing
        yield return new WaitForSecondsRealtime(2f);
        _watching = false;

        var pos = HeroPos;
        T.Check($"{step} {label}: teleports started", TeleportManager.DevStartedCount - started == 1,
            $"started={TeleportManager.DevStartedCount - started}");
        if (expectScene != null) {
            T.Check($"{step} {label}: in target scene", T.CurrentScene == expectScene,
                $"scene={T.CurrentScene} expected={expectScene}");
        }

        if (expectPos.HasValue) {
            var distance = Vector2.Distance(pos, expectPos.Value);
            T.Check($"{step} {label}: near target", distance < maxDistance,
                $"distance={distance:0.00} max={maxDistance} pos={pos} target={expectPos.Value}");
        }

        var hc = HeroController.instance;
        T.Check($"{step} {label}: no hazard death", _hazardDeaths == 0, $"hazardDeaths={_hazardDeaths}");
        T.Check($"{step} {label}: health intact", pd.health == health && _minHealth == health,
            $"health {health} -> {pd.health} (min {_minHealth})");
        T.Check($"{step} {label}: soul intact", pd.MPCharge == soul, $"soul {soul} -> {pd.MPCharge}");
        var hazard = HazardAt(pos);
        T.Check($"{step} {label}: standing clear of hazards", hazard == null, $"hazard={hazard} pos={pos}");
        T.Check($"{step} {label}: hazard respawn point safe",
            HazardAt(pd.hazardRespawnLocation) == null && Physics2D.Raycast(pd.hazardRespawnLocation, Vector2.down, 50f, 256).collider != null,
            $"hazardRespawnLocation={pd.hazardRespawnLocation}");
        T.Log($"{step} after: onGround={hc?.cState.onGround} {T.State()}");

        T.Signal($"{step}-arrived", $"{T.CurrentScene};{pos.x};{pos.y}");
        if (p1Checks) {
            yield return T.WaitSignal($"{step}-p1-checked", 30f);
        }

        yield return T.CheckControl(step, label, expectScene);
    }

    /// <summary>
    /// p2 side of a request that must NOT teleport: nothing starts or the teleport is cancelled, the scene stays the
    /// same and the hero keeps its control.
    /// </summary>
    private static IEnumerator NoTeleportAndCheck(string step, string label, Action request, float wait = 6f,
        Action restore = null, bool countMustNotChange = true) {
        yield return T.WaitUntil(() => Settled() && HeroController.instance.hero_state != ActorStates.no_input, 10f);
        yield return new WaitForSecondsRealtime(0.5f);
        var scene = T.CurrentScene;
        var pos = HeroPos;
        var started = TeleportManager.DevStartedCount;
        T.Log($"{step} {label}: before: {T.State()}");
        request();
        var waited = 0f;
        while (waited < wait) {
            yield return new WaitForSecondsRealtime(0.5f);
            waited += 0.5f;
            if (((int) (waited * 2)) % 2 == 0) {
                T.Log($"{step} t+{waited:0.0}s: {T.State()}");
            }
        }

        restore?.Invoke();
        yield return T.WaitUntil(Settled, 25f);
        yield return new WaitForSecondsRealtime(1f);
        if (countMustNotChange) {
            T.Check($"{step} {label}: no teleport started", TeleportManager.DevStartedCount == started,
                $"started={TeleportManager.DevStartedCount - started}");
        }

        T.Check($"{step} {label}: scene unchanged", T.CurrentScene == scene, $"scene={T.CurrentScene} before={scene}");
        T.Check($"{step} {label}: position unchanged (x)", Mathf.Abs(HeroPos.x - pos.x) < 3f,
            $"pos={HeroPos} before={pos}");
        yield return T.CheckControl(step, label, null);
        if (T.CurrentScene != scene) {
            T.Log($"{step}: going back to {scene}");
            yield return T.GoTo(scene, "left1");
        }
    }

    /// <summary>
    /// p1 side: after p2 arrived, check that its remote player is shown near its reported position.
    /// </summary>
    private static IEnumerator RemoteCheck(string step) {
        yield return T.WaitSignal($"{step}-arrived", 120f);
        ParseTarget(out var scene, out var reported);
        if (scene == T.CurrentScene) {
            yield return new WaitForSecondsRealtime(1.5f);
            var remote = TeleportManager.Instance.DevPlayers.FirstOrDefault(p => p.Username == "P2");
            var active = remote != null && remote.IsInLocalScene && remote.PlayerContainer != null &&
                         remote.PlayerContainer.activeInHierarchy;
            var shown = active ? (Vector2) remote.PlayerContainer.transform.position : Vector2.zero;
            T.Check($"{step} target: remote P2 shown", active, $"inLocalScene={remote?.IsInLocalScene}");
            T.Check($"{step} target: remote P2 near its position", active && Vector2.Distance(shown, reported) < 3f,
                $"shown={shown} reported={reported}");
        } else {
            T.Log($"{step}: P2 not in our scene ({T._signalValue})");
        }

        T.Signal($"{step}-p1-checked");
    }

    // ------------------------------------------------------------------ p1: target

    private static IEnumerator Target() {
        // e1: in the air above spikes (King's Pass)
        yield return Ensure(StartScene, "right1");
        yield return T.WaitSignal("e1-ready");
        var spike = FindSpikeHover();
        if (spike.HasValue) {
            _hover = spike.Value;
            yield return new WaitForSecondsRealtime(1.5f);
            T.Log($"e1 hovering above spikes at {HeroPos}");
            WriteTarget("e1-target");
        } else {
            T.Check("e1 target: spikes found", false, "no spikes with ground below in " + T.CurrentScene);
            T.Signal("e1-target", "none");
        }

        yield return RemoteCheck("e1");
        yield return T.WaitSignal("e1-done", 300f);
        StopHover();

        // e2: in the air above a bottomless pit (the well of Dirtmouth)
        yield return T.GoTo(Town, "left1");
        var pit = FindPitHover();
        if (pit.HasValue) {
            _hover = pit.Value;
            yield return new WaitForSecondsRealtime(1.5f);
            T.Log($"e2 hovering above the well at {HeroPos}");
            WriteTarget("e2-target");
        } else {
            T.Check("e2 target: pit found", false, "no point without ground found");
            T.Signal("e2-target", "none");
        }

        yield return RemoteCheck("e2");
        yield return T.WaitSignal("e2-done", 300f);
        StopHover();

        // e3: inside the False Knight arena with its gates closed
        yield return T.GoTo(Arena, "left1");
        yield return StartArena("e3");
        WriteTarget("e3-target");
        yield return RemoteCheck("e3");
        yield return T.WaitSignal("e3-done", 300f);

        // e4: change scene right when p2 asks (twice, with different timing), then wait for p2's retry
        yield return T.WaitSignal("e4-ready", 300f);
        T.Signal("e4a-go");
        yield return T.GoTo(Crossroads, "right1");
        yield return T.WaitSignal("e4a-done", 300f);
        T.Signal("e4b-go");
        yield return T.GoTo(Town, "left1");
        WriteTarget("e4-settled");
        yield return T.WaitSignal("e4-done", 300f);

        // e5: repeated requests / double click: target in another scene
        yield return T.GoTo(Crossroads, "right1");
        WriteTarget("e5-target");
        yield return T.WaitSignal("e5-done", 300f);

        // e6: both teleport to each other at the same time
        yield return T.GoTo(Town, "left1");
        yield return Barrier("e6-go", 300f, true);
        var e6Started = TeleportManager.DevStartedCount;
        TeleportManager.Instance.Request("P2");
        yield return WaitTeleport();
        yield return new WaitForSecondsRealtime(3f);
        T.Signal("e6-p1-scene", T.CurrentScene);
        yield return T.WaitSignal("e6-p2-scene", 120f);
        T.Check("e6 mutual: both end in the same scene", T._signalValue == T.CurrentScene,
            $"p1={T.CurrentScene} p2={T._signalValue} p1Started={TeleportManager.DevStartedCount - e6Started}");
        yield return T.CheckControl("e6-p1", "mutual teleport (p1)", null);
        yield return Barrier("e6-done", 300f, true);

        // e7: p2 tries teleporting in bad states; e8: blocked scenes. Stay in Crossroads_01 as target
        yield return Ensure(Crossroads, "right1");
        T.Signal("e7-target-scene", T.CurrentScene);
        yield return T.WaitSignal("e7-done", 900f);
        yield return T.WaitSignal("e8-server-go", 600f);
        // Target inside a Colosseum trial: the server must refuse
        yield return T.GoTo("Room_Colosseum_Bronze", "left1");
        WriteTarget("e8-server-target");
        yield return T.WaitSignal("e8-server-done", 300f);
        yield return T.GoTo(Town, "left1");
        T.Signal("e8-p1-back");

        if (_players >= 3) {
            yield return Barrier("e9-ready");
            yield return T.WaitSignal("e9-done", 600f);
        }
    }

    /// <summary>
    /// A point in the air 2.5 units above spikes (with ground below the spikes) of the current scene.
    /// </summary>
    private static Vector2? FindSpikeHover() {
        var hero = HeroPos;
        var candidates = new List<(Vector2 pos, float dist)>();
        foreach (var damage in Object.FindObjectsOfType<DamageHero>()) {
            if (damage == null || !damage.isActiveAndEnabled || damage.hazardType != 2) {
                continue;
            }

            var collider = damage.GetComponent<Collider2D>();
            if (collider == null || !collider.enabled) {
                continue;
            }

            var bounds = collider.bounds;
            var point = new Vector2(bounds.center.x, bounds.max.y + 2.5f);
            if (Physics2D.OverlapPoint(point, 256) != null) {
                continue;
            }

            var ground = Physics2D.Raycast(point, Vector2.down, 50f, 256);
            if (ground.collider == null || HazardAt(ground.point + new Vector2(0f, 0.6f)) == null) {
                continue;
            }

            candidates.Add((point, Vector2.Distance(point, hero)));
        }

        T.Log($"spike hover candidates: {candidates.Count}");
        return candidates.Count == 0 ? null : candidates.OrderBy(c => c.dist).First().pos;
    }

    /// <summary>
    /// A point in the air without any ground below (the well of Dirtmouth, above its bottom transition).
    /// </summary>
    private static Vector2? FindPitHover() {
        var gates = TransitionPoint.TransitionPoints.Where(t => t != null && t.name.StartsWith("bot")).ToList();
        T.Log($"pit gates: {string.Join(" ", gates.Select(g => $"{g.name}@{g.transform.position}"))}");
        foreach (var gate in gates) {
            foreach (var dy in new[] { 4f, 6f, 3f, 8f, 10f }) {
                foreach (var dx in new[] { 0f, -1f, 1f }) {
                    var point = (Vector2) gate.transform.position + new Vector2(dx, dy);
                    if (Physics2D.OverlapPoint(point, 256) != null) {
                        continue;
                    }

                    if (Physics2D.Raycast(point, Vector2.down, 50f, 256).collider == null) {
                        return point;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walk into the battle scene trigger of the arena and wait for its gates to close.
    /// </summary>
    private static IEnumerator StartArena(string step) {
        var battle = Object.FindObjectsOfType<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Battle Control");
        if (battle == null) {
            T.Check($"{step} target: battle scene found", false, "no Battle Control FSM");
            yield break;
        }

        var collider = battle.GetComponents<Collider2D>().FirstOrDefault(c => c.enabled);
        T.Log($"{step} battle scene {battle.name} state={battle.ActiveStateName} bounds={collider?.bounds}");
        if (collider != null) {
            var center = collider.bounds.center;
            HeroController.instance.transform.SetPosition2D(center.x, center.y);
        }

        yield return T.WaitUntil(() => battle.ActiveStateName != "Detect", 10f);
        yield return new WaitForSecondsRealtime(5f);
        var gates = GateStates();
        T.Log($"{step} battle state={battle.ActiveStateName} gates: {gates}");
        T.Check($"{step} target: arena started and gates closed", battle.ActiveStateName != "Detect" && gates.Contains("Close"),
            $"battle={battle.ActiveStateName} gates={gates}");
    }

    private static string GateStates() {
        return string.Join(" ", Object.FindObjectsOfType<PlayMakerFSM>()
            .Where(f => f.FsmName == "BG Control")
            .Select(f => $"{f.name}={f.ActiveStateName}"));
    }

    // ------------------------------------------------------------------ p2: teleporter

    private static IEnumerator Teleporter() {
        // e1: other scene, target above spikes; damage enabled to see spike hits
        yield return T.GoTo(Town, "left1");
        T.Signal("e1-ready");
        yield return T.WaitSignal("e1-target", 300f);
        if (ParseTarget(out var scene, out var target)) {
            PlayerData.instance.isInvincible = false;
            yield return TeleportAndCheck("e1", "target in the air above spikes", () => TeleportManager.Instance.Request("P1"),
                scene, target, 40f, true);
            PlayerData.instance.isInvincible = true;
        }

        T.Signal("e1-done");

        // e2: target above a bottomless pit (we are in King's Pass now)
        yield return T.WaitSignal("e2-target", 300f);
        if (ParseTarget(out scene, out target)) {
            yield return TeleportAndCheck("e2", "target in the air above a pit", () => TeleportManager.Instance.Request("P1"),
                scene, target, 25f, true);
        }

        T.Signal("e2-done");

        // e3: target inside an arena with closed gates
        yield return T.WaitSignal("e3-target", 300f);
        if (ParseTarget(out scene, out target)) {
            yield return TeleportAndCheck("e3", "target in a boss arena with closed gates",
                () => TeleportManager.Instance.Request("P1"), scene, target, 5f, true);
            var battle = Object.FindObjectsOfType<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Battle Control");
            var gateXs = Object.FindObjectsOfType<PlayMakerFSM>().Where(f => f.FsmName == "BG Control")
                .Select(f => f.transform.position.x).ToList();
            var inside = gateXs.Count >= 2 && gateXs.Min() - 1f <= HeroPos.x && HeroPos.x <= gateXs.Max() + 1f;
            T.Log($"e3 p2 side: battle={battle?.ActiveStateName} gates: {GateStates()}");
            T.Check("e3 arena: p2 between the arena gates", inside,
                $"pos={HeroPos} gates x={string.Join(",", gateXs.Select(x => x.ToString("0.0")))}");
        }

        T.Signal("e3-done");

        // e4: target changes scene right when we ask (a: at once, b: 1 s later)
        T.Signal("e4-ready");
        yield return T.WaitSignal("e4a-go", 300f);
        yield return MidChange("e4a", 0f);
        T.Signal("e4a-done");
        yield return T.WaitSignal("e4b-go", 300f);
        yield return MidChange("e4b", 1f);
        yield return T.WaitSignal("e4-settled", 300f);
        if (ParseTarget(out scene, out target)) {
            yield return new WaitForSecondsRealtime(Gap);
            if (T.CurrentScene == scene) {
                T.Log("e4 retry: already in the target scene");
            }

            yield return TeleportAndCheck("e4", "retry after the target settled", () => TeleportManager.Instance.Request("P1"),
                scene, target, 4f, false);
        }

        T.Signal("e4-done");

        // e5: five requests in one frame, then a double click on the pause menu button
        yield return T.WaitSignal("e5-target", 300f);
        if (ParseTarget(out scene, out target)) {
            yield return new WaitForSecondsRealtime(Gap);
            yield return TeleportAndCheck("e5a", "four /tp P1 0.3 s apart", () => {
                TeleportManager.Instance.Request("P1");
                for (var i = 1; i < 4; i++) {
                    MonoBehaviourUtil.Instance.StartCoroutine(After(0.3f * i, () => TeleportManager.Instance.Request("P1")));
                }
            }, scene, target, 4f, false);

            yield return new WaitForSecondsRealtime(Gap);
            HeroController.instance.transform.SetPosition2D(HeroPos + new Vector2(-4f, 0f));
            UIManager.instance.TogglePauseGame();
            yield return new WaitForSecondsRealtime(1.5f);
            var started = TeleportManager.DevStartedCount;
            T.Log($"e5b paused: {T.State()}");
            _uiManager.DevPressTeleport();
            _uiManager.DevPressTeleport();
            yield return new WaitForSecondsRealtime(0.3f);
            _uiManager.DevPressTeleport();
            yield return WaitTeleport();
            yield return new WaitForSecondsRealtime(2f);
            var gm = global::GameManager.instance;
            T.Check("e5b double click: at most one teleport", TeleportManager.DevStartedCount - started <= 1,
                $"started={TeleportManager.DevStartedCount - started}");
            T.Check("e5b double click: not paused again", !gm.isPaused && UIManager.instance.uiState == UIState.PLAYING,
                $"paused={gm.isPaused} ui={UIManager.instance.uiState}");
            // The button cycles through P1 and P3, so the scene is not checked
            yield return T.CheckControl("e5b", "double click on the button", null);
        }

        T.Signal("e5-done");

        // e6: both at once
        yield return Barrier("e6-go", 300f, true);
        TeleportManager.Instance.Request("P1");
        yield return WaitTeleport();
        yield return new WaitForSecondsRealtime(3f);
        T.Signal("e6-p2-scene", T.CurrentScene);
        yield return T.CheckControl("e6-p2", "mutual teleport (p2)", null);
        yield return Barrier("e6-done", 300f, true);

        // e7: bad states of the teleporter; target stays in Crossroads_01
        yield return T.WaitSignal("e7-target-scene", 300f);
        var targetScene = T._signalValue;
        yield return BadStates(targetScene);
        T.Signal("e7-done");

        // e8: blocked scenes
        yield return BlockedScenes();
        T.Signal("e8-server-go");
        yield return T.WaitSignal("e8-server-target", 300f);
        yield return new WaitForSecondsRealtime(Gap);
        yield return NoTeleportAndCheck("e8-server", "target in a Colosseum trial (server refuses)",
            () => TeleportManager.Instance.Request("P1"), 8f);
        T.Signal("e8-server-done");
        yield return T.WaitSignal("e8-p1-back", 300f);

        if (_players >= 3) {
            yield return Ensure(Crossroads, "right1");
            yield return Barrier("e9-ready");
            yield return T.WaitSignal("e9-done", 600f);
        }
    }

    /// <summary>
    /// Ask to teleport while the target is changing scene: whatever happens, we must end with control in a loaded
    /// scene.
    /// </summary>
    private static IEnumerator MidChange(string step, float delay) {
        if (delay > 0f) {
            yield return new WaitForSecondsRealtime(delay);
        }

        var started = TeleportManager.DevStartedCount;
        T.Log($"{step} request while the target changes scene: {T.State()}");
        TeleportManager.Instance.Request("P1");
        yield return WaitTeleport(30f);
        yield return new WaitForSecondsRealtime(2f);
        T.Log($"{step} result: started={TeleportManager.DevStartedCount - started} {T.State()}");
        yield return T.CheckControl(step, "request while the target changes scene", null);
    }

    private static IEnumerator BadStates(string targetScene) {
        var hc = HeroController.instance;
        Action tp = () => TeleportManager.Instance.Request("P1");

        // a: in the air
        yield return Ensure(Town, "left1");
        yield return new WaitForSecondsRealtime(Gap);
        yield return TeleportAndCheck("e7a", "in the air", () => {
            HeroController.instance.transform.SetPosition2D(HeroPos + new Vector2(0f, 4f));
            tp();
        }, targetScene, null, 0f, false);

        // b: pause menu open (Esc)
        yield return T.GoTo(Town, "left1");
        UIManager.instance.TogglePauseGame();
        yield return new WaitForSecondsRealtime(1.5f);
        yield return TeleportAndCheck("e7b", "pause menu open", tp, targetScene, null, 0f, false);

        // c: dive in progress (simulated state, ends after 1 s): waits and then teleports
        yield return T.GoTo(Town, "left1");
        yield return new WaitForSecondsRealtime(Gap);
        yield return TeleportAndCheck("e7c", "during a dive (simulated, 1 s)", () => {
            HeroController.instance.cState.spellQuake = true;
            MonoBehaviourUtil.Instance.StartCoroutine(After(1f, () => HeroController.instance.cState.spellQuake = false));
            tp();
        }, targetScene, null, 0f, false);

        // d: super dash that lasts (simulated, 5 s): cancelled
        yield return T.GoTo(Town, "left1");
        yield return new WaitForSecondsRealtime(Gap);
        yield return NoTeleportAndCheck("e7d", "during a long super dash (simulated, 5 s)", () => {
            HeroController.instance.cState.superDashing = true;
            MonoBehaviourUtil.Instance.StartCoroutine(After(5f, () => HeroController.instance.cState.superDashing = false));
            tp();
        }, 7f, null, false);

        // e: focusing (simulated, 1 s): waits and then teleports
        yield return new WaitForSecondsRealtime(Gap);
        yield return TeleportAndCheck("e7e", "while focusing (simulated, 1 s)", () => {
            HeroController.instance.cState.focusing = true;
            MonoBehaviourUtil.Instance.StartCoroutine(After(1f, () => HeroController.instance.cState.focusing = false));
            tp();
        }, targetScene, null, 0f, false);

        // f: sitting on a bench (simulated: atBench + control relinquished)
        yield return T.GoTo(Town, "left1");
        yield return new WaitForSecondsRealtime(Gap);
        yield return NoTeleportAndCheck("e7f", "sitting on a bench (simulated)", () => {
            PlayerData.instance.SetBool("atBench", true);
            HeroController.instance.RelinquishControl();
            tp();
        }, 5f, () => {
            PlayerData.instance.SetBool("atBench", false);
            HeroController.instance.RegainControl();
        }, false);

        // g: in a dialogue (simulated: control relinquished)
        yield return new WaitForSecondsRealtime(Gap);
        yield return NoTeleportAndCheck("e7g", "in a dialogue (simulated)", () => {
            HeroController.instance.RelinquishControl();
            tp();
        }, 5f, () => HeroController.instance.RegainControl(), false);

        // h: half-paused state of the old button (isPaused set, input handler stopped)
        yield return new WaitForSecondsRealtime(Gap);
        yield return TeleportAndCheck("e7h", "half-paused (isPaused set, input stopped)", () => {
            global::GameManager.instance.isPaused = true;
            InputHandler.Instance.StopAcceptingInput();
            tp();
        }, targetScene, null, 0f, false);

        // i: arrival gets stuck (something takes the control right when the scene loaded): restored
        yield return T.GoTo(Town, "left1");
        yield return new WaitForSecondsRealtime(Gap);
        var stuckStarted = TeleportManager.DevStartedCount;
        tp();
        yield return T.WaitUntil(() => T.CurrentScene == targetScene && !global::GameManager.instance.IsInSceneTransition, 30f);
        HeroController.instance.RelinquishControl();
        T.Log($"e7i forced stuck arrival: {T.State()}");
        yield return new WaitForSecondsRealtime(12f);
        T.Check("e7i stuck arrival: teleport started", TeleportManager.DevStartedCount - stuckStarted == 1,
            $"started={TeleportManager.DevStartedCount - stuckStarted}");
        yield return T.CheckControl("e7i", "stuck arrival (forced failure)", targetScene);

        // j: dead (real death): no teleport, control after respawning
        yield return T.GoTo(Town, "left1");
        yield return new WaitForSecondsRealtime(Gap);
        var pd = PlayerData.instance;
        pd.isInvincible = false;
        pd.SetInt("healthBlue", 0);
        pd.SetInt("health", 1);
        yield return T.WaitUntil(() => HeroController.instance.transitionState == HeroTransitionState.WAITING_TO_TRANSITION &&
                                       !HeroController.instance.cState.invulnerable, 10f);
        var source = new GameObject("HKMP Test Damage Source");
        source.transform.position = HeroController.instance.transform.position + Vector3.left;
        HeroController.instance.TakeDamage(source, CollisionSide.left, 1, 1);
        yield return T.WaitUntil(() => HeroController.instance.cState.dead, 5f);
        Object.Destroy(source);
        var deadStarted = TeleportManager.DevStartedCount;
        T.Log($"e7j dead={HeroController.instance.cState.dead}: {T.State()}");
        tp();
        yield return new WaitForSecondsRealtime(4f);
        T.Check("e7j dead: no teleport started", TeleportManager.DevStartedCount == deadStarted,
            $"started={TeleportManager.DevStartedCount - deadStarted}");
        yield return T.WaitUntil(Settled, 90f);
        yield return new WaitForSecondsRealtime(3f);
        PlayerData.instance.isInvincible = true;
        PlayerData.instance.SetInt("health", PlayerData.instance.maxHealth);
        yield return T.CheckControl("e7j", "after dying with a teleport request", null);
    }

    private static IEnumerator After(float seconds, Action action) {
        yield return new WaitForSecondsRealtime(seconds);
        try {
            action();
        } catch (Exception e) {
            T.Log($"after action failed: {e.Message}");
        }
    }

    private static IEnumerator BlockedScenes() {
        yield return Ensure(Crossroads, "right1");
        var scenes = new List<string> {
            "GG_Atrium", "GG_Hornet_1", "Dream_01_False_Knight", "White_Palace_01", "Room_Colosseum_Bronze"
        };
        if (_unsafe) {
            scenes.AddRange(new[] { "Menu_Title", "Opening_Sequence", "Cinematic_Stag_travel", "" });
        }

        foreach (var scene in scenes) {
            yield return NoTeleportAndCheck($"e8-{(scene == "" ? "empty" : scene)}", $"forged teleport to {scene}",
                () => TeleportManager.Instance.OnPlayerTeleport(new PlayerTeleport { SceneName = scene, X = 10f, Y = 10f }),
                6f, null, false);
            if (T.CurrentScene != Crossroads) {
                yield return T.WaitUntil(Settled, 30f);
                yield return T.GoTo(Crossroads, "right1");
            }
        }
    }

    // ------------------------------------------------------------------ p3: third player

    private static IEnumerator Third() {
        // Wait until p1 and p2 are done with their two-player cases
        yield return Barrier("e9-ready", 1800f);
        yield return new WaitForSecondsRealtime(3f);
        var players = TeleportManager.Instance.DevPlayers.OrderBy(p => p.Id).ToList();
        T.Log($"e9 players: {string.Join(" ", players.Select(p => $"{p.Id}:{p.Username}"))}");
        // Scenes of the others (p1 in Town, p2 in Crossroads_01)
        var sceneOf = new Dictionary<string, string> { { "P1", Town }, { "P2", Crossroads } };

        if (players.Count >= 2) {
            yield return TeleportAndCheck3("e9a", $"button cycling: first ({players[0].Username})",
                () => TeleportManager.Instance.Request(), sceneOf[players[0].Username]);
            yield return new WaitForSecondsRealtime(Gap);
            yield return TeleportAndCheck3("e9b", $"button cycling: next ({players[1].Username})",
                () => TeleportManager.Instance.Request(), sceneOf[players[1].Username]);
            yield return new WaitForSecondsRealtime(Gap);
            yield return TeleportAndCheck3("e9c", "/tp P1 (change of target)", () => TeleportManager.Instance.Request("P1"),
                Town);
            yield return new WaitForSecondsRealtime(Gap);
            yield return NoTeleportAndCheck("e9d", "/tp p (ambiguous name)", () => TeleportManager.Instance.Request("p"), 4f);
            yield return NoTeleportAndCheck("e9e", "/tp Nobody (unknown name)", () => TeleportManager.Instance.Request("Nobody"), 4f);
        } else {
            T.Check("e9 three players", false, $"only {players.Count} other players");
        }

        T.Signal("e9-done");
    }

    private static IEnumerator TeleportAndCheck3(string step, string label, Action request, string expectScene) {
        var started = TeleportManager.DevStartedCount;
        var health = PlayerData.instance.health;
        T.Log($"{step} {label}: before: {T.State()}");
        request();
        yield return WaitTeleport();
        yield return new WaitForSecondsRealtime(1f);
        T.Check($"{step} {label}: one teleport", TeleportManager.DevStartedCount - started == 1,
            $"started={TeleportManager.DevStartedCount - started}");
        T.Check($"{step} {label}: in target scene", T.CurrentScene == expectScene,
            $"scene={T.CurrentScene} expected={expectScene}");
        T.Check($"{step} {label}: health intact", PlayerData.instance.health == health,
            $"health {health} -> {PlayerData.instance.health}");
        yield return T.CheckControl(step, label, expectScene);
    }
}
