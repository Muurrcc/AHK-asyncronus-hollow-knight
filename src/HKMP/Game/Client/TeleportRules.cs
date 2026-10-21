using System;
using System.Collections.Generic;

namespace Hkmp.Game.Client;

/// <summary>
/// Snapshot of the parts of the game and hero state that matter for teleporting (no game types, so the rules can be
/// unit tested).
/// </summary>
internal struct TeleportGameState {
    /// <summary>Whether there is a hero (HeroController.instance).</summary>
    public bool HeroExists;
    /// <summary>GameManager.gameState is PLAYING.</summary>
    public bool StatePlaying;
    /// <summary>GameManager.gameState is PAUSED.</summary>
    public bool StatePaused;
    /// <summary>GameManager.isPaused.</summary>
    public bool IsPaused;
    /// <summary>GameManager.IsInSceneTransition.</summary>
    public bool InSceneTransition;
    /// <summary>The time scale is 0.</summary>
    public bool TimeStopped;
    /// <summary>HeroController.cState.dead.</summary>
    public bool Dead;
    /// <summary>HeroController.cState.transitioning.</summary>
    public bool Transitioning;
    /// <summary>HeroController.cState.hazardRespawning or hazardDeath.</summary>
    public bool HazardRespawning;
    /// <summary>Super dash or desolate dive in progress (both lock the hero when a scene is entered).</summary>
    public bool UsingMovementAbility;
    /// <summary>HeroController.cState.focusing (the focus FSM would keep running in the new scene).</summary>
    public bool Focusing;
    /// <summary>HeroController.controlReqlinquished (cutscene, dialogue, bench, stag...).</summary>
    public bool ControlRelinquished;
    /// <summary>PlayerData atBench.</summary>
    public bool AtBench;
    /// <summary>HeroController.acceptingInput.</summary>
    public bool HeroAcceptingInput;
    /// <summary>InputHandler.acceptingInput.</summary>
    public bool InputHandlerAcceptingInput;
}

/// <summary>
/// What kind of teleport is needed.
/// </summary>
internal enum TeleportKind {
    /// <summary>Target is in our scene: move the hero.</summary>
    SameScene,
    /// <summary>Target is in another scene that can be loaded.</summary>
    OtherScene,
    /// <summary>Target scene is unknown or can't be loaded: cancel.</summary>
    InvalidScene,
    /// <summary>Target (or our own) scene is one that must not be entered or left by teleporting: cancel.</summary>
    BlockedScene
}

/// <summary>
/// A possible landing spot for a teleport (pure data for <see cref="TeleportRules.ChooseLanding"/>).
/// </summary>
internal struct LandingSpot {
    /// <summary>X position (start of the ground search).</summary>
    public float X;
    /// <summary>Y position (start of the ground search).</summary>
    public float Y;
    /// <summary>Whether there is ground below the spot (within the hazard respawn search distance).</summary>
    public bool HasGround;
    /// <summary>Whether a hazard (spikes, acid, lava, pit) touches the knight standing on that ground.</summary>
    public bool Hazard;
    /// <summary>Whether the knight standing there touches a transition gate (it would walk into the next scene).</summary>
    public bool InTransition;

    /// <summary>Whether the knight can safely stand there.</summary>
    public bool Safe => HasGround && !Hazard && !InTransition;

    public LandingSpot(float x, float y, bool hasGround, bool hazard, bool inTransition = false) {
        X = x;
        Y = y;
        HasGround = hasGround;
        Hazard = hazard;
        InTransition = inTransition;
    }
}

/// <summary>
/// Pure rules of the teleport flow (covered by unit tests).
/// </summary>
internal static class TeleportRules {
    /// <summary>
    /// Seconds a teleport request blocks a new one (double click on the button, repeated /tp).
    /// </summary>
    public const double MinRequestInterval = 1.5;

    /// <summary>
    /// Seconds to wait for the server to answer a request before a new request is allowed again (the server answers
    /// refusals with a chat message, which the client can't tell apart).
    /// </summary>
    public const double AnswerTimeout = 4.0;

    /// <summary>
    /// Scenes whose name starts with "GG_" but that are part of Hallownest, not of Godhome.
    /// </summary>
    private static readonly HashSet<string> HallownestGgScenes = new(StringComparer.Ordinal) {
        "GG_Waterways", "GG_Pipeway", "GG_Lurker"
    };

    /// <summary>
    /// Scenes that are not gameplay (menus, cutscenes, credits); "Cinematic_" scenes are matched by prefix.
    /// </summary>
    private static readonly HashSet<string> NonGameplayScenes = new(StringComparer.Ordinal) {
        "Menu_Title", "Menu_Credits", "Quit_To_Menu", "Opening_Sequence", "Pre_Menu_Intro", "Intro_Cutscene",
        "Intro_Cutscene_Prologue", "Prologue_Excerpt", "Knight Pickup", "Knight_Pickup", "BetaEnd", "End_Credits",
        "End_Game_Completion", "PermaDeath", "PermaDeath_Unlock", "Cutscene_Boss_Door", "GG_Boss_Door_Entrance",
        "GG_End_Sequence", "GG_Entrance_Cutscene", "GG_Unlock"
    };

    /// <summary>
    /// The Colosseum trials (the lobby Room_Colosseum_01 and the spectator rooms are normal scenes).
    /// </summary>
    private static readonly HashSet<string> ColosseumTrials = new(StringComparer.Ordinal) {
        "Room_Colosseum_Bronze", "Room_Colosseum_Silver", "Room_Colosseum_Gold"
    };

    /// <summary>
    /// Why the given scene can't be entered or left by teleporting, or null if it can. Entering one of these scenes
    /// without its own entry (dream nail, Godhome statues, Colosseum lever, cutscene) breaks its state: Godhome boss
    /// fights and pantheons apply bindings and keep the return scene, dreams need the dream entry and the dream return,
    /// the Colosseum trial is driven by its own manager, and menus/cutscenes have no hero to place.
    /// </summary>
    /// <param name="scene">The scene name.</param>
    public static string SceneBlockReason(string scene) {
        if (string.IsNullOrEmpty(scene)) {
            return "not in a scene";
        }

        if (NonGameplayScenes.Contains(scene) || scene.StartsWith("Cinematic_", StringComparison.Ordinal)) {
            return "in a menu or cutscene";
        }

        if (scene.StartsWith("GG_", StringComparison.Ordinal) && !HallownestGgScenes.Contains(scene)) {
            return "in Godhome";
        }

        if (scene.StartsWith("Dream_", StringComparison.Ordinal) ||
            scene.StartsWith("White_Palace_", StringComparison.Ordinal)) {
            return "in a dream";
        }

        if (ColosseumTrials.Contains(scene)) {
            return "in a Colosseum trial";
        }

        return null;
    }

    /// <summary>
    /// Decide what kind of teleport to do.
    /// </summary>
    /// <param name="currentScene">The scene we are in.</param>
    /// <param name="targetScene">The scene of the target player.</param>
    /// <param name="targetSceneLoadable">Whether the target scene exists in the build.</param>
    public static TeleportKind Classify(string currentScene, string targetScene, bool targetSceneLoadable) {
        if (string.IsNullOrEmpty(targetScene)) {
            return TeleportKind.InvalidScene;
        }

        if (SceneBlockReason(targetScene) != null || SceneBlockReason(currentScene) != null) {
            return TeleportKind.BlockedScene;
        }

        if (targetScene == currentScene) {
            return TeleportKind.SameScene;
        }

        return targetSceneLoadable ? TeleportKind.OtherScene : TeleportKind.InvalidScene;
    }

    /// <summary>
    /// Whether the game has to be unpaused before teleporting. A scene change started while paused leaves
    /// GameManager.isPaused set in the new scene, and the hero ignores all input while it is set.
    /// </summary>
    public static bool NeedsUnpause(TeleportGameState state) {
        return state.IsPaused || state.StatePaused;
    }

    /// <summary>
    /// Why a teleport can't start right now, or null if it can. Checked after unpausing; short states (an ability,
    /// focus, a hazard respawn) are waited for a moment before giving up.
    /// </summary>
    public static string BlockReason(TeleportGameState state) {
        if (!state.HeroExists) {
            return "not in game";
        }

        if (state.InSceneTransition || state.Transitioning) {
            return "changing scene";
        }

        if (state.Dead) {
            return "dead";
        }

        if (state.HazardRespawning) {
            return "respawning";
        }

        if (state.UsingMovementAbility) {
            return "using an ability";
        }

        if (state.Focusing) {
            return "focusing";
        }

        if (state.AtBench) {
            return "resting at a bench";
        }

        if (state.ControlRelinquished) {
            return "in a cutscene, dialogue or menu";
        }

        if (NeedsUnpause(state)) {
            return "paused";
        }

        if (!state.StatePlaying) {
            return "not playing";
        }

        return null;
    }

    /// <summary>
    /// Why a teleport request should not even be sent, or null if it can be sent. Only states that won't end by
    /// themselves in a moment (the pause menu is fine: the teleport unpauses; short abilities and short losses of
    /// control such as a boss roar are waited for when the answer arrives).
    /// </summary>
    /// <param name="state">The current state.</param>
    /// <param name="currentScene">The scene we are in.</param>
    public static string RequestBlockReason(TeleportGameState state, string currentScene) {
        if (!state.HeroExists) {
            return "not in game";
        }

        var sceneReason = SceneBlockReason(currentScene);
        if (sceneReason != null) {
            return $"you are {sceneReason}";
        }

        if (state.InSceneTransition || state.Transitioning) {
            return "changing scene";
        }

        if (state.Dead) {
            return "dead";
        }

        if (state.AtBench) {
            return "resting at a bench";
        }

        return null;
    }

    /// <summary>
    /// Why a new request has to wait (a teleport is running, the last request was a moment ago or is still
    /// unanswered), or null if it can be sent.
    /// </summary>
    /// <param name="busy">Whether a teleport is running.</param>
    /// <param name="secondsSinceLastRequest">Seconds since the last request was sent (infinity if none).</param>
    /// <param name="awaitingAnswer">Whether the last request has not been answered yet.</param>
    public static string ThrottleReason(bool busy, double secondsSinceLastRequest, bool awaitingAnswer) {
        if (busy) {
            return "a teleport is already in progress";
        }

        if (awaitingAnswer && secondsSinceLastRequest < AnswerTimeout) {
            return "waiting for the server to answer the last request";
        }

        if (secondsSinceLastRequest < MinRequestInterval) {
            return "you just asked to teleport";
        }

        return null;
    }

    /// <summary>
    /// Whether the player has full control again (what must hold when a teleport is over).
    /// </summary>
    public static bool IsControlRestored(TeleportGameState state) {
        return state.HeroExists &&
               state.StatePlaying &&
               !state.IsPaused &&
               !state.TimeStopped &&
               !state.InSceneTransition &&
               !state.Transitioning &&
               !state.HazardRespawning &&
               !state.ControlRelinquished &&
               state.HeroAcceptingInput &&
               state.InputHandlerAcceptingInput;
    }

    /// <summary>
    /// Whether a damaging collider with the given DamageHero.hazardType is a hazard that makes a spot unsafe. The
    /// values are those of HeroController.TakeDamage: 1 normal damage (enemies), 2 spikes, 3 acid, 4 lava, 5 pit.
    /// </summary>
    /// <param name="hazardType">DamageHero.hazardType.</param>
    /// <param name="hasAcidArmour">Whether the player has Isma's Tear (acid does not hurt).</param>
    public static bool IsHazard(int hazardType, bool hasAcidArmour) {
        if (hazardType < 2) {
            return false;
        }

        return hazardType != 3 || !hasAcidArmour;
    }

    /// <summary>
    /// Choose where to land: the target itself if it is above safe ground; otherwise (in the air above spikes, acid
    /// or a bottomless pit, or in a transition gate because the target just entered the scene) the closest safe spot of the scene (hazard respawn markers); if there is none, the target
    /// anyway (the game's own hazard respawn handles it).
    /// </summary>
    /// <param name="target">The target position with its ground check.</param>
    /// <param name="candidates">Safe-point candidates of the scene with their ground checks.</param>
    /// <param name="fallback">True if a candidate was chosen instead of the target.</param>
    public static LandingSpot ChooseLanding(
        LandingSpot target,
        IEnumerable<LandingSpot> candidates,
        out bool fallback
    ) {
        fallback = false;
        if (target.Safe) {
            return target;
        }

        LandingSpot? best = null;
        var bestDistance = double.MaxValue;
        foreach (var candidate in candidates ?? Array.Empty<LandingSpot>()) {
            if (!candidate.Safe) {
                continue;
            }

            var dx = (double) candidate.X - target.X;
            var dy = (double) candidate.Y - target.Y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance) {
                bestDistance = distance;
                best = candidate;
            }
        }

        if (best == null) {
            return target;
        }

        fallback = true;
        return best.Value;
    }
}

/// <summary>
/// Server side: which players are teleporting right now (between the server sending them the destination and them
/// entering the new scene). Used to refuse a teleport to a player that is about to leave (two players teleporting to
/// each other at the same time would swap places) and repeated requests of the same player. Thread safe.
/// </summary>
internal class TeleportFlights {
    /// <summary>
    /// Seconds after which a teleport counts as over even without an enter-scene (same-scene teleports, failures).
    /// </summary>
    public const double FlightSeconds = 5.0;

    private readonly Dictionary<ushort, double> _until = new();
    private readonly object _lock = new();

    /// <summary>
    /// Mark the player as teleporting from <paramref name="now"/> (seconds, any monotonic clock).
    /// </summary>
    public void Start(ushort id, double now) {
        lock (_lock) {
            _until[id] = now + FlightSeconds;
        }
    }

    /// <summary>
    /// Whether the player is teleporting at <paramref name="now"/>.
    /// </summary>
    public bool IsTeleporting(ushort id, double now) {
        lock (_lock) {
            if (!_until.TryGetValue(id, out var until)) {
                return false;
            }

            if (now < until) {
                return true;
            }

            _until.Remove(id);
            return false;
        }
    }

    /// <summary>
    /// The player arrived (entered a scene) or left: no longer teleporting.
    /// </summary>
    public void End(ushort id) {
        lock (_lock) {
            _until.Remove(id);
        }
    }

    /// <summary>
    /// Why the server refuses a request of <paramref name="requester"/> to teleport to <paramref name="target"/>, or
    /// null if it accepts it. Only the flight part (scene checks are done by the caller).
    /// </summary>
    public string RefuseReason(ushort requester, ushort target, string targetName, double now) {
        if (IsTeleporting(requester, now)) {
            return "You are already teleporting";
        }

        if (IsTeleporting(target, now)) {
            return $"{targetName} is teleporting right now, try again in a moment";
        }

        return null;
    }
}
