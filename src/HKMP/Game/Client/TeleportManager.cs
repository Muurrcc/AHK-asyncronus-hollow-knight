using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using Hkmp.Game.Settings;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Ui;
using Hkmp.Util;
using Modding;
using Modding.Utils;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client;

/// <summary>
/// Client side of teleporting to another player: sends the request to the server and, when the server answers with
/// the scene and position of the target, moves the local hero there (changing scene first if needed).
/// <para>A scene change uses the game's own hazard respawn entry: <c>GameManager.hazardRespawningHero</c> plus
/// <c>PlayerData.hazardRespawnLocation</c> make the game place the hero on the ground at the target position, fade
/// the scene in and give control back (<c>HeroController.HazardRespawn</c>), without needing a transition gate of
/// the target scene. When the new scene is set up the landing spot is checked: if the target was in the air above
/// spikes, acid or a bottomless pit, the closest safe hazard respawn marker of the scene is used instead (otherwise
/// the hero would land in the hazard and respawn there again and again). The game is always unpaused first and, if
/// anything does not end in a playable state, the full control of the hero is restored (<see cref="Recover"/>), never
/// in the middle of a scene load.</para>
/// </summary>
internal class TeleportManager {
    /// <summary>
    /// Seconds to wait for a state in which teleporting is possible (e.g. end of an attack or a dive).
    /// </summary>
    private const float WaitForStateTimeout = 3f;

    /// <summary>
    /// Seconds to wait for the target scene to load (slow disks can take long).
    /// </summary>
    private const float SceneLoadTimeout = 90f;

    /// <summary>
    /// Seconds to wait for the hero to have control again after arriving.
    /// </summary>
    private const float ControlTimeout = 6f;

    /// <summary>
    /// Hard limit for a whole teleport, after which it is aborted and control restored.
    /// </summary>
    private const float WatchdogTimeout = 120f;

    /// <summary>
    /// Seconds a deferred recovery waits for a running scene load to end before restoring anyway.
    /// </summary>
    private const float RecoverTransitionTimeout = 120f;

    /// <summary>
    /// Distance of the ground search below a landing spot (HeroController.FIND_GROUND_POINT_DISTANCE_EXT, used by
    /// the hazard respawn).
    /// </summary>
    private const float GroundSearchDistance = 50f;

    /// <summary>
    /// Layer mask of the terrain (layer 8), as used by HeroController.FindGroundPoint.
    /// </summary>
    private const int TerrainMask = 256;

    /// <summary>
    /// Private field of GameManager that makes EnterHero do a hazard respawn.
    /// </summary>
    private const string HazardRespawningField = "hazardRespawningHero";

    private readonly NetClient _netClient;
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;
    private readonly ServerSettings _serverSettings;

    private ushort? _lastPickedId;

    /// <summary>
    /// Whether a teleport is running.
    /// </summary>
    private bool _busy;

    /// <summary>
    /// The running teleport coroutine (to stop it), or null.
    /// </summary>
    private Coroutine _routine;

    /// <summary>
    /// Incremented for every teleport, so a stale watchdog does not touch a newer teleport.
    /// </summary>
    private int _teleportNumber;

    /// <summary>
    /// Realtime (Time.realtimeSinceStartup) of the last request sent, or negative if none.
    /// </summary>
    private float _lastRequestTime = -1000f;

    /// <summary>
    /// Whether the last request has not been answered yet.
    /// </summary>
    private bool _awaitingAnswer;

    /// <summary>
    /// Name of the player of the last request (for the arrival message).
    /// </summary>
    private string _pendingTargetName;

    /// <summary>
    /// Scene of the scene change of the running teleport, or null.
    /// </summary>
    private string _arrivalScene;

    /// <summary>
    /// Target of the scene change of the running teleport.
    /// </summary>
    private Vector2 _arrivalTarget;

    /// <summary>
    /// Whether the landing spot of the scene change still has to be checked when the scene is set up (see
    /// <see cref="ResolveArrival"/>).
    /// </summary>
    private bool _landingPending;

    /// <summary>
    /// Whether the landing spot of the last teleport was moved to a safe spot.
    /// </summary>
    private bool _landingMoved;

    /// <summary>
    /// Whether HKMP was told that we entered the scene of the running teleport.
    /// </summary>
    private bool _enterSignalled;

    public TeleportManager(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        ServerSettings serverSettings
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _serverSettings = serverSettings;
        Instance = this;

        // Check the landing spot once the target scene is set up, before the game places the hero there
        On.GameManager.BeginScene += (orig, self) => {
            ResolveArrival("BeginScene");
            orig(self);
        };
        On.HeroController.HazardRespawn += (orig, self) => {
            ResolveArrival("HazardRespawn");
            return orig(self);
        };
    }

    /// <summary>
    /// The last created instance (used by the development test).
    /// </summary>
    internal static TeleportManager Instance { get; private set; }

    /// <summary>
    /// The remote players (used by the development test).
    /// </summary>
    internal IEnumerable<ClientPlayerData> DevPlayers => _playerData.Values;

    /// <summary>
    /// Number of teleports started (used by the development test).
    /// </summary>
    internal static int DevStartedCount { get; private set; }

    /// <summary>
    /// Whether teleporting is possible right now (connected and allowed by the server).
    /// </summary>
    public bool IsAvailable => _netClient.IsConnected && _serverSettings.AllowTeleport;

    /// <summary>
    /// Show a message to the player (and log it).
    /// </summary>
    private static void Say(string message) {
        Logger.Info($"Teleport message: {message}");
        try {
            UiManager.InternalChatBox.AddMessage(message);
        } catch (Exception e) {
            Logger.Warn($"Teleport: could not show message: {e.Message}");
        }
    }

    /// <summary>
    /// Request a teleport to the next other player (cycles through the players on repeated calls) or, if a name is
    /// given, to that player.
    /// </summary>
    /// <param name="name">Optional player name.</param>
    public void Request(string name = null) {
        if (!_netClient.IsConnected) {
            return;
        }

        if (!_serverSettings.AllowTeleport) {
            Say("Teleporting is disabled on this server");
            return;
        }

        var now = Time.realtimeSinceStartup;
        var throttle = TeleportRules.ThrottleReason(_busy, now - _lastRequestTime, _awaitingAnswer);
        if (throttle != null) {
            Say($"Teleport ignored: {throttle}");
            return;
        }

        string blocked;
        try {
            blocked = TeleportRules.RequestBlockReason(Snapshot(), SceneUtil.GetCurrentSceneName());
        } catch (Exception e) {
            Logger.Warn($"Teleport: could not check the state: {e.Message}");
            blocked = null;
        }

        if (blocked != null) {
            Say($"Can't teleport right now: {blocked}");
            return;
        }

        var players = _playerData.Values.Select(p => (p.Id, p.Username)).ToList();
        ushort targetId;
        string targetName;
        if (string.IsNullOrWhiteSpace(name)) {
            if (!TeleportTargetSelector.TryPickNext(players, _lastPickedId, out var picked)) {
                Say("There are no other players to teleport to");
                return;
            }

            targetId = picked.Id;
            targetName = picked.Name;
        } else {
            if (!TeleportTargetSelector.TryFindByName(players, name, out targetId)) {
                Say($"No unique player found for \"{name}\"");
                return;
            }

            targetName = _playerData[targetId].Username;
        }

        _lastPickedId = targetId;
        _lastRequestTime = now;
        _awaitingAnswer = true;
        _pendingTargetName = targetName;
        Say($"Teleporting to {targetName}...");
        _netClient.UpdateManager.SetTeleportRequest(targetId);
    }

    /// <summary>
    /// Callback for when the server tells us where to teleport to.
    /// </summary>
    /// <param name="teleport">The packet data.</param>
    public void OnPlayerTeleport(PlayerTeleport teleport) {
        _awaitingAnswer = false;
        if (_busy) {
            Logger.Info("Teleport ignored: a teleport is already in progress");
            Say("Teleport ignored: a teleport is already in progress");
            return;
        }

        var number = ++_teleportNumber;
        var target = new Vector2(teleport.X, teleport.Y);
        Logger.Info($"Teleport #{number} to {teleport.SceneName} {target} ({DescribeState()})");
        _busy = true;
        DevStartedCount++;
        _routine = null;
        _arrivalScene = null;
        _landingPending = false;
        _landingMoved = false;
        _enterSignalled = false;
        // The coroutine runs synchronously until its first yield and may already be done when StartCoroutine returns
        var routine = MonoBehaviourUtil.Instance.StartCoroutine(Teleport(number, teleport.SceneName, target));
        if (_busy && number == _teleportNumber) {
            _routine = routine;
            MonoBehaviourUtil.Instance.StartCoroutine(Watchdog(number, teleport.SceneName));
        }
    }

    /// <summary>
    /// The teleport itself. Every step that touches the game is guarded: on an exception or a timeout the teleport
    /// ends through <see cref="Recover"/>, so the player is never left without control.
    /// </summary>
    private IEnumerator Teleport(int number, string scene, Vector2 target) {
        var currentScene = SceneUtil.GetCurrentSceneName();

        // 0. Scenes that must not be entered or left this way (Godhome, dreams, Colosseum trials, menus): nothing
        //    is touched, not even the pause menu
        var sceneReason = TeleportRules.SceneBlockReason(currentScene) is { } own
            ? $"you are {own}"
            : TeleportRules.SceneBlockReason(scene) is { } other
                ? $"the player is {other}"
                : null;
        if (sceneReason != null) {
            Logger.Info($"Teleport cancelled: {sceneReason} ({currentScene} -> {scene})");
            yield return End(number, $"Can't teleport: {sceneReason}", false);
            yield break;
        }

        // 1. A scene change (or moving) while paused leaves GameManager.isPaused set and the hero ignores all input
        //    afterwards (it can't move nor attack, even in later scenes), so leave the pause menu first.
        if (TeleportRules.NeedsUnpause(Snapshot())) {
            Logger.Info("Teleport: unpausing first");
            if (!Guard(Unpause, "unpause")) {
                yield return End(number, "Teleport failed", true);
                yield break;
            }

            yield return WaitRealtime(() => !TeleportRules.NeedsUnpause(Snapshot()), 1f);
        }

        // 2. Wait (a little) for a state where it is safe to leave: not dead, not in a transition, cutscene,
        //    bench, dive, focus...
        string reason = null;
        var waited = 0f;
        while (waited < WaitForStateTimeout) {
            reason = TeleportRules.BlockReason(Snapshot());
            if (reason == null) {
                break;
            }

            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (reason != null) {
            Logger.Info($"Teleport cancelled: {reason} ({DescribeState()})");
            // Nothing was changed, so the hero keeps whatever control it has (e.g. a dialogue); only if unpausing
            // did not work, restore the playing state
            yield return End(number, $"Can't teleport right now: {reason}", reason == "paused");
            yield break;
        }

        currentScene = SceneUtil.GetCurrentSceneName();
        var kind = TeleportRules.Classify(currentScene, scene, Application.CanStreamedLevelBeLoaded(scene ?? ""));
        Logger.Info($"Teleport: {kind} from {currentScene} to {scene} {target}");

        if (kind is TeleportKind.InvalidScene or TeleportKind.BlockedScene) {
            yield return End(number, $"Can't teleport: the scene \"{scene}\" can't be entered", false);
            yield break;
        }

        if (kind == TeleportKind.SameScene) {
            var moved = Guard(() => MoveHeroInScene(target), "move hero");
            yield return End(number, moved ? ArrivalMessage() : "Teleport failed", !moved);
            yield break;
        }

        // 3. Another scene: let the game load it and respawn the hero at the target position
        _arrivalScene = scene;
        _arrivalTarget = target;
        _landingPending = true;
        if (!Guard(() => BeginHazardRespawnTransition(scene, target), "start scene change")) {
            _arrivalScene = null;
            _landingPending = false;
            yield return End(number, "Teleport failed", true);
            yield break;
        }

        yield return WaitRealtime(
            () => SceneUtil.GetCurrentSceneName() == scene && !global::GameManager.instance.IsInSceneTransition,
            SceneLoadTimeout
        );
        if (SceneUtil.GetCurrentSceneName() != scene || global::GameManager.instance.IsInSceneTransition) {
            Logger.Warn($"Teleport: scene {scene} did not load ({DescribeState()})");
            // Recovery waits for a load that is still running (see Recover)
            yield return End(number, "Teleport failed: the scene did not load in time", true);
            yield break;
        }

        // Should have happened when the scene was set up; never leave it pending for a later scene
        ResolveArrival("after load");
        Guard(AfterSceneLoaded, "after scene load");
        _enterSignalled = true;

        // 4. Wait for the hazard respawn (animation + fade) to give control back
        yield return WaitRealtime(() => TeleportRules.IsControlRestored(Snapshot()), ControlTimeout);
        var restored = TeleportRules.IsControlRestored(Snapshot());
        if (!restored) {
            Logger.Warn($"Teleport: no control after arriving ({DescribeState()})");
        }

        yield return End(number, ArrivalMessage(), !restored);

        // 5. Once landed, remember a safe hazard respawn point
        yield return new WaitForSecondsRealtime(0.75f);
        Guard(SetSafeHazardRespawn, "hazard respawn point");
    }

    private string ArrivalMessage() {
        var name = _pendingTargetName != null ? $"Teleported to {_pendingTargetName}" : "Teleported";
        return _landingMoved ? $"{name} (moved to the closest safe spot)" : name;
    }

    /// <summary>
    /// Hard limit for a teleport: if it is still running after <see cref="WatchdogTimeout"/> seconds, stop it and
    /// restore control.
    /// </summary>
    private IEnumerator Watchdog(int number, string scene) {
        yield return new WaitForSecondsRealtime(WatchdogTimeout);
        if (!_busy || number != _teleportNumber) {
            yield break;
        }

        Logger.Warn($"Teleport #{number} timed out ({DescribeState()})");
        if (_routine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(_routine);
        }

        _routine = null;
        _busy = false;
        Say("Teleport took too long, control restored");
        RecoverWhenLoaded(scene);
    }

    /// <summary>
    /// End the teleport: optional chat message, recovery if needed and a final log of the state.
    /// </summary>
    private IEnumerator End(int number, string message, bool recover) {
        if (recover) {
            RecoverWhenLoaded(_arrivalScene);
        }

        if (message != null) {
            Say(message);
        }

        Logger.Info($"Teleport #{number} done (recovered: {recover}, {DescribeState()})");
        if (number == _teleportNumber) {
            _routine = null;
            _busy = false;
        }

        yield break;
    }

    /// <summary>
    /// Restore control now or, if a scene is still loading, once it is loaded: clearing the hazard respawn flag or
    /// touching the hero in the middle of a load makes the game enter the scene without an entry point (black
    /// screen, hero stuck).
    /// </summary>
    /// <param name="arrivalScene">The scene of the teleport, if it was a scene change.</param>
    private void RecoverWhenLoaded(string arrivalScene) {
        var gm = global::GameManager.instance;
        if (gm != null && gm.IsInSceneTransition) {
            Logger.Warn("Teleport: scene still loading, restoring control once it is loaded");
            MonoBehaviourUtil.Instance.StartCoroutine(RecoverAfterTransition(arrivalScene));
            return;
        }

        FinishArrival(arrivalScene);
        Recover();
    }

    private IEnumerator RecoverAfterTransition(string arrivalScene) {
        yield return WaitRealtime(() => !global::GameManager.instance.IsInSceneTransition, RecoverTransitionTimeout);
        yield return new WaitForSecondsRealtime(0.5f);
        FinishArrival(arrivalScene);
        Recover();
    }

    /// <summary>
    /// If the teleport loaded its scene but did not get to tell HKMP (aborted), do it now.
    /// </summary>
    private void FinishArrival(string arrivalScene) {
        if (arrivalScene == null || _enterSignalled || SceneUtil.GetCurrentSceneName() != arrivalScene) {
            return;
        }

        _enterSignalled = true;
        ResolveArrival("recover");
        Guard(AfterSceneLoaded, "after scene load (recover)");
    }

    /// <summary>
    /// Move the hero within the current scene (to the closest safe spot if the target is above a hazard or a pit).
    /// </summary>
    private void MoveHeroInScene(Vector2 target) {
        var hero = HeroController.instance;
        var landing = FindLanding(target, out _landingMoved);
        if (_landingMoved) {
            // A safe spot of the scene: stand on its ground, as a hazard respawn does
            landing = hero.FindGroundPoint(landing, true);
        }

        hero.transform.SetPosition2D(landing.x, landing.y);
        var body = hero.GetComponent<Rigidbody2D>();
        if (body) {
            body.linearVelocity = Vector2.zero;
        }

        global::GameManager.instance.cameraCtrl.PositionToHero(false);
    }

    /// <summary>
    /// Called when the target scene is set up (GameManager.BeginScene, before the hero is placed at the hazard
    /// respawn location) and as a fallback from HeroController.HazardRespawn: if the target is above a hazard or a
    /// pit, use the closest safe spot of the scene as the hazard respawn location.
    /// </summary>
    private void ResolveArrival(string when) {
        if (!_landingPending) {
            return;
        }

        _landingPending = false;
        var scene = _arrivalScene;
        var current = SceneUtil.GetCurrentSceneName();
        if (current != scene) {
            Logger.Warn($"Teleport: landing check ({when}) in {current}, expected {scene}");
        }

        Guard(() => {
            var landing = FindLanding(_arrivalTarget, out _landingMoved);
            Logger.Info($"Teleport: landing ({when}) target={_arrivalTarget} landing={landing} moved={_landingMoved}");
            if (_landingMoved) {
                PlayerData.instance.SetVector3(nameof(PlayerData.hazardRespawnLocation),
                    new Vector3(landing.x, landing.y, 0f));
            }
        }, "landing check");
    }

    /// <summary>
    /// Where to land for the given target in the current scene: the target if there is safe ground below it,
    /// otherwise the closest safe hazard respawn marker.
    /// </summary>
    private static Vector2 FindLanding(Vector2 target, out bool moved) {
        var hasAcidArmour = PlayerData.instance != null && PlayerData.instance.GetBool(nameof(PlayerData.hasAcidArmour));
        var targetSpot = CheckSpot(target, hasAcidArmour);
        moved = false;
        if (targetSpot.Safe) {
            return target;
        }

        var markers = UnityEngine.Object.FindObjectsOfType<HazardRespawnMarker>()
            .Where(m => m != null && m.isActiveAndEnabled)
            .Select(m => (Vector2) m.transform.position)
            .ToList();
        var chosen = TeleportRules.ChooseLanding(
            targetSpot,
            markers.Select(m => CheckSpot(m, hasAcidArmour)),
            out moved
        );
        Logger.Info($"Teleport: target {target} unsafe (ground={targetSpot.HasGround}, hazard={targetSpot.Hazard}, " +
                    $"gate={targetSpot.InTransition}), " +
                    $"{markers.Count} markers, moved={moved}");
        return new Vector2(chosen.X, chosen.Y);
    }

    /// <summary>
    /// Ground and hazard check of a spot: the ground the hazard respawn would put the knight on, and whether a
    /// hazard collider touches the knight standing there.
    /// </summary>
    private static LandingSpot CheckSpot(Vector2 point, bool hasAcidArmour) {
        var hit = Physics2D.Raycast(point, Vector2.down, GroundSearchDistance, TerrainMask);
        if (hit.collider == null) {
            return new LandingSpot(point.x, point.y, false, false);
        }

        var hazard = false;
        var inTransition = false;
        foreach (var collider in Physics2D.OverlapBoxAll(hit.point + new Vector2(0f, 0.65f), new Vector2(0.9f, 1.3f), 0f)) {
            if (collider == null || !collider.enabled) {
                continue;
            }

            if (collider.isTrigger && collider.GetComponent<TransitionPoint>() is { } gate && gate.isActiveAndEnabled &&
                !gate.isADoor) {
                // The target just entered through this gate: landing here walks back into the previous scene
                inTransition = true;
                continue;
            }

            var damage = collider.GetComponent<DamageHero>() ?? collider.GetComponentInParent<DamageHero>();
            if (damage != null && damage.isActiveAndEnabled &&
                TeleportRules.IsHazard(damage.hazardType, hasAcidArmour)) {
                hazard = true;
                break;
            }
        }

        return new LandingSpot(point.x, point.y, true, hazard, inTransition);
    }

    /// <summary>
    /// Start the scene change. With hazardRespawningHero set, GameManager positions the hero at
    /// hazardRespawnLocation when the scene is set up and EnterHero runs HeroController.HazardRespawn (ground the
    /// hero, fade in, FinishedEnteringScene: input, gravity, damage, gameState PLAYING).
    /// </summary>
    private static void BeginHazardRespawnTransition(string scene, Vector2 target) {
        var gm = global::GameManager.instance;
        var pd = PlayerData.instance;
        var hero = HeroController.instance;

        pd.SetVector3(nameof(PlayerData.hazardRespawnLocation), new Vector3(target.x, target.y, 0f));
        pd.SetBool(nameof(PlayerData.hazardRespawnFacingRight), hero.cState.facingRight);
        ReflectionHelper.SetField(gm, HazardRespawningField, true);

        gm.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = "",
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = global::GameManager.SceneLoadVisualizations.Default,
            AlwaysUnloadUnusedAssets = false
        });
    }

    /// <summary>
    /// Right after the scene loaded (hero already placed by HazardRespawn).
    /// </summary>
    private static void AfterSceneLoaded() {
        var gm = global::GameManager.instance;
        if (ReflectionHelper.GetField<global::GameManager, bool>(gm, HazardRespawningField)) {
            // EnterHero did not consume it (should not happen); never leave it for a later scene change
            Logger.Warn("Teleport: hazard respawn flag was not consumed");
            ReflectionHelper.SetField(gm, HazardRespawningField, false);
        }

        // The hazard respawn entry does not raise the enter-scene event that every gate/respawn entry raises, so
        // tell HKMP (server, other players, entity host) that we entered the scene
        CustomHooks.InvokeAfterEnterSceneHeroTransformed();

        // Make sure the camera follows the hero and the scene fades in (as GameManager.HazardRespawn does)
        gm.cameraCtrl.camTarget.mode = CameraTarget.TargetMode.FOLLOW_HERO;
        gm.FadeSceneIn();
    }

    /// <summary>
    /// After landing at the target, use the position as hazard respawn point; if we did not land, use the
    /// respawn marker of a gate of the scene so a fall into spikes can't loop.
    /// </summary>
    private static void SetSafeHazardRespawn() {
        var hero = HeroController.instance;
        if (!hero || hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning) {
            return;
        }

        var position = (Vector2) hero.transform.position;
        var hasAcidArmour = PlayerData.instance.GetBool(nameof(PlayerData.hasAcidArmour));
        if (hero.cState.onGround && CheckSpot(position + new Vector2(0f, 0.5f), hasAcidArmour).Safe) {
            PlayerData.instance.SetHazardRespawn(hero.transform.position, hero.cState.facingRight);
            return;
        }

        var marker = TransitionPoint.TransitionPoints
            .Where(t => t != null && t.respawnMarker != null)
            .OrderBy(t => Vector2.Distance(t.transform.position, position))
            .Select(t => t.respawnMarker)
            .FirstOrDefault();
        if (marker != null) {
            PlayerData.instance.SetHazardRespawn(marker);
        }
    }

    /// <summary>
    /// Leave the pause menu / paused state completely (also the half-paused state where the pause menu is hidden
    /// but GameManager.isPaused is still set).
    /// </summary>
    private static void Unpause() {
        var gm = global::GameManager.instance;
        var ui = UIManager.instance;
        if (ui != null && ui.uiState == UIState.PAUSED) {
            ui.SetState(UIState.PLAYING);
        }

        if (gm.isPaused || gm.gameState == GameState.PAUSED) {
            ReflectionHelper.GetField<global::GameManager, GameCameras>(gm, "gameCams")?.ResumeCameraShake();
            gm.actorSnapshotUnpaused.TransitionTo(0f);
            gm.ui.AudioGoToGameplay(0.2f);
        }

        gm.isPaused = false;
        if (gm.gameState == GameState.PAUSED) {
            gm.SetState(GameState.PLAYING);
        }

        if (HeroController.instance != null) {
            HeroController.instance.UnPause();
        }

        if (Time.timeScale < 0.01f) {
            TimeController.GenericTimeScale = 1f;
        }

        gm.inputHandler.StartAcceptingInput();
        gm.inputHandler.AllowPause();
        MenuButtonList.ClearAllLastSelected();
    }

    /// <summary>
    /// Restore full control of the hero after a failed or stuck teleport: pause, game state, time scale, input,
    /// hero transition/respawn state, gravity, renderer, camera and fade. Only called when no scene is loading
    /// (see <see cref="RecoverWhenLoaded"/>).
    /// </summary>
    private static void Recover() {
        Logger.Warn($"Teleport: restoring control ({DescribeState()})");
        var gm = global::GameManager.instance;
        if (gm == null || !gm.IsGameplayScene()) {
            // Menu or cutscene (e.g. quit to the menu during the teleport): nothing to give back
            return;
        }

        Guard(() => ReflectionHelper.SetField(gm, HazardRespawningField, false), "recover hazard flag");
        Guard(Unpause, "recover unpause");
        Guard(() => {
            if (!gm.IsInSceneTransition && gm.gameState != GameState.PLAYING && gm.IsGameplayScene()) {
                gm.SetState(GameState.PLAYING);
            }
        }, "recover game state");

        var hero = HeroController.instance;
        if (hero != null && !hero.cState.dead && !gm.IsInSceneTransition) {
            Guard(() => {
                // Keep the damage mode of the player (SetDamageMode also clears PlayerData.isInvincible)
                var invincible = PlayerData.instance.GetBool(nameof(PlayerData.isInvincible));
                hero.cState.transitioning = false;
                hero.cState.hazardRespawning = false;
                hero.transitionState = HeroTransitionState.WAITING_TO_TRANSITION;
                hero.AffectedByGravity(true);
                hero.SetDamageMode(DamageMode.FULL_DAMAGE);
                PlayerData.instance.SetBool(nameof(PlayerData.isInvincible), invincible);
                var body = hero.GetComponent<Rigidbody2D>();
                if (body) {
                    body.isKinematic = false;
                }

                var renderer = hero.GetComponent<MeshRenderer>();
                if (renderer) {
                    renderer.enabled = true;
                }

                hero.StartAnimationControl();
                // RegainControl always accepts input again and resets the hero state; it also undoes
                // RelinquishControl if that was left set
                hero.RegainControl();
            }, "recover hero");
        }

        Guard(() => {
            gm.cameraCtrl.camTarget.mode = CameraTarget.TargetMode.FOLLOW_HERO;
            gm.cameraCtrl.PositionToHero(false);
            gm.FadeSceneIn();
        }, "recover camera");
    }

    /// <summary>
    /// Run an action, logging instead of throwing.
    /// </summary>
    /// <returns>True if it ran without exception.</returns>
    private static bool Guard(Action action, string what) {
        try {
            action();
            return true;
        } catch (Exception e) {
            Logger.Error($"Teleport: {what} failed: {e}");
            return false;
        }
    }

    private static IEnumerator WaitRealtime(Func<bool> condition, float timeout) {
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

    /// <summary>
    /// Take a snapshot of the state relevant for <see cref="TeleportRules"/>.
    /// </summary>
    private static TeleportGameState Snapshot() {
        var gm = global::GameManager.instance;
        var hero = HeroController.instance;
        var ih = InputHandler.Instance;
        var state = new TeleportGameState {
            HeroExists = hero != null,
            StatePlaying = gm != null && gm.gameState == GameState.PLAYING,
            StatePaused = gm != null && gm.gameState == GameState.PAUSED,
            IsPaused = gm != null && gm.isPaused,
            InSceneTransition = gm != null && gm.IsInSceneTransition,
            TimeStopped = Time.timeScale < 0.01f,
            InputHandlerAcceptingInput = ih != null && ih.acceptingInput,
            AtBench = PlayerData.instance != null && PlayerData.instance.GetBool(nameof(PlayerData.atBench))
        };
        if (hero != null) {
            state.Dead = hero.cState.dead;
            state.Transitioning = hero.cState.transitioning;
            state.HazardRespawning = hero.cState.hazardRespawning || hero.cState.hazardDeath;
            state.UsingMovementAbility = hero.cState.superDashing || hero.cState.spellQuake;
            state.Focusing = hero.cState.focusing;
            state.ControlRelinquished = hero.controlReqlinquished;
            state.HeroAcceptingInput = hero.acceptingInput;
        }

        return state;
    }

    private static string DescribeState() {
        try {
            var gm = global::GameManager.instance;
            var hero = HeroController.instance;
            return $"scene={SceneUtil.GetCurrentSceneName()} gs={gm?.gameState} paused={gm?.isPaused} " +
                   $"ts={Time.timeScale} inTransition={gm?.IsInSceneTransition} " +
                   $"input={InputHandler.Instance?.acceptingInput} " +
                   (hero == null
                       ? "hero=null"
                       : $"heroInput={hero.acceptingInput} relinquished={hero.controlReqlinquished} " +
                         $"heroState={hero.hero_state} transition={hero.transitionState} " +
                         $"hazard={hero.cState.hazardRespawning} pos={hero.transform.position}");
        } catch (Exception e) {
            return $"state error: {e.Message}";
        }
    }

    /// <summary>
    /// Stop a running teleport on disconnect and forget the cycling state. A scene change that was already started
    /// keeps loading; control is restored once it is loaded.
    /// </summary>
    public void OnDisconnect() {
        var wasBusy = _busy;
        if (_routine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(_routine);
        }

        _routine = null;
        _busy = false;
        _teleportNumber++;
        _lastPickedId = null;
        _awaitingAnswer = false;
        _arrivalScene = null;
        _landingPending = false;
        if (wasBusy) {
            // Without telling HKMP about the scene (we are not connected anymore)
            Guard(() => RecoverWhenLoaded(null), "recover on disconnect");
        }
    }
}
