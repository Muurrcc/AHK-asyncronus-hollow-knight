using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using UnityEngine;

namespace Hkmp.Ui;

/// <summary>
/// Attack forwarding test (option <c>-hkmp-test-attacks Scene:gate</c>): the C role (not scene host) attacks the
/// strongest enemy of the scene with real attacks of the hero (several nail swings, spells, nail arts, charms) and H
/// (scene host) checks that every hit C forwarded was applied exactly once on the host object (same count and
/// damage) and that no other hit reached the host object (a replicated effect of C that is not recognised would
/// count twice).
/// </summary>
internal static partial class DevEntityTest {
    private static PlayMakerFSM HeroFsm(string name) =>
        HeroController.instance.gameObject.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == name);

    /// <summary>
    /// Set the hero FSM <paramref name="fsmName"/> to the first existing state of <paramref name="states"/>.
    /// </summary>
    private static bool EnterHeroState(string fsmName, params string[] states) {
        var fsm = HeroFsm(fsmName);
        if (fsm == null) {
            Log($"attack: FSM {fsmName} missing");
            return false;
        }

        var state = states.FirstOrDefault(st => fsm.FsmStates.Any(x => x.Name == st));
        if (state == null) {
            Log($"attack: FSM {fsmName} has none of [{string.Join(", ", states)}]; states: {string.Join(", ", fsm.FsmStates.Select(x => x.Name))}");
            return false;
        }

        Log($"attack: {fsmName} '{fsm.ActiveStateName}' -> '{state}'");
        fsm.SetState(state);
        return true;
    }

    private static void HeroAttack() {
        typeof(HeroController)
            .GetMethod("Attack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(HeroController.instance, new object[] { GlobalEnums.AttackDirection.normal });
    }

    private static void PlaceHeroLeftOf(HealthManager hm, float dx, float dy = 0.3f) {
        var hero = HeroController.instance;
        var pos = hm.transform.position;
        hero.transform.position = new Vector3(pos.x - dx, pos.y + dy, hero.transform.position.z);
        hero.FaceRight();
    }

    private static void SetCharms(params int[] charms) {
        var pd = PlayerData.instance;
        for (var i = 1; i <= 40; i++) {
            pd.SetBool($"equippedCharm_{i}", charms.Contains(i));
        }

        pd.SetInt("charmSlots", 11);
        HeroController.instance.CharmUpdate();
    }

    /// <summary>
    /// Attacks performed by C. Each returns after the attack had time to land.
    /// </summary>
    private static readonly (string Name, Func<HealthManager, IEnumerator> Run)[] Attacks = {
        ("nail x4", NailSwings),
        ("vengeful spirit", hm => CastSpell(hm, "fireballLevel", 4f, "QUICK CAST", null)),
        ("desolate dive", hm => CastSpell(hm, "quakeLevel", 0f, null, new[] { "Quake Antic", "Q On Ground" })),
        ("howling wraiths", hm => CastSpell(hm, "screamLevel", 0f, null, new[] { "Scream Antic1", "Scream Get?", "Scream Burst 1" })),
        ("great slash", hm => NailArt(hm, "hasDashSlash", 1.5f, new[] { "Has G Slash?", "G Slash" })),
        ("dash slash", hm => NailArt(hm, "hasUpwardSlash", 4f, new[] { "DSlash Start", "Dash Slash" })),
        ("cyclone slash", hm => NailArt(hm, "hasCyclone", 0.5f, new[] { "Has Cyclone?", "Cyclone Start" })),
        ("grubberfly elegy nail beam", hm => WithCharms(hm, new[] { 35 }, Beam)),
        ("flukenest fireball", hm => WithCharms(hm, new[] { 11 }, h => CastSpell(h, "fireballLevel", 2.5f, "QUICK CAST", null))),
        ("shaman stone fireball", hm => WithCharms(hm, new[] { 19 }, h => CastSpell(h, "fireballLevel", 4f, "QUICK CAST", null))),
        ("thorns of agony", hm => WithCharms(hm, new[] { 12 }, Thorns)),
    };

    private static IEnumerator NailSwings(HealthManager hm) {
        for (var i = 0; i < 4; i++) {
            PlaceHeroLeftOf(hm, 1.3f);
            yield return new WaitForSecondsRealtime(0.15f);
            HeroAttack();
            yield return new WaitForSecondsRealtime(0.6f);
        }
    }

    private static IEnumerator CastSpell(HealthManager hm, string levelVar, float dx, string evt, string[] states) {
        var pd = PlayerData.instance;
        pd.SetBool("hasSpell", true);
        pd.SetInt(levelVar, 1);
        pd.SetInt("MPCharge", 99);
        PlaceHeroLeftOf(hm, dx == 0f ? 0.5f : dx, dx == 0f ? 2f : 0.3f);
        yield return new WaitForSecondsRealtime(0.4f);
        if (evt != null) {
            HeroFsm("Spell Control")?.SendEvent(evt);
        } else {
            EnterHeroState("Spell Control", states);
        }

        yield return new WaitForSecondsRealtime(2.5f);
        Log($"attack: after spell Spell Control='{HeroFsm("Spell Control")?.ActiveStateName}' MP={pd.GetInt("MPCharge")}");
        pd.SetInt(levelVar, 0);
    }

    private static IEnumerator NailArt(HealthManager hm, string hasVar, float dx, string[] states) {
        var pd = PlayerData.instance;
        pd.SetBool("hasNailArt", true);
        pd.SetBool(hasVar, true);
        HeroFsm("Nail Arts")?.SetState("Inactive");
        PlaceHeroLeftOf(hm, dx);
        yield return new WaitForSecondsRealtime(0.4f);
        EnterHeroState("Nail Arts", states);
        yield return new WaitForSecondsRealtime(1f);
        var na = HeroFsm("Nail Arts");
        if (na != null && na.ActiveStateName == "Cyclone Start") {
            // Cyclone Start waits for an input/animation event that does not come without a real button press
            Log("attack: Cyclone Start stuck, sending FINISHED");
            na.SendEvent("FINISHED");
        }

        yield return new WaitForSecondsRealtime(2.5f);
        Log($"attack: after nail art Nail Arts='{HeroFsm("Nail Arts")?.ActiveStateName}'");
        pd.SetBool(hasVar, false);
    }

    private static IEnumerator Beam(HealthManager hm) {
        HeroController.instance.MaxHealth();
        PlaceHeroLeftOf(hm, 5f);
        yield return new WaitForSecondsRealtime(0.3f);
        HeroAttack();
        yield return new WaitForSecondsRealtime(1.5f);
    }

    private static IEnumerator Thorns(HealthManager hm) {
        PlaceHeroLeftOf(hm, 1.5f);
        yield return new WaitForSecondsRealtime(0.3f);
        _keepAlive = false;
        PlayerData.instance.isInvincible = false;
        HeroController.instance.MaxHealth();
        yield return DamageLocalHero(1);
        yield return new WaitForSecondsRealtime(2f);
        PlayerData.instance.isInvincible = true;
        _keepAlive = true;
    }

    private static IEnumerator WithCharms(HealthManager hm, int[] charms, Func<HealthManager, IEnumerator> run) {
        SetCharms(charms);
        yield return new WaitForSecondsRealtime(0.5f);
        yield return run(hm);
        SetCharms();
    }

    private static IEnumerator AttackTest(string scene, string gate) {
        var s = scene;
        Log($"===== attacks {s} =====");

        if (IsH) {
            yield return WaitSignal($"atk-{s}-C-out", 300f);
            yield return GoTo(scene, gate);
            yield return WaitUntil(() => Em.IsSceneHost, 30f);
            Signal($"atk-{s}-H-host");
        } else {
            if (CurrentScene == scene || CurrentScene != _stagingScene) {
                yield return GoTo(_stagingScene, _stagingGate);
            }

            Signal($"atk-{s}-C-out");
            yield return WaitSignal($"atk-{s}-H-host", 300f);
            yield return GoTo(scene, gate);
        }

        Check($"{s} attacks roles", IsH == Em.IsSceneHost && CurrentScene == scene, $"scene={CurrentScene} sceneHost={Em.IsSceneHost}");
        yield return new WaitForSecondsRealtime(3f);

        // H wakes the strongest enemy and announces the target, then goes back to the entry (away from the target)
        int target = -1;
        if (IsH) {
            var strongest = Em.Entities.Values
                .Where(e => e.HealthComponent != null && IsAliveInView(e))
                .OrderByDescending(e => ViewHm(e).hp)
                .FirstOrDefault();
            if (strongest != null) {
                MoveHeroNear(strongest);
                yield return new WaitForSecondsRealtime(ViewHm(strongest).hp >= 40 ? 8f : 2f);
                MoveHeroBack();
                target = strongest.Id;
            }

            Signal($"atk-{s}-target", target.ToString());
            Note($"{s} attack target", strongest == null ? "none" : Describe(strongest));
        } else {
            yield return WaitSignal($"atk-{s}-target", 300f);
            target = int.Parse(_signalValue ?? "-1");
            DumpFsms(HeroController.instance.gameObject, "hero");
        }

        if (target >= 0) {
            var id = (ushort) target;
            foreach (var (name, run) in Attacks) {
                var key = $"atk-{s}-{name.Replace(' ', '_')}";
                if (!IsH) {
                    var hm = Find(target)?.HealthComponent?.ClientHealthManager;
                    if (hm == null || hm.GetIsDead() || !hm.gameObject.activeInHierarchy) {
                        Signal($"{key}-C", "skip|0|0");
                        yield return WaitSignal($"{key}-H", 120f);
                        continue;
                    }

                    HealthManagerComponent.SentByEntity.TryGetValue(id, out var before);
                    var hitsBefore = HealthManagerComponent.HitsSent;
                    Log($"attack {name} on {target}");
                    yield return run(hm);
                    yield return new WaitForSecondsRealtime(0.5f);
                    HealthManagerComponent.SentByEntity.TryGetValue(id, out var after);
                    Log($"attack {name}: forwarded {after.Count - before.Count} hit(s) dmg {after.Damage - before.Damage} on target (all entities {HealthManagerComponent.HitsSent - hitsBefore})");
                    Screenshot($"atk-{name.Replace(' ', '_')}");
                    MoveHeroBack();
                    Signal($"{key}-C", $"ok|{after.Count - before.Count}|{after.Damage - before.Damage}");
                    yield return WaitSignal($"{key}-H", 120f);
                } else {
                    HealthManagerComponent.AppliedByEntity.TryGetValue(id, out var appliedBefore);
                    HealthManagerComponent.OtherHostHitsByEntity.TryGetValue(id, out var otherBefore);
                    var ignoredBefore = HealthManagerComponent.ReplicaHitsIgnored;
                    var hm = ViewHm(Find(target));
                    var hpBefore = hm != null ? hm.hp : -1;
                    yield return WaitSignal($"{key}-C", 120f);
                    yield return new WaitForSecondsRealtime(1.5f);
                    var parts = (_signalValue ?? "timeout|0|0").Split('|');
                    HealthManagerComponent.AppliedByEntity.TryGetValue(id, out var appliedAfter);
                    HealthManagerComponent.OtherHostHitsByEntity.TryGetValue(id, out var otherAfter);
                    var sentCount = int.Parse(parts[1]);
                    var sentDamage = int.Parse(parts[2]);
                    var appliedCount = appliedAfter.Count - appliedBefore.Count;
                    var appliedDamage = appliedAfter.Damage - appliedBefore.Damage;
                    var other = otherAfter.Count - otherBefore.Count;
                    var detail = $"target {target}: C forwarded {sentCount} hit(s) dmg {sentDamage}; host applied {appliedCount} dmg {appliedDamage}; " +
                                 $"other hits on host object {other}; replica hits ignored {HealthManagerComponent.ReplicaHitsIgnored - ignoredBefore}; " +
                                 $"host hp {hpBefore} -> {(hm != null ? hm.hp : -1)} dead={hm == null || hm.GetIsDead()}";
                    if (parts[0] != "ok") {
                        Note($"{s} attack {name}", $"skipped ({parts[0]}): target gone or dead");
                    } else if (sentCount == 0) {
                        Note($"{s} attack {name}", "did not connect on C (no hit forwarded): " + detail);
                    } else {
                        Check($"{s} attack {name} C->H applied exactly once",
                            appliedCount == sentCount && appliedDamage == sentDamage && other == 0, detail);
                    }

                    Signal($"{key}-H");
                }
            }
        }

        // Leave the scene: C first, then H
        if (!IsH) {
            yield return GoTo(_stagingScene, _stagingGate);
            Signal($"atk-{s}-C-left");
            yield return WaitSignal($"atk-{s}-H-left", 300f);
        } else {
            yield return WaitSignal($"atk-{s}-C-left", 300f);
            yield return GoTo(_stagingScene, _stagingGate);
            Signal($"atk-{s}-H-left");
        }
    }

    private static readonly string[] BattleStartEvents = {
        "BATTLE START", "CHALLENGE START", "START BATTLE", "BATTLE START NOW", "CHALLENGE", "START FIGHT", "MLORD CHALLENGE"
    };

    /// <summary>
    /// Boss fights that need a trigger the test does not walk into (battle scene trigger, challenge prompt): dump
    /// every FSM of the scene with the transitions of its active state, and send the known battle start events to
    /// the FSMs waiting for them.
    /// </summary>
    private static void WakeBossScene(string label, bool send = true) {
        var sb = new System.Text.StringBuilder();
        var sent = new List<string>();
        foreach (var fsm in UnityEngine.Resources.FindObjectsOfTypeAll<PlayMakerFSM>()) {
            if (fsm == null || !fsm.gameObject.scene.IsValid() || fsm.gameObject.scene.name == "DontDestroyOnLoad") continue;
            var state = fsm.Fsm?.ActiveState;
            var events = state == null ? new string[0] : state.Transitions.Select(t => t.EventName).ToArray();
            sb.AppendLine($"{fsm.gameObject.scene.name} | {fsm.gameObject.name} / {fsm.FsmName} active={fsm.gameObject.activeInHierarchy} state='{fsm.ActiveStateName}' -> {string.Join(", ", events)}");
            if (!send || !fsm.gameObject.activeInHierarchy) continue;
            foreach (var ev in BattleStartEvents.Where(e => events.Contains(e))) {
                fsm.SendEvent(ev);
                sent.Add($"{fsm.gameObject.name}/{fsm.FsmName}:{ev}");
            }
        }

        File.WriteAllText(Path.Combine(_dir, $"scenefsm-{_role}-{label}.txt"), sb.ToString());
        Log($"wake boss scene {label}: sent [{string.Join(", ", sent)}]");
    }

    /// <summary>
    /// Boss specific steps a player does between phases (killer only), called from the boss kill loop when no
    /// boss part can be hit. Soul Master: after phase 1, touch the fake Desolate Dive orb to start phase 2.
    /// Returns a description of what was done, or null.
    /// </summary>
    private static string BossPhaseStep() {
        var hero = HeroController.instance;
        if (hero == null) return null;
        // Once the orb was touched, the hero must land on its own (the "Land hero" FSM waits for it)
        if (Object_FindActive("Knight Get Quake Fake") != null) return null;
        foreach (var name in new[] { "Quake Fake" }) {
            var go = UnityEngine.SceneManagement.SceneManager.GetActiveScene().IsValid()
                ? Object_FindActive(name)
                : null;
            if (go == null) continue;
            hero.transform.position = new Vector3(go.transform.position.x, go.transform.position.y, hero.transform.position.z);
            return $"moved hero onto '{name}' at {go.transform.position}";
        }

        return null;
    }

    private static GameObject Object_FindActive(string name) {
        return UnityEngine.Object.FindObjectsOfType<Transform>()
            .Where(t => t.name == name && t.gameObject.activeInHierarchy)
            .Select(t => t.gameObject)
            .FirstOrDefault(g => name != "Quake Fake Parent" || g.transform.childCount == 0 || g.GetComponentsInChildren<Transform>(false).Length > 1);
    }
}
