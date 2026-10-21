using System.Collections;
using System.Globalization;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;

namespace Hkmp.Ui;

/// <summary>
/// Development-only test of <see cref="EnemyTargetManager"/>: with <c>-hkmp-test-target Scene:gate</c> (and
/// <c>-hkmp-test-scenes none</c>), the H role becomes scene host of the scene, an enemy far from H is chosen, the C
/// role stands next to it and the test checks that the enemy's FSM target variables point to C's player object and
/// that the enemy gets closer to C than to H (on both instances). Then the roles swap places and the target must go
/// back to H. Output lines start with <c>HKMP_TEST</c>.
/// </summary>
internal static partial class DevEntityTest {
    /// <summary>
    /// Name of the remote player objects (see PlayerManager).
    /// </summary>
    private const string RemotePlayerObjectName = "Player Prefab";

    private static string F(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Names of the FSM GameObject variables of the enemy that point to a player object, with the object name.
    /// </summary>
    private static string PlayerVariables(Entity entity, out int toRemote, out int toHero) {
        toRemote = 0;
        toHero = 0;
        var hero = HeroController.instance != null ? HeroController.instance.gameObject : null;
        var parts = new System.Collections.Generic.List<string>();
        var remoteCount = 0;
        var heroCount = 0;

        void Count(string label, GameObject value) {
            if (value == null) {
                return;
            }

            if (value == hero) {
                heroCount++;
                parts.Add($"{label}=hero");
            } else if (value.name == RemotePlayerObjectName) {
                remoteCount++;
                parts.Add($"{label}=remote");
            }
        }

        foreach (var fsm in entity.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true)) {
            // Local variables
            foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
                Count($"{fsm.FsmName}.{variable.Name}", variable.Value);
            }

            // Target fields of the actions that follow the chosen target (global "Hero" references are swapped for
            // a per-enemy proxy)
            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action == null || EnemyTargetManager.IsExcludedAction(action)) {
                        continue;
                    }

                    foreach (var field in action.GetType().GetFields(System.Reflection.BindingFlags.Instance |
                                                                     System.Reflection.BindingFlags.Public)) {
                        var value = field.GetValue(action);
                        var label = $"{fsm.FsmName}/{state.Name}/{action.GetType().Name}.{field.Name}";
                        if (value is HutongGames.PlayMaker.FsmGameObject fgo && !string.IsNullOrEmpty(fgo.Name)) {
                            Count(label, fgo.Value);
                        } else if (value is HutongGames.PlayMaker.FsmOwnerDefault od &&
                                   od.OwnerOption == HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject &&
                                   od.GameObject != null && !string.IsNullOrEmpty(od.GameObject.Name)) {
                            Count(label, od.GameObject.Value);
                        }
                    }
                }
            }
        }

        toRemote = remoteCount;
        toHero = heroCount;
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The position of the enemy as seen on this instance.
    /// </summary>
    private static Vector3 ViewPosition(Entity entity) =>
        Em.IsSceneHost ? entity.Object.Host.transform.position : entity.Object.Client.transform.position;

    /// <summary>
    /// The synchronised position of the other player on this instance, or null.
    /// </summary>
    private static Vector3? OtherPlayerPosition() {
        var obj = Object.FindObjectsOfType<Transform>()
            .Where(t => t.name == RemotePlayerObjectName && t.gameObject.activeInHierarchy &&
                        t.parent != null && t.parent.name.StartsWith("Player Container"))
            .Select(t => t.gameObject)
            .FirstOrDefault();
        return obj == null ? null : obj.transform.position;
    }

    private static IEnumerator EnemyTargetTest(string scene, string gate) {
        var s = scene;
        Log($"===== enemy target {s} =====");

        // H becomes scene host of the empty scene, then C joins
        if (IsH) {
            yield return WaitSignal("tgt-C-out");
            if (CurrentScene == scene) {
                yield return GoTo(_stagingScene, _stagingGate);
            }

            yield return GoTo(scene, gate);
            yield return WaitUntil(() => Em.IsSceneHost, 30f);
            Check($"{s} H is scene host", Em.IsSceneHost && CurrentScene == scene, $"scene={CurrentScene}");
            yield return new WaitForSecondsRealtime(3f);
            Signal("tgt-H-host");
        } else {
            if (CurrentScene == scene || CurrentScene != _stagingScene) {
                yield return GoTo(_stagingScene, _stagingGate);
            }

            Signal("tgt-C-out");
            yield return WaitSignal("tgt-H-host");
            yield return GoTo(scene, gate);
            Check($"{s} C is scene client", !Em.IsSceneHost && CurrentScene == scene, $"scene={CurrentScene}");
            yield return new WaitForSecondsRealtime(3f);
            Signal("tgt-C-in");
        }

        // H picks the enemy: a live flying chaser if possible, the farthest from H
        Entity enemy = null;
        if (IsH) {
            yield return WaitSignal("tgt-C-in");
            yield return new WaitForSecondsRealtime(2f);
            var heroPos = HeroController.instance.transform.position;
            var alive = Em.Entities.Values
                .Where(e => e.HealthComponent != null && e.IsHostSimulated && IsAliveInView(e))
                .ToList();
            // Prefer enemies whose AI references the player (Crawlids, Tiktiks, ... never target anyone)
            int Refs(Entity e) {
                PlayerVariables(e, out var r, out var h);
                return r + h;
            }

            enemy = alive
                .Where(e => Refs(e) > 0)
                .OrderByDescending(e => e.Type == EntityType.Vengefly)
                .ThenByDescending(e => Vector2.Distance(e.Object.Host.transform.position, heroPos))
                .FirstOrDefault() ?? alive
                .OrderByDescending(e => Vector2.Distance(e.Object.Host.transform.position, heroPos))
                .FirstOrDefault();

            if (enemy == null) {
                Check($"{s} enemy found", false, "no live enemy");
                Signal("tgt-enemy", "-1");
                yield break;
            }

            foreach (var e in alive) {
                var all = e.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true)
                    .SelectMany(f => f.FsmVariables.GameObjectVariables.Select(v =>
                        $"{f.FsmName}.{v.Name}={(v.Value == null ? "null" : v.Value.name)}"));
                Log($"  go vars of {e.Id} '{e.Object.Host.name}' state={string.Join("/", e.Object.Host.GetComponents<PlayMakerFSM>().Select(f => f.ActiveStateName))}: {string.Join(" ", all)}");
            }

            DumpFsms(enemy.Object.Host, $"target-{enemy.Object.Host.name}");

            var pos = enemy.Object.Host.transform.position;
            Log($"enemy {Describe(enemy)} at ({F(pos.x)}, {F(pos.y)}), H at ({F(heroPos.x)}, {F(heroPos.y)}), vars: {PlayerVariables(enemy, out _, out _)}");
            Signal("tgt-enemy", enemy.Id.ToString());
        } else {
            yield return WaitSignal("tgt-enemy");
            enemy = Find(int.Parse(_signalValue));
            if (enemy == null) {
                Check($"{s} enemy found", false, $"id {_signalValue} not found");
                yield break;
            }
        }

        // Phase 1: C stands next to the enemy, H stays far away
        if (!IsH) {
            for (var i = 0; i < 3; i++) {
                var pos = ViewPosition(enemy);
                var hero = HeroController.instance.transform;
                hero.position = new Vector3(pos.x + 1.5f, pos.y, hero.position.z);
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        yield return new WaitForSecondsRealtime(1f);
        Screenshot("target-1-start");

        float startOther = 0f, startLocal = 0f;
        for (var t = 0; t <= 8; t++) {
            var enemyPos = ViewPosition(enemy);
            var localPos = HeroController.instance.transform.position;
            var otherPos = OtherPlayerPosition();
            var dLocal = Vector2.Distance(enemyPos, localPos);
            var dOther = otherPos.HasValue ? Vector2.Distance(enemyPos, otherPos.Value) : -1f;
            if (t == 0) {
                startLocal = dLocal;
                startOther = dOther;
            }

            var vars = IsH
                ? $"states={string.Join("/", enemy.Object.Host.GetComponents<PlayMakerFSM>().Select(f => f.ActiveStateName))} {PlayerVariables(enemy, out _, out _)}"
                : "";
            Log($"phase1 t={t}s enemy=({F(enemyPos.x)}, {F(enemyPos.y)}) dLocal={F(dLocal)} dOther={F(dOther)} {vars}");
            yield return new WaitForSecondsRealtime(1f);
        }

        {
            var enemyPos = ViewPosition(enemy);
            var dLocal = Vector2.Distance(enemyPos, HeroController.instance.transform.position);
            var otherPos = OtherPlayerPosition();
            var dOther = otherPos.HasValue ? Vector2.Distance(enemyPos, otherPos.Value) : -1f;
            if (IsH) {
                var vars = PlayerVariables(enemy, out var toRemote, out var toHero);
                Check($"{s} host enemy targets remote player", toRemote > 0 && toHero == 0,
                    $"toRemote={toRemote} toHero={toHero} vars: {vars}");
                Check($"{s} host enemy is nearer the remote player", dOther >= 0f && dOther < dLocal,
                    $"dRemote {F(startOther)} -> {F(dOther)}, dHost {F(startLocal)} -> {F(dLocal)}");
            } else {
                Check($"{s} client sees enemy at the local (non-host) player", dLocal < dOther || dOther < 0f,
                    $"dLocal {F(startLocal)} -> {F(dLocal)}, dHost {F(startOther)} -> {F(dOther)}");
            }
        }

        Screenshot("target-1-end");
        Signal($"tgt-phase1-{_role}");
        yield return WaitSignal($"tgt-phase1-{(_role == "p1" ? "p2" : "p1")}");

        // Phase 2: swap, H next to the enemy and C back at its entry position
        if (IsH) {
            for (var i = 0; i < 3; i++) {
                var pos = enemy.Object.Host.transform.position;
                var hero = HeroController.instance.transform;
                hero.position = new Vector3(pos.x + 1.5f, pos.y, hero.position.z);
                yield return new WaitForSecondsRealtime(0.5f);
            }
        } else {
            MoveHeroBack();
        }

        yield return new WaitForSecondsRealtime(4f);

        if (IsH) {
            var vars = PlayerVariables(enemy, out var toRemote, out var toHero);
            Check($"{s} host enemy targets host again", toHero > 0 && toRemote == 0,
                $"toRemote={toRemote} toHero={toHero} vars: {vars}");
        }

        Screenshot("target-2-end");
        Signal($"tgt-phase2-{_role}");
        yield return WaitSignal($"tgt-phase2-{(_role == "p1" ? "p2" : "p1")}");
    }
}
