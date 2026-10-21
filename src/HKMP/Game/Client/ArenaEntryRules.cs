using System;
using System.Collections.Generic;

namespace Hkmp.Game.Client;

/// <summary>
/// Pure logic (no game dependencies) behind <see cref="ArenaEntryGate"/>, covered by unit tests.
/// </summary>
internal static class ArenaEntryRules {
    /// <summary>
    /// The FSM event that battle scenes send to the battle gates to close them.
    /// </summary>
    public const string GateCloseEvent = "BG CLOSE";

    /// <summary>
    /// How many FSM transitions after the trigger are searched for a state that closes the gates. Wave rooms close
    /// them right away ("Detect" -START-> "Wave 1"), boss arenas sometimes after a short intro.
    /// </summary>
    public const int MaxSearchDepth = 4;

    /// <summary>
    /// Whether entering the given state (directly or through at most <paramref name="maxDepth"/> further transitions)
    /// leads to a state that closes the battle gates.
    /// </summary>
    /// <param name="startState">The state the trigger event transitions to, or null if there is no transition.</param>
    /// <param name="nextStates">The states reachable with one transition from a given state.</param>
    /// <param name="closesGates">Whether the actions of a given state close the gates.</param>
    /// <param name="maxDepth">The maximum number of transitions to follow after the start state.</param>
    public static bool LeadsToGateClose(
        string startState,
        Func<string, IEnumerable<string>> nextStates,
        Func<string, bool> closesGates,
        int maxDepth = MaxSearchDepth
    ) {
        if (string.IsNullOrEmpty(startState)) {
            return false;
        }

        var visited = new HashSet<string> { startState };
        var frontier = new List<string> { startState };

        for (var depth = 0; depth <= maxDepth && frontier.Count > 0; depth++) {
            var next = new List<string>();

            foreach (var state in frontier) {
                if (closesGates(state)) {
                    return true;
                }

                if (depth == maxDepth) {
                    continue;
                }

                foreach (var target in nextStates(state) ?? Array.Empty<string>()) {
                    if (!string.IsNullOrEmpty(target) && visited.Add(target)) {
                        next.Add(target);
                    }
                }
            }

            frontier = next;
        }

        return false;
    }

    /// <summary>
    /// Whether an arena start should be held back: the setting is enabled and at least one player of the scene is
    /// not inside the arena trigger yet.
    /// </summary>
    /// <param name="waitForAllPlayers">The value of the server setting.</param>
    /// <param name="playersInside">For each player in the scene (local and remote), whether it is inside.</param>
    public static bool ShouldHold(bool waitForAllPlayers, IEnumerable<bool> playersInside) {
        if (!waitForAllPlayers) {
            return false;
        }

        foreach (var inside in playersInside) {
            if (!inside) {
                return true;
            }
        }

        return false;
    }
}
