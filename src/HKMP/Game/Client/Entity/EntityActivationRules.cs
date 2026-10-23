using System.Collections.Generic;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Pure rules for the active state that an entity's host object had in the scene file. Linked into the unit tests.
/// <para>The Unity 6 build of the game has the <c>AwakenChildren</c> component: in <c>Awake</c> it activates every
/// inactive object in its target list (so their FSMs and components initialise) and remembers which were inactive;
/// in <c>Start</c> it deactivates those again. HKMP registers the entities of a scene between both calls, so an
/// entity that the scene keeps inactive (the dream warriors Xero, Gorb, Marmu, ... wait inactive in their "Warrior"
/// folder until the ghost is challenged with the Dream Nail) looks active at that moment. Taking that value as the
/// original state activated the boss for good on the scene host (it attacked without being challenged) and made it
/// appear on every scene client.</para>
/// </summary>
internal static class EntityActivationRules {
    /// <summary>
    /// The active state of the host object as the scene defines it.
    /// </summary>
    /// <param name="activeSelf">The current activeSelf of the host object.</param>
    /// <param name="awakenRecordedActive">The activeSelf that an <c>AwakenChildren</c> component recorded for the
    /// host object before it temporarily activated it, or null if no such component targets the host object.</param>
    /// <param name="awakenStarted">Whether that <c>AwakenChildren</c> component already ran its Start (restored the
    /// recorded state). After that, the current state is the real one (e.g. a warrior activated for its fight).</param>
    public static bool OriginalActive(bool activeSelf, bool? awakenRecordedActive, bool awakenStarted) {
        if (awakenRecordedActive == null || awakenStarted) {
            return activeSelf;
        }

        return awakenRecordedActive.Value;
    }

    /// <summary>
    /// Find the active state recorded by an <c>AwakenChildren</c> for the given object: its index in the target list
    /// and the recorded state at that index.
    /// </summary>
    /// <typeparam name="T">The object type (GameObject in the game, anything in tests).</typeparam>
    /// <param name="targets">The target list of the component.</param>
    /// <param name="recordedActive">The recorded active states (same order as the targets), may be null when the
    /// component did not run its Awake yet.</param>
    /// <param name="obj">The object to look up.</param>
    /// <returns>The recorded active state, or null if the object is not a target or nothing was recorded.</returns>
    public static bool? RecordedActive<T>(IList<T> targets, IList<bool> recordedActive, T obj) where T : class {
        if (targets == null || recordedActive == null) {
            return null;
        }

        for (var i = 0; i < targets.Count && i < recordedActive.Count; i++) {
            if (ReferenceEquals(targets[i], obj)) {
                return recordedActive[i];
            }
        }

        return null;
    }
}
