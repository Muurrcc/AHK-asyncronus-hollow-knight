using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Hkmp.Game.Client.Save;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <summary>
/// Player health independence test (part of the development test, option <c>-hkmp-test-health 1|only</c>).
/// Both instances stand in Town (H role = scene host). In each step one role (actor) changes its own combat state
/// through the real game methods (TakeDamage, AddMPCharge, AddHealth, Die/respawn, ...) and the other role
/// (observer) checks that its own PlayerData did not change at any moment (polled every 0.1 s).
/// </summary>
internal static partial class DevEntityTest {
    private const string HpScene = "Town";
    private const string HpGate = "left1";

    /// <summary>
    /// Snapshot of the individual state of the local character.
    /// </summary>
    private class HpSnap {
        public bool Hero;
        public int Health, MaxHealth, MaxHealthBase, Blue, Joni, Mp, MpReserve, Geo, GeoPool;
        public string Shade, Respawn, Scene;
        public bool Dead;

        public override string ToString() =>
            $"health={Health} maxHealth={MaxHealth} maxHealthBase={MaxHealthBase} healthBlue={Blue} " +
            $"joniHealthBlue={Joni} MPCharge={Mp} MPReserve={MpReserve} geo={Geo} geoPool={GeoPool} " +
            $"shadeScene={Shade} respawnScene={Respawn} scene={Scene} dead={Dead}";
    }

    private static HpSnap Snap() {
        var pd = PlayerData.instance;
        var hc = HeroController.instance;
        return new HpSnap {
            Hero = hc != null,
            Health = pd.GetInt("health"),
            MaxHealth = pd.GetInt("maxHealth"),
            MaxHealthBase = pd.GetInt("maxHealthBase"),
            Blue = pd.GetInt("healthBlue"),
            Joni = pd.GetInt("joniHealthBlue"),
            Mp = pd.GetInt("MPCharge"),
            MpReserve = pd.GetInt("MPReserve"),
            Geo = pd.GetInt("geo"),
            GeoPool = pd.GetInt("geoPool"),
            Shade = pd.GetString("shadeScene"),
            Respawn = pd.GetString("respawnScene"),
            Scene = CurrentScene,
            Dead = hc != null && hc.cState.dead
        };
    }

    private static List<string> Diff(HpSnap a, HpSnap b) {
        var diffs = new List<string>();
        void D<T>(string n, T x, T y) {
            if (!Equals(x, y)) diffs.Add($"{n} {x}->{y}");
        }

        D("health", a.Health, b.Health);
        D("maxHealth", a.MaxHealth, b.MaxHealth);
        D("maxHealthBase", a.MaxHealthBase, b.MaxHealthBase);
        D("healthBlue", a.Blue, b.Blue);
        D("joniHealthBlue", a.Joni, b.Joni);
        D("MPCharge", a.Mp, b.Mp);
        D("MPReserve", a.MpReserve, b.MpReserve);
        D("geo", a.Geo, b.Geo);
        D("geoPool", a.GeoPool, b.GeoPool);
        D("shadeScene", a.Shade, b.Shade);
        D("respawnScene", a.Respawn, b.Respawn);
        D("scene", a.Scene, b.Scene);
        D("dead", a.Dead, b.Dead);
        return diffs;
    }

    private static readonly MethodInfo CanTakeDamageMethod =
        typeof(HeroController).GetMethod("CanTakeDamage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static bool HeroCanTakeDamage() {
        var hc = HeroController.instance;
        return hc != null && (bool) CanTakeDamageMethod.Invoke(hc, null);
    }

    private static bool HeroReady() {
        var hc = HeroController.instance;
        var gm = global::GameManager.instance;
        return hc != null && !hc.cState.dead && !hc.cState.hazardRespawning && !hc.cState.transitioning &&
               gm != null && gm.gameState == GameState.PLAYING && !gm.IsInSceneTransition && hc.acceptingInput &&
               Em != null && Em.IsSceneHostDetermined;
    }

    /// <summary>
    /// Damage the local hero through HeroController.TakeDamage (same path as an enemy or hazard).
    /// </summary>
    private static IEnumerator DamageLocalHero(int amount, int hazardType = 1) {
        yield return WaitUntil(HeroCanTakeDamage, 10f);
        var hc = HeroController.instance;
        if (!HeroCanTakeDamage()) {
            Log($"hero cannot take damage: damageMode={hc.damageMode} transitionState={hc.transitionState} invulnerable={hc.cState.invulnerable} recoiling={hc.cState.recoiling} isInvincible={PlayerData.instance.GetBool("isInvincible")}");
        }

        var source = new GameObject("HKMP Test Damage Source");
        source.transform.position = hc.transform.position + Vector3.left;
        Log($"TakeDamage(amount={amount}, hazardType={hazardType})");
        hc.TakeDamage(source, CollisionSide.left, amount, hazardType);
        yield return new WaitForSecondsRealtime(0.5f);
        Object.Destroy(source);
    }

    /// <summary>
    /// One step: the actor role runs <paramref name="act"/> and validates its own state with
    /// <paramref name="actorCheck"/> (null = ok); the observer checks that its own state never changed.
    /// </summary>
    private static IEnumerator HpStep(
        string step,
        bool hActs,
        Func<IEnumerator> act,
        Func<HpSnap, HpSnap, string> actorCheck,
        float observerDelay = 2f
    ) {
        var actor = hActs == IsH;
        var before = Snap();
        if (actor) {
            yield return WaitSignal($"hp-{step}-obs-ready", 300f);
            Log($"hp {step}: acting; before [{before}]");
            yield return act();
            var after = Snap();
            var err = actorCheck(before, after);
            Check($"player-health {step} (actor {_role}/{(IsH ? "H" : "C")})", err == null,
                $"{err ?? "ok"}; before [{before}] after [{after}]");
            Signal($"hp-{step}-actor", after.ToString());
            yield return WaitSignal($"hp-{step}-obs", 300f);
            Log($"hp {step}: observer state [{_signalValue}]");
            yield break;
        }

        Signal($"hp-{step}-obs-ready", before.ToString());

        // Poll own state while the actor acts: health must never drop, the hero must never die
        var path = Path.Combine(_dir, $"hp-{step}-actor");
        var minHealth = before.Health;
        var maxHealthSeen = before.Health;
        var everDead = false;
        var waited = 0f;
        var extra = 0f;
        while (waited < 300f && extra < observerDelay) {
            var now = Snap();
            minHealth = Mathf.Min(minHealth, now.Health);
            maxHealthSeen = Mathf.Max(maxHealthSeen, now.Health);
            everDead |= now.Dead;
            if (File.Exists(path)) {
                extra += 0.1f;
            }

            waited += 0.1f;
            yield return new WaitForSecondsRealtime(0.1f);
        }

        var actorState = File.Exists(path) ? File.ReadAllText(path) : "timeout";
        var final = Snap();
        var diffs = Diff(before, final);
        var ok = diffs.Count == 0 && minHealth == before.Health && maxHealthSeen == before.Health && !everDead;
        Check($"player-health {step} (observer {_role}/{(IsH ? "H" : "C")}) own state unchanged", ok,
            $"{(diffs.Count == 0 ? "no change" : string.Join(", ", diffs))}, health min/max during step {minHealth}/{maxHealthSeen}, everDead={everDead}; actor [{actorState}]; mine [{final}]");
        Signal($"hp-{step}-obs", final.ToString());
    }

    /// <summary>
    /// Heal 1 mask by focusing: first through the hero's Spell Control FSM state "Focus Heal" (the state that
    /// applies the heal of a real focus), falling back to what that state does (TakeMP + AddHealth).
    /// </summary>
    private static IEnumerator FocusHeal() {
        var hc = HeroController.instance;
        var pd = PlayerData.instance;
        var h0 = pd.GetInt("health");
        var mp0 = pd.GetInt("MPCharge");
        var fsm = hc.gameObject.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Spell Control");
        if (fsm != null && fsm.FsmStates.Any(st => st.Name == "Focus Heal")) {
            Log($"focus: Spell Control active='{fsm.ActiveStateName}', entering 'Focus Heal'");
            fsm.SetState("Focus Heal");
            yield return WaitUntil(() => PlayerData.instance.GetInt("health") > h0, 3f);
            yield return new WaitForSecondsRealtime(1.5f);
            Log($"focus: after FSM heal health {h0}->{pd.GetInt("health")} MP {mp0}->{pd.GetInt("MPCharge")} state='{fsm.ActiveStateName}'");
            if (pd.GetInt("health") > h0) {
                if (pd.GetInt("MPCharge") == mp0) {
                    // The soul is drained in the "Focus" state (holding the button), which is skipped here
                    hc.TakeMP(33);
                }

                Note("player-health focus path", "Spell Control FSM 'Focus Heal' state healed the hero");
                yield break;
            }
        } else {
            Log($"focus: Spell Control FSM {(fsm == null ? "missing" : "has no 'Focus Heal' state")}");
        }

        Note("player-health focus path", "fallback: HeroController.TakeMP(33) + AddHealth(1)");
        hc.TakeMP(33);
        hc.AddHealth(1);
        yield return new WaitForSecondsRealtime(0.5f);
    }

    /// <summary>
    /// Rest at the Town bench as far as health and respawn point go: set the bench as respawn point and refill
    /// health (what the bench FSM does).
    /// </summary>
    private static IEnumerator SimulateBench() {
        var markers = GameObject.FindGameObjectsWithTag("RespawnPoint")
            .Where(g => g.scene.name == HpScene)
            .OrderByDescending(g => g.name.IndexOf("bench", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        Log($"respawn points in {HpScene}: {string.Join(", ", markers.Select(m => m.name))}");
        var marker = markers.FirstOrDefault();
        if (marker != null) {
            HeroController.instance.SetBenchRespawn(marker.name, HpScene, 0, true);
        }

        HeroController.instance.MaxHealth();
        yield return new WaitForSecondsRealtime(0.5f);
    }

    /// <summary>
    /// Kill the local hero with real damage and wait until it has respawned.
    /// </summary>
    private static IEnumerator DieAndRespawn() {
        var pd = PlayerData.instance;
        pd.SetInt("healthBlue", 0);
        pd.SetInt("health", 1);
        yield return DamageLocalHero(1);
        yield return WaitUntil(() => HeroController.instance == null || HeroController.instance.cState.dead, 5f);
        Log($"died: dead={HeroController.instance != null && HeroController.instance.cState.dead}");
        yield return new WaitForSecondsRealtime(2f);
        yield return WaitUntil(HeroReady, 120f);
        yield return new WaitForSecondsRealtime(2f);
        Log($"respawned: ready={HeroReady()} scene={CurrentScene}");
    }

    /// <summary>
    /// Disconnect, go to the main menu, wipe the local health state (as a fresh game would) and connect again;
    /// the server save must give back this player's own values.
    /// </summary>
    private static IEnumerator ReconnectLocal() {
        if (!DevAutoConnect.DisconnectToMenu()) {
            Log("reconnect: auto-connect not configured");
            yield break;
        }

        yield return WaitUntil(
            () => UIManager.instance != null && UIManager.instance.menuState == MainMenuState.MAIN_MENU &&
                  global::GameManager.instance.gameState == GameState.MAIN_MENU, 60f);
        Log($"reconnect: at main menu (menuState={UIManager.instance.menuState})");

        var pd = PlayerData.instance;
        pd.SetInt("maxHealthBase", 5);
        pd.SetInt("maxHealth", 5);
        pd.SetInt("health", 1);
        pd.SetInt("MPCharge", 0);
        pd.SetInt("geoPool", 0);
        pd.SetString("shadeScene", "None");
        Log($"reconnect: local state wiped [{Snap()}]");

        yield return DevAutoConnect.Reconnect();
        yield return WaitUntil(HeroReady, 180f);
        yield return new WaitForSecondsRealtime(3f);
        PlayerData.instance.isInvincible = false;
        Log($"reconnect: back in game ready={HeroReady()} scene={CurrentScene}");
    }

    private static IEnumerator PlayerHealthTest() {
        Log("===== player health independence =====");

        // Code level: the combat state is never synced and individual persistent values are per player
        var mapping = SaveDataMapping.Instance;
        var synced = SaveDataMapping.LocalOnlyPlayerDataFields
            .Where(f => mapping.PlayerDataVarProperties.TryGetValue(f, out var p) && p.Sync).ToList();
        Check("player-health mapping: combat state never synced", synced.Count == 0,
            $"local-only fields: {string.Join(" ", SaveDataMapping.LocalOnlyPlayerDataFields)}; synced: {string.Join(" ", synced)}");
        var notPlayer = SaveDataMapping.PlayerSpecificDataFields
            .Where(f => mapping.PlayerDataVarProperties.TryGetValue(f, out var p) && p.Sync &&
                        p.SyncType != SaveDataMapping.SyncType.Player).ToList();
        var perPlayer = SaveDataMapping.PlayerSpecificDataFields
            .Count(f => mapping.PlayerDataVarProperties.TryGetValue(f, out var p) && p.Sync);
        Check("player-health mapping: individual persistent fields stored per player", notPlayer.Count == 0,
            $"{perPlayer} synced as Player; wrong: {string.Join(" ", notPlayer)}");

        // H enters Town first (scene host of the empty scene), then C
        if (IsH) {
            yield return GoTo(HpScene, HpGate);
            yield return WaitUntil(() => Em.IsSceneHost, 30f);
            Signal("hp-H-in");
            yield return WaitSignal("hp-C-in", 300f);
        } else {
            yield return WaitSignal("hp-H-in", 300f);
            yield return GoTo(HpScene, HpGate);
            Signal("hp-C-in");
            yield return new WaitForSecondsRealtime(1f);
        }

        Check("player-health roles", IsH == Em.IsSceneHost && CurrentScene == HpScene,
            $"scene={CurrentScene} sceneHost={Em.IsSceneHost}");
        yield return WaitUntil(HeroReady, 30f);

        // From here on the hero takes real damage
        _keepAlive = false;
        var pd = PlayerData.instance;
        pd.isInvincible = false;
        HeroController.instance.MaxHealth();
        pd.SetInt("MPCharge", 0);
        pd.SetInt("MPReserve", 0);
        pd.SetInt("healthBlue", 0);
        yield return new WaitForSecondsRealtime(1f);
        Log($"baseline [{Snap()}]");

        yield return HpStep("damage-H", true, () => DamageLocalHero(2),
            (b, a) => a.Health == b.Health - 2 ? null : $"expected health {b.Health - 2}");
        yield return HpStep("damage-C", false, () => DamageLocalHero(1),
            (b, a) => a.Health == b.Health - 1 ? null : $"expected health {b.Health - 1}");
        yield return HpStep("soul-H", true, () => {
                HeroController.instance.AddMPCharge(33);
                return WaitSeconds(0.5f);
            },
            (b, a) => a.Mp == b.Mp + 33 ? null : $"expected MPCharge {b.Mp + 33}");
        yield return HpStep("focus-heal-H", true, FocusHeal,
            (b, a) => a.Health == b.Health + 1 && a.Mp == b.Mp - 33 ? null : $"expected health {b.Health + 1} MPCharge {b.Mp - 33}");
        yield return HpStep("bench-C", false, SimulateBench,
            (b, a) => a.Health == a.MaxHealth && a.Respawn == HpScene ? null : $"expected full health and respawnScene {HpScene}");
        yield return HpStep("lifeblood-H", true, () => {
                PlayerData.instance.SetInt("healthBlue", 2);
                return DamageLocalHero(1);
            },
            (b, a) => a.Health == b.Health && a.Blue == 1 ? null : $"expected health {b.Health} (lifeblood absorbs) healthBlue 1");
        yield return HpStep("hazard-C", false, () => HazardAndWait(),
            (b, a) => a.Health == b.Health - 1 && a.Scene == HpScene && !a.Dead ? null : $"expected health {b.Health - 1} in {HpScene}");
        yield return HpStep("geo-H", true, () => {
                HeroController.instance.AddGeo(50);
                return WaitSeconds(1f);
            },
            (b, a) => a.Geo == b.Geo + 50 ? null : $"expected geo {b.Geo + 50}");
        yield return HpStep("geo-C", false, () => {
                HeroController.instance.AddGeo(30);
                return WaitSeconds(1f);
            },
            (b, a) => a.Geo == b.Geo + 30 ? null : $"expected geo {b.Geo + 30}");
        yield return HpStep("death-H", true, DieAndRespawn,
            (b, a) => a.Health == a.MaxHealth && !a.Dead && a.Shade == HpScene && a.Geo == 0 && a.GeoPool == b.Geo
                ? null
                : $"expected respawn with full health, shadeScene {HpScene}, geo 0, geoPool {b.Geo}", 4f);
        yield return HpStep("death-C", false, DieAndRespawn,
            (b, a) => a.Health == a.MaxHealth && !a.Dead && a.Shade == HpScene && a.Geo == 0 && a.GeoPool == b.Geo && a.Scene == HpScene
                ? null
                : $"expected respawn at the {HpScene} bench with full health, shadeScene {HpScene}, geo 0, geoPool {b.Geo}", 4f);
        // Masks are shared progress now (the observer gets the mask too): tested in DevSharedProgressTest
        yield return HpStep("reconnect-H", true, ReconnectLocal,
            (b, a) => a.MaxHealthBase == b.MaxHealthBase && a.MaxHealth == b.MaxHealth && a.Health == a.MaxHealth &&
                      a.GeoPool == b.GeoPool && a.Shade == b.Shade && a.Geo == b.Geo && !a.Dead
                ? null
                : "expected own maxHealthBase/maxHealth (full health), geo, geoPool and shadeScene back from the server", 4f);
        yield return HpStep("reconnect-C", false, ReconnectLocal,
            (b, a) => a.MaxHealthBase == b.MaxHealthBase && a.MaxHealth == b.MaxHealth && a.Health == a.MaxHealth &&
                      a.GeoPool == b.GeoPool && a.Shade == b.Shade && a.Geo == b.Geo && a.Respawn == HpScene && !a.Dead
                ? null
                : "expected own maxHealthBase/maxHealth (full health), geo, geoPool, shadeScene and respawn point back from the server", 4f);

        Log($"end of player health test [{Snap()}]");
        PlayerData.instance.isInvincible = true;
        HeroController.instance?.MaxHealth();
        _keepAlive = true;
        Signal($"hp-done-{_role}");
        yield return WaitSignal($"hp-done-{(_role == "p1" ? "p2" : "p1")}", 300f);
    }

    private static IEnumerator HazardAndWait() {
        yield return DamageLocalHero(1, 2);
        yield return WaitUntil(() => HeroController.instance.cState.hazardRespawning || HeroController.instance.cState.hazardDeath, 3f);
        yield return WaitUntil(HeroReady, 30f);
        yield return new WaitForSecondsRealtime(1f);
    }

    private static IEnumerator WaitSeconds(float seconds) {
        yield return new WaitForSecondsRealtime(seconds);
    }
}
