using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using Modding;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <summary>
/// Development-only automated test of enemy synchronisation between two game instances, driven by command line
/// arguments. Inactive unless <c>-hkmp-test p1|p2</c> is given. Both instances must be connected to the same server
/// (see <see cref="DevAutoConnect"/>) with FullSynchronisation.
/// <para>Options:</para>
/// <list type="bullet">
/// <item><c>-hkmp-test p1|p2</c>: role of this instance.</item>
/// <item><c>-hkmp-test-host p1|p2</c>: which role should be scene host of the tested scenes (default p1).</item>
/// <item><c>-hkmp-test-dir path</c>: shared directory used to synchronise both instances (default
/// E:/HKMP-Enemies/testrun). Must be emptied before a run.</item>
/// <item><c>-hkmp-test-scenes Scene:gate,Scene:gate</c>: scenes to test, with the entry gate to use.</item>
/// <item><c>-hkmp-test-staging Scene:gate</c>: scene used to leave the tested scene (default Town:left1).</item>
/// </list>
/// All output lines start with <c>HKMP_TEST</c> in the ModLog.
/// </summary>
internal static partial class DevEntityTest {
    private const string DefaultScenes = "Tutorial_01:right1,Crossroads_01:right1,Crossroads_10:left1,Fungus1_01:left1,Fungus2_03:left1";

    private static string _role;
    private static string _hostRole;
    private static string _dir;
    private static bool IsH => _role == _hostRole;
    private static string Tag => IsH ? $"{_role}/H" : $"{_role}/C";

    private static int _pass;
    private static int _fail;
    private static int _info;

    private static string _signalValue;

    private static string _stagingScene = "Town";
    private static string _stagingGate = "left1";

    /// <summary>
    /// Start the test if requested on the command line.
    /// </summary>
    public static void TryStart() {
        _role = GetOption("-hkmp-test");
        if (string.IsNullOrEmpty(_role)) {
            return;
        }

        _role = _role.ToLowerInvariant();
        _hostRole = (GetOption("-hkmp-test-host") ?? "p1").ToLowerInvariant();
        _dir = GetOption("-hkmp-test-dir") ?? "E:/HKMP-Enemies/testrun";
        Directory.CreateDirectory(_dir);

        var staging = GetOption("-hkmp-test-staging");
        if (!string.IsNullOrEmpty(staging)) {
            var parts = staging.Split(':');
            _stagingScene = parts[0];
            _stagingGate = parts.Length > 1 ? parts[1] : "left1";
        }

        var scenesOption = GetOption("-hkmp-test-scenes") ?? DefaultScenes;
        if (scenesOption == "none") scenesOption = "";
        var scenes = scenesOption
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => {
                var parts = s.Split(':');
                return (Scene: parts[0], Gate: parts.Length > 1 ? parts[1] : "left1");
            })
            .ToList();

        Log($"start role={_role} host={_hostRole} dir={_dir} scenes={string.Join(",", scenes.Select(s => s.Scene + ":" + s.Gate))} staging={_stagingScene}:{_stagingGate}");

        MonoBehaviourUtil.Instance.StartCoroutine(Run(scenes));
    }

    private static void Log(string message) {
        Logger.Info($"HKMP_TEST [{Tag}] {message}");
    }

    private static void Check(string name, bool ok, string detail) {
        if (ok) {
            _pass++;
        } else {
            _fail++;
        }

        Logger.Info($"HKMP_TEST {(ok ? "PASS" : "FAIL")} [{Tag}] {name}: {detail}");
    }

    private static void Note(string name, string detail) {
        _info++;
        Logger.Info($"HKMP_TEST INFO [{Tag}] {name}: {detail}");
    }

    private static string CurrentScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

    private static EntityManager Em => EntityManager.Instance;

    private static void Signal(string key, string value = "1") {
        File.WriteAllText(Path.Combine(_dir, key), value);
        Log($"signal {key}={value}");
    }

    private static IEnumerator WaitSignal(string key, float timeout = 180f) {
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

        // Small delay so the writer finished writing
        yield return new WaitForSecondsRealtime(0.1f);
        _signalValue = File.ReadAllText(path);
    }

    private static IEnumerator WaitUntil(Func<bool> condition, float timeout) {
        var waited = 0f;
        while (!SafeCondition(condition) && waited < timeout) {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private static bool SafeCondition(Func<bool> condition) {
        try {
            return condition();
        } catch (Exception) {
            return false;
        }
    }

    private static void Screenshot(string step) {
        try {
            var path = Path.Combine(_dir, $"{_role}-{CurrentScene}-{step}.png");
            ScreenCapture.CaptureScreenshot(path);
            Log($"screenshot {path}");
        } catch (Exception e) {
            Log($"screenshot failed: {e.Message}");
        }
    }

    private static IEnumerator GoTo(string scene, string gate) {
        if (CurrentScene == scene) {
            yield break;
        }

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

        yield return WaitUntil(() => CurrentScene == scene && HeroController.instance != null, 60f);
        if (CurrentScene != scene) {
            Check($"{scene} transition", false, $"still in {CurrentScene}");
            yield break;
        }

        yield return WaitUntil(() => Em.IsSceneHostDetermined, 30f);
        yield return WaitUntil(() => !global::GameManager.instance.IsInSceneTransition, 20f);

        var gates = Object.FindObjectsOfType<TransitionPoint>()
            .Select(t => $"{t.name}->{t.targetScene}[{t.entryPoint}]");
        var inTransition = global::GameManager.instance.IsInSceneTransition;
        _entryPosition = HeroController.instance != null ? HeroController.instance.transform.position : null;
        Log($"entered {scene} (hostDetermined={Em.IsSceneHostDetermined}, sceneHost={Em.IsSceneHost}, inTransition={inTransition}); gates: {string.Join(" ", gates)}");
    }

    private static string Describe(Entity entity) {
        var hc = entity.HealthComponent;
        return $"id={entity.Id} type={entity.Type} name='{(entity.Object.Host != null ? entity.Object.Host.name : "null")}' {(hc == null ? "noHM" : hc.Describe())}";
    }

    private static void DumpEntities(string step) {
        var withHm = Em.Entities.Values.Where(e => e.HealthComponent != null).OrderBy(e => e.Id).ToList();
        var generic = Em.Entities.Values.Count(e => e.Type == EntityType.GenericEnemy);
        Log($"{step}: scene={CurrentScene} sceneHost={Em.IsSceneHost} entities={Em.Entities.Count} withHealth={withHm.Count} generic={generic} queued={Em.QueuedUpdateCount}");
        foreach (var entity in withHm) {
            Log($"  {Describe(entity)}");
        }
    }

    /// <summary>
    /// Health manager of the object that is "live" on this instance (host object on scene host, client object on
    /// scene clients).
    /// </summary>
    private static HealthManager ViewHm(Entity entity) {
        var hc = entity.HealthComponent;
        if (hc == null) {
            return null;
        }

        return Em.IsSceneHost ? hc.HostHealthManager : hc.ClientHealthManager;
    }

    private static bool IsAliveInView(Entity entity) {
        var hm = ViewHm(entity);
        return hm != null && hm.gameObject.activeInHierarchy && !hm.GetIsDead();
    }

    private static Entity Find(int id) {
        if (id < 0) {
            return null;
        }

        return Em.Entities.TryGetValue((ushort) id, out var entity) ? entity : null;
    }

    /// <summary>
    /// Position of the hero right after entering the current scene (a safe spot).
    /// </summary>
    private static Vector3? _entryPosition;

    /// <summary>
    /// Keep the hero invincible and at full health during the test, so it is never sent back to a bench.
    /// </summary>
    private static IEnumerator KeepHeroAlive() {
        while (true) {
            if (!_keepAlive) {
                yield return new WaitForSecondsRealtime(0.25f);
                continue;
            }

            try {
                var pd = PlayerData.instance;
                if (pd != null) {
                    pd.isInvincible = true;
                    var max = pd.GetInt("maxHealth");
                    if (pd.GetInt("health") < max) {
                        pd.SetInt("health", max);
                    }
                }
            } catch (Exception) {
                // ignored
            }

            yield return new WaitForSecondsRealtime(0.25f);
        }
    }

    /// <summary>
    /// Whether <see cref="KeepHeroAlive"/> is active (disabled during the player health test).
    /// </summary>
    private static bool _keepAlive = true;

    /// <summary>
    /// Move the hero back to where it entered the scene.
    /// </summary>
    private static void MoveHeroBack() {
        var hero = HeroController.instance;
        if (hero != null && _entryPosition.HasValue) {
            hero.transform.position = _entryPosition.Value;
        }
    }

    /// <summary>
    /// Move the local hero next to the given entity so the screenshots show it.
    /// </summary>
    private static void MoveHeroNear(Entity entity) {
        try {
            var hm = entity == null ? null : (ViewHm(entity) ?? entity.HealthComponent?.HostHealthManager);
            var hero = HeroController.instance;
            if (hm == null || hero == null) {
                return;
            }

            var pos = hm.transform.position;
            hero.transform.position = new Vector3(pos.x - 2f, pos.y + 1f, hero.transform.position.z);
        } catch (Exception e) {
            Log($"move hero failed: {e.Message}");
        }
    }

    private static void HitLocally(HealthManager hm, int damage) {
        ReflectionHelper.SetField(hm, "evasionByHitRemaining", 0f);
        hm.Hit(new HitInstance {
            Source = HeroController.instance.gameObject,
            AttackType = AttackTypes.Nail,
            DamageDealt = damage,
            Direction = 0f,
            MagnitudeMultiplier = 1f,
            Multiplier = 1f,
            MoveAngle = 0f,
            IgnoreInvulnerable = true,
            CircleDirection = false,
            MoveDirection = false,
            IsExtraDamage = false,
            SpecialType = SpecialTypes.None
        });
    }

    private static bool? SceneDataKilled(Entity entity) {
        var host = entity.Object.Host;
        if (host == null) {
            return null;
        }

        var pbi = host.GetComponent<PersistentBoolItem>();
        if (pbi == null) {
            return null;
        }

        var id = Game.Client.Entity.Component.HealthManagerComponent.GetPersistentId(pbi, host);
        var state = SceneData.instance.FindMyState(new PersistentBoolData {
            id = id,
            sceneName = global::GameManager.GetBaseSceneName(host.scene.name)
        });
        return state?.activated;
    }

    private static IEnumerator Run(List<(string Scene, string Gate)> scenes) {
        // Wait until in game, connected and the scene host is determined
        yield return WaitUntil(
            () => HeroController.instance != null && Em != null && Em.IsSceneHostDetermined &&
                  global::GameManager.instance.gameState == GlobalEnums.GameState.PLAYING,
            600f
        );
        Log($"in game: scene={CurrentScene} sceneHost={Em?.IsSceneHost}");
        yield return new WaitForSecondsRealtime(3f);

        PlayerData.instance.isInvincible = true;
        MonoBehaviourUtil.Instance.StartCoroutine(KeepHeroAlive());

        OrphanUpdateTest();

        Signal($"ready-{_role}");
        yield return WaitSignal($"ready-{(_role == "p1" ? "p2" : "p1")}", 600f);

        var health = (GetOption("-hkmp-test-health") ?? "").ToLowerInvariant();
        if (health == "1" || health == "only") {
            yield return PlayerHealthTest();
        }

        if (health == "only") {
            Log($"SUMMARY pass={_pass} fail={_fail} info={_info}");
            Signal($"finished-{_role}", $"{_pass} {_fail}");
            yield break;
        }

        var attacks = GetOption("-hkmp-test-attacks");
        if (!string.IsNullOrEmpty(attacks)) {
            foreach (var entry in attacks.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                var p = entry.Split(':');
                yield return AttackTest(p[0], p.Length > 1 ? p[1] : "left1");
            }
        }

        if (scenes.Count == 0) {
            Log($"SUMMARY pass={_pass} fail={_fail} info={_info}");
            Signal($"finished-{_role}", $"{_pass} {_fail}");
            yield break;
        }

        foreach (var (scene, gate) in scenes) {
            yield return TestScene(scene, gate);
        }

        // Bench reset: C simulates resting at a bench (resets semi-persistent enemies). The server must forget the
        // stored semi-persistent deaths of the (now empty) tested scenes.
        if (!IsH) {
            yield return GoTo(_stagingScene, _stagingGate);
            Signal("bench-C-out");
        } else {
            yield return WaitSignal("bench-C-out");
            yield return GoTo(_stagingScene, _stagingGate);
            Signal("bench-H-out");
        }

        if (!IsH) {
            yield return WaitSignal("bench-H-out");
            yield return new WaitForSecondsRealtime(2f);
            Log("simulating bench rest: GameManager.ResetSemiPersistentItems()");
            global::GameManager.instance.ResetSemiPersistentItems();
            yield return new WaitForSecondsRealtime(2f);
            Signal("bench-done");
        } else {
            yield return WaitSignal("bench-done");
        }

        Log($"SUMMARY pass={_pass} fail={_fail} info={_info}");
        Signal($"finished-{_role}", $"{_pass} {_fail}");
    }

    /// <summary>
    /// Regression test for EntityManager.CheckReceivedUpdates: an update for an entity that does not exist must not
    /// make the check loop forever.
    /// </summary>
    private static void OrphanUpdateTest() {
        var update = new ReliableEntityUpdate { Id = 60000 };
        update.UpdateTypes.Add(EntityUpdateType.Active);
        update.IsActive = true;

        var before = Em.QueuedUpdateCount;
        Em.HandleReliableEntityUpdate(update);
        var start = DateTime.UtcNow;
        Em.CheckReceivedUpdates();
        var ms = (DateTime.UtcNow - start).TotalMilliseconds;
        Check("orphan update", ms < 1000, $"CheckReceivedUpdates with orphan update returned in {ms:F1} ms, queued {before} -> {Em.QueuedUpdateCount}");
    }

    private static IEnumerator TestScene(string scene, string gate) {
        var s = scene;
        Log($"===== scene {s} =====");

        // Phase 0: the H role becomes scene host of the (empty) scene, then C joins
        if (IsH) {
            yield return WaitSignal($"{s}-C-out");
            yield return GoTo(scene, gate);
            yield return WaitUntil(() => Em.IsSceneHost, 30f);
            Check($"{s} H is scene host", Em.IsSceneHost && CurrentScene == scene, $"scene={CurrentScene} sceneHost={Em.IsSceneHost}");
            yield return new WaitForSecondsRealtime(3f);
            Signal($"{s}-H-host");
        } else {
            if (CurrentScene == scene || CurrentScene != _stagingScene) {
                yield return GoTo(_stagingScene, _stagingGate);
            }

            Signal($"{s}-C-out");
            yield return WaitSignal($"{s}-H-host");
            yield return GoTo(scene, gate);
            Check($"{s} C is scene client", !Em.IsSceneHost && CurrentScene == scene, $"scene={CurrentScene} sceneHost={Em.IsSceneHost}");
            yield return new WaitForSecondsRealtime(3f);
            Signal($"{s}-C-in");
        }

        if (CurrentScene != scene) {
            Check($"{s} abort", false, "could not enter scene, skipping it");
            Signal($"{s}-abort-{_role}");
            yield break;
        }

        if (GetOption("-hkmp-test-bossonly") != null) {
            _boss = null;
            yield return new WaitForSecondsRealtime(2f);
            // Boss rooms are often an additive "<scene>_boss" scene loaded a bit later
            yield return WaitUntil(() => Em.Entities.Values.Any(IsBossPart), 20f);
            if (IsH) {
                // Wake the boss up
                var boss = Em.Entities.Values.Where(IsBossPart).OrderByDescending(e => ViewHm(e).hp).FirstOrDefault();
                if (boss != null) {
                    MoveHeroNear(boss);
                    yield return new WaitForSecondsRealtime(3f);
                    WakeBossScene(s);
                    yield return new WaitForSecondsRealtime(8f);
                }
            }

            yield return BossKill(s);
            Signal($"{s}-done-{_role}");
            yield return WaitSignal($"{s}-done-{(_role == "p1" ? "p2" : "p1")}");
            yield break;
        }

        // Phase 1: target selection on H
        int a = -1, b = -1, d = -1;
        int hpA = 0;
        if (IsH) {
            yield return WaitSignal($"{s}-C-in");
            yield return new WaitForSecondsRealtime(2f);
            DumpEntities("initial");

            List<Entity> Candidates() => Em.Entities.Values
                .Where(e => e.HealthComponent != null && IsAliveInView(e) && ViewHm(e).hp > 1 && !ViewHm(e).IsInvincible)
                .OrderBy(e => e.Id)
                .ToList();

            // Wake up the scene (boss intros, hidden enemies) by moving next to the strongest enemy first
            var strongest = Em.Entities.Values
                .Where(e => e.HealthComponent != null && IsAliveInView(e))
                .OrderByDescending(e => ViewHm(e).hp)
                .FirstOrDefault();
            if (strongest != null) {
                Note($"{s} strongest enemy", Describe(strongest));
                MoveHeroNear(strongest);
                yield return new WaitForSecondsRealtime(ViewHm(strongest).hp >= 40 ? 8f : 2f);
                DumpEntities("after waking the strongest enemy");
                MoveHeroBack();
            }

            var candidates = Candidates();
            // A (partial hit, HP sync, host transfer) is the boss if there is one, so bosses are covered as well
            var boss = candidates.Where(e => ViewHm(e).hp >= 40).OrderByDescending(e => ViewHm(e).hp).FirstOrDefault();
            var ordered = boss == null ? candidates : new[] { boss }.Concat(candidates.Where(e => e != boss)).ToList();
            if (ordered.Count > 0) a = ordered[0].Id;
            if (ordered.Count > 1) b = ordered[1].Id;
            if (ordered.Count > 2) d = ordered[2].Id;

            string Info(int id) {
                var e = Find(id);
                if (e == null) return $"{id}|-|0|0|0";
                var pbi = e.Object.Host.GetComponent<PersistentBoolItem>();
                return $"{id}|{e.Object.Host.name}|{ViewHm(e).hp}|{(pbi != null ? 1 : 0)}|{(pbi != null && pbi.semiPersistent ? 1 : 0)}";
            }

            var targets = $"{Info(a)};{Info(b)};{Info(d)}";
            Note($"{s} targets", $"{candidates.Count} candidates; A;B;D = {targets}");
            Signal($"{s}-targets", targets);
        } else {
            yield return WaitSignal($"{s}-targets");
            yield return new WaitForSecondsRealtime(2f);
            DumpEntities("initial");
        }

        // Both parse the target info (H reads its own file too)
        var targetsValue = File.Exists(Path.Combine(_dir, $"{s}-targets")) ? File.ReadAllText(Path.Combine(_dir, $"{s}-targets")) : "";
        var parts = targetsValue.Split(';');
        if (parts.Length < 3) {
            Check($"{s} targets", false, $"no target info ({targetsValue})");
            yield break;
        }

        string[] F(int i) => parts[i].Split('|');
        a = int.Parse(F(0)[0]);
        b = int.Parse(F(1)[0]);
        d = int.Parse(F(2)[0]);
        hpA = int.Parse(F(0)[2]);
        var persistentB = F(1)[3] == "1";
        var persistentD = F(2)[3] == "1";

        // Check the ID -> object mapping is the same on both instances
        foreach (var i in new[] { 0, 1, 2 }) {
            var id = int.Parse(F(i)[0]);
            if (id < 0) continue;
            var e = Find(id);
            var localName = e?.Object.Host != null ? e.Object.Host.name : "missing";
            Check($"{s} id mapping {id}", localName == F(i)[1], $"H name '{F(i)[1]}', local name '{localName}'");
        }

        if (a < 0) {
            Note($"{s} no enemies", "no alive enemy with HealthManager found, skipping hit tests");
            yield return Finish(scene, gate);
            yield break;
        }

        // Phase 2: (a) partial hit by C on A
        if (IsH) {
            MoveHeroNear(Find(a));
            yield return new WaitForSecondsRealtime(2f);
            var ha = ViewHm(Find(a));
            Log($"H ready for A: {(ha == null ? "missing" : $"hp={ha.hp} invincible={ha.IsInvincible}")}");
            if (ha != null) {
                hpA = ha.hp;
            }

            Signal($"{s}-H-readyA", hpA.ToString());
        } else {
            yield return WaitSignal($"{s}-H-readyA");
            int.TryParse(_signalValue, out hpA);
        }

        var damageA = System.Math.Max(1, hpA / 2);
        var expectedHpA = hpA - damageA;
        if (!IsH) {
            var ea = Find(a);
            var hm = ea?.HealthComponent?.ClientHealthManager;
            if (hm == null) {
                Check($"{s} C hit A", false, "client health manager missing");
            } else {
                var before = hm.hp;
                HitLocally(hm, damageA);
                Log($"C hit A ({a}) dmg={damageA}: local hp {before} -> {hm.hp}");
            }

            Signal($"{s}-C-hitA");
            yield return WaitSignal($"{s}-H-A");
            yield return new WaitForSecondsRealtime(1f);
            var hostHp = _signalValue;
            var localHp = Find(a)?.HealthComponent?.ClientHealthManager?.hp;
            Check($"{s} (a) partial hit C->H, hp synced back to C", localHp.HasValue && localHp.Value.ToString() == hostHp,
                $"entity {a}: host hp={hostHp}, C client hp={localHp}, expected {expectedHpA}");
        } else {
            yield return WaitSignal($"{s}-C-hitA");
            var hostHm = Find(a)?.HealthComponent?.HostHealthManager;
            yield return WaitUntil(() => hostHm.hp != hpA, 5f);
            Check($"{s} (a) partial hit C->H applied on host", hostHm != null && hostHm.hp == expectedHpA,
                $"entity {a}: hp {hpA} -> {hostHm?.hp} (expected {expectedHpA})");
            Signal($"{s}-H-A", hostHm != null ? hostHm.hp.ToString() : "null");
        }

        // Phase 3: (a) kill by C of B
        if (b >= 0) {
            MoveHeroNear(Find(b));
            yield return new WaitForSecondsRealtime(1.5f);
            Screenshot("before-C-kills-B");
            if (IsH) {
                var hb = ViewHm(Find(b));
                var hpBeforeSwing = hb != null ? hb.hp : -1;
                var ignoredBefore = HealthManagerComponent.ReplicaHitsIgnored;
                var appliedBefore = HealthManagerComponent.HitsApplied;
                Log($"H ready for B: {(hb == null ? "missing" : $"hp={hb.hp} invincible={hb.IsInvincible}")}");
                Signal($"{s}-H-readyB");

                // Real nail swing of C (not a synthetic hit): the forwarded hit must be applied exactly once, and
                // the replicated slash of C on this instance must not add damage
                yield return WaitSignal($"{s}-C-swing");
                yield return new WaitForSecondsRealtime(1f);
                var swing = (_signalValue ?? "0|0").Split('|');
                var sent = int.Parse(swing[0]);
                var damage = int.Parse(swing[1]);
                var hpAfterSwing = hb != null ? hb.hp : -1;
                var expected = System.Math.Max(hpBeforeSwing - damage, -50);
                var dead = hb == null || hb.GetIsDead();
                var detail = $"entity {b}: C sent {sent} hit(s) dmg {damage}; host hp {hpBeforeSwing} -> {hpAfterSwing} dead={dead}; " +
                             $"hits applied on host (all entities) {HealthManagerComponent.HitsApplied - appliedBefore}, replica hits ignored (all entities) {HealthManagerComponent.ReplicaHitsIgnored - ignoredBefore}";
                if (sent == 0) {
                    Note($"{s} (a) real nail swing C->H", "C's swing did not connect locally: " + detail);
                } else {
                    Check($"{s} (a) real nail swing C->H applied once", dead ? damage >= hpBeforeSwing : hpAfterSwing == expected, detail);
                }

                Signal($"{s}-H-swing");

                // Real fireball (Vengeful Spirit) cast by C at A
                var spellAppliedBefore = HealthManagerComponent.SpellHitsApplied;
                var ha2 = ViewHm(Find(a));
                var hpBeforeSpell = ha2 != null ? ha2.hp : -1;
                yield return WaitSignal($"{s}-C-spell");
                yield return new WaitForSecondsRealtime(1.5f);
                var spellInfo = (_signalValue ?? "0|0").Split('|');
                var spellDetail = $"entity {a}: C forwarded {spellInfo[0]} spell hit(s) dmg {spellInfo[1]}; host applied {HealthManagerComponent.SpellHitsApplied - spellAppliedBefore} spell hit(s); host hp {hpBeforeSpell} -> {(ha2 != null ? ha2.hp : -1)}";
                if (spellInfo[0] == "0") {
                    Note($"{s} (a) real fireball C->H", "fireball did not hit locally: " + spellDetail);
                } else {
                    // A fireball passes through and must hit an enemy once: one forwarded and one applied spell hit
                    Check($"{s} (a) real fireball C->H applied once on host", HealthManagerComponent.SpellHitsApplied - spellAppliedBefore == 1 && spellInfo[0] == "1", spellDetail);
                }

                if (ha2 != null && !ha2.GetIsDead()) {
                    expectedHpA = ha2.hp;
                }

                Signal($"{s}-H-spell", expectedHpA.ToString());
            } else {
                yield return WaitSignal($"{s}-H-readyB");

                HealthManagerComponent.SentByEntity.TryGetValue((ushort) b, out var sentBefore);
                var totalBefore = HealthManagerComponent.HitsSent;
                var eb = Find(b);
                var cb = eb?.HealthComponent?.ClientHealthManager;
                var hero = HeroController.instance;
                if (cb != null && hero != null) {
                    var pos = cb.transform.position;
                    hero.transform.position = new Vector3(pos.x - 1.3f, pos.y + 0.3f, hero.transform.position.z);
                    hero.FaceRight();
                    yield return new WaitForSecondsRealtime(0.4f);
                    try {
                        typeof(HeroController)
                            .GetMethod("Attack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                            ?.Invoke(hero, new object[] { GlobalEnums.AttackDirection.normal });
                    } catch (Exception e) {
                        Log($"attack failed: {e.Message}");
                    }

                    yield return new WaitForSecondsRealtime(1.2f);
                    Screenshot("C-real-swing-B");
                }

                HealthManagerComponent.SentByEntity.TryGetValue((ushort) b, out var sentAfter);
                var sentNow = sentAfter.Count - sentBefore.Count;
                var damageNow = sentAfter.Damage - sentBefore.Damage;
                Log($"C real nail swing at B ({b}): forwarded {sentNow} hit(s) on B, dmg {damageNow} (hits on all entities: {HealthManagerComponent.HitsSent - totalBefore}), local {(cb == null ? "missing" : $"hp={cb.hp}")}");
                Signal($"{s}-C-swing", $"{sentNow}|{damageNow}");
                yield return WaitSignal($"{s}-H-swing");
                yield return new WaitForSecondsRealtime(0.5f);

                // Real fireball cast at A: give the spell and soul, face A from the left and send the cast event
                var spellBefore = HealthManagerComponent.SpellHitsSent;
                HealthManagerComponent.SentByEntity.TryGetValue((ushort) a, out var spellEntBefore);
                var ea2 = Find(a);
                var ca = ea2?.HealthComponent?.ClientHealthManager;
                if (ca != null && ca.hp <= 20) {
                    Log($"skipping fireball: A hp {ca.hp} too low (it would die and break the later checks)");
                } else if (ca != null && hero != null) {
                    var pd = PlayerData.instance;
                    pd.SetInt("fireballLevel", 1);
                    pd.SetBool("hasSpell", true);
                    pd.SetInt("MPCharge", 99);
                    var pos = ca.transform.position;
                    hero.transform.position = new Vector3(pos.x - 4f, pos.y + 0.3f, hero.transform.position.z);
                    hero.FaceRight();
                    yield return new WaitForSecondsRealtime(0.4f);
                    var spellFsm = hero.gameObject.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Spell Control");
                    Log($"casting fireball: spellFsm={(spellFsm == null ? "missing" : spellFsm.ActiveStateName)}");
                    spellFsm?.SendEvent("QUICK CAST");
                    yield return new WaitForSecondsRealtime(2f);
                    Log($"after cast: spellFsm={(spellFsm == null ? "missing" : spellFsm.ActiveStateName)} MP={pd.GetInt("MPCharge")}");
                    Screenshot("C-fireball-A");
                }

                HealthManagerComponent.SentByEntity.TryGetValue((ushort) a, out var spellEntAfter);
                var spellSent = HealthManagerComponent.SpellHitsSent - spellBefore;
                Log($"C fireball at A ({a}): forwarded {spellSent} spell hit(s), dmg on A {spellEntAfter.Damage - spellEntBefore.Damage}");
                Signal($"{s}-C-spell", $"{spellSent}|{spellEntAfter.Damage - spellEntBefore.Damage}");
                yield return WaitSignal($"{s}-H-spell");
                if (int.TryParse(_signalValue, out var hpAfterSpell)) {
                    expectedHpA = hpAfterSpell;
                }

                MoveHeroBack();
            }

            if (!IsH) {
                var hm = Find(b)?.HealthComponent?.ClientHealthManager;
                if (hm != null) {
                    HitLocally(hm, 9999);
                    Log($"C hit B ({b}) dmg=9999");
                }

                Signal($"{s}-C-killB");
                yield return WaitSignal($"{s}-H-B");
                yield return WaitUntil(() => !IsAliveInView(Find(b)), 5f);
                var eb = Find(b);
                Check($"{s} (a) kill C->H, C sees death", eb != null && !IsAliveInView(eb), $"H says: {_signalValue}; C: {(eb == null ? "missing" : Describe(eb))}");
                Screenshot("C-killed-B");
            } else {
                yield return WaitSignal($"{s}-C-killB");
                var eb = Find(b);
                yield return WaitUntil(() => !IsAliveInView(eb), 5f);
                Check($"{s} (a) kill C->H, host enemy died", eb != null && !IsAliveInView(eb), eb == null ? "missing" : Describe(eb));
                Screenshot("C-killed-B");
                yield return new WaitForSecondsRealtime(0.5f);
                MoveHeroBack();
                Signal($"{s}-H-B", eb == null ? "missing" : Describe(eb));
            }
        }

        // Phase 4: (b) kill by H of D
        if (d >= 0) {
            MoveHeroNear(Find(d));
            yield return new WaitForSecondsRealtime(1.5f);
            Screenshot("before-H-kills-D");
            if (IsH) {
                var hm = Find(d)?.HealthComponent?.HostHealthManager;
                if (hm != null) {
                    Log($"H ready for D: hp={hm.hp} invincible={hm.IsInvincible}");
                    HitLocally(hm, 9999);
                    Log($"H hit D ({d}) dmg=9999: dead={hm.GetIsDead()}");
                }

                Signal($"{s}-H-killD");
                yield return WaitSignal($"{s}-C-D");
                Screenshot("H-killed-D");
                yield return new WaitForSecondsRealtime(0.5f);
                MoveHeroBack();
            } else {
                yield return WaitSignal($"{s}-H-killD");
                var ed = Find(d);
                yield return WaitUntil(() => !IsAliveInView(ed), 5f);
                Check($"{s} (b) kill H->C, C sees death", ed != null && !IsAliveInView(ed), ed == null ? "missing" : Describe(ed));
                Screenshot("H-killed-D");
                Signal($"{s}-C-D");
            }
        }

        // Phase 4b: enemy without registry entry (generic entity): killed by C, must die on both
        if (IsH) {
            var generic = Em.Entities.Values
                .Where(e => e.Type == EntityType.GenericEnemy && e.HealthComponent != null && IsAliveInView(e))
                .OrderBy(e => e.Id)
                .FirstOrDefault();
            if (generic != null) {
                MoveHeroNear(generic);
                yield return new WaitForSecondsRealtime(1f);
            }

            Signal($"{s}-generic", generic == null ? "-1" : $"{generic.Id}|{generic.Object.Host.name}");
            if (generic != null) {
                yield return WaitSignal($"{s}-C-killG");
                yield return WaitUntil(() => !IsAliveInView(generic), 5f);
                Check($"{s} (e) generic enemy killed by C dies on host", !IsAliveInView(generic), Describe(generic));
                Screenshot("C-killed-generic");
                MoveHeroBack();
                Signal($"{s}-H-G");
            }
        } else {
            yield return WaitSignal($"{s}-generic");
            var parts2 = (_signalValue ?? "-1").Split('|');
            var gid = int.Parse(parts2[0]);
            if (gid >= 0) {
                var g = Find(gid);
                var localName = g?.Object.Host != null ? g.Object.Host.name : "missing";
                Check($"{s} (e) generic id mapping {gid}", g != null && g.Type == EntityType.GenericEnemy && localName == parts2[1],
                    $"H name '{parts2[1]}', local '{localName}' type {g?.Type}");
                var hm = g?.HealthComponent?.ClientHealthManager;
                if (hm != null) {
                    HitLocally(hm, 9999);
                }

                Signal($"{s}-C-killG");
                yield return WaitSignal($"{s}-H-G");
                yield return WaitUntil(() => !IsAliveInView(g), 5f);
                Check($"{s} (e) generic enemy killed by C dies on C", g != null && !IsAliveInView(g), g == null ? "missing" : Describe(g));
            } else {
                Note($"{s} (e) generic", "no alive generic enemy in this scene");
            }
        }

        // Phase 5: (c) C leaves and re-enters while H stays (late join)
        if (!IsH) {
            yield return GoTo(_stagingScene, _stagingGate);
            yield return new WaitForSecondsRealtime(2f);
            yield return GoTo(scene, gate);
            yield return new WaitForSecondsRealtime(4f);
            DumpEntities("C re-entered (H stayed)");
            CheckDeadAfterReentry(s, "late join", b, d, persistentB, persistentD, true);
            var ea = Find(a);
            var hp = ea?.HealthComponent?.ClientHealthManager?.hp;
            Check($"{s} (c) late join: A hp from server", ea != null && IsAliveInView(ea) && hp == expectedHpA,
                $"entity {a}: {(ea == null ? "missing" : Describe(ea))}, expected hp {expectedHpA}");
            Signal($"{s}-C-rejoined");
        } else {
            yield return WaitSignal($"{s}-C-rejoined");
        }

        // Phase 6: (d) scene host transfer: H leaves, C becomes host
        var hpAfterHostHit = expectedHpA;
        if (IsH) {
            yield return GoTo(_stagingScene, _stagingGate);
            Signal($"{s}-H-out");
            yield return WaitSignal($"{s}-C-hosthit");
            int.TryParse(_signalValue, out hpAfterHostHit);
            yield return GoTo(scene, gate);
            yield return new WaitForSecondsRealtime(4f);
            DumpEntities("H re-entered as client");
            Check($"{s} (d) old host re-enters as client", !Em.IsSceneHost, $"sceneHost={Em.IsSceneHost}");
            var ea = Find(a);
            var hp = ea?.HealthComponent?.ClientHealthManager?.hp;
            Check($"{s} (d) A hp after new host hit, seen by old host", hp == hpAfterHostHit,
                $"entity {a}: {(ea == null ? "missing" : Describe(ea))}, expected hp {hpAfterHostHit}");
            CheckDeadAfterReentry(s, "old host re-entry", b, d, persistentB, persistentD, true);
            Signal($"{s}-H-back");
        } else {
            yield return WaitSignal($"{s}-H-out");
            yield return WaitUntil(() => Em.IsSceneHost, 20f);
            Check($"{s} (d) C became scene host", Em.IsSceneHost, $"sceneHost={Em.IsSceneHost}");
            yield return new WaitForSecondsRealtime(1f);
            var ea = Find(a);
            var hostHm = ea?.HealthComponent?.HostHealthManager;
            Check($"{s} (d) A hp kept on host transfer", hostHm != null && hostHm.hp == expectedHpA && hostHm.gameObject.activeInHierarchy,
                $"entity {a}: {(ea == null ? "missing" : Describe(ea))}, expected hp {expectedHpA}");
            CheckDeadAfterReentry(s, "after host transfer", b, d, persistentB, persistentD, true);

            if (hostHm != null && hostHm.hp > 1) {
                var damage = System.Math.Max(1, hostHm.hp / 2);
                var before = hostHm.hp;
                HitLocally(hostHm, damage);
                Log($"new host hit A ({a}) dmg={damage}: hp {before} -> {hostHm.hp}");
            }

            yield return new WaitForSecondsRealtime(1f);
            Signal($"{s}-C-hosthit", hostHm != null ? hostHm.hp.ToString() : "-1");
            yield return WaitSignal($"{s}-H-back");
        }

        yield return Finish(scene, gate, b, d, persistentB, persistentD);
    }

    /// <summary>
    /// Phase 7: (c) both players leave the scene (it becomes empty on the server) and come back: killed enemies must
    /// stay dead on both instances.
    /// </summary>
    private static IEnumerator Finish(string scene, string gate, int b = -1, int d = -1, bool persistentB = false, bool persistentD = false) {
        var s = scene;

        // Both leave (C first, then H), the scene is now empty
        if (!IsH) {
            yield return GoTo(_stagingScene, _stagingGate);
            Signal($"{s}-C-left");
            yield return WaitSignal($"{s}-H-left");
        } else {
            yield return WaitSignal($"{s}-C-left");
            yield return GoTo(_stagingScene, _stagingGate);
            yield return new WaitForSecondsRealtime(2f);
            Signal($"{s}-H-left");
        }

        if (b < 0 && d < 0) {
            Signal($"{s}-done-{_role}");
            yield break;
        }

        // H re-enters first (becomes host of the empty scene), then C
        if (IsH) {
            yield return GoTo(scene, gate);
            yield return new WaitForSecondsRealtime(4f);
            DumpEntities("H reloaded empty scene");
            CheckDeadAfterReentry(s, "reload of empty scene (host)", b, d, persistentB, persistentD, false);
            Signal($"{s}-H-reloaded");
            yield return WaitSignal($"{s}-C-reloaded");
        } else {
            yield return WaitSignal($"{s}-H-reloaded");
            yield return GoTo(scene, gate);
            yield return new WaitForSecondsRealtime(4f);
            DumpEntities("C reloaded scene");
            CheckDeadAfterReentry(s, "reload (client)", b, d, persistentB, persistentD, false);
            Signal($"{s}-C-reloaded");
        }

        yield return BossKill(s);

        Signal($"{s}-done-{_role}");
        yield return WaitSignal($"{s}-done-{(_role == "p1" ? "p2" : "p1")}");
    }

    /// <summary>
    /// Bosses known to the test: entity types that make up the boss and the PlayerData bool set when it is defeated.
    /// </summary>
    private static readonly (EntityType[] Types, string Flag)[] Bosses = {
        (new[] { EntityType.FalseKnight, EntityType.FalseKnightHead }, "falseKnightDefeated"),
        (new[] { EntityType.GruzMother }, "killedBigFly"),
        (new[] { EntityType.Hornet }, "hornet1Defeated"),
        (new[] { EntityType.VengeflyKing }, "killedBigBuzzer"),
        (new[] { EntityType.BroodingMawlek, EntityType.BroodingMawlekHead, EntityType.BroodingMawlekArm }, "killedMawlek"),
        (new[] { EntityType.SoulMaster, EntityType.SoulMasterPhase2 }, "killedMageLord"),
        (new[] { EntityType.MantisLord }, "defeatedMantisLords"),
        (new[] { EntityType.BrokenVessel }, "killedInfectedKnight"),
    };

    private static (EntityType[] Types, string Flag)? _boss;

    private static bool IsBossPart(Entity e) {
        if (e.HealthComponent == null) return false;
        var boss = _boss ?? Bosses.FirstOrDefault(b => Em.Entities.Values.Any(x => x.HealthComponent != null && b.Types.Contains(x.Type)));
        return boss.Types != null && boss.Types.Contains(e.Type);
    }

    /// <summary>
    /// Write the structure (states, transitions, actions with field values) of all FSMs of the given object to a
    /// file in the test directory.
    /// </summary>
    private static void DumpFsms(GameObject go, string label) {
        try {
            var sb = new System.Text.StringBuilder();
            foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>(true)) {
                sb.AppendLine($"=== {fsm.gameObject.name} / FSM '{fsm.FsmName}' active='{fsm.ActiveStateName}'");
                foreach (var v in fsm.FsmVariables.IntVariables) sb.AppendLine($"  int {v.Name}={v.Value}");
                foreach (var v in fsm.FsmVariables.BoolVariables) sb.AppendLine($"  bool {v.Name}={v.Value}");
                foreach (var state in fsm.FsmStates) {
                    sb.AppendLine($"  [{state.Name}] -> {string.Join(", ", state.Transitions.Select(t => $"{t.EventName}:{t.ToState}"))}");
                    foreach (var action in state.Actions) {
                        var fields = action.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                            .Select(f => {
                                object val;
                                try { val = f.GetValue(action); } catch { val = "?"; }
                                if (val is HutongGames.PlayMaker.NamedVariable nv) {
                                    return $"{f.Name}={(string.IsNullOrEmpty(nv.Name) ? "" : nv.Name + ":")}{nv.RawValue}";
                                }
                                if (val is HutongGames.PlayMaker.FsmEvent fe) return $"{f.Name}=ev:{fe.Name}";
                                if (val is HutongGames.PlayMaker.FsmOwnerDefault od) return $"{f.Name}={od.OwnerOption}:{(od.GameObject?.Value != null ? od.GameObject.Value.name : "")}";
                                return $"{f.Name}={val}";
                            });
                        sb.AppendLine($"      {action.GetType().Name} {string.Join(" ", fields)}");
                    }
                }
            }

            var path = Path.Combine(_dir, $"fsmdump-{_role}-{label}.txt");
            File.WriteAllText(path, sb.ToString());
            Log($"fsm dump written: {path}");
        } catch (Exception e) {
            Log($"fsm dump failed: {e}");
        }
    }

    /// <summary>
    /// Log every active state change of all FSMs under the given objects (as HKMP_FSM lines) while running.
    /// </summary>
    private static IEnumerator TraceFsms(Func<IEnumerable<GameObject>> roots, Func<bool> keepRunning) {
        var last = new Dictionary<PlayMakerFSM, string>();
        while (keepRunning()) {
            foreach (var root in roots()) {
                if (root == null) continue;
                foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true)) {
                    var st = fsm.ActiveStateName;
                    if (!last.TryGetValue(fsm, out var prev) || prev != st) {
                        last[fsm] = st;
                        Logger.Info($"HKMP_FSM [{Tag}] {fsm.gameObject.name}/{fsm.FsmName}: {prev} -> {st}");
                    }
                }
            }

            yield return null;
        }
    }

    private static string BossFlag => _boss?.Flag ?? "falseKnightDefeated";

    private static bool BossDefeated() => PlayerData.instance.GetBool(BossFlag);

    /// <summary>
    /// Kill a boss (False Knight) to the end. The killer role (h or c, option -hkmp-test-bosskiller, default c) hits all
    /// alive parts of the boss in its own view; the other role observes. Logs FSM state changes on both.
    /// </summary>
    private static IEnumerator BossKill(string s) {
        if (!Em.Entities.Values.Any(IsBossPart)) {
            yield break;
        }

        _boss = Bosses.FirstOrDefault(b => Em.Entities.Values.Any(x => x.HealthComponent != null && b.Types.Contains(x.Type)));
        Log($"boss in scene: {string.Join("+", _boss.Value.Types)} flag={BossFlag} initially={BossDefeated()}");
        var killer = (GetOption("-hkmp-test-bosskiller") ?? "c").ToLowerInvariant();
        var iKill = killer == "h" ? IsH : !IsH;
        var parts = Em.Entities.Values.Where(IsBossPart).ToList();

        foreach (var e in parts.Where(e => e.Object.Host != null).Take(1)) {
            DumpFsms(e.Object.Host, "host-" + e.Object.Host.name);
        }

        Log($"boss kill start (killer={killer}, me={(iKill ? "killer" : "observer")}): {string.Join(" | ", parts.Select(Describe))}");
        var running = true;
        MonoBehaviourUtil.Instance.StartCoroutine(TraceFsms(
            () => parts.SelectMany(e => new[] { e.Object.Host, e.Object.Client }),
            () => running
        ));

        Signal($"{s}-boss-start-{_role}");
        yield return WaitSignal($"{s}-boss-start-{(_role == "p1" ? "p2" : "p1")}");

        if (iKill) {
            var hits = 0;
            var waited = 0f;
            var lastLog = 0f;
            // Like a real player: the armour is only attacked when the False Knight is not in its rage sequence
            // (hitting it mid-leap stuns it again before "Rage Check" and the fight never advances a phase)
            var body = parts.FirstOrDefault(e => e.Type == EntityType.FalseKnight);
            var historyIndex = 0;
            var armourAllowed = true;
            var lastArmourHit = -10f;
            while (waited < 180f && !BossDefeated()) {
                if (body != null) {
                    var history = body.HostStateHistory;
                    for (; historyIndex < history.Count; historyIndex++) {
                        var st = history[historyIndex];
                        if (!st.StartsWith("FalseyControl/")) continue;
                        st = st.Substring("FalseyControl/".Length);
                        if (st == "Recover" || st == "Stun Fail") {
                            armourAllowed = false;
                        } else if (st.StartsWith("To Phase") || st == "Rage End" || st.StartsWith("JA ") || st == "Particle Pause" || st.StartsWith("Floor")) {
                            armourAllowed = true;
                        }
                    }
                }

                var anyHittable = false;
                foreach (var e in parts) {
                    var hm = ViewHm(e);
                    if (hm == null || !hm.gameObject.activeInHierarchy || hm.GetIsDead() || hm.IsInvincible) {
                        continue;
                    }

                    anyHittable = true;

                    if (e.Type == EntityType.FalseKnight && (!armourAllowed || waited - lastArmourHit < 0.4f)) {
                        continue;
                    }

                    MoveHeroNear(e);
                    HitLocally(hm, 13);
                    if (e.Type == EntityType.FalseKnight) lastArmourHit = waited;
                    hits++;
                }

                if (!anyHittable && _boss?.Flag == "killedMageLord") {
                    var step = BossPhaseStep();
                    if (step != null && waited - lastLog >= 5f) Log($"boss phase step: {step}");
                }

                if (waited - lastLog >= 5f) {
                    lastLog = waited;
                    Log($"boss kill t={waited:F0}s hits={hits} armourAllowed={armourAllowed} state={body?.GetHostFsmState("FalseyControl")}: {string.Join(" | ", parts.Select(Describe))}");
                }

                waited += 0.4f;
                yield return new WaitForSecondsRealtime(0.4f);
            }

            Log($"boss kill loop end t={waited:F0}s hits={hits} defeated={BossDefeated()}");
            yield return new WaitForSecondsRealtime(8f);
            Screenshot("boss-end");
            Signal($"{s}-boss-done");
        } else {
            var waited = 0f;
            while (!File.Exists(Path.Combine(_dir, $"{s}-boss-done")) && waited < 180f) {
                Log($"boss observe t={waited:F0}s defeated={BossDefeated()}: {string.Join(" | ", parts.Select(Describe))}");
                // Soul Master: phase 2 is started by a scene trigger (fake Desolate Dive orb) that only the scene
                // host simulates, so the observer touches it too if it is the scene host (see NOTES)
                if (_boss?.Flag == "killedMageLord" && Em.IsSceneHost) {
                    if (((int) waited) % 20 == 0) WakeBossScene($"{s}-t{waited:F0}", false);
                    for (var i = 0; i < 10; i++) {
                        var step = BossPhaseStep();
                        if (step != null && i == 0) Log($"boss phase step (observer host): {step}");
                        yield return new WaitForSecondsRealtime(0.5f);
                    }
                } else {
                    yield return new WaitForSecondsRealtime(5f);
                }

                waited += 5f;
            }

            yield return WaitSignal($"{s}-boss-done");
            yield return new WaitForSecondsRealtime(3f);
            Screenshot("boss-end");
        }

        running = false;
        var detail = $"{BossFlag}={BossDefeated()}; {string.Join(" | ", parts.Select(Describe))}";
        Check($"{s} (e) boss killed to the end (killer={killer}) seen as defeated", BossDefeated(), detail);
        Signal($"{s}-boss-checked-{_role}");
        yield return WaitSignal($"{s}-boss-checked-{(_role == "p1" ? "p2" : "p1")}");
    }

    private static void CheckDeadAfterReentry(string s, string when, int b, int d, bool persistentB, bool persistentD, bool sceneOccupied) {
        foreach (var (label, id, persistent) in new[] { ("B", b, persistentB), ("D", d, persistentD) }) {
            if (id < 0) continue;
            var e = Find(id);
            var killedInSceneData = e == null ? null : SceneDataKilled(e);
            var detail = $"entity {id}: {(e == null ? "missing" : Describe(e))} sceneDataKilled={killedInSceneData?.ToString() ?? "n/a"} persistentBool={persistent}";
            if (e == null) {
                Check($"{s} (c) {label} dead on {when}", false, detail);
                continue;
            }

            if (!persistent && !sceneOccupied) {
                // Vanilla respawns enemies without a persistent bool when the scene is reloaded
                Note($"{s} (c) {label} on {when} (no persistent bool, vanilla respawns it)", detail);
                continue;
            }

            Check($"{s} (c) {label} dead on {when}", !IsAliveInView(e), detail);
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
