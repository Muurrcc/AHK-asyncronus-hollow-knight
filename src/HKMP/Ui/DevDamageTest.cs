using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GlobalEnums;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Util;
using Modding;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <summary>
/// Development test of (1) the shared ability gates on join (canDash only when somebody has the Mothwing Cloak)
/// and (2) the damage that a scene client deals to every enemy of a room, as seen by the scene host. Inactive unless
/// <c>-hkmp-test-dmg p1|p2</c> is given. Lines in the ModLog start with <c>HKMP_DMG</c>.
/// <list type="bullet">
/// <item><c>-hkmp-test-dmg-hostgame N</c> (p1 only): host the game from the main menu on save slot N (new game if
/// empty) on <c>-hkmp-test-dmg-port</c>, like the player that creates the server. p2 joins with
/// <c>-hkmp-connect</c>.</item>
/// <item><c>-hkmp-test-dmg-sh p1|p2</c>: role that is scene host of the tested rooms (default p1).</item>
/// <item><c>-hkmp-test-dmg-scenes Scene:gate,...</c>: rooms to sweep. <c>-hkmp-test-dir</c>: signal directory.</item>
/// <item><c>-hkmp-test-dmg-prekill N</c>: before entering each room, the scene host marks the first N enemies of
/// the room as killed in its own scene data only (as after playing the room alone before the other player
/// joined).</item>
/// </list>
/// For each room: the scene host enters first, the client second; both write the entity list; the client hits every
/// enemy it can see (entity client copies and unregistered health managers) through <see cref="HealthManager.Hit"/>
/// with the local hero as source (same path as a nail swing); the scene host checks the HP of its objects.
/// </summary>
internal static class DevDamageTest {
    private static string _role;
    private static string _sceneHostRole;
    private static string _dir;
    private static NetClient _netClient;
    private static UiManager _uiManager;
    private static int _pass, _fail, _info;
    private static string _signalValue;
    private static int Damage => int.TryParse(GetOption("-hkmp-test-dmg-damage"), out var d) ? d : 4;

    private static bool IsH => _role == _sceneHostRole;
    private static string Tag => IsH ? $"{_role}/H" : $"{_role}/C";
    private static string Other => _role == "p1" ? "p2" : "p1";
    private static string CurrentScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    private static EntityManager Em => EntityManager.Instance;

    public static void TryStart(UiManager uiManager, NetClient netClient) {
        _role = GetOption("-hkmp-test-dmg")?.ToLowerInvariant();
        if (_role != "p1" && _role != "p2") {
            return;
        }

        _uiManager = uiManager;
        _netClient = netClient;
        _sceneHostRole = (GetOption("-hkmp-test-dmg-sh") ?? "p1").ToLowerInvariant();
        _dir = GetOption("-hkmp-test-dir") ?? "E:/AHK-BugTest/signals";
        Directory.CreateDirectory(_dir);

        var scenes = (GetOption("-hkmp-test-dmg-scenes") ?? "Crossroads_01:right1")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => {
                var p = s.Split(':');
                return (Scene: p[0], Gate: p.Length > 1 ? p[1] : "left1");
            })
            .ToList();

        Log($"start role={_role} sceneHost={_sceneHostRole} dir={_dir} scenes={scenes.Count}");
        MonoBehaviourUtil.Instance.StartCoroutine(Run(scenes));
    }

    private static IEnumerator Run(List<(string Scene, string Gate)> scenes) {
        AudioListener.volume = 0f;

        var hostSlot = GetOption("-hkmp-test-dmg-hostgame");
        if (_role == "p1" && hostSlot != null) {
            yield return HostGame(int.Parse(hostSlot));
        }

        var waited = 0f;
        yield return new WaitUntil(() => {
            waited += Time.unscaledDeltaTime;
            return waited > 600f || HeroController.instance != null && Em != null && Em.IsSceneHostDetermined &&
                   global::GameManager.instance.gameState == GameState.PLAYING && _netClient.IsConnected;
        });
        yield return new WaitForSecondsRealtime(4f);
        AudioListener.volume = 0f;
        Log($"in game: scene={CurrentScene} hosting={hostSlot != null && _role == "p1"}");

        PlayerData.instance.isInvincible = true;
        MonoBehaviourUtil.Instance.StartCoroutine(KeepAlive());

        var census = GetOption("-hkmp-test-dmg-census");
        if (census != null) {
            yield return Census(File.ReadAllLines(census).Where(l => l.Trim().Length > 0).ToList());
            Log($"SUMMARY census done");
            Signal($"finished-{_role}", "census");
            yield break;
        }

        Signal($"ready-{_role}");
        yield return WaitSignal($"ready-{Other}", 900f);

        // ---- (1) ability gates on join: nobody picked up any ability yet in this fresh world
        var pd = PlayerData.instance;
        Log($"abilities on join: {Abilities()}");
        Check("canDash only with hasDash", pd.canDash == pd.hasDash, Abilities());
        Check("no dash in a fresh world", !pd.canDash && !pd.hasDash, Abilities());

        // ---- (2) damage sweep
        foreach (var (scene, gate) in scenes) {
            yield return SweepScene(scene, gate);
        }

        // ---- (1b) somebody unlocks the dash: the other player gets hasDash and canDash live
        if (_role == "p1") {
            pd.SetBool("hasDash", true);
            pd.SetBool("canDash", true);
            Log("p1 picked up the Mothwing Cloak (hasDash, canDash)");
            Signal("dash-given");
        } else {
            yield return WaitSignal("dash-given", 900f);
            var w = 0f;
            yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 10f || pd.hasDash && pd.canDash);
            Check("dash unlocked by the other player arrives", pd.hasDash && pd.canDash, Abilities());
        }

        Log($"SUMMARY pass={_pass} fail={_fail} info={_info}");
        Signal($"finished-{_role}", $"{_pass} {_fail}");
    }

    /// <summary>
    /// Single instance census: visit every scene (hazard respawn entry, no gate needed) and list the health managers
    /// that are not networked: children of registered entities and objects that were not registered at all.
    /// </summary>
    private static IEnumerator Census(List<string> scenes) {
        foreach (var scene in scenes) {
            var w = 0f;
            yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 30f || !global::GameManager.instance.IsInSceneTransition);
            if (global::GameManager.instance.IsInSceneTransition) {
                Log($"CENSUS {scene}: still in transition, skipping");
                continue;
            }

            var pd = PlayerData.instance;
            pd.SetVector3(nameof(PlayerData.hazardRespawnLocation), new Vector3(15f, 15f, 0f));
            ReflectionHelper.SetField(global::GameManager.instance, "hazardRespawningHero", true);
            try {
                global::GameManager.instance.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
                    SceneName = scene,
                    EntryGateName = "",
                    EntryDelay = 0f,
                    PreventCameraFadeOut = true,
                    WaitForSceneTransitionCameraFade = false,
                    Visualization = global::GameManager.SceneLoadVisualizations.Default,
                    AlwaysUnloadUnusedAssets = false
                });
            } catch (Exception e) {
                Log($"CENSUS {scene}: transition failed {e.Message}");
                continue;
            }

            w = 0f;
            yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 40f || CurrentScene == scene);
            yield return new WaitForSecondsRealtime(4f);
            ReflectionHelper.SetField(global::GameManager.instance, "hazardRespawningHero", false);
            if (CurrentScene != scene) {
                Log($"CENSUS {scene}: not loaded (in {CurrentScene})");
                continue;
            }

            try {
                CensusScene(scene);
            } catch (Exception e) {
                Log($"CENSUS {scene}: dump failed {e}");
            }
        }
    }

    private static void CensusScene(string scene) {
        var entityHms = new HashSet<HealthManager>();
        var entityObjects = new Dictionary<GameObject, Entity>();
        foreach (var e in Em.Entities.Values) {
            var hc = e.HealthComponent;
            if (hc?.HostHealthManager != null) entityHms.Add(hc.HostHealthManager);
            if (hc?.ClientHealthManager != null) entityHms.Add(hc.ClientHealthManager);
            if (e.Object.Host != null) entityObjects[e.Object.Host] = e;
            if (e.Object.Client != null) entityObjects[e.Object.Client] = e;
        }

        var all = Object.FindObjectsOfType<HealthManager>(true)
            .Where(hm => hm.gameObject.scene.name != null && hm.gameObject.scene.name.StartsWith(scene))
            .ToList();
        var lines = new List<string>();
        foreach (var hm in all) {
            if (entityHms.Contains(hm)) {
                continue;
            }

            Entity owner = null;
            var t = hm.transform.parent;
            while (t != null && owner == null) {
                entityObjects.TryGetValue(t.gameObject, out owner);
                t = t.parent;
            }

            var kind = owner == null
                ? "UNREGISTERED"
                : $"CHILD-OF-ENTITY {owner.Id} {owner.Type} ({(owner.Object.Client != null && hm.transform.IsChildOf(owner.Object.Client.transform) ? "client copy" : "host")})";
            lines.Add($"{kind} '{GetPath(hm.transform)}' hp={hm.hp} active={hm.gameObject.activeInHierarchy}");
        }

        var registered = Em.Entities.Values.Count(e => e.HealthComponent != null);
        Log($"CENSUS {scene}: {all.Count} health managers, {registered} networked entities, {lines.Count} not networked");
        foreach (var l in lines) {
            Log($"CENSUS {scene}:   {l}");
        }
    }

    private static string Abilities() {
        var pd = PlayerData.instance;
        return $"hasDash={pd.hasDash} canDash={pd.canDash} hasWalljump={pd.hasWalljump} canWallJump={pd.canWallJump} " +
               $"hasShadowDash={pd.hasShadowDash} canShadowDash={pd.canShadowDash} hasSuperDash={pd.hasSuperDash} " +
               $"hasDoubleJump={pd.hasDoubleJump} hasAcidArmour={pd.hasAcidArmour} hasDreamNail={pd.hasDreamNail}";
    }

    private static IEnumerator HostGame(int slot) {
        yield return new WaitUntil(() =>
            global::GameManager.instance != null && global::GameManager.instance.gameState == GameState.MAIN_MENU &&
            UIManager.instance != null && UIManager.instance.menuState == MainMenuState.MAIN_MENU &&
            InputHandler.Instance != null && InputHandler.Instance.acceptingInput);
        yield return new WaitForSecondsRealtime(1f);
        yield return _uiManager.OpenMultiplayerMenu();
        yield return new WaitForSecondsRealtime(1f);

        var port = int.TryParse(GetOption("-hkmp-test-dmg-port"), out var p) ? p : 26970;
        var file = Path.Combine(Application.persistentDataPath, slot == 0 ? "user.dat" : $"user{slot}.dat");
        Log($"hosting slot {slot} on port {port} (new game: {!File.Exists(file)})");
        _uiManager.DevStartHost("P1", port, slot, File.Exists(file));
    }

    private static IEnumerator KeepAlive() {
        while (true) {
            try {
                var pd = PlayerData.instance;
                pd.isInvincible = true;
                if (pd.health < pd.maxHealth) {
                    pd.health = pd.maxHealth;
                }

                AudioListener.volume = 0f;
                var hc = HeroController.instance;
                if (hc != null && !hc.acceptingInput && !global::GameManager.instance.IsInSceneTransition &&
                    hc.transitionState == HeroTransitionState.WAITING_TO_TRANSITION) {
                    hc.AcceptInput();
                }
            } catch (Exception) {
                // ignored
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    private static string GetPath(Transform t) {
        var path = t.name;
        while (t.parent != null) {
            t = t.parent;
            path = t.name + "/" + path;
        }

        return path;
    }

    private static HealthManager ViewHm(Entity e) {
        var hc = e.HealthComponent;
        if (hc == null) {
            return null;
        }

        return Em.IsSceneHost ? hc.HostHealthManager : hc.ClientHealthManager;
    }

    private static bool Alive(HealthManager hm) =>
        hm != null && hm.gameObject.activeInHierarchy && !hm.GetIsDead() && hm.hp > 0;

    /// <summary>
    /// All health managers of the active scene(s) that are live on this instance but not the networked health
    /// manager of an entity (children of entities, enemies that were not registered, late spawns).
    /// </summary>
    private static List<HealthManager> Unmanaged() {
        var managed = new HashSet<HealthManager>();
        foreach (var e in Em.Entities.Values) {
            var hc = e.HealthComponent;
            if (hc?.HostHealthManager != null) managed.Add(hc.HostHealthManager);
            if (hc?.ClientHealthManager != null) managed.Add(hc.ClientHealthManager);
        }

        return Object.FindObjectsOfType<HealthManager>()
            .Where(hm => !managed.Contains(hm) && Alive(hm) && hm.gameObject.scene.name != "DontDestroyOnLoad")
            .OrderBy(hm => GetPath(hm.transform), StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteDump(string s) {
        var lines = new List<string>();
        foreach (var e in Em.Entities.Values.Where(e => e.HealthComponent != null).OrderBy(e => e.Id)) {
            var hm = ViewHm(e);
            var host = e.HealthComponent.HostHealthManager;
            lines.Add(string.Join("|", "E", e.Id, e.Type,
                host != null ? GetPath(host.transform) : "null",
                hm != null ? hm.hp : -1, Alive(hm) ? 1 : 0, hm != null && hm.IsInvincible ? 1 : 0));
        }

        foreach (var hm in Unmanaged()) {
            lines.Add(string.Join("|", "U", -1, "-", GetPath(hm.transform), hm.hp, 1, hm.IsInvincible ? 1 : 0));
        }

        File.WriteAllLines(Path.Combine(_dir, $"{s}-{_role}-dump"), lines);
        Log($"{s} dump: sceneHost={Em.IsSceneHost} entities={Em.Entities.Count} lines={lines.Count}");
        foreach (var l in lines) {
            Log($"  {l}");
        }
    }

    private static string SceneBase(HealthManager hm) => global::GameManager.GetBaseSceneName(hm.gameObject.scene.name);

    private static void HitLocally(HealthManager hm) {
        ReflectionHelper.SetField(hm, "evasionByHitRemaining", 0f);
        hm.Hit(new HitInstance {
            Source = HeroController.instance.gameObject,
            AttackType = AttackTypes.Nail,
            DamageDealt = Damage,
            Direction = 0f,
            MagnitudeMultiplier = 1f,
            Multiplier = 1f,
            IgnoreInvulnerable = true,
            SpecialType = SpecialTypes.None
        });
    }

    /// <summary>
    /// Mark the first N enemies of the room as killed in the local scene data only (scene host side), before the
    /// room is loaded.
    /// </summary>
    private static IEnumerator PreKill(string s, int count) {
        var file = Path.Combine(_dir, $"prekill-{s}");
        if (!File.Exists(file)) {
            yield break;
        }

        foreach (var line in File.ReadAllLines(file).Take(count)) {
            var p = line.Split('|');
            SceneData.instance.SaveMyState(new PersistentBoolData {
                id = p[0], sceneName = p[1], activated = true, semiPersistent = p[2] == "1"
            });
            Log($"{s} pre-kill (local scene data only): {line}");
        }
    }

    private static IEnumerator SweepScene(string s, string gate) {
        Log($"===== {s} =====");
        var preKill = int.TryParse(GetOption("-hkmp-test-dmg-prekill"), out var pk) ? pk : 0;

        if (IsH) {
            yield return WaitSignal($"{s}-C-out", 240f);
            if (preKill > 0) {
                yield return PreKill(s, preKill);
            }

            yield return GoTo(s, gate);
            var w = 0f;
            yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 30f || Em.IsSceneHostDetermined);
            Check($"{s} H is scene host", Em.IsSceneHost && CurrentScene == s, $"scene={CurrentScene} sceneHost={Em.IsSceneHost}");
            yield return new WaitForSecondsRealtime(3f);
            Signal($"{s}-H-in");
        } else {
            yield return GoTo("Town", "left1");
            Signal($"{s}-C-out");
            yield return WaitSignal($"{s}-H-in", 240f);
            yield return GoTo(s, gate);
            Check($"{s} C is scene client", !Em.IsSceneHost && CurrentScene == s, $"scene={CurrentScene} sceneHost={Em.IsSceneHost}");
            yield return new WaitForSecondsRealtime(3f);
            Signal($"{s}-C-in");
        }

        if (IsH) {
            yield return WaitSignal($"{s}-C-in", 240f);
        }

        yield return new WaitForSecondsRealtime(4f);
        if (CurrentScene != s) {
            Check($"{s} entered", false, $"in {CurrentScene}");
            Signal($"{s}-done-{_role}");
            yield return WaitSignal($"{s}-done-{Other}", 120f);
            yield break;
        }

        // Record the persistent ids of the enemies (for a pre-kill run)
        if (IsH && preKill == 0) {
            var lines = Em.Entities.Values
                .Where(e => e.HealthComponent?.HostHealthManager != null)
                .OrderBy(e => e.Id)
                .Select(e => e.HealthComponent.HostHealthManager)
                .Select(hm => (hm, pbi: hm.GetComponent<PersistentBoolItem>()))
                .Where(x => x.pbi != null)
                .Select(x => $"{HealthManagerComponent.GetPersistentId(x.pbi, x.hm.gameObject)}|" +
                             $"{SceneBase(x.hm)}|{(x.pbi.semiPersistent ? 1 : 0)}");
            File.WriteAllLines(Path.Combine(_dir, $"prekill-{s}"), lines);
        }

        WriteDump(s);
        Signal($"{s}-dump-{_role}");
        yield return WaitSignal($"{s}-dump-{Other}", 120f);

        var hostHp = new Dictionary<ushort, int>();
        var hostUnmanaged = new Dictionary<string, int>();
        if (IsH) {
            foreach (var e in Em.Entities.Values.Where(e => e.HealthComponent?.HostHealthManager != null)) {
                hostHp[e.Id] = e.HealthComponent.HostHealthManager.hp;
            }

            foreach (var hm in Unmanaged()) {
                hostUnmanaged[GetPath(hm.transform)] = hm.hp;
            }

            var appliedBefore = new Dictionary<ushort, (int Count, int Damage)>(HealthManagerComponent.AppliedByEntity);
            Signal($"{s}-H-ready");
            yield return WaitSignal($"{s}-C-hit", 120f);
            yield return new WaitForSecondsRealtime(3f);

            var other = File.ReadAllLines(Path.Combine(_dir, $"{s}-{Other}-dump"));
            var myDump = File.ReadAllLines(Path.Combine(_dir, $"{s}-{_role}-dump"));
            var hits = File.ReadAllLines(Path.Combine(_dir, $"{s}-hits"));
            foreach (var hit in hits) {
                var p = hit.Split('|');
                if (p[0] == "E") {
                    var id = ushort.Parse(p[1]);
                    var cPath = p[2];
                    HealthManagerComponent.AppliedByEntity.TryGetValue(id, out var appliedNow);
                    appliedBefore.TryGetValue(id, out var appliedThen);
                    if (!Em.Entities.TryGetValue(id, out var e) || e.HealthComponent?.HostHealthManager == null) {
                        // Killed by the hit and destroyed (e.g. Pigeon, 1 HP): the forwarded hit must have been applied
                        Check($"{s} C hit entity {id} '{cPath}'", appliedNow.Count > appliedThen.Count,
                            $"host object gone (killed and destroyed?); hits applied on host for this id: {appliedNow.Count - appliedThen.Count}");
                        continue;
                    }

                    var hm = e.HealthComponent.HostHealthManager;
                    var hPath = GetPath(hm.transform);
                    var before = hostHp.TryGetValue(id, out var b) ? b : -1;
                    var ok = hm.hp < before || hm.GetIsDead();
                    var mapping = hPath == cPath ? "same object" : $"DIFFERENT OBJECT on host: '{hPath}'";
                    Check($"{s} C hit entity {id} '{cPath}'", ok && hPath == cPath,
                        $"host hp {before} -> {hm.hp} dead={hm.GetIsDead()} active={hm.gameObject.activeInHierarchy}; {mapping}");
                } else {
                    var path = p[2];
                    var hostHm = Object.FindObjectsOfType<HealthManager>().FirstOrDefault(h => GetPath(h.transform) == path);
                    var before = hostUnmanaged.TryGetValue(path, out var b) ? b : (hostHm != null ? hostHm.hp : -1);
                    var registered = hostHm != null && Em.Entities.Values.Any(e =>
                        e.HealthComponent?.HostHealthManager == hostHm);
                    var ok = hostHm != null && (hostHm.hp < before || hostHm.GetIsDead());
                    Check($"{s} C hit unregistered '{path}'", ok,
                        $"host object {(hostHm == null ? "missing" : $"hp {before} -> {hostHm.hp} registered-on-host={registered}")}");
                }
            }

            // ID mapping between both instances
            var mine = myDump.Where(l => l.StartsWith("E|")).Select(l => l.Split('|')).ToDictionary(p => p[1], p => p[3]);
            var theirs = other.Where(l => l.StartsWith("E|")).Select(l => l.Split('|')).ToDictionary(p => p[1], p => p[3]);
            var mismatched = mine.Keys.Union(theirs.Keys)
                .Where(k => !mine.TryGetValue(k, out var a) || !theirs.TryGetValue(k, out var b2) || a != b2)
                .Select(k => $"{k}: H '{(mine.TryGetValue(k, out var a) ? a : "-")}' C '{(theirs.TryGetValue(k, out var b3) ? b3 : "-")}'")
                .ToList();
            Check($"{s} entity id mapping identical", mismatched.Count == 0,
                mismatched.Count == 0 ? $"{mine.Count} entities" : string.Join("; ", mismatched));
        } else {
            yield return WaitSignal($"{s}-H-ready", 120f);
            var hits = new List<string>();
            foreach (var e in Em.Entities.Values.Where(e => e.HealthComponent != null).OrderBy(e => e.Id).ToList()) {
                var hm = e.HealthComponent.ClientHealthManager;
                if (!Alive(hm) || hm.IsInvincible) {
                    continue;
                }

                var host = e.HealthComponent.HostHealthManager;
                var before = hm.hp;
                try {
                    HitLocally(hm);
                } catch (Exception ex) {
                    Log($"hit failed on {e.Id}: {ex.Message}");
                }

                hits.Add($"E|{e.Id}|{(host != null ? GetPath(host.transform) : "null")}");
                Log($"C hit entity {e.Id} ({e.Type}) local hp {before} -> {hm.hp}");
            }

            foreach (var hm in Unmanaged()) {
                if (hm.IsInvincible) {
                    continue;
                }

                var path = GetPath(hm.transform);
                var before = hm.hp;
                try {
                    HitLocally(hm);
                } catch (Exception ex) {
                    Log($"hit failed on '{path}': {ex.Message}");
                }

                hits.Add($"U|-1|{path}");
                Log($"C hit UNREGISTERED '{path}' local hp {before} -> {hm.hp}");
            }

            File.WriteAllLines(Path.Combine(_dir, $"{s}-hits"), hits);
            Note($"{s} C hits", $"{hits.Count} enemies hit");
            Signal($"{s}-C-hit");

            if (Damage >= 999) {
                // Kill run: the deaths decided by the scene host must come back to this client
                yield return new WaitForSecondsRealtime(5f);
                foreach (var line in hits.Where(h => h.StartsWith("E|"))) {
                    var id = ushort.Parse(line.Split('|')[1]);
                    if (!Em.Entities.TryGetValue(id, out var e)) {
                        continue;
                    }

                    var hm = e.HealthComponent?.ClientHealthManager;
                    var gone = hm == null || hm.GetIsDead() || !hm.gameObject.activeInHierarchy;
                    Check($"{s} C sees entity {id} '{line.Split('|')[2]}' killed", gone,
                        hm == null ? "client object gone" : $"client hp={hm.hp} dead={hm.GetIsDead()} active={hm.gameObject.activeInHierarchy} netDead={e.HealthComponent.IsDead}");
                }
            }
        }

        Signal($"{s}-mid-{_role}");
        yield return WaitSignal($"{s}-mid-{Other}", 120f);

        if (GetOption("-hkmp-test-dmg-late") != null) {
            yield return LatePhase(s);
        }

        Signal($"{s}-done-{_role}");
        yield return WaitSignal($"{s}-done-{Other}", 120f);
    }

    /// <summary>
    /// Wake the room up (both heroes next to the strongest enemy for a while, so fights start and spawners run),
    /// then the client hits every health manager it sees that is not networked (late spawns, children) and the
    /// scene host reports what happened to its own object with the same path.
    /// </summary>
    private static IEnumerator LatePhase(string s) {
        var strongest = Em.Entities.Values
            .Where(e => Alive(ViewHm(e)))
            .OrderByDescending(e => ViewHm(e).hp)
            .FirstOrDefault();
        var hero = HeroController.instance;
        if (strongest != null && hero != null) {
            var pos = ViewHm(strongest).transform.position;
            hero.transform.position = new Vector3(pos.x - 2f, pos.y + 1f, hero.transform.position.z);
            Log($"{s} late: hero moved next to entity {strongest.Id} '{strongest.Object.Host?.name}'");
        }

        yield return new WaitForSecondsRealtime(10f);

        if (IsH) {
            var hostBefore = Object.FindObjectsOfType<HealthManager>(true)
                .GroupBy(hm => GetPath(hm.transform))
                .ToDictionary(g => g.Key, g => g.First().hp);
            Signal($"{s}-H-late");
            yield return WaitSignal($"{s}-C-late", 120f);
            yield return new WaitForSecondsRealtime(3f);
            var lines = File.Exists(Path.Combine(_dir, $"{s}-late-hits"))
                ? File.ReadAllLines(Path.Combine(_dir, $"{s}-late-hits"))
                : Array.Empty<string>();
            foreach (var line in lines) {
                var p = line.Split('|');
                var path = p[0];
                var hostHm = Object.FindObjectsOfType<HealthManager>(true).FirstOrDefault(h => GetPath(h.transform) == path);
                var registered = hostHm != null && Em.Entities.Values.Any(e => e.HealthComponent?.HostHealthManager == hostHm);
                var before = hostBefore.TryGetValue(path, out var b) ? b : -1;
                Check($"{s} late: C hit a health manager that is not networked '{path}'", false,
                    $"C local hp {p[1]} -> {p[2]}; host object {(hostHm == null ? "MISSING (exists only on C)" : $"hp {before} -> {hostHm.hp} active={hostHm.gameObject.activeInHierarchy} dead={hostHm.GetIsDead()} registered-on-host={registered}")}");
            }

            Note($"{s} late", $"{lines.Length} non-networked health managers hit by C");
        } else {
            yield return WaitSignal($"{s}-H-late", 120f);
            var lines = new List<string>();
            foreach (var hm in Unmanaged()) {
                if (hm.IsInvincible) {
                    continue;
                }

                var before = hm.hp;
                try {
                    HitLocally(hm);
                } catch (Exception ex) {
                    Log($"late hit failed: {ex.Message}");
                }

                var path = GetPath(hm.transform);
                lines.Add($"{path}|{before}|{hm.hp}");
                Log($"{s} late: C hit NON-NETWORKED '{path}' ({hm.gameObject.scene.name}) local hp {before} -> {hm.hp}");
            }

            File.WriteAllLines(Path.Combine(_dir, $"{s}-late-hits"), lines);
            Signal($"{s}-C-late");
        }

        if (hero != null) {
            var w = 0f;
            while ((w += 0.5f) < 2f) {
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }
    }

    private static IEnumerator GoTo(string scene, string gate) {
        if (CurrentScene == scene) {
            yield break;
        }

        var w = 0f;
        yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 30f || !global::GameManager.instance.IsInSceneTransition);
        Log($"transition {CurrentScene} -> {scene}:{gate}");
        // Gate "*": enter like a teleport (hazard respawn entry at a fixed point), for rooms whose gates are unknown
        var hazard = gate == "*";
        if (hazard) {
            PlayerData.instance.SetVector3(nameof(PlayerData.hazardRespawnLocation), new Vector3(15f, 15f, 0f));
            ReflectionHelper.SetField(global::GameManager.instance, "hazardRespawningHero", true);
        }

        global::GameManager.instance.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = hazard ? "" : gate,
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = global::GameManager.SceneLoadVisualizations.Default,
            AlwaysUnloadUnusedAssets = false
        });

        if (hazard) {
            w = 0f;
            yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 60f || CurrentScene == scene);
            yield return new WaitForSecondsRealtime(1.5f);
            ReflectionHelper.SetField(global::GameManager.instance, "hazardRespawningHero", false);
            Game.Client.CustomHooks.InvokeAfterEnterSceneHeroTransformed();
            global::GameManager.instance.FadeSceneIn();
        }

        w = 0f;
        yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 60f ||
                                         CurrentScene == scene && HeroController.instance != null &&
                                         Em.IsSceneHostDetermined);
        w = 0f;
        yield return new WaitUntil(() => (w += Time.unscaledDeltaTime) > 20f || !global::GameManager.instance.IsInSceneTransition);
        var gates = Object.FindObjectsOfType<TransitionPoint>().Select(t => t.name);
        Log($"entered {CurrentScene} (sceneHost={Em.IsSceneHost}); gates: {string.Join(" ", gates)}");
    }

    // ------------------------------------------------------------------------------------------------------------

    private static void Log(string m) => Logger.Info($"HKMP_DMG [{Tag}] {m}");

    private static void Check(string name, bool ok, string detail) {
        if (ok) _pass++; else _fail++;
        Logger.Info($"HKMP_DMG {(ok ? "PASS" : "FAIL")} [{Tag}] {name}: {detail}");
    }

    private static void Note(string name, string detail) {
        _info++;
        Logger.Info($"HKMP_DMG INFO [{Tag}] {name}: {detail}");
    }

    private static void Signal(string key, string value = "1") {
        File.WriteAllText(Path.Combine(_dir, key), value);
    }

    private static IEnumerator WaitSignal(string key, float timeout) {
        _signalValue = null;
        var path = Path.Combine(_dir, key);
        var waited = 0f;
        while (!File.Exists(path)) {
            if (waited > timeout) {
                Log($"timeout waiting for {key}");
                yield break;
            }

            waited += 0.25f;
            yield return new WaitForSecondsRealtime(0.25f);
        }

        yield return new WaitForSecondsRealtime(0.1f);
        _signalValue = File.ReadAllText(path);
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
