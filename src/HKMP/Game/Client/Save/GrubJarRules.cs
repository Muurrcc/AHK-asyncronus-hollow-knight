namespace Hkmp.Game.Client.Save;

/// <summary>
/// Pure rules for synchronising grub jars (the "Bottle Control" FSM on the persistent "Grub Bottle" objects).
/// </summary>
internal static class GrubJarRules {
    /// <summary>
    /// Whether the persistent item id belongs to a grub jar (normal or mimic jar).
    /// </summary>
    public static bool IsGrubJar(string persistentItemId) =>
        persistentItemId != null &&
        (persistentItemId.StartsWith("Grub Bottle") || persistentItemId.StartsWith("Grub Mimic Bottle"));

    /// <summary>
    /// Whether a grub jar whose FSM is in the given state should be shattered because another player broke it.
    /// Only a jar that is waiting for a hit qualifies: one that is already shattering (or was restored broken when
    /// the scene loaded) must not run the break (and its effects) again.
    /// </summary>
    public static bool ShouldShatterRemoteBreak(string activeStateName) =>
        activeStateName is "Idle" or "Return Pause";
}
