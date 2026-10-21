using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hkmp.Game.Client.Save;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Grub jar synchronisation: whoever breaks a jar must send it (not only the scene host) and the receiver breaks the
/// jar live in its scene.
/// </summary>
public class GrubSyncTests {
    private static readonly JsonElement Root = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-data.json")))
        .RootElement;

    [Theory]
    [InlineData("grubsCollected")]
    [InlineData("scenesGrubRescued")]
    public void GrubCounters_AreSentByWhoeverRescues(string field) {
        var props = Root.GetProperty("playerData").GetProperty(field);
        Assert.True(props.GetProperty("IgnoreSceneHost").GetBoolean());
    }

    [Fact]
    public void EveryGrubJar_IsSentByWhoeverBreaksIt() {
        var jars = Root.GetProperty("persistentBoolItems").EnumerateArray()
            .Where(i => GrubJarRules.IsGrubJar(i.GetProperty("Key").GetProperty("id").GetString()))
            .ToList();

        // 44 jars + 4 mimic jars
        Assert.Equal(48, jars.Count);
        Assert.All(jars, jar => {
            var value = jar.GetProperty("Value");
            Assert.True(value.GetProperty("Sync").GetBoolean());
            Assert.True(value.TryGetProperty("IgnoreSceneHost", out var ignore) && ignore.GetBoolean(),
                jar.GetProperty("Key").ToString());
        });
    }

    [Theory]
    [InlineData("Grub Bottle", true)]
    [InlineData("Grub Bottle (1)", true)]
    [InlineData("Grub Mimic Bottle (2)", true)]
    [InlineData("Grub Mimic", false)]
    [InlineData("Grub Scene", false)]
    [InlineData("Shiny Item Grubsong", false)]
    [InlineData(null, false)]
    public void IsGrubJar_MatchesOnlyJars(string id, bool expected) => Assert.Equal(expected, GrubJarRules.IsGrubJar(id));

    [Theory]
    [InlineData("Idle", true)]
    [InlineData("Return Pause", true)]
    [InlineData("Shatter", false)]
    [InlineData("Destroy Self", false)]
    [InlineData("Set Map?", false)]
    [InlineData("Set", false)]
    [InlineData("Activate", false)]
    [InlineData("Init", false)]
    public void RemoteBreak_OnlyShattersAJarThatWaitsForAHit(string state, bool expected) =>
        Assert.Equal(expected, GrubJarRules.ShouldShatterRemoteBreak(state));
}
