using System;
using System.Collections.Generic;
using System.Reflection;
using Hkmp.Game.Settings;
using Hkmp.Util;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Makes enemies simulated by the scene host go after the nearest player (the host or a remote player) instead of
/// always the host's own knight.
/// <para>Only the scene host runs the real enemy AI, and that AI only knows the local knight:</para>
/// <list type="bullet">
/// <item>Most enemy FSMs use the PlayMaker <b>global</b> variable "Hero" (the knight) directly in their actions
/// (ChaseObject, FaceObject, DistanceFly, GetPosition, FireAtTarget, ...). A global is shared by every FSM, so it is
/// not changed: instead, in the actions of each locally simulated enemy, the fields that reference a global holding
/// the knight are swapped for a per-enemy proxy variable (<see cref="EnemyState.Proxies"/>), whose value is the
/// chosen target. Actions that act on the target object itself (send events / set FSM variables / call methods on
/// it, see <see cref="IsExcludedAction"/>) keep the real knight.</item>
/// <item>Other FSMs store the knight in a local FsmGameObject variable (<see cref="GetHero"/>,
/// <see cref="HutongGames.PlayMaker.Actions.FindGameObject"/> "Knight" / tag "Player"); those variables are pointed
/// at the chosen target.</item>
/// <item>Waking up often needs <see cref="LineOfSightDetector.CanSeeHero"/> (raycast to HeroController.instance);
/// for local enemies it is also true when a remote player in the scene is in line of sight. Alert ranges
/// (<see cref="AlertRange"/>) already react to remote player objects (layer 9).</item>
/// </list>
/// <para>The remote player is represented by its player object, whose position is the synchronised one. The target
/// is chosen per enemy by <see cref="EnemyTargetSelector"/> (nearest, with hysteresis). Damage and health sync are
/// untouched: the enemy moves on the host and its synchronised copy hits the remote player on their side.</para>
/// </summary>
internal class EnemyTargetManager {
    /// <summary>
    /// Seconds between target evaluations.
    /// </summary>
    private const float EvaluateInterval = 0.1f;

    /// <summary>
    /// Seconds between refreshes of the cached FSM list (including children) of an enemy.
    /// </summary>
    private const float FsmCacheRefreshInterval = 2f;

    /// <summary>
    /// Layer mask of the terrain, used by <see cref="LineOfSightDetector"/>.
    /// </summary>
    private const int TerrainMask = 256;

    /// <summary>
    /// Prefixes of action type names that act on the target object itself (its FSMs, components or state) rather
    /// than only reading its position. These keep referring to the real knight.
    /// </summary>
    private static readonly string[] ExcludedActionPrefixes = {
        "Set", "Send", "Call", "Invoke", "Activate", "Destroy", "Add", "GetFsm", "Broadcast"
    };

    /// <summary>
    /// The private "canSeeHero" field of <see cref="LineOfSightDetector"/>.
    /// </summary>
    private static readonly FieldInfo LosCanSeeHeroField =
        typeof(LineOfSightDetector).GetField("canSeeHero", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The private "alertRanges" field of <see cref="LineOfSightDetector"/>.
    /// </summary>
    private static readonly FieldInfo LosAlertRangesField =
        typeof(LineOfSightDetector).GetField("alertRanges", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Cache of the FsmGameObject / FsmOwnerDefault fields per action type.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> ActionFieldCache = new();

    /// <summary>
    /// Private members of <see cref="Walker"/> used to make it turn towards a remote target.
    /// </summary>
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo WalkerHeroField = typeof(Walker).GetField("hero", PrivateInstance);
    private static readonly FieldInfo WalkerPreventTurnToHeroField =
        typeof(Walker).GetField("preventTurningToFaceHero", PrivateInstance);
    private static readonly FieldInfo WalkerTurnCooldownField =
        typeof(Walker).GetField("turnCooldownRemaining", PrivateInstance);
    private static readonly FieldInfo WalkerFacingField = typeof(Walker).GetField("currentFacing", PrivateInstance);
    private static readonly FieldInfo WalkerLosField = typeof(Walker).GetField("lineOfSightDetector", PrivateInstance);
    private static readonly FieldInfo WalkerAlertRangeField = typeof(Walker).GetField("alertRange", PrivateInstance);
    private static readonly MethodInfo WalkerBeginTurningMethod =
        typeof(Walker).GetMethod("BeginTurning", PrivateInstance);

    /// <summary>
    /// The server settings (for <see cref="ServerSettings.EnemiesTargetNearestPlayer"/>).
    /// </summary>
    private readonly ServerSettings _serverSettings;

    /// <summary>
    /// Reference to the client player data dictionary (<see cref="ClientManager"/>).
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// Per-enemy state by entity ID.
    /// </summary>
    private readonly Dictionary<ushort, EnemyState> _enemies = new();

    /// <summary>
    /// Line of sight detectors of local enemies (for the LineOfSightDetector hook).
    /// </summary>
    private readonly HashSet<LineOfSightDetector> _localDetectors = new();

    /// <summary>
    /// Walkers of local enemies, with their enemy state (for the Walker hook).
    /// </summary>
    private readonly Dictionary<Walker, EnemyState> _localWalkers = new();

    /// <summary>
    /// All remote player objects seen so far. Used to recognise FSM variables that point to a player, also after
    /// that player left the scene (its object goes back to the pool).
    /// </summary>
    private readonly HashSet<GameObject> _knownRemoteObjects = new();

    /// <summary>
    /// Reused list of target candidates.
    /// </summary>
    private readonly List<EnemyTargetCandidate> _candidates = new();

    /// <summary>
    /// Reused map from candidate ID to its game object.
    /// </summary>
    private readonly Dictionary<int, GameObject> _candidateObjects = new();

    /// <summary>
    /// Global FsmGameObject variables whose value is the local knight (e.g. "Hero").
    /// </summary>
    private readonly List<FsmGameObject> _heroGlobals = new();

    /// <summary>
    /// The time of the next evaluation.
    /// </summary>
    private float _nextEvaluateTime;

    /// <summary>
    /// Whether some FSM variable or proxy may currently point to a remote player, so a restore pass is needed when
    /// the feature is turned off or the remote players leave.
    /// </summary>
    private bool _anyRedirected;

    /// <summary>
    /// Whether some enemy FSM of the scene got proxies, which then need to mirror their global on every evaluation.
    /// </summary>
    private bool _anyPatched;

    /// <summary>
    /// State of one enemy.
    /// </summary>
    private class EnemyState {
        public readonly EnemyTargetTracker Tracker = new();
        public bool IsEnemy;
        public float FsmCacheTime = float.MinValue;
        public PlayMakerFSM[] Fsms = Array.Empty<PlayMakerFSM>();
        public LineOfSightDetector[] Detectors = Array.Empty<LineOfSightDetector>();
        public Walker[] Walkers = Array.Empty<Walker>();
        public readonly HashSet<PlayMakerFSM> PatchedFsms = new();

        /// <summary>
        /// The current target if it is a remote player, otherwise null.
        /// </summary>
        public GameObject RemoteTarget;

        /// <summary>
        /// Proxy variables, by the global variable they replace.
        /// </summary>
        public readonly Dictionary<FsmGameObject, FsmGameObject> Proxies = new();
    }

    public EnemyTargetManager(ServerSettings serverSettings, Dictionary<ushort, ClientPlayerData> playerData) {
        _serverSettings = serverSettings;
        _playerData = playerData;
    }

    /// <summary>
    /// Register the hooks.
    /// </summary>
    public void RegisterHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        On.GetHero.OnEnter += OnGetHeroEnter;
        On.HutongGames.PlayMaker.Actions.FindGameObject.Find += OnFindGameObject;
        On.LineOfSightDetector.Update += OnLineOfSightUpdate;
        On.Walker.UpdateWalking += OnWalkerUpdateWalking;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
    }

    /// <summary>
    /// Deregister the hooks.
    /// </summary>
    public void DeregisterHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        On.GetHero.OnEnter -= OnGetHeroEnter;
        On.HutongGames.PlayMaker.Actions.FindGameObject.Find -= OnFindGameObject;
        On.LineOfSightDetector.Update -= OnLineOfSightUpdate;
        On.Walker.UpdateWalking -= OnWalkerUpdateWalking;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnSceneChanged;

        Clear();
    }

    private void Clear() {
        _enemies.Clear();
        _localDetectors.Clear();
        _localWalkers.Clear();
        _anyRedirected = false;
        _anyPatched = false;
    }

    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        Clear();
    }

    /// <summary>
    /// Whether targets should be redirected at all right now.
    /// </summary>
    private bool IsActive(out EntityManager entityManager) {
        entityManager = EntityManager.Instance;
        return _serverSettings.EnemiesTargetNearestPlayer &&
               entityManager != null &&
               entityManager.IsSceneHost &&
               HeroController.instance != null;
    }

    private void OnUpdate() {
        var time = Time.time;
        if (time < _nextEvaluateTime) {
            return;
        }

        _nextEvaluateTime = time + EvaluateInterval;

        var active = IsActive(out var entityManager);
        if (active) {
            BuildCandidates();
        }

        // Only the local player is left (or the feature is off / we are not the scene host): restore variables and
        // proxies that still point to a remote player once, then idle
        var onlyLocal = !active || _candidates.Count <= 1;
        if (onlyLocal && !_anyRedirected && !_anyPatched) {
            _localDetectors.Clear();
            _localWalkers.Clear();
            return;
        }

        if (entityManager == null || HeroController.instance == null) {
            return;
        }

        var hero = HeroController.instance.gameObject;
        if (!onlyLocal) {
            FindHeroGlobals(hero);
        }

        _localDetectors.Clear();
        _localWalkers.Clear();
        var redirected = false;

        foreach (var entity in entityManager.Entities.Values) {
            if (!IsLocalEnemy(entity, out var state)) {
                continue;
            }

            RefreshFsms(entity, state, time, !onlyLocal);

            var target = onlyLocal ? hero : UpdateTarget(entity, state, time);
            if (target == null) {
                continue;
            }

            ApplyTarget(state, target, hero);
            state.RemoteTarget = target == hero ? null : target;

            if (target != hero) {
                redirected = true;
            }
        }

        _anyRedirected = redirected;
    }

    /// <summary>
    /// Whether the entity is an enemy whose host object is simulated locally and active.
    /// </summary>
    private bool IsLocalEnemy(Entity entity, out EnemyState state) {
        state = null;
        if (entity == null || !entity.IsHostSimulated) {
            return false;
        }

        var host = entity.Object.Host;
        if (host == null || !host.activeInHierarchy) {
            return false;
        }

        if (!_enemies.TryGetValue(entity.Id, out state)) {
            state = new EnemyState {
                IsEnemy = host.GetComponent<HealthManager>() != null
            };
            _enemies[entity.Id] = state;
        }

        return state.IsEnemy;
    }

    /// <summary>
    /// Refresh the cached FSMs (and line of sight detectors) of the enemy, and swap the global hero references in
    /// their actions for the enemy's proxies.
    /// </summary>
    private void RefreshFsms(Entity entity, EnemyState state, float time, bool patch) {
        if (time - state.FsmCacheTime >= FsmCacheRefreshInterval) {
            state.FsmCacheTime = time;
            state.Fsms = entity.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true);
            state.Detectors = entity.Object.Host.GetComponentsInChildren<LineOfSightDetector>(true);
            state.Walkers = entity.Object.Host.GetComponentsInChildren<Walker>(true);
        }

        foreach (var walker in state.Walkers) {
            if (walker != null) {
                _localWalkers[walker] = state;
            }
        }

        foreach (var detector in state.Detectors) {
            if (detector != null) {
                _localDetectors.Add(detector);
            }
        }

        if (!patch || _heroGlobals.Count == 0) {
            return;
        }

        foreach (var fsm in state.Fsms) {
            if (fsm == null || state.PatchedFsms.Contains(fsm)) {
                continue;
            }

            state.PatchedFsms.Add(fsm);
            var swapped = PatchFsm(fsm, state);
            if (swapped > 0) {
                _anyPatched = true;
                Logger.Info(
                    $"HKMP_TARGET entity ({entity.Id}, {entity.Type}) '{entity.Object.Host.name}' FSM '{fsm.FsmName}': {swapped} hero references now follow the chosen target");
            }
        }
    }

    /// <summary>
    /// Swap the fields of the FSM's actions that reference a hero global for the enemy's proxy of that global.
    /// </summary>
    /// <returns>The number of swapped fields.</returns>
    private int PatchFsm(PlayMakerFSM fsm, EnemyState state) {
        var swapped = 0;

        foreach (var fsmState in fsm.FsmStates) {
            FsmStateAction[] actions;
            try {
                actions = fsmState.Actions;
            } catch (Exception e) {
                Logger.Debug($"Could not get actions of state '{fsmState.Name}': {e.Message}");
                continue;
            }

            foreach (var action in actions) {
                if (action == null || IsExcludedAction(action)) {
                    continue;
                }

                foreach (var field in GetTargetFields(action.GetType())) {
                    var value = field.GetValue(action);
                    if (value is FsmGameObject fsmGameObject) {
                        var proxy = GetProxy(state, fsmGameObject);
                        if (proxy != null) {
                            field.SetValue(action, proxy);
                            swapped++;
                        }
                    } else if (value is FsmOwnerDefault ownerDefault &&
                               ownerDefault.OwnerOption == OwnerDefaultOption.SpecifyGameObject) {
                        var proxy = GetProxy(state, ownerDefault.GameObject);
                        if (proxy != null) {
                            ownerDefault.GameObject = proxy;
                            swapped++;
                        }
                    }
                }
            }
        }

        return swapped;
    }

    /// <summary>
    /// Whether the action acts on the target object itself, so it must keep the real knight.
    /// </summary>
    internal static bool IsExcludedAction(FsmStateAction action) {
        var name = action.GetType().Name;
        foreach (var prefix in ExcludedActionPrefixes) {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The instance fields of type FsmGameObject or FsmOwnerDefault of the given action type (cached).
    /// </summary>
    private static FieldInfo[] GetTargetFields(Type type) {
        if (ActionFieldCache.TryGetValue(type, out var fields)) {
            return fields;
        }

        var list = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(FsmStateAction); t = t.BaseType) {
            foreach (var field in t.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly)) {
                if (field.FieldType == typeof(FsmGameObject) || field.FieldType == typeof(FsmOwnerDefault)) {
                    list.Add(field);
                }
            }
        }

        fields = list.ToArray();
        ActionFieldCache[type] = fields;
        return fields;
    }

    /// <summary>
    /// Get (or create) the proxy of the enemy for the given variable if it is a hero global, otherwise null.
    /// </summary>
    private FsmGameObject GetProxy(EnemyState state, FsmGameObject variable) {
        if (variable == null || !ContainsReference(_heroGlobals, variable)) {
            return null;
        }

        if (!state.Proxies.TryGetValue(variable, out var proxy)) {
            proxy = new FsmGameObject(variable);
            state.Proxies[variable] = proxy;
        }

        return proxy;
    }

    private static bool ContainsReference(List<FsmGameObject> list, FsmGameObject variable) {
        foreach (var item in list) {
            if (ReferenceEquals(item, variable)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Find the global FsmGameObject variables that hold the local knight.
    /// </summary>
    private void FindHeroGlobals(GameObject hero) {
        var globals = FsmVariables.GlobalVariables?.GameObjectVariables;
        if (globals == null) {
            return;
        }

        foreach (var variable in globals) {
            if (variable != null && variable.Value == hero && !ContainsReference(_heroGlobals, variable)) {
                _heroGlobals.Add(variable);
                Logger.Info($"HKMP_TARGET global FSM variable '{variable.Name}' holds the knight");
            }
        }
    }

    private bool IsGlobal(FsmGameObject variable) {
        var globals = FsmVariables.GlobalVariables?.GameObjectVariables;
        if (globals == null || variable == null) {
            return false;
        }

        foreach (var global in globals) {
            if (ReferenceEquals(global, variable)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fill <see cref="_candidates"/> and <see cref="_candidateObjects"/> with the local player and the remote players
    /// in the local scene.
    /// </summary>
    private void BuildCandidates() {
        _candidates.Clear();
        _candidateObjects.Clear();

        var hero = HeroController.instance;
        if (hero != null) {
            var heroPos = hero.transform.position;
            _candidates.Add(new EnemyTargetCandidate(EnemyTargetSelector.LocalPlayerId, heroPos.x, heroPos.y));
            _candidateObjects[EnemyTargetSelector.LocalPlayerId] = hero.gameObject;
        }

        foreach (var playerData in _playerData.Values) {
            var playerObject = playerData.PlayerObject;
            if (playerObject == null) {
                continue;
            }

            _knownRemoteObjects.Add(playerObject);

            if (!playerData.IsInLocalScene || !playerObject.activeInHierarchy) {
                continue;
            }

            var pos = playerObject.transform.position;
            _candidates.Add(new EnemyTargetCandidate(playerData.Id, pos.x, pos.y));
            _candidateObjects[playerData.Id] = playerObject;
        }
    }

    /// <summary>
    /// Re-evaluate the target of the given enemy and return its game object.
    /// </summary>
    private GameObject UpdateTarget(Entity entity, EnemyState state, float time) {
        var tracker = state.Tracker;
        var position = entity.Object.Host.transform.position;
        var previous = tracker.CurrentId;
        if (tracker.Update(time, position.x, position.y, _candidates)) {
            Logger.Info(
                $"HKMP_TARGET entity ({entity.Id}, {entity.Type}) '{entity.Object.Host.name}' target {DescribeTarget(previous)} -> {DescribeTarget(tracker.CurrentId)} at ({position.x:0.0}, {position.y:0.0})");
        }

        return _candidateObjects.TryGetValue(tracker.CurrentId, out var target) ? target : null;
    }

    private string DescribeTarget(int id) {
        if (id == EnemyTargetSelector.NoTarget) {
            return "none";
        }

        if (id == EnemyTargetSelector.LocalPlayerId) {
            return "host (local)";
        }

        return _playerData.TryGetValue((ushort) id, out var playerData)
            ? $"remote {id} '{playerData.Username}'"
            : $"remote {id}";
    }

    /// <summary>
    /// Whether the given object is a player object: the local knight or a (remote) player object.
    /// </summary>
    private bool IsPlayerObject(GameObject obj, GameObject hero) {
        if (obj == null) {
            return false;
        }

        return obj == hero || _knownRemoteObjects.Contains(obj);
    }

    /// <summary>
    /// Point the enemy's proxies and every local FsmGameObject variable of its FSMs that refers to a player at the
    /// target. When the target is the local knight, the proxies mirror their global again.
    /// </summary>
    private void ApplyTarget(EnemyState state, GameObject target, GameObject hero) {
        foreach (var pair in state.Proxies) {
            var value = target == hero ? pair.Key.Value : target;
            if (pair.Value.Value != value) {
                pair.Value.Value = value;
            }
        }

        foreach (var fsm in state.Fsms) {
            if (fsm == null) {
                continue;
            }

            var variables = fsm.FsmVariables.GameObjectVariables;
            for (var i = 0; i < variables.Length; i++) {
                var variable = variables[i];
                var value = variable.Value;
                if (value == null || value == target || !IsPlayerObject(value, hero)) {
                    continue;
                }

                variable.Value = target;
            }
        }
    }

    /// <summary>
    /// Find the locally simulated enemy entity that owns the given FSM game object (itself or one of its parents).
    /// </summary>
    private Entity FindLocalEnemy(GameObject fsmObject, out EnemyState state) {
        state = null;
        var entityManager = EntityManager.Instance;
        if (entityManager == null || fsmObject == null) {
            return null;
        }

        for (var transform = fsmObject.transform; transform != null; transform = transform.parent) {
            var obj = transform.gameObject;
            foreach (var entity in entityManager.Entities.Values) {
                if (entity.Object.Host == obj) {
                    return IsLocalEnemy(entity, out state) ? entity : null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Get the target for a player reference just stored by an action of an enemy FSM into the given variable, or
    /// null to leave it.
    /// </summary>
    private GameObject GetTargetForAction(GameObject fsmObject, FsmGameObject variable) {
        var stored = variable?.Value;
        if (stored == null || IsGlobal(variable) || !IsActive(out _)) {
            return null;
        }

        var hero = HeroController.instance.gameObject;
        if (!IsPlayerObject(stored, hero)) {
            return null;
        }

        var entity = FindLocalEnemy(fsmObject, out var state);
        if (entity == null) {
            return null;
        }

        BuildCandidates();
        if (_candidates.Count <= 1) {
            return null;
        }

        var target = UpdateTarget(entity, state, Time.time);
        if (target != hero) {
            _anyRedirected = true;
        }

        return target;
    }

    private void OnGetHeroEnter(On.GetHero.orig_OnEnter orig, GetHero self) {
        orig(self);

        var target = GetTargetForAction(self.Fsm?.GameObject, self.storeResult);
        if (target != null && target != self.storeResult.Value) {
            self.storeResult.Value = target;
        }
    }

    private void OnFindGameObject(
        On.HutongGames.PlayMaker.Actions.FindGameObject.orig_Find orig,
        HutongGames.PlayMaker.Actions.FindGameObject self
    ) {
        orig(self);

        var target = GetTargetForAction(self.Fsm?.GameObject, self.store);
        if (target != null && target != self.store.Value) {
            self.store.Value = target;
        }
    }

    /// <summary>
    /// After the vanilla line of sight check (to the local knight only), a local enemy also sees a remote player of
    /// the scene that is in line of sight, under the same alert range condition.
    /// </summary>
    private void OnLineOfSightUpdate(On.LineOfSightDetector.orig_Update orig, LineOfSightDetector self) {
        orig(self);

        if (self.CanSeeHero || _candidates.Count <= 1 || !_localDetectors.Contains(self) ||
            LosCanSeeHeroField == null || !IsActive(out _)) {
            return;
        }

        if (LosAlertRangesField?.GetValue(self) is AlertRange[] alertRanges && alertRanges.Length > 0) {
            var inRange = false;
            foreach (var alertRange in alertRanges) {
                if (alertRange != null && alertRange.IsHeroInRange) {
                    inRange = true;
                    break;
                }
            }

            if (!inRange) {
                return;
            }
        }

        Vector2 origin = self.transform.position;
        foreach (var candidate in _candidates) {
            if (candidate.Id == EnemyTargetSelector.LocalPlayerId) {
                continue;
            }

            var direction = new Vector2(candidate.X, candidate.Y) - origin;
            if (!Physics2D.Raycast(origin, direction.normalized, direction.magnitude, TerrainMask)) {
                LosCanSeeHeroField.SetValue(self, true);
                return;
            }
        }
    }

    /// <summary>
    /// A walking enemy (<see cref="Walker"/>) turns to face the knight when it sees it in its alert range. When the
    /// enemy's target is a remote player, it turns to face that player instead, and the vanilla check against the
    /// knight is disabled for that update (its line of sight may be true because of the remote player).
    /// </summary>
    private void OnWalkerUpdateWalking(On.Walker.orig_UpdateWalking orig, Walker self) {
        if (!_localWalkers.TryGetValue(self, out var state) || state.RemoteTarget == null ||
            !state.RemoteTarget.activeInHierarchy || WalkerHeroField == null || !IsActive(out _)) {
            orig(self);
            return;
        }

        try {
            if (TryTurnTowards(self, state.RemoteTarget)) {
                return;
            }
        } catch (Exception e) {
            Logger.Debug($"Walker turn towards remote target failed: {e.Message}");
        }

        var hero = WalkerHeroField.GetValue(self);
        WalkerHeroField.SetValue(self, null);
        try {
            orig(self);
        } finally {
            WalkerHeroField.SetValue(self, hero);
        }
    }

    /// <summary>
    /// Same condition as the vanilla "turn to face hero" check of <see cref="Walker"/>, against the given target.
    /// </summary>
    /// <returns>True if the walker started turning.</returns>
    private static bool TryTurnTowards(Walker walker, GameObject target) {
        if (WalkerPreventTurnToHeroField == null || WalkerTurnCooldownField == null || WalkerFacingField == null ||
            WalkerLosField == null || WalkerAlertRangeField == null || WalkerBeginTurningMethod == null) {
            return false;
        }

        if ((bool) WalkerPreventTurnToHeroField.GetValue(walker) || (float) WalkerTurnCooldownField.GetValue(walker) > 0f) {
            return false;
        }

        var facing = (int) WalkerFacingField.GetValue(walker);
        var targetIsRight = target.transform.position.x > walker.transform.position.x;
        if (targetIsRight == facing > 0) {
            return false;
        }

        if (WalkerLosField.GetValue(walker) is not LineOfSightDetector los || !los.CanSeeHero ||
            WalkerAlertRangeField.GetValue(walker) is not AlertRange alertRange || !alertRange.IsHeroInRange) {
            return false;
        }

        WalkerBeginTurningMethod.Invoke(walker, new object[] { -facing });
        return true;
    }
}
