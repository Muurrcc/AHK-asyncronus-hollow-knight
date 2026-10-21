using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hkmp.Game.Client.Save;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Shared progress between players (masks, soul vessels, spells, abilities, story) versus the state that stays
/// individual per character: checks save-data.json against <see cref="SharedProgressRules"/> and the merge and
/// live-apply rules.
/// </summary>
public class SharedProgressTests {
    private static readonly JsonElement Root = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-data.json")))
        .RootElement;

    private static JsonElement PlayerData => Root.GetProperty("playerData");

    private static JsonElement Props(string field) => PlayerData.GetProperty(field);

    private static string SyncType(string field) => Props(field).GetProperty("SyncType").GetString();

    private static bool Flag(JsonElement props, string name) =>
        props.TryGetProperty(name, out var value) && value.GetBoolean();

    [Theory]
    // 1) masks and mask shards
    [InlineData("maxHealthBase")]
    [InlineData("heartPieces")]
    [InlineData("heartPieceCollected")]
    [InlineData("heartPieceMax")]
    // 2) soul vessels
    [InlineData("MPReserveMax")]
    [InlineData("vesselFragments")]
    [InlineData("vesselFragmentCollected")]
    [InlineData("vesselFragmentMax")]
    // 3) spells
    [InlineData("hasSpell")]
    [InlineData("fireballLevel")]
    [InlineData("quakeLevel")]
    [InlineData("screamLevel")]
    // 4) story, abilities, maps, zones
    [InlineData("hasDreamNail")]
    [InlineData("dreamNailUpgraded")]
    [InlineData("hasDash")]
    [InlineData("hasWalljump")]
    [InlineData("hasDoubleJump")]
    [InlineData("hasSuperDash")]
    [InlineData("hasShadowDash")]
    [InlineData("hasKingsBrand")]
    [InlineData("mapCrossroads")]
    [InlineData("visitedGreenpath")]
    [InlineData("metElderbug")]
    [InlineData("xunRewardGiven")]
    public void RequestedProgress_IsShared(string field) {
        Assert.Contains(field, SharedProgressRules.SharedProgressFields);
        var props = Props(field);
        Assert.True(props.GetProperty("Sync").GetBoolean());
        Assert.Equal("Server", SyncType(field));
        // Any player may obtain it, not only the scene host
        Assert.True(Flag(props, "IgnoreSceneHost"));
    }

    [Theory]
    [InlineData("geo")]
    [InlineData("geoPool")]
    [InlineData("equippedCharm_1")]
    [InlineData("equippedCharms")]
    [InlineData("respawnScene")]
    [InlineData("respawnMarkerName")]
    [InlineData("shadeScene")]
    [InlineData("dreamGateScene")]
    [InlineData("nailDamage")]
    [InlineData("soulLimited")]
    public void PersonalState_StaysPerPlayer(string field) {
        Assert.Contains(field, SharedProgressRules.PlayerSpecificDataFields);
        Assert.Equal("Player", SyncType(field));
    }

    [Fact]
    public void EverySharedField_IsServerInJson() {
        var wrong = SharedProgressRules.SharedProgressFields
            .Where(f => !PlayerData.TryGetProperty(f, out var p) ||
                        !p.GetProperty("Sync").GetBoolean() ||
                        p.GetProperty("SyncType").GetString() != "Server" ||
                        !Flag(p, "IgnoreSceneHost") ||
                        Flag(p, "Additive") != SharedProgressRules.AdditiveSharedFields.Contains(f))
            .ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void EveryPlayerSpecificField_IsPlayerInJson() {
        var wrong = SharedProgressRules.PlayerSpecificDataFields
            .Where(f => PlayerData.TryGetProperty(f, out var p) && p.GetProperty("Sync").GetBoolean() &&
                        (p.GetProperty("SyncType").GetString() != "Player" || Flag(p, "Additive")))
            .ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void LocalOnlyCombatState_IsNeverSynced() {
        Assert.Equal(16, SharedProgressRules.LocalOnlyPlayerDataFields.Count);
        var synced = SharedProgressRules.LocalOnlyPlayerDataFields
            .Where(f => PlayerData.TryGetProperty(f, out var p) && p.GetProperty("Sync").GetBoolean())
            .ToList();
        Assert.Empty(synced);
    }

    [Fact]
    public void PlayerSpecificList_OnlyLostTheSharedMaxValues() {
        // Phase 3 had 73 per-player fields; only the masks and soul vessels left the list
        Assert.Equal(69, SharedProgressRules.PlayerSpecificDataFields.Count);
        foreach (var field in new[] { "maxHealthBase", "heartPieces", "MPReserveMax", "vesselFragments" }) {
            Assert.DoesNotContain(field, SharedProgressRules.PlayerSpecificDataFields);
        }
    }

    [Fact]
    public void Lists_AreDisjointAndWithoutDuplicates() {
        var categories = new[] {
            SharedProgressRules.MaskFields, SharedProgressRules.SoulVesselFields, SharedProgressRules.SpellFields,
            SharedProgressRules.AbilityFields, SharedProgressRules.KeyItemFields,
            SharedProgressRules.MapAndZoneFields, SharedProgressRules.StoryFields
        };
        var all = categories.SelectMany(c => c).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Empty(SharedProgressRules.SharedProgressFields.Intersect(SharedProgressRules.PlayerSpecificDataFields));
        Assert.Empty(SharedProgressRules.SharedProgressFields.Intersect(SharedProgressRules.LocalOnlyPlayerDataFields));
        Assert.Empty(SharedProgressRules.PlayerSpecificDataFields.Intersect(SharedProgressRules.LocalOnlyPlayerDataFields));
        Assert.Subset(SharedProgressRules.SharedProgressFields, SharedProgressRules.MonotonicIntFields);
        Assert.Subset(SharedProgressRules.SharedProgressFields, SharedProgressRules.AdditiveSharedFields);
        Assert.Subset(SharedProgressRules.SharedProgressFields, SharedProgressRules.ConsumableFlags);
    }

    [Fact]
    public void GrubFields_AreNotPartOfThisChange() {
        // Grubs are synchronised separately (grubsCollected, scenesGrubRescued, jars, rewards)
        Assert.DoesNotContain(SharedProgressRules.SharedProgressFields,
            f => f.StartsWith("grub", StringComparison.OrdinalIgnoreCase) || f.Contains("Grub"));
        Assert.DoesNotContain("Heart Piece@Crossroads_38", SharedProgressRules.SharedPickups);
    }

    [Fact]
    public void ShardVesselAndSpellPickups_AreShared() {
        var items = Root.GetProperty("persistentBoolItems").EnumerateArray()
            .ToDictionary(
                i => $"{i.GetProperty("Key").GetProperty("id").GetString()}@{i.GetProperty("Key").GetProperty("sceneName").GetString()}",
                i => i.GetProperty("Value")
            );
        foreach (var pickup in SharedProgressRules.SharedPickups) {
            Assert.True(items.ContainsKey(pickup), $"missing pickup {pickup}");
            Assert.Equal("Server", items[pickup].GetProperty("SyncType").GetString());
            Assert.True(Flag(items[pickup], "IgnoreSceneHost"), pickup);
        }

        // Every mask shard and vessel fragment pickup is shared (except the grub reward shard)
        var perPlayerShards = items
            .Where(i => (i.Key.StartsWith("Heart Piece@") || i.Key.StartsWith("Vessel Fragment@")) &&
                        i.Key != "Heart Piece@Crossroads_38" &&
                        i.Value.GetProperty("SyncType").GetString() != "Server")
            .Select(i => i.Key)
            .ToList();
        Assert.Empty(perPlayerShards);
    }

    [Fact]
    public void MergeValue_OrFlagsMaxLevelsUnionLists() {
        Assert.Equal(true, SharedProgressRules.MergeValue("hasDash", false, true));
        Assert.Equal(true, SharedProgressRules.MergeValue("hasDash", true, false));
        Assert.Equal(false, SharedProgressRules.MergeValue("hasDash", false, false));
        Assert.Equal(2, SharedProgressRules.MergeValue("fireballLevel", 1, 2));
        Assert.Equal(2, SharedProgressRules.MergeValue("fireballLevel", 2, 1));
        Assert.Equal(new List<string> { "a", "b", "c" },
            SharedProgressRules.MergeValue("unlockedBossScenes", new List<string> { "a", "b" }, new List<string> { "b", "c" }));
        Assert.Equal(1, SharedProgressRules.MergeValue("quakeLevel", null, 1));
        Assert.Equal(1, SharedProgressRules.MergeValue("quakeLevel", 1, null));
    }

    [Fact]
    public void MergeValue_UsedKeyStaysUsed() {
        // A key that was used up on the server must not come back from an old player save
        Assert.Equal(false, SharedProgressRules.MergeValue("hasWhiteKey", false, true));
        Assert.Equal(true, SharedProgressRules.MergeValue("usedWhiteKey", false, true));
    }

    [Fact]
    public void MergePair_KeepsMasksAndShardsTogether() {
        Func<int, int, int> masks = SharedProgressRules.MaskShardTotal;
        // 6 masks beat 5 masks + 3 shards; the result is never 6 masks + 3 shards
        Assert.Equal((6, 0), SharedProgressRules.MergePair((5, 3), (6, 0), masks));
        Assert.Equal((6, 0), SharedProgressRules.MergePair((6, 0), (5, 3), masks));
        Assert.Equal((6, 1), SharedProgressRules.MergePair((6, 0), (6, 1), masks));
        // Tie keeps the server pair
        Assert.Equal((5, 2), SharedProgressRules.MergePair((5, 2), (5, 2), masks));
    }

    [Fact]
    public void VesselTotals_CountFragments() {
        Assert.Equal(0, SharedProgressRules.VesselFragmentTotal(0, 0));
        Assert.Equal(4, SharedProgressRules.VesselFragmentTotal(33, 1));
        Assert.Equal(9, SharedProgressRules.VesselFragmentTotal(99, 0));
        Assert.Equal((33, 0),
            SharedProgressRules.MergePair((0, 2), (33, 0), SharedProgressRules.VesselFragmentTotal));
    }

    [Theory]
    [InlineData(5, 5, 6, 6)] // full health: the new mask is full too
    [InlineData(3, 5, 6, 3)] // damaged: keep the damage, the new mask is empty
    [InlineData(6, 6, 5, 5)] // correction downwards: clamp
    [InlineData(4, 6, 5, 4)]
    [InlineData(0, 5, 6, 0)] // dead stays dead
    public void HealthAfterMaxHealthChange_DoesNotHealDamage(int health, int oldMax, int newMax, int expected) {
        Assert.Equal(expected, SharedProgressRules.HealthAfterMaxHealthChange(health, oldMax, newMax));
    }

    [Theory]
    [InlineData(20, 66, 20)]
    [InlineData(66, 33, 33)]
    [InlineData(-5, 33, 0)]
    public void ReserveAfterMaxChange_ClampsToNewMax(int reserve, int newMax, int expected) {
        Assert.Equal(expected, SharedProgressRules.ReserveAfterMaxChange(reserve, newMax));
    }

    [Fact]
    public void Regression_OnlyForLevelsThatNeverDecrease() {
        Assert.True(SharedProgressRules.IsRegression("maxHealthBase", 7, 6));
        Assert.True(SharedProgressRules.IsRegression("fireballLevel", 2, 1));
        Assert.False(SharedProgressRules.IsRegression("maxHealthBase", 7, 8));
        // Shards wrap around (3 -> 0 when a mask completes)
        Assert.False(SharedProgressRules.IsRegression("heartPieces", 3, 0));
    }
}
