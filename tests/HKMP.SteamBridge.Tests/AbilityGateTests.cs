using System;
using System.IO;
using System.Text.Json;
using Hkmp.Game.Client.Save;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Shared abilities and their gate flags (hasDash -> canDash): a client must only be able to dash when some player
/// of the server unlocked the Mothwing Cloak. Bug: the full world state sent on join contains hasDash=false and the
/// client opened canDash for any received hasDash, so every client could dash from the start.
/// </summary>
public class AbilityGateTests {
    private static readonly JsonElement PlayerData = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-data.json")))
        .RootElement.GetProperty("playerData");

    [Fact]
    public void ReceivingAbilityNotUnlockedKeepsGateClosed() {
        Assert.False(AbilityGateRules.GateAfterAbilityUpdate(false));
    }

    [Fact]
    public void ReceivingUnlockedAbilityOpensGate() {
        Assert.True(AbilityGateRules.GateAfterAbilityUpdate(true));
    }

    [Theory]
    [InlineData("hasDash", "canDash")]
    [InlineData("hasWalljump", "canWallJump")]
    [InlineData("hasShadowDash", "canShadowDash")]
    [InlineData("hasSuperDash", "canSuperDash")]
    public void AbilityHasGate(string ability, string gate) {
        Assert.True(AbilityGateRules.TryGetGate(ability, out var found));
        Assert.Equal(gate, found);
    }

    [Theory]
    [InlineData("hasDoubleJump")]
    [InlineData("hasAcidArmour")]
    [InlineData("hasDreamNail")]
    [InlineData("canDash")]
    [InlineData(null)]
    public void OtherFieldsHaveNoGate(string name) {
        Assert.False(AbilityGateRules.TryGetGate(name, out var gate));
        Assert.Null(gate);
    }

    [Theory]
    // Stale local save of an older version: gate open without the ability -> closed
    [InlineData(false, true, false)]
    // Legit unlock is never taken away
    [InlineData(true, true, true)]
    // Never opened by the repair
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void RepairOnlyClosesGatesWithoutAbility(bool hasAbility, bool gate, bool expected) {
        Assert.Equal(expected, AbilityGateRules.RepairGate(hasAbility, gate));
    }

    [Fact]
    public void JoinWithNobodyHavingDashThenSomebodyUnlocksIt() {
        // Join: full world state with hasDash=false (the old code opened the gate here)
        var canDash = AbilityGateRules.GateAfterAbilityUpdate(false);
        Assert.False(canDash);

        // Another player picks up the Mothwing Cloak: the shared hasDash=true arrives live
        canDash = AbilityGateRules.GateAfterAbilityUpdate(true);
        Assert.True(canDash);

        // Later scene changes repair nothing away
        Assert.True(AbilityGateRules.RepairGate(true, canDash));
    }

    [Fact]
    public void AbilitiesAreSharedAndGatesAreNeverNetworked() {
        foreach (var pair in AbilityGateRules.GateByAbility) {
            var props = PlayerData.GetProperty(pair.Key);
            Assert.True(props.GetProperty("Sync").GetBoolean(), pair.Key);
            Assert.Equal("Server", props.GetProperty("SyncType").GetString());

            // The gate is derived locally from the shared flag; syncing it would bypass the rule
            if (PlayerData.TryGetProperty(pair.Value, out var gateProps)) {
                Assert.False(gateProps.GetProperty("Sync").GetBoolean(), pair.Value);
            }
        }
    }
}
