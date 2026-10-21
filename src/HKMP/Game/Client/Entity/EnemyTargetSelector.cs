using System;
using System.Collections.Generic;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// A player that an enemy can target: an ID and a position. Pure data (no Unity types) so the selection rules can be
/// unit tested.
/// </summary>
internal readonly struct EnemyTargetCandidate {
    /// <summary>
    /// The ID of the candidate. <see cref="EnemyTargetSelector.LocalPlayerId"/> for the local player (the scene host),
    /// the player ID for remote players.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The X coordinate of the candidate.
    /// </summary>
    public float X { get; }

    /// <summary>
    /// The Y coordinate of the candidate.
    /// </summary>
    public float Y { get; }

    public EnemyTargetCandidate(int id, float x, float y) {
        Id = id;
        X = x;
        Y = y;
    }
}

/// <summary>
/// Pure rules for choosing which player an enemy (simulated by the scene host) goes after: the nearest player, with
/// hysteresis so that an enemy between two (nearly) equidistant players does not oscillate between them.
/// <para>Rules, applied every evaluation:</para>
/// <list type="number">
/// <item>No candidates: no target (<see cref="NoTarget"/>).</item>
/// <item>No current target, or the current target is no longer a candidate (left the scene): the nearest candidate.
/// </item>
/// <item>Otherwise keep the current target unless the nearest other candidate is clearly closer: its distance is at
/// most <see cref="SwitchDistanceRatio"/> times the distance to the current target AND at least
/// <see cref="SwitchDistanceMargin"/> units closer, and the current target was held for at least
/// <see cref="MinHoldSeconds"/>.</item>
/// </list>
/// Ties in distance are broken by the lowest ID, so the result only depends on the inputs (deterministic).
/// </summary>
internal static class EnemyTargetSelector {
    /// <summary>
    /// The candidate ID of the local player (the scene host itself).
    /// </summary>
    public const int LocalPlayerId = -1;

    /// <summary>
    /// The value returned when there is no target.
    /// </summary>
    public const int NoTarget = int.MinValue;

    /// <summary>
    /// The other player must be at most this fraction of the current target's distance to take over (0.8 = 20% closer).
    /// </summary>
    public const float SwitchDistanceRatio = 0.8f;

    /// <summary>
    /// The other player must also be at least this many units closer than the current target. Avoids switching when
    /// both players are very close to the enemy, where 20% is only a few pixels.
    /// </summary>
    public const float SwitchDistanceMargin = 1.5f;

    /// <summary>
    /// Minimum time (in seconds) a target is kept after switching before switching again.
    /// </summary>
    public const float MinHoldSeconds = 1f;

    /// <summary>
    /// Get the nearest candidate to the given position. Ties are broken by the lowest ID.
    /// </summary>
    /// <returns>The ID of the nearest candidate, or <see cref="NoTarget"/> if there are no candidates.</returns>
    public static int GetNearest(float x, float y, IReadOnlyList<EnemyTargetCandidate> candidates) {
        var bestId = NoTarget;
        var bestDistance = float.MaxValue;

        if (candidates == null) {
            return NoTarget;
        }

        foreach (var candidate in candidates) {
            var distance = Distance(x, y, candidate);
            if (distance < bestDistance || (distance == bestDistance && candidate.Id < bestId)) {
                bestDistance = distance;
                bestId = candidate.Id;
            }
        }

        return bestId;
    }

    /// <summary>
    /// Choose the target of an enemy at the given position.
    /// </summary>
    /// <param name="currentId">The current target, or <see cref="NoTarget"/>.</param>
    /// <param name="secondsSinceSwitch">Seconds since the current target was chosen.</param>
    /// <param name="x">The X coordinate of the enemy.</param>
    /// <param name="y">The Y coordinate of the enemy.</param>
    /// <param name="candidates">The players that can be targeted.</param>
    /// <returns>The ID of the target, or <see cref="NoTarget"/> if there are no candidates.</returns>
    public static int Choose(
        int currentId,
        float secondsSinceSwitch,
        float x,
        float y,
        IReadOnlyList<EnemyTargetCandidate> candidates
    ) {
        var nearestId = GetNearest(x, y, candidates);
        if (nearestId == NoTarget || nearestId == currentId) {
            return nearestId;
        }

        if (!TryGet(candidates, currentId, out var current)) {
            return nearestId;
        }

        if (secondsSinceSwitch < MinHoldSeconds) {
            return currentId;
        }

        TryGet(candidates, nearestId, out var nearest);

        var currentDistance = Distance(x, y, current);
        var nearestDistance = Distance(x, y, nearest);

        if (nearestDistance <= currentDistance * SwitchDistanceRatio &&
            currentDistance - nearestDistance >= SwitchDistanceMargin) {
            return nearestId;
        }

        return currentId;
    }

    private static bool TryGet(IReadOnlyList<EnemyTargetCandidate> candidates, int id, out EnemyTargetCandidate result) {
        foreach (var candidate in candidates) {
            if (candidate.Id == id) {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static float Distance(float x, float y, EnemyTargetCandidate candidate) {
        var dx = candidate.X - x;
        var dy = candidate.Y - y;
        return (float) System.Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Per-enemy target state: the current target and when it was chosen. Wraps <see cref="EnemyTargetSelector.Choose"/>.
/// </summary>
internal class EnemyTargetTracker {
    /// <summary>
    /// The current target, or <see cref="EnemyTargetSelector.NoTarget"/>.
    /// </summary>
    public int CurrentId { get; private set; } = EnemyTargetSelector.NoTarget;

    /// <summary>
    /// The time (in seconds, caller's clock) at which the current target was chosen.
    /// </summary>
    public float SwitchTime { get; private set; }

    /// <summary>
    /// Re-evaluate the target.
    /// </summary>
    /// <param name="time">The current time in seconds (any monotonic clock).</param>
    /// <param name="x">The X coordinate of the enemy.</param>
    /// <param name="y">The Y coordinate of the enemy.</param>
    /// <param name="candidates">The players that can be targeted.</param>
    /// <returns>True if the target changed.</returns>
    public bool Update(float time, float x, float y, IReadOnlyList<EnemyTargetCandidate> candidates) {
        var newId = EnemyTargetSelector.Choose(CurrentId, time - SwitchTime, x, y, candidates);
        if (newId == CurrentId) {
            return false;
        }

        CurrentId = newId;
        SwitchTime = time;
        return true;
    }
}
