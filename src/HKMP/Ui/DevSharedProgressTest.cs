using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GlobalEnums;
using Hkmp.Networking.Client;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <summary>
/// Development test for shared progress (inactive unless <c>-hkmp-test-shared p1|p2</c> is given, needs
/// <c>-hkmp-connect</c> too). p1 joins first; p2 joins with progress stored per player by an older version
/// (seeded in the server save), which must be merged into the shared progress and reach p1 live. Then each player
/// in turn obtains masks, soul vessels, spells, abilities and story flags and the other one checks that it has them
/// right away (including the HUD and without being healed), that the personal state (geo, health) stays
/// individual and that a reconnect gives the shared progress back. Lines in the ModLog start with
/// <c>HKMP_SHARED</c>; the instances synchronise through flag files in <c>-hkmp-test-dir</c>.
/// </summary>
internal static class DevSharedProgressTest {
    private static NetClient _netClient;
    private static string _role;
    private static string _dir;
    private static int _pass;
    private static int _fail;
    private static string _signalValue;

    private static bool IsP1 => _role == "p1";
    private static string Other => IsP1 ? "p2" : "p1";

    public static void TryStart(NetClient netClient) {
        _role = GetOption("-hkmp-test-shared")?.ToLowerInvariant();
        if (_role != "p1" && _role != "p2") {
            return;
        }

        // Test instances are muted (the user hears them otherwise)
        AudioListener.volume = 0f;
        _netClient = netClient;
        _dir = GetOption("-hkmp-test-dir") ?? "E:/HKMP-Enemies/testrun-shared";
        Directory.CreateDirectory(_dir);
        Log($"start role={_role} dir={_dir}");
        MonoBehaviourUtil.Instance.StartCoroutine(Run());
    }

    private static void Log(string message) => Logger.Info($"HKMP_SHARED [{_role}] {message}");

    private static void Check(string name, bool ok, string detail) {
        if (ok) {
            _pass++;
        } else {
            _fail++;
        }

        Logger.Info($"HKMP_SHARED {(ok ? "PASS" : "FAIL")} [{_role}] {name}: {detail}");
    }

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

    private static bool HeroReady() =>
        HeroController.instance != null && _netClient.IsConnected &&
        global::GameManager.instance.gameState == GameState.PLAYING &&
        HeroController.instance.acceptingInput && !global::GameManager.instance.IsInSceneTransition;

    private static string ReadyState() {
        var hc = HeroController.instance;
        var gm = global::GameManager.instance;
        return $"hero={hc != null} connected={_netClient.IsConnected} state={(gm ? gm.gameState.ToString() : "-")} " +
               $"accepting={(hc != null && hc.acceptingInput)} transition={(gm && gm.IsInSceneTransition)} " +
               $"paused={(gm && gm.isPaused)} heroState={(hc != null ? hc.hero_state.ToString() : "-")} " +
               $"controlRelinquished={(hc != null && hc.controlReqlinquished)} " +
               $"scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}";
    }

    private static IEnumerator WaitInGame() {
        var waited = 0f;
        while (!HeroReady() && waited < 600f) {
            // The second instance sometimes stays without input after the wake up animation (also without this
            // change, unfocused window); after 30 s in game give the input back so the test can go on
            var hc = HeroController.instance;
            var gm = global::GameManager.instance;
            if (waited >= 30f && hc != null && !hc.acceptingInput && _netClient.IsConnected && gm &&
                gm.gameState == GameState.PLAYING && !gm.IsInSceneTransition && !hc.controlReqlinquished) {
                Log($"hero idle without input for 30 s, calling AcceptInput: {ReadyState()}");
                hc.AcceptInput();
            }

            yield return new WaitForSecondsRealtime(1f);
            waited += 1f;
            if ((int) waited % 20 == 0) {
                Log($"waiting for game: {ReadyState()}");
            }
        }

        yield return new WaitForSecondsRealtime(3f);
        Log($"in game scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
    }

    private static string Snap() {
        var pd = PlayerData.instance;
        return $"maxHealthBase={pd.maxHealthBase} heartPieces={pd.heartPieces} maxHealth={pd.maxHealth} " +
               $"health={pd.health} MPReserveMax={pd.MPReserveMax} vesselFragments={pd.vesselFragments} " +
               $"MPReserve={pd.MPReserve} MPCharge={pd.MPCharge} fireball={pd.fireballLevel} quake={pd.quakeLevel} " +
               $"scream={pd.screamLevel} hasSpell={pd.hasSpell} hasDash={pd.hasDash} canDash={pd.canDash} " +
               $"hasWalljump={pd.hasWalljump} hasDoubleJump={pd.hasDoubleJump} hasSuperDash={pd.hasSuperDash} " +
               $"hasShadowDash={pd.hasShadowDash} hasDreamNail={pd.hasDreamNail} dreamNailUpgraded={pd.dreamNailUpgraded} " +
               $"hasKingsBrand={pd.hasKingsBrand} hasLantern={pd.hasLantern} metElderbug={pd.metElderbug} " +
               $"mapCrossroads={pd.mapCrossroads} visitedGreenpath={pd.visitedGreenpath} " +
               $"xunRewardGiven={pd.xunRewardGiven} geo={pd.geo} respawnScene={pd.respawnScene}";
    }

    private static readonly Regex HealthMaskName = new(@"^Health \d+$");

    /// <summary>
    /// Count the masks shown in the HUD: "Health N" objects of the HUD that are active and whose display FSM is
    /// not in an idle/hidden state. Also logs every mask object with its FSM state, for inspection.
    /// </summary>
    private static int HudMasks(string label) {
        var masks = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>()
            .Where(g => g.scene.IsValid() && HealthMaskName.IsMatch(g.name))
            .OrderBy(g => int.Parse(g.name.Substring(7)))
            .ToList();
        var shown = 0;
        var parts = new List<string>();
        foreach (var mask in masks) {
            var fsm = mask.GetComponent<PlayMakerFSM>();
            var state = fsm ? fsm.ActiveStateName : "-";
            // Mask FSM states: "Inactive" = mask not owned, "Idle" = full, "Empty" = damaged
            if (mask.activeInHierarchy && state != "Inactive") {
                shown++;
            }

            parts.Add($"{mask.name}:{(mask.activeInHierarchy ? "on" : "off")}/{state}");
        }

        Log($"hud {label}: {shown} of {masks.Count} masks owned; {string.Join(" ", parts)}");
        return shown;
    }

    /// <summary>
    /// Log the soul vessel HUD objects (names containing "Vessel" in the HUD) with their state.
    /// </summary>
    private static int HudVessels(string label) {
        var vessels = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>()
            .Where(g => g.scene.IsValid() && Regex.IsMatch(g.name, @"^Vessel \d+$"))
            .OrderBy(g => g.name)
            .ToList();
        // Vessel FSM state "Not Obtained" = vessel not owned
        var shown = vessels.Count(v => {
            var fsm = v.GetComponent<PlayMakerFSM>();
            return v.activeInHierarchy && (fsm == null || fsm.ActiveStateName != "Not Obtained");
        });
        var parts = vessels.Select(v => {
            var fsm = v.GetComponent<PlayMakerFSM>();
            return $"{v.name}:{(v.activeInHierarchy ? "on" : "off")}/{(fsm ? fsm.ActiveStateName : "-")}";
        });
        Log($"hud {label}: {shown} of {vessels.Count} vessels owned; {string.Join(" ", parts)}");
        return shown;
    }

    private static void Screenshot(string label) {
        try {
            var path = Path.Combine(_dir, $"{_role}-{label}.png");
            ScreenCapture.CaptureScreenshot(path);
            Log($"screenshot {path}");
        } catch (Exception e) {
            Log($"screenshot failed: {e.Message}");
        }
    }

    private static PlayMakerFSM HeroFsm(string name) =>
        HeroController.instance.gameObject.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == name);

    /// <summary>
    /// Try to cast Vengeful Spirit through the hero's Spell Control FSM; returns whether soul was spent (the spell
    /// was cast). The spell level is not touched: the FSM decides with the current (synchronised) level.
    /// </summary>
    private static IEnumerator TryQuickCast(Action<bool> result) {
        var pd = PlayerData.instance;
        pd.MPCharge = 99;
        GameCameras.instance.soulOrbFSM.SendEvent("MP SET");
        yield return new WaitForSecondsRealtime(0.5f);
        var fsm = HeroFsm("Spell Control");
        var before = pd.MPCharge;
        fsm?.SendEvent("QUICK CAST");
        yield return new WaitForSecondsRealtime(2f);
        var spent = pd.MPCharge < before;
        Log($"quick cast: fireballLevel={pd.fireballLevel} MP {before}->{pd.MPCharge} state='{fsm?.ActiveStateName}'");
        result(spent);
    }

    /// <summary>
    /// Obtain one mask the way the game does when the fourth mask shard is picked up.
    /// </summary>
    private static IEnumerator ObtainMaskByShards() {
        var pd = PlayerData.instance;
        while (pd.heartPieces < 3) {
            pd.heartPieces++;
            Log($"picked mask shard, heartPieces={pd.heartPieces}");
            yield return new WaitForSecondsRealtime(0.7f);
        }

        pd.heartPieces = 0;
        HeroController.instance.AddToMaxHealth(1);
        PlayMakerFSM.BroadcastEvent("MAX HP UP");
        Log($"obtained mask: [{Snap()}]");
    }

    /// <summary>
    /// Obtain one soul vessel the way the game does when the third vessel fragment is picked up.
    /// </summary>
    private static IEnumerator ObtainVesselByFragments() {
        var pd = PlayerData.instance;
        while (pd.vesselFragments < 2) {
            pd.vesselFragments++;
            Log($"picked vessel fragment, vesselFragments={pd.vesselFragments}");
            yield return new WaitForSecondsRealtime(0.7f);
        }

        pd.vesselFragments = 0;
        HeroController.instance.AddToMaxMPReserve(33);
        PlayMakerFSM.BroadcastEvent("NEW SOUL ORB");
        Log($"obtained vessel: [{Snap()}]");
    }

    /// <summary>
    /// One step: the actor obtains something, the observer waits for the actor and checks its own state.
    /// </summary>
    private static IEnumerator Step(string step, bool p1Acts, Func<IEnumerator> act, Func<IEnumerator> observe,
        Func<IEnumerator> prepareObserver = null) {
        var actor = p1Acts == IsP1;
        if (actor) {
            yield return WaitSignal($"{step}-obs-ready");
            Log($"{step}: acting; before [{Snap()}]");
            yield return act();
            yield return new WaitForSecondsRealtime(0.5f);
            Signal($"{step}-acted", Snap());
            yield return WaitSignal($"{step}-obs-done");
            yield break;
        }

        if (prepareObserver != null) {
            yield return prepareObserver();
        }

        Log($"{step}: observing; before [{Snap()}]");
        Signal($"{step}-obs-ready");
        yield return WaitSignal($"{step}-acted");
        // Give the save update time to arrive
        yield return new WaitForSecondsRealtime(2.5f);
        Log($"{step}: actor [{_signalValue}]");
        yield return observe();
        Signal($"{step}-obs-done");
    }

    private static IEnumerator Run() {
        yield return WaitInGame();
        AudioListener.volume = 0f;
        var pd = PlayerData.instance;
        pd.isInvincible = true;
        Log($"baseline [{Snap()}]");
        HudMasks("baseline");
        HudVessels("baseline");
        Screenshot("0-baseline");

        // A) p1 is in game first; p2 joins with stored per-player progress of an older version
        int p1GeoBefore = pd.geo;
        if (IsP1) {
            Signal("ready-p1");
            yield return WaitSignal("ready-p2", 600f);
            yield return new WaitForSecondsRealtime(3f);
            Log($"after p2 joined [{Snap()}]");
            var masks = HudMasks("after-join");
            HudVessels("after-join");
            Screenshot("1-after-p2-join");
            Check("join-merge p1 receives p2's stored masks live",
                pd.maxHealthBase == 7 && pd.heartPieces == 2 && pd.maxHealth == 7 && pd.health == 7,
                Snap());
            Check("join-merge p1 HUD shows the new masks", masks == pd.maxHealth, $"hud masks {masks}, maxHealth {pd.maxHealth}");
            Check("join-merge p1 receives p2's spell and abilities",
                pd.quakeLevel == 1 && pd.hasDoubleJump && pd.metElderbug, Snap());
            Check("join-merge p1 geo stays its own", pd.geo == p1GeoBefore, $"geo {pd.geo}, before {p1GeoBefore}");
        } else {
            Signal("ready-p2");
            Check("join-merge p2 keeps its stored progress",
                pd.maxHealthBase == 7 && pd.heartPieces == 2 && pd.quakeLevel == 1 && pd.hasDoubleJump &&
                pd.metElderbug && pd.maxHealth == 7, Snap());
            Check("join-merge p2 keeps its own geo", pd.geo == 777, $"geo {pd.geo}");
            var masks = HudMasks("after-join");
            Check("join-merge p2 HUD", masks == pd.maxHealth, $"hud masks {masks}, maxHealth {pd.maxHealth}");
        }

        // B) p1 completes a mask; p2 was damaged before and must not be healed
        yield return Step("mask-p1", true, ObtainMaskByShards, () => {
            var masks = HudMasks("mask-p1");
            Screenshot("2-mask-from-p1");
            Check("mask p1->p2 live",
                pd.maxHealthBase == 8 && pd.heartPieces == 0 && pd.maxHealth == 8 && pd.health == 6,
                $"expected maxHealthBase 8 heartPieces 0 maxHealth 8 health 6 (damage kept); {Snap()}");
            Check("mask p1->p2 HUD", masks == 8, $"hud masks {masks}");
            return WaitSeconds(0f);
        }, () => {
            HeroController.instance.TakeHealth(1);
            return WaitSeconds(1f);
        });
        if (IsP1) {
            Check("mask p1 own", pd.maxHealthBase == 8 && pd.maxHealth == 8 && pd.health == 8, Snap());
        } else {
            HeroController.instance.MaxHealth();
        }

        // C) p2 completes a mask; p1 is at full health and stays at full health
        yield return Step("mask-p2", false, ObtainMaskByShards, () => {
            var masks = HudMasks("mask-p2");
            Screenshot("3-mask-from-p2");
            Check("mask p2->p1 live",
                pd.maxHealthBase == 9 && pd.heartPieces == 0 && pd.maxHealth == 9 && pd.health == 9,
                $"expected maxHealthBase 9 maxHealth 9 health 9 (was full); {Snap()}");
            Check("mask p2->p1 HUD", masks == 9, $"hud masks {masks}");
            return WaitSeconds(0f);
        }, () => {
            HeroController.instance.MaxHealth();
            return WaitSeconds(1f);
        });

        // D) soul vessels both ways
        yield return Step("vessel-p2", false, ObtainVesselByFragments, () => {
            var vessels = HudVessels("vessel-p2");
            Screenshot("4-vessel-from-p2");
            Check("vessel p2->p1 live", pd.MPReserveMax == 33 && pd.vesselFragments == 0 && pd.MPReserve == 0,
                $"expected MPReserveMax 33 vesselFragments 0 MPReserve 0; {Snap()}; hud vessels {vessels}");
            Check("vessel p2->p1 HUD", vessels == 1, $"hud vessels {vessels}");
            return WaitSeconds(0f);
        });
        yield return Step("vessel-p1", true, ObtainVesselByFragments, () => {
            var vessels = HudVessels("vessel-p1");
            Check("vessel p1->p2 live", pd.MPReserveMax == 66 && pd.vesselFragments == 0,
                $"expected MPReserveMax 66; {Snap()}; hud vessels {vessels}");
            Check("vessel p1->p2 HUD", vessels == 2, $"hud vessels {vessels}");
            return WaitSeconds(0f);
        });

        // E) spells: p2 cannot cast Vengeful Spirit before p1 obtains it, and can right after
        var castBefore = false;
        yield return Step("spell-p1", true, () => {
            pd.hasSpell = true;
            pd.fireballLevel = 1;
            return WaitSeconds(0.5f);
        }, () => {
            var castAfter = false;
            return Sequence(
                TryQuickCast(r => castAfter = r),
                Do(() => Check("spell p1->p2 live (Vengeful Spirit castable)",
                    pd.fireballLevel == 1 && pd.hasSpell && castAfter && !castBefore,
                    $"before: cast={castBefore}; after: cast={castAfter}; {Snap()}"))
            );
        }, () => TryQuickCast(r => castBefore = r));
        yield return Step("spell-p2", false, () => {
            pd.screamLevel = 1;
            return WaitSeconds(0.5f);
        }, () => {
            Check("spell p2->p1 live", pd.screamLevel == 1, Snap());
            return WaitSeconds(0f);
        });

        // F) abilities and story flags both ways, personal geo stays individual
        var observerGeo = 0;
        yield return Step("story-p2", false, () => {
            pd.hasDash = true;
            pd.canDash = true;
            pd.hasWalljump = true;
            pd.hasSuperDash = true;
            pd.hasDreamNail = true;
            pd.dreamNailUpgraded = true;
            pd.hasKingsBrand = true;
            pd.mapCrossroads = true;
            pd.visitedGreenpath = true;
            HeroController.instance.AddGeo(100);
            return WaitSeconds(0.5f);
        }, () => {
            Check("story p2->p1 live",
                pd.hasDash && pd.canDash && pd.hasWalljump && pd.hasSuperDash && pd.hasDreamNail &&
                pd.dreamNailUpgraded && pd.hasKingsBrand && pd.mapCrossroads && pd.visitedGreenpath, Snap());
            Check("story p2->p1 geo stays own", pd.geo == observerGeo, $"geo {pd.geo}, before {observerGeo}");
            return WaitSeconds(0f);
        }, () => {
            observerGeo = pd.geo;
            return WaitSeconds(0f);
        });
        yield return Step("story-p1", true, () => {
            pd.hasShadowDash = true;
            pd.hasLantern = true;
            pd.xunRewardGiven = true;
            return WaitSeconds(0.5f);
        }, () => {
            Check("story p1->p2 live", pd.hasShadowDash && pd.hasLantern && pd.xunRewardGiven, Snap());
            return WaitSeconds(0f);
        });

        // G) p2 reconnects with a wiped local state and gets the shared progress back; p1 must not lose anything
        if (IsP1) {
            Signal("reconnect-p1-ready", Snap());
            yield return WaitSignal("reconnect-p2-done", 400f);
            yield return new WaitForSecondsRealtime(3f);
            Check("reconnect p1 keeps shared progress while p2 rejoins",
                pd.maxHealthBase == 9 && pd.MPReserveMax == 66 && pd.fireballLevel == 1 && pd.screamLevel == 1 &&
                pd.quakeLevel == 1 && pd.hasDash, Snap());
        } else {
            yield return WaitSignal("reconnect-p1-ready");
            var geoBefore = pd.geo;
            if (DevAutoConnect.DisconnectToMenu()) {
                yield return WaitUntil(
                    () => UIManager.instance != null && UIManager.instance.menuState == MainMenuState.MAIN_MENU &&
                          global::GameManager.instance.gameState == GameState.MAIN_MENU, 60f);
                pd = PlayerData.instance;
                pd.maxHealthBase = 5;
                pd.maxHealth = 5;
                pd.heartPieces = 0;
                pd.MPReserveMax = 0;
                pd.fireballLevel = 0;
                pd.quakeLevel = 0;
                pd.screamLevel = 0;
                pd.hasDash = false;
                pd.hasKingsBrand = false;
                Log($"reconnect: local state wiped [{Snap()}]");
                yield return DevAutoConnect.Reconnect();
                yield return WaitUntil(HeroReady, 180f);
                yield return new WaitForSecondsRealtime(3f);
                pd = PlayerData.instance;
                pd.isInvincible = true;
                var masks = HudMasks("after-reconnect");
                Screenshot("5-after-reconnect");
                Check("reconnect p2 gets the shared progress back",
                    pd.maxHealthBase == 9 && pd.maxHealth == 9 && pd.MPReserveMax == 66 && pd.fireballLevel == 1 &&
                    pd.screamLevel == 1 && pd.quakeLevel == 1 && pd.hasDash && pd.hasKingsBrand &&
                    pd.hasShadowDash, Snap());
                Check("reconnect p2 HUD", masks == 9, $"hud masks {masks}");
                Check("reconnect p2 geo stays its own", pd.geo == geoBefore, $"geo {pd.geo}, before {geoBefore}");
            } else {
                Check("reconnect p2", false, "auto-connect not configured");
            }

            Signal("reconnect-p2-done", Snap());
        }

        Log($"===== done: {_pass} passed, {_fail} failed [{Snap()}]");
        Signal($"done-{_role}", $"{_pass}/{_fail}");
    }

    private static IEnumerator WaitSeconds(float seconds) {
        if (seconds > 0f) {
            yield return new WaitForSecondsRealtime(seconds);
        }
    }

    private static IEnumerator Do(Action action) {
        action();
        yield break;
    }

    private static IEnumerator Sequence(params IEnumerator[] parts) {
        foreach (var part in parts) {
            yield return part;
        }
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
