using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Settings;
using Hkmp.Util;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client;

/// <summary>
/// Makes arenas with battle gates (wave rooms and traps, boss arenas) wait until every player in the scene is inside
/// before they start and close their gates.
/// <para>How vanilla detects the entry: the arena FSM (e.g. "Battle Scene" / "Battle Control") waits in a state like
/// "Detect" with a <see cref="Trigger2dEvent"/> on the arena collider (filtered on the "Player" tag, so only the
/// local knight). The event (START) moves it to a state like "Wave 1" that sends "BG CLOSE" to the gates and spawns
/// the first wave. When the arena is over the FSM sends "BG OPEN" itself, which is left untouched.</para>
/// <para>This class hooks the trigger action: if the action leads (within a few transitions) to a state that sends
/// "BG CLOSE" and some player of the scene is not inside the arena collider yet (remote players by their synchronised
/// position), the event is held back. Enter triggers are kept pending and fired once everyone is inside, as long as
/// the FSM is still in the same state and the triggering collider is still in the arena. The check runs on every
/// client: the gates are entities driven by the scene host, and the non-entity arena FSM of each client starts with
/// the same condition.</para>
/// </summary>
internal class ArenaEntryGate {
    /// <summary>
    /// Distance (in units) from the arena collider within which a remote player counts as inside. The synchronised
    /// position is the center of the knight, while the trigger reacts to the edge of its collider.
    /// </summary>
    private const float RemoteInsideTolerance = 0.75f;

    /// <summary>
    /// The server settings (for <see cref="ServerSettings.ArenaWaitForAllPlayers"/>).
    /// </summary>
    private readonly ServerSettings _serverSettings;

    /// <summary>
    /// Reference to the client player data dictionary (<see cref="ClientManager"/>).
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// Cache of whether a trigger action starts an arena (leads to closing the gates).
    /// </summary>
    private readonly Dictionary<Trigger2dEvent, bool> _arenaTriggerCache = new();

    /// <summary>
    /// Enter and stay triggers that were held back, with the collider that triggered them.
    /// </summary>
    private readonly Dictionary<Trigger2dEvent, Collider2D> _pendingEnters = new();

    /// <summary>
    /// Cache of the event-like fields (FsmString, FsmEvent, string) of FSM action types.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> EventFieldCache = new();

    /// <summary>
    /// Whether we are firing a held trigger ourselves, so the hook lets it through.
    /// </summary>
    private bool _bypass;

    public ArenaEntryGate(ServerSettings serverSettings, Dictionary<ushort, ClientPlayerData> playerData) {
        _serverSettings = serverSettings;
        _playerData = playerData;
    }

    /// <summary>
    /// Register the hooks.
    /// </summary>
    public void RegisterHooks() {
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerEnter2D += OnDoTriggerEnter2D;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerStay2D += OnDoTriggerStay2D;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
    }

    /// <summary>
    /// Deregister the hooks.
    /// </summary>
    public void DeregisterHooks() {
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerEnter2D -= OnDoTriggerEnter2D;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerStay2D -= OnDoTriggerStay2D;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnSceneChanged;

        Clear();
    }

    /// <summary>
    /// Clear all cached and pending state.
    /// </summary>
    private void Clear() {
        _arenaTriggerCache.Clear();
        _pendingEnters.Clear();
    }

    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        Clear();
    }

    private void OnDoTriggerEnter2D(
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.orig_DoTriggerEnter2D orig,
        Trigger2dEvent self,
        Collider2D other
    ) {
        if (_bypass || !ShouldHold(self, other, PlayMakerUnity2d.Trigger2DType.OnTriggerEnter2D)) {
            orig(self, other);
            return;
        }

        if (!_pendingEnters.ContainsKey(self)) {
            Logger.Info(
                $"HKMP_ARENA holding arena start '{DescribeAction(self)}' until all players in the scene are inside");
        }

        _pendingEnters[self] = other;
    }

    private void OnDoTriggerStay2D(
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.orig_DoTriggerStay2D orig,
        Trigger2dEvent self,
        Collider2D other
    ) {
        if (_bypass || !ShouldHold(self, other, PlayMakerUnity2d.Trigger2DType.OnTriggerStay2D)) {
            // A held stay trigger can also be released by the physics callback itself
            if (!_bypass && _pendingEnters.Remove(self)) {
                Logger.Info($"HKMP_ARENA all players inside, starting arena '{DescribeAction(self)}'");
            }

            orig(self, other);
            return;
        }

        // Also keep stay triggers pending: the physics callback is not called while the bodies sleep
        if (!_pendingEnters.ContainsKey(self)) {
            Logger.Info(
                $"HKMP_ARENA holding arena start '{DescribeAction(self)}' until all players in the scene are inside");
        }

        _pendingEnters[self] = other;
    }

    /// <summary>
    /// Fire the held triggers once all players are inside, or drop them if they no longer apply.
    /// </summary>
    private void OnUpdate() {
        if (_pendingEnters.Count == 0) {
            return;
        }

        foreach (var pair in _pendingEnters.ToList()) {
            var action = pair.Key;
            var other = pair.Value;

            Collider2D[] colliders;
            try {
                if (!IsStillWaiting(action, other, out colliders)) {
                    _pendingEnters.Remove(action);
                    Logger.Info($"HKMP_ARENA dropped held arena start '{DescribeAction(action)}' (left the arena or state changed)");
                    continue;
                }
            } catch (Exception e) {
                _pendingEnters.Remove(action);
                Logger.Warn($"HKMP_ARENA could not check held arena start: {e.Message}");
                continue;
            }

            if (_serverSettings.ArenaWaitForAllPlayers &&
                ArenaEntryRules.ShouldHold(true, GetPlayersInside(colliders, other))) {
                continue;
            }

            _pendingEnters.Remove(action);
            Logger.Info($"HKMP_ARENA all players inside, starting arena '{DescribeAction(action)}'");

            _bypass = true;
            try {
                if (action.trigger == PlayMakerUnity2d.Trigger2DType.OnTriggerStay2D) {
                    action.DoTriggerStay2D(other);
                } else {
                    action.DoTriggerEnter2D(other);
                }
            } catch (Exception e) {
                Logger.Error($"HKMP_ARENA exception while starting held arena:\n{e}");
            } finally {
                _bypass = false;
            }
        }
    }

    /// <summary>
    /// Whether a held enter trigger still applies: its FSM is still in the trigger state and the collider that
    /// entered is still inside the arena.
    /// </summary>
    private static bool IsStillWaiting(Trigger2dEvent action, Collider2D other, out Collider2D[] colliders) {
        colliders = null;

        if (action?.Fsm == null || action.State == null || action.Owner == null || !action.Owner.activeInHierarchy) {
            return false;
        }

        if (action.Fsm.ActiveState != action.State) {
            return false;
        }

        if (other == null || !other.gameObject.activeInHierarchy) {
            return false;
        }

        colliders = GetArenaColliders(action.Owner);
        return colliders.Length > 0 && IsColliderInside(colliders, other);
    }

    /// <summary>
    /// Whether the given trigger event should be held back.
    /// </summary>
    private bool ShouldHold(Trigger2dEvent action, Collider2D other, PlayMakerUnity2d.Trigger2DType type) {
        if (!_serverSettings.ArenaWaitForAllPlayers || action == null || other == null || action.trigger != type) {
            return false;
        }

        // Alone in the scene: vanilla behaviour
        if (!_playerData.Values.Any(IsActiveInScene)) {
            return false;
        }

        try {
            if (!TagMatches(action, other) || !IsArenaTrigger(action)) {
                return false;
            }

            var colliders = GetArenaColliders(action.Owner);
            if (colliders.Length == 0) {
                return false;
            }

            return ArenaEntryRules.ShouldHold(true, GetPlayersInside(colliders, other));
        } catch (Exception e) {
            Logger.Warn($"HKMP_ARENA could not check arena trigger, not holding it: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// For each player in the scene (the local player first), whether it is inside the arena colliders.
    /// </summary>
    private IEnumerable<bool> GetPlayersInside(Collider2D[] colliders, Collider2D triggeringCollider) {
        var hero = HeroController.instance;
        if (hero != null) {
            if (triggeringCollider != null && triggeringCollider.gameObject == hero.gameObject) {
                yield return IsColliderInside(colliders, triggeringCollider);
            } else {
                var heroCollider = hero.GetComponent<Collider2D>();
                yield return heroCollider != null
                    ? IsColliderInside(colliders, heroCollider)
                    : IsPointInside(colliders, hero.transform.position, 0f);
            }
        }

        foreach (var playerData in _playerData.Values) {
            if (!IsActiveInScene(playerData)) {
                continue;
            }

            yield return IsPointInside(colliders, playerData.PlayerObject.transform.position, RemoteInsideTolerance);
        }
    }

    /// <summary>
    /// Whether the given remote player is in the local scene with a spawned player object.
    /// </summary>
    private static bool IsActiveInScene(ClientPlayerData playerData) {
        return playerData.IsInLocalScene &&
               playerData.PlayerObject != null &&
               playerData.PlayerObject.activeInHierarchy;
    }

    /// <summary>
    /// Whether the trigger action would fire for the given collider (same tag filter as the action itself).
    /// </summary>
    private static bool TagMatches(Trigger2dEvent action, Collider2D other) {
        var tag = action.collideTag;
        if (tag == null || tag.IsNone || string.IsNullOrEmpty(tag.Value) || tag.Value == "Untagged") {
            return true;
        }

        return other.gameObject.tag == tag.Value;
    }

    /// <summary>
    /// Whether the trigger action starts an arena: its event leads to a state that closes the battle gates.
    /// </summary>
    private bool IsArenaTrigger(Trigger2dEvent action) {
        if (_arenaTriggerCache.TryGetValue(action, out var cached)) {
            return cached;
        }

        var result = false;

        var fsm = action.Fsm;
        var state = action.State;
        var eventName = action.sendEvent?.Name;
        if (fsm != null && state != null && !string.IsNullOrEmpty(eventName)) {
            var target = FindTransitionTarget(fsm, state, eventName);

            result = ArenaEntryRules.LeadsToGateClose(
                target,
                stateName => fsm.GetState(stateName)?.Transitions?.Select(t => t.ToState) ?? Enumerable.Empty<string>(),
                stateName => StateClosesGates(fsm.GetState(stateName))
            );
        }

        _arenaTriggerCache[action] = result;

        if (result) {
            Logger.Info($"HKMP_ARENA found arena trigger '{DescribeAction(action)}'");
        }

        return result;
    }

    /// <summary>
    /// Find the state the given event transitions to from the given state (or through a global transition).
    /// </summary>
    private static string FindTransitionTarget(HutongGames.PlayMaker.Fsm fsm, FsmState state, string eventName) {
        foreach (var transition in state.Transitions ?? Array.Empty<FsmTransition>()) {
            if (transition.EventName == eventName) {
                return transition.ToState;
            }
        }

        foreach (var transition in fsm.GlobalTransitions ?? Array.Empty<FsmTransition>()) {
            if (transition.EventName == eventName) {
                return transition.ToState;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether one of the actions of the given state sends the "BG CLOSE" event (SendEventByName, SendEvent,
    /// SendEventToRegister, broadcasts, ...).
    /// </summary>
    private static bool StateClosesGates(FsmState state) {
        if (state?.Actions == null) {
            return false;
        }

        foreach (var action in state.Actions) {
            if (action == null) {
                continue;
            }

            foreach (var field in GetEventFields(action.GetType())) {
                if (IsGateCloseValue(field.GetValue(action))) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the given field value is (or contains) the "BG CLOSE" event or event name.
    /// </summary>
    private static bool IsGateCloseValue(object value) {
        switch (value) {
            case null:
                return false;
            case string s:
                return s == ArenaEntryRules.GateCloseEvent;
            case FsmString fsmString:
                return fsmString.Value == ArenaEntryRules.GateCloseEvent;
            case FsmEvent fsmEvent:
                return fsmEvent.Name == ArenaEntryRules.GateCloseEvent;
            case Array array:
                foreach (var element in array) {
                    if (IsGateCloseValue(element)) {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Get the public instance fields of an action type that can hold an event name.
    /// </summary>
    private static FieldInfo[] GetEventFields(Type type) {
        if (EventFieldCache.TryGetValue(type, out var fields)) {
            return fields;
        }

        fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(string) ||
                        f.FieldType == typeof(FsmString) ||
                        f.FieldType == typeof(FsmEvent) ||
                        f.FieldType == typeof(FsmString[]) ||
                        f.FieldType == typeof(FsmEvent[]) ||
                        f.FieldType == typeof(string[]))
            .ToArray();
        EventFieldCache[type] = fields;

        return fields;
    }

    /// <summary>
    /// The enabled trigger colliders of the arena object (any enabled collider if there is no trigger).
    /// </summary>
    private static Collider2D[] GetArenaColliders(GameObject owner) {
        if (owner == null) {
            return Array.Empty<Collider2D>();
        }

        var colliders = owner.GetComponents<Collider2D>().Where(c => c != null && c.enabled).ToArray();
        var triggers = colliders.Where(c => c.isTrigger).ToArray();

        return triggers.Length > 0 ? triggers : colliders;
    }

    /// <summary>
    /// Whether the given collider overlaps one of the arena colliders.
    /// </summary>
    private static bool IsColliderInside(Collider2D[] colliders, Collider2D other) {
        foreach (var collider in colliders) {
            var distance = collider.Distance(other);
            if (distance.isValid) {
                if (distance.distance <= 0f) {
                    return true;
                }
            } else if (IsPointInside(new[] { collider }, other.bounds.center, 0f)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the given point is inside (or within the tolerance of) one of the arena colliders.
    /// </summary>
    private static bool IsPointInside(Collider2D[] colliders, Vector3 position, float tolerance) {
        var point = new Vector2(position.x, position.y);
        foreach (var collider in colliders) {
            if (collider.OverlapPoint(point)) {
                return true;
            }

            if (tolerance > 0f && Vector2.Distance(collider.ClosestPoint(point), point) <= tolerance) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Short description of a trigger action for logging.
    /// </summary>
    private static string DescribeAction(Trigger2dEvent action) {
        var owner = action?.Owner != null ? action.Owner.name : "null";
        var fsm = action?.Fsm != null ? action.Fsm.Name : "null";
        var state = action?.State != null ? action.State.Name : "null";
        return $"{owner}/{fsm}/{state}";
    }
}
