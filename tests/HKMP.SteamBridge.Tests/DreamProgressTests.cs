using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Save;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Dream warriors and dream bosses: their progress must reach every player whoever stores it (not only the scene
/// host), must never go back, and the entity of a warrior that the scene keeps asleep must not start awake
/// (<see cref="EntityActivationRules"/>).
/// </summary>
public class DreamProgressTests {
    private static readonly JsonElement PlayerData = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-data.json")))
        .RootElement.GetProperty("playerData");

    private static bool Flag(JsonElement props, string name) =>
        props.TryGetProperty(name, out var value) && value.GetBoolean();

    [Theory]
    [InlineData("xeroDefeated")]
    [InlineData("aladarSlugDefeated")]
    [InlineData("elderHuDefeated")]
    [InlineData("mumCaterpillarDefeated")]
    [InlineData("noEyesDefeated")]
    [InlineData("markothDefeated")]
    [InlineData("galienDefeated")]
    [InlineData("falseKnightDreamDefeated")]
    [InlineData("mageLordDreamDefeated")]
    [InlineData("infectedKnightDreamDefeated")]
    [InlineData("whiteDefenderDefeated")]
    [InlineData("greyPrinceDefeated")]
    [InlineData("xeroPinned")]
    public void DreamBossProgress_IsSharedFromAnyPlayer(string field) {
        Assert.Contains(field, SharedProgressRules.SharedProgressFields);
        var props = PlayerData.GetProperty(field);
        Assert.True(props.GetProperty("Sync").GetBoolean());
        Assert.Equal("Server", props.GetProperty("SyncType").GetString());
        // The essence of a warrior is collected by whoever dream nails it, the scene host or not
        Assert.True(Flag(props, "IgnoreSceneHost"), field);
        Assert.False(Flag(props, "Additive"), field);
    }

    [Fact]
    public void DreamBossLists_AreDisjointFromPersonalState() {
        var all = SharedProgressRules.DreamWarriorFields.Concat(SharedProgressRules.DreamBossFields).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Empty(all.Intersect(SharedProgressRules.PlayerSpecificDataFields));
        Assert.Empty(all.Intersect(SharedProgressRules.LocalOnlyPlayerDataFields));
        Assert.Subset(SharedProgressRules.SharedProgressFields, SharedProgressRules.MonotonicBoolFields);
        // The per player essence of the dream bosses stays per player
        foreach (var field in new[] { "falseKnightOrbsCollected", "dreamOrbs", "whiteDefenderDefeats" }) {
            Assert.DoesNotContain(field, SharedProgressRules.SharedProgressFields);
        }
    }

    [Theory]
    [InlineData("xeroDefeated", 2, 1, true)] // a stale "defeated" must not bring the collected warrior back
    [InlineData("xeroDefeated", 1, 0, true)] // nor the defeated one alive
    [InlineData("galienDefeated", 2, 0, true)]
    [InlineData("xeroDefeated", 1, 2, false)]
    [InlineData("xeroDefeated", 0, 1, false)]
    public void WarriorState_NeverGoesBack(string field, int stored, int incoming, bool regression) {
        Assert.Equal(regression, SharedProgressRules.IsRegression(field, stored, incoming));
    }

    [Theory]
    [InlineData("falseKnightDreamDefeated", true, false, true)]
    [InlineData("greyPrinceDefeated", true, false, true)]
    [InlineData("falseKnightDreamDefeated", false, true, false)]
    [InlineData("falseKnightDreamDefeated", true, true, false)]
    [InlineData("hasSlykey", true, false, false)] // consumable keys may turn off
    [InlineData("xeroPinned", true, false, false)]
    public void DreamBossDefeat_NeverTurnsOff(string field, bool stored, bool incoming, bool regression) {
        Assert.Equal(regression, SharedProgressRules.IsRegression(field, stored, incoming));
    }

    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(1, 0, 1)]
    public void WarriorState_MergesToTheMostAdvanced(int server, int player, int expected) {
        Assert.Equal(expected, SharedProgressRules.MergeValue("markothDefeated", server, player));
    }

    [Theory]
    // The Unity 6 AwakenChildren woke a warrior that the scene keeps inactive: it is inactive
    [InlineData(true, false, false, false)]
    // Its Start already ran: whatever it is now is real (e.g. the warrior was activated for its fight)
    [InlineData(true, false, true, true)]
    // Was active in the scene anyway
    [InlineData(true, true, false, true)]
    // No AwakenChildren targets it
    [InlineData(true, null, false, true)]
    [InlineData(false, null, false, false)]
    public void EntityOriginalActive_IgnoresTemporaryAwakening(bool activeSelf, bool? recorded, bool started,
        bool expected) {
        Assert.Equal(expected, EntityActivationRules.OriginalActive(activeSelf, recorded, started));
    }

    [Fact]
    public void RecordedActive_FindsTheTargetByReference() {
        var warrior = new object();
        var sword = new object();
        var targets = new List<object> { sword, warrior };
        var recorded = new[] { true, false };
        Assert.False(EntityActivationRules.RecordedActive(targets, recorded, warrior));
        Assert.True(EntityActivationRules.RecordedActive(targets, recorded, sword));
        Assert.Null(EntityActivationRules.RecordedActive(targets, recorded, new object()));
        // Before its Awake there is nothing recorded yet
        Assert.Null(EntityActivationRules.RecordedActive(targets, null, warrior));
        Assert.Null(EntityActivationRules.RecordedActive<object>(null, recorded, warrior));
    }
}
