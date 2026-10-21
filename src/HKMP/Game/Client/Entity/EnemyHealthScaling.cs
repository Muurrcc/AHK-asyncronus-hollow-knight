namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Scaling of the effective health of enemies with the number of connected players. Pure logic (no game
/// dependencies), so it is covered by unit tests.
/// <para>The scaling is applied as damage scaling on the scene host: every point of damage that reaches the real
/// (host) health manager is divided by the multiplier, with the fractional rest carried over to the next hit. The
/// result is the same as multiplying the health by the multiplier, but the HP value itself stays in vanilla units:</para>
/// <list type="bullet">
/// <item>The HP that is synchronised to the other players and stored by the server (partial health that persists
/// when leaving and entering a scene) does not depend on the number of players, so it never has to be converted
/// when a player joins or leaves; the new multiplier simply applies from the next hit on, which keeps the remaining
/// fraction of the effective health.</item>
/// <item>Boss FSMs that compare the HP with fixed thresholds (phase changes) keep working at the same relative
/// moments of the fight.</item>
/// <item>Only the scene host applies it, so it is consistent with the hit forwarding: scene clients forward the raw
/// hits and receive the resulting HP.</item>
/// </list>
/// </summary>
internal static class EnemyHealthScaling {
    /// <summary>
    /// Tolerance for float rounding when converting the scaled damage to whole points.
    /// </summary>
    private const float Epsilon = 1e-4f;

    /// <summary>
    /// The multiplier currently in use on this client (1 when not connected).
    /// </summary>
    public static float CurrentMultiplier { get; private set; } = 1f;

    /// <summary>
    /// The player count the current multiplier was computed for.
    /// </summary>
    public static int CurrentPlayerCount { get; private set; } = 1;

    /// <summary>
    /// Get the health multiplier for the given number of connected players: 1 + percent/100 * (players - 1).
    /// </summary>
    /// <param name="playerCount">The number of connected players, including the local player.</param>
    /// <param name="percentPerExtraPlayer">Extra health in percent for each player beyond the first.</param>
    public static float GetMultiplier(int playerCount, byte percentPerExtraPlayer) {
        if (playerCount <= 1 || percentPerExtraPlayer == 0) {
            return 1f;
        }

        return 1f + percentPerExtraPlayer / 100f * (playerCount - 1);
    }

    /// <summary>
    /// Recompute the current multiplier.
    /// </summary>
    /// <returns>True if the multiplier changed.</returns>
    public static bool Update(int playerCount, byte percentPerExtraPlayer) {
        var multiplier = GetMultiplier(playerCount, percentPerExtraPlayer);
        var changed = System.Math.Abs(multiplier - CurrentMultiplier) > Epsilon;

        CurrentMultiplier = multiplier;
        CurrentPlayerCount = System.Math.Max(1, playerCount);

        return changed;
    }

    /// <summary>
    /// Reset the multiplier to 1 (e.g. after disconnecting).
    /// </summary>
    public static void Reset() {
        CurrentMultiplier = 1f;
        CurrentPlayerCount = 1;
    }

    /// <summary>
    /// Scale the given damage by the multiplier, carrying the fractional rest over to the next hit so the total damage
    /// over a fight is exactly the vanilla damage divided by the multiplier.
    /// </summary>
    /// <param name="damage">The damage that would be subtracted from the HP.</param>
    /// <param name="multiplier">The health multiplier.</param>
    /// <param name="remainder">The carried fractional damage of this enemy (in [0, 1)).</param>
    /// <returns>The damage to actually subtract (may be 0 for a small hit).</returns>
    public static int ScaleDamage(int damage, float multiplier, ref float remainder) {
        if (damage <= 0) {
            return damage;
        }

        if (multiplier <= 1f + Epsilon) {
            remainder = 0f;
            return damage;
        }

        var exact = damage / multiplier + remainder;
        var scaled = (int) System.Math.Floor(exact + Epsilon);
        remainder = System.Math.Max(0f, exact - scaled);

        return scaled;
    }

    /// <summary>
    /// The effective health (in vanilla damage points) of an enemy with the given HP and multiplier, for logging.
    /// </summary>
    public static int GetEffectiveHp(int hp, float multiplier) {
        if (hp <= 0) {
            return hp;
        }

        return (int) System.Math.Ceiling(hp * System.Math.Max(1f, multiplier) - Epsilon);
    }
}
