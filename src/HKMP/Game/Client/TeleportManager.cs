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
/// the target scene. The game is always unpaused first and, if anything does not end in a playable state, the full
/// control of the hero is restored (<see cref="Recover"/>).</para>
/// </summary>
internal class TeleportManager {
    /// <summary>
    /// Seconds to wait for a state in which teleporting is possible (e.g. end of an attack or a dive).
    /// </summary>
    private const float WaitForStateTimeout = 3f;

    /// <summary>
    /// Seconds to wait for the target scene to load.
    /// </summary>
    private const float SceneLoadTimeout = 30f;

    /// <summary>
    /// Seconds to wait for the hero to have control again after arriving.
    /// </summary>
    private const float ControlTimeout = 6f;

    /// <summary>
    /// Hard limit for a whole teleport, after which it is aborted and control restored.
    /// </summary>
    private const float WatchdogTimeout = 45f;

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

    public TeleportManager(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        ServerSettings serverSettings
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _serverSettings = serverSettings;
        Instance = this;
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
    /// Whether teleporting is possible right now (connected and allowed by the server).
    /// </summary>
    public bool IsAvailable => _netClient.IsConnected && _serverSettings.AllowTeleport;

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
            UiManager.InternalChatBox.AddMessage("Teleporting is disabled on this server");
            return;
        }

        var players = _playerData.Values.Select(p => (p.Id, p.Username)).ToList();
        ushort targetId;
        string targetName;
        if (string.IsNullOrWhiteSpace(name)) {
            if (!TeleportTargetSelector.TryPickNext(players, _lastPickedId, out var picked)) {
                UiManager.InternalChatBox.AddMessage("There are no other players");
                return;
            }

            targetId = picked.Id;
            targetName = picked.Name;
        } else {
            if (!TeleportTargetSelector.TryFindByName(players, name, out targetId)) {
                UiManager.InternalChatBox.AddMessage($"No unique player found for \"{name}\"");
                return;
            }

            targetName = _playerData[targetId].Username;
        }

        _lastPickedId = targetId;
        UiManager.InternalChatBox.AddMessage($"Teleporting to {targetName}...");
        _netClient.UpdateManager.SetTeleportRequest(targetId);
    }

    /// <summary>
    /// Callback for when the server tells us where to teleport to.
    /// </summary>
    /// <param name="teleport">The packet data.</param>
    public void OnPlayerTeleport(PlayerTeleport teleport) {
        if (_busy) {
            Logger.Info("Teleport ignored: a teleport is already in progress");
            return;
        }

        var number = ++_teleportNumber;
        var target = new Vector2(teleport.X, teleport.Y);
        Logger.Info($"Teleport #{number} to {teleport.SceneName} {target} ({DescribeState()})");
        _busy = true;
        _routine = null;
        // The coroutine runs synchronously until its first yield and may already be done when StartCoroutine returns
        var routine = MonoBehaviourUtil.Instance.StartCoroutine(Teleport(number, teleport.SceneName, target));
        if (_busy && number == _teleportNumber) {
            _routine = routine;
            MonoBehaviourUtil.Instance.StartCoroutine(Watchdog(number));
        }
    }

    /// <summary>
    /// The teleport itself. Every step that touches the game is guarded: on an exception or a timeout the teleport
    /// ends through <see cref="Recover"/>, so the player is never left without control.
    /// </summary>
    private IEnumerator Teleport(int number, string scene, Vector2 target) {
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
        //    bench, dive...
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
            yield return End(number, $"Can't teleport right now ({reason})", reason == "paused");
            yield break;
        }

        var currentScene = SceneUtil.GetCurrentSceneName();
        var kind = TeleportRules.Classify(currentScene, scene, Application.CanStreamedLevelBeLoaded(scene ?? ""));
        Logger.Info($"Teleport: {kind} from {currentScene} to {scene} {target}");

        if (kind == TeleportKind.InvalidScene) {
            yield return End(number, $"Can't teleport: unknown scene \"{scene}\"", false);
            yield break;
        }

        if (kind == TeleportKind.SameScene) {
            var moved = Guard(() => MoveHeroInScene(target), "move hero");
            yield return End(number, moved ? null : "Teleport failed", !moved);
            yield break;
        }

        // 3. Another scene: let the game load it and respawn the hero at the target position
        if (!Guard(() => BeginHazardRespawnTransition(scene, target), "start scene change")) {
            yield return End(number, "Teleport failed", true);
            yield break;
        }

        yield return WaitRealtime(
            () => SceneUtil.GetCurrentSceneName() == scene && !global::GameManager.instance.IsInSceneTransition,
            SceneLoadTimeout
        );
        if (SceneUtil.GetCurrentSceneName() != scene || global::GameManager.instance.IsInSceneTransition) {
            Logger.Warn($"Teleport: scene {scene} did not load ({DescribeState()})");
            yield return End(number, "Teleport failed: the scene did not load", true);
            yield break;
        }

        Guard(AfterSceneLoaded, "after scene load");

        // 4. Wait for the hazard respawn (animation + fade) to give control back
        yield return WaitRealtime(() => TeleportRules.IsControlRestored(Snapshot()), ControlTimeout);
        var restored = TeleportRules.IsControlRestored(Snapshot());
        if (!restored) {
            Logger.Warn($"Teleport: no control after arriving ({DescribeState()})");
        }

        yield return End(number, null, !restored);

        // 5. Once landed, remember a safe hazard respawn point (the target may have been in the air)
        yield return new WaitForSecondsRealtime(0.75f);
        Guard(SetSafeHazardRespawn, "hazard respawn point");
    }

    /// <summary>
    /// Hard limit for a teleport: if it is still running after <see cref="WatchdogTimeout"/> seconds, stop it and
    /// restore control.
    /// </summary>
    private IEnumerator Watchdog(int number) {
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
        Recover();
    }

    /// <summary>
    /// End the teleport: optional chat message, recovery if needed and a final log of the state.
    /// </summary>
    private IEnumerator End(int number, string message, bool recover) {
        if (recover) {
            Recover();
        }

        if (message != null) {
            UiManager.InternalChatBox.AddMessage(message);
        }

        Logger.Info($"Teleport #{number} done (recovered: {recover}, {DescribeState()})");
        if (number == _teleportNumber) {
            _routine = null;
            _busy = false;
        }

        yield break;
    }

    /// <summary>
    /// Move the hero within the current scene.
    /// </summary>
    private static void MoveHeroInScene(Vector2 target) {
        var hero = HeroController.instance;
        hero.transform.SetPosition2D(target.x, target.y);
        var body = hero.GetComponent<Rigidbody2D>();
        if (body) {
            body.linearVelocity = Vector2.zero;
        }

        global::GameManager.instance.cameraCtrl.PositionToHero(false);
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
        if (!hero || hero.cState.dead || hero.cState.hazardDeath) {
            return;
        }

        if (hero.cState.onGround) {
            PlayerData.instance.SetHazardRespawn(hero.transform.position, hero.cState.facingRight);
            return;
        }

        var position = (Vector2) hero.transform.position;
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
    /// hero transition/respawn state, gravity, renderer, camera and fade.
    /// </summary>
    private static void Recover() {
        Logger.Warn($"Teleport: restoring control ({DescribeState()})");
        var gm = global::GameManager.instance;
        if (gm == null) {
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
                hero.cState.transitioning = false;
                hero.cState.hazardRespawning = false;
                hero.transitionState = HeroTransitionState.WAITING_TO_TRANSITION;
                hero.AffectedByGravity(true);
                hero.SetDamageMode(DamageMode.FULL_DAMAGE);
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
    /// Stop a running teleport on disconnect (the game keeps loading a scene it already started) and forget the
    /// cycling state.
    /// </summary>
    public void OnDisconnect() {
        if (_routine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(_routine);
        }

        _routine = null;
        _busy = false;
        _teleportNumber++;
        _lastPickedId = null;
    }
}
