using System;
using System.Collections.Generic;
using System.Linq;

namespace Hkmp.Game.Client;

/// <summary>
/// Pure logic for choosing which remote player to teleport to (no game dependencies, covered by unit tests).
/// </summary>
internal static class TeleportTargetSelector {
    /// <summary>
    /// Find a player by name: exact (case-insensitive) match first, then a unique prefix match.
    /// </summary>
    /// <param name="players">The remote players as (ID, name) pairs.</param>
    /// <param name="name">The name typed by the user.</param>
    /// <param name="id">The ID of the found player.</param>
    /// <returns>True if exactly one player matched.</returns>
    public static bool TryFindByName(IEnumerable<(ushort Id, string Name)> players, string name, out ushort id) {
        id = 0;
        if (string.IsNullOrWhiteSpace(name)) {
            return false;
        }

        var list = players.ToList();
        var exact = list.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) {
            id = exact[0].Id;
            return true;
        }

        if (exact.Count > 1) {
            return false;
        }

        var prefix = list.Where(p => p.Name != null && p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (prefix.Count != 1) {
            return false;
        }

        id = prefix[0].Id;
        return true;
    }

    /// <summary>
    /// Pick the next player after <paramref name="lastId"/> (ordered by ID, wrapping around). Used by the button,
    /// which cycles through the other players.
    /// </summary>
    /// <param name="players">The remote players as (ID, name) pairs.</param>
    /// <param name="lastId">The ID that was picked last, or null.</param>
    /// <param name="picked">The picked player.</param>
    /// <returns>True if there is at least one player to pick.</returns>
    public static bool TryPickNext(
        IEnumerable<(ushort Id, string Name)> players,
        ushort? lastId,
        out (ushort Id, string Name) picked
    ) {
        var ordered = players.OrderBy(p => p.Id).ToList();
        if (ordered.Count == 0) {
            picked = default;
            return false;
        }

        picked = ordered[0];
        if (lastId.HasValue) {
            foreach (var candidate in ordered) {
                if (candidate.Id > lastId.Value) {
                    picked = candidate;
                    break;
                }
            }
        }

        return true;
    }
}
