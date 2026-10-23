using System.Collections.Generic;

namespace Hkmp.Game.Client.Save;

/// <summary>
/// Pure (game independent) rules for the PlayerData "gate" flags that the game keeps next to an ability flag.
/// Picking up an ability sets both (Mothwing Cloak: <c>hasDash</c> and <c>canDash</c>), but only the ability flag
/// (<c>has*</c>) is shared through save-data.json; the gate flags are never networked. The HeroController decides
/// whether the knight can dash from <c>canDash</c> alone, so the gate must follow the shared ability flag:
/// true when somebody unlocked the ability, false while nobody did. Linked into the unit tests.
/// </summary>
internal static class AbilityGateRules {
    /// <summary>
    /// Shared ability flag to the PlayerData gate flag the game sets together with it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GateByAbility = new Dictionary<string, string> {
        ["hasDash"] = "canDash",
        ["hasWalljump"] = "canWallJump",
        ["hasShadowDash"] = "canShadowDash",
        ["hasSuperDash"] = "canSuperDash"
    };

    /// <summary>
    /// Whether the given PlayerData field is a shared ability flag with a gate flag.
    /// </summary>
    /// <param name="abilityName">The PlayerData field name, e.g. "hasDash".</param>
    /// <param name="gateName">The gate flag, e.g. "canDash", or null.</param>
    public static bool TryGetGate(string abilityName, out string gateName) {
        if (abilityName != null && GateByAbility.TryGetValue(abilityName, out gateName)) {
            return true;
        }

        gateName = null;
        return false;
    }

    /// <summary>
    /// The value of the gate flag after a save update of its ability flag: the gate follows the received value, so
    /// receiving "nobody has the ability yet" (false, e.g. the full world state sent when joining) never grants it.
    /// </summary>
    /// <param name="receivedAbility">The received value of the ability flag.</param>
    public static bool GateAfterAbilityUpdate(bool receivedAbility) => receivedAbility;

    /// <summary>
    /// Repair a gate flag against its ability flag: a gate that is open while the ability is not unlocked (left in
    /// local saves by older versions, which opened <c>canDash</c> on any received <c>hasDash</c>, also false) is
    /// closed. An unlocked ability is never taken away and a closed gate is never opened here.
    /// </summary>
    /// <param name="hasAbility">The current value of the ability flag.</param>
    /// <param name="gate">The current value of the gate flag.</param>
    /// <returns>The repaired value of the gate flag.</returns>
    public static bool RepairGate(bool hasAbility, bool gate) => gate && hasAbility;
}
