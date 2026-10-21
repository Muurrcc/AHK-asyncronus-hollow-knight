using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Tests for the enemy health scaling with the number of players.
/// </summary>
public class EnemyHealthScalingTests {
    [Theory]
    [InlineData(0, 50, 1f)]
    [InlineData(1, 50, 1f)]
    [InlineData(2, 50, 1.5f)]
    [InlineData(3, 50, 2f)]
    [InlineData(4, 50, 2.5f)]
    [InlineData(2, 0, 1f)]
    [InlineData(3, 100, 3f)]
    [InlineData(2, 25, 1.25f)]
    public void MultiplierIsOnePlusPercentPerExtraPlayer(int players, byte percent, float expected) {
        Assert.Equal(expected, EnemyHealthScaling.GetMultiplier(players, percent), 4);
    }

    /// <summary>
    /// Number of hits of the given damage needed to kill an enemy with the given HP.
    /// </summary>
    private static int HitsToKill(int hp, int damage, float multiplier) {
        var remainder = 0f;
        var hits = 0;
        while (hp > 0) {
            hp -= EnemyHealthScaling.ScaleDamage(damage, multiplier, ref remainder);
            hits++;
            Assert.True(hits < 10000);
        }

        return hits;
    }

    [Theory]
    [InlineData(30, 5, 1f, 6)]
    [InlineData(30, 5, 1.5f, 9)]
    [InlineData(30, 5, 2f, 12)]
    [InlineData(100, 21, 1.5f, 8)] // effective 150 HP: 7 hits = 147, 8 hits kill
    [InlineData(1, 1, 1.5f, 2)]
    public void HitsToKillMatchScaledHealth(int hp, int damage, float multiplier, int expectedHits) {
        Assert.Equal(expectedHits, HitsToKill(hp, damage, multiplier));
    }

    [Fact]
    public void TotalDamageEqualsVanillaDamageDividedByMultiplier() {
        var remainder = 0f;
        var total = 0;
        for (var i = 0; i < 300; i++) {
            total += EnemyHealthScaling.ScaleDamage(13, 1.5f, ref remainder);
        }

        // 300 * 13 / 1.5 = 2600 exactly
        Assert.Equal(2600, total);
        Assert.InRange(remainder, 0f, 1f);
    }

    [Fact]
    public void NoScalingWithOnePlayerAndRemainderIsReset() {
        var remainder = 0.7f;
        Assert.Equal(7, EnemyHealthScaling.ScaleDamage(7, 1f, ref remainder));
        Assert.Equal(0f, remainder);
    }

    [Fact]
    public void NonPositiveDamageIsUntouched() {
        var remainder = 0.5f;
        Assert.Equal(0, EnemyHealthScaling.ScaleDamage(0, 2f, ref remainder));
        Assert.Equal(-3, EnemyHealthScaling.ScaleDamage(-3, 2f, ref remainder));
        Assert.Equal(0.5f, remainder);
    }

    [Fact]
    public void PartialHealthKeepsItsFractionWhenAPlayerLeaves() {
        // 2 players: 40 HP enemy has 60 effective HP. Deal 30 effective damage (half).
        var hp = 40;
        var remainder = 0f;
        for (var i = 0; i < 6; i++) {
            hp -= EnemyHealthScaling.ScaleDamage(5, 1.5f, ref remainder);
        }

        Assert.Equal(20, hp);
        Assert.Equal(30, EnemyHealthScaling.GetEffectiveHp(hp, 1.5f));

        // The second player leaves: the stored HP is unchanged (still half of the health) and now takes vanilla damage
        Assert.Equal(20, EnemyHealthScaling.GetEffectiveHp(hp, 1f));
        Assert.Equal(4, HitsToKill(hp, 5, 1f));
    }

    [Fact]
    public void UpdateReportsChangesAndResetRestoresOne() {
        EnemyHealthScaling.Reset();
        Assert.True(EnemyHealthScaling.Update(2, 50));
        Assert.Equal(1.5f, EnemyHealthScaling.CurrentMultiplier, 4);
        Assert.Equal(2, EnemyHealthScaling.CurrentPlayerCount);
        Assert.False(EnemyHealthScaling.Update(2, 50));
        Assert.True(EnemyHealthScaling.Update(3, 50));
        Assert.Equal(2f, EnemyHealthScaling.CurrentMultiplier, 4);

        EnemyHealthScaling.Reset();
        Assert.Equal(1f, EnemyHealthScaling.CurrentMultiplier);
        Assert.Equal(1, EnemyHealthScaling.CurrentPlayerCount);
    }
}

/// <summary>
/// Tests for the rules that make arenas wait until all players are inside.
/// </summary>
public class ArenaEntryRulesTests {
    /// <summary>
    /// Simplified "Battle Control" FSM of a wave room, as found in the game files.
    /// </summary>
    private static readonly Dictionary<string, string[]> BattleControl = new() {
        ["Init"] = new[] { "Detect", "Activate" },
        ["Detect"] = new[] { "Wave 1" },
        ["Wave 1"] = new[] { "Wave Pause" },
        ["Wave Pause"] = new[] { "Wave 2" },
        ["Wave 2"] = new[] { "End Wait" },
        ["End Wait"] = new[] { "End" },
        ["End"] = Array.Empty<string>(),
        ["Activate"] = Array.Empty<string>()
    };

    private static IEnumerable<string> Next(Dictionary<string, string[]> fsm, string state) =>
        fsm.TryGetValue(state, out var next) ? next : Array.Empty<string>();

    [Fact]
    public void WaveRoomTriggerLeadsToGateClose() {
        Assert.True(ArenaEntryRules.LeadsToGateClose(
            "Wave 1",
            s => Next(BattleControl, s),
            s => s == "Wave 1"
        ));
    }

    [Fact]
    public void GateCloseAfterAShortIntroIsFound() {
        var fsm = new Dictionary<string, string[]> {
            ["Wake"] = new[] { "Roar" },
            ["Roar"] = new[] { "Roar End" },
            ["Roar End"] = new[] { "Close Gates" },
            ["Close Gates"] = Array.Empty<string>()
        };

        Assert.True(ArenaEntryRules.LeadsToGateClose("Wake", s => Next(fsm, s), s => s == "Close Gates"));
    }

    [Fact]
    public void GateCloseTooFarAwayIsIgnored() {
        var fsm = new Dictionary<string, string[]>();
        for (var i = 0; i < 10; i++) {
            fsm[$"S{i}"] = new[] { $"S{i + 1}" };
        }

        Assert.False(ArenaEntryRules.LeadsToGateClose("S0", s => Next(fsm, s), s => s == "S9"));
        Assert.True(ArenaEntryRules.LeadsToGateClose("S0", s => Next(fsm, s), s => s == "S4"));
        Assert.False(ArenaEntryRules.LeadsToGateClose("S0", s => Next(fsm, s), s => s == "S5"));
    }

    [Fact]
    public void TriggersThatDoNotCloseGatesAreIgnored() {
        Assert.False(ArenaEntryRules.LeadsToGateClose("Activate", s => Next(BattleControl, s), s => s == "Wave 1"));
        Assert.False(ArenaEntryRules.LeadsToGateClose(null, s => Next(BattleControl, s), _ => true));
        Assert.False(ArenaEntryRules.LeadsToGateClose("", s => Next(BattleControl, s), _ => true));
    }

    [Fact]
    public void CyclesTerminate() {
        var fsm = new Dictionary<string, string[]> {
            ["A"] = new[] { "B" },
            ["B"] = new[] { "A", "B" }
        };

        Assert.False(ArenaEntryRules.LeadsToGateClose("A", s => Next(fsm, s), _ => false, 100));
    }

    [Theory]
    [InlineData(true, new[] { true }, false)]
    [InlineData(true, new[] { true, true }, false)]
    [InlineData(true, new[] { true, false }, true)]
    [InlineData(true, new[] { false, true, true }, true)]
    [InlineData(false, new[] { true, false }, false)]
    [InlineData(true, new bool[0], false)]
    public void HoldUntilEveryPlayerIsInside(bool setting, bool[] inside, bool expectedHold) {
        Assert.Equal(expectedHold, ArenaEntryRules.ShouldHold(setting, inside));
    }

    [Fact]
    public void HoldIsReleasedWhenTheLastPlayerEnters() {
        var inside = new List<bool> { true, false };
        Assert.True(ArenaEntryRules.ShouldHold(true, inside));
        inside[1] = true;
        Assert.False(ArenaEntryRules.ShouldHold(true, inside.ToArray()));
    }
}
