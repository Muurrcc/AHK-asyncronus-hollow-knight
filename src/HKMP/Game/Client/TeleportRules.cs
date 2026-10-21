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
    InvalidScene
}

/// <summary>
/// Pure rules of the teleport flow (covered by unit tests).
/// </summary>
internal static class TeleportRules {
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
    /// Why a teleport can't start right now, or null if it can. Checked after unpausing.
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

        if (state.AtBench) {
            return "resting at a bench";
        }

        if (state.ControlRelinquished) {
            return "in a cutscene or dialogue";
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
}
