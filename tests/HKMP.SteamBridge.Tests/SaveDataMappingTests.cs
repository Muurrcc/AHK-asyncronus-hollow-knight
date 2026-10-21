using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Checks on the embedded save-data.json (which fields are synced and how).
/// </summary>
public class SaveDataMappingTests {
    private static readonly JsonElement Root = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-data.json")))
        .RootElement;

    [Theory]
    [InlineData("grubsCollected")]
    [InlineData("scenesGrubRescued")]
    [InlineData("grubRewards")]
    [InlineData("finalGrubRewardCollected")]
    [InlineData("fatGrubKing")]
    [InlineData("hasPinGrub")]
    public void GrubPlayerData_IsPerPlayer(string field) {
        var props = Root.GetProperty("playerData").GetProperty(field);
        Assert.Equal("Player", props.GetProperty("SyncType").GetString());
        Assert.False(props.TryGetProperty("Additive", out var additive) && additive.GetBoolean());
    }

    [Fact]
    public void GrubJars_ArePerPlayer() {
        var jars = Root.GetProperty("persistentBoolItems").EnumerateArray()
            .Where(i => i.GetProperty("Key").GetProperty("id").GetString().Contains("Grub") &&
                        i.GetProperty("Key").GetProperty("id").GetString().Contains("Bottle"))
            .ToList();

        Assert.NotEmpty(jars);
        Assert.All(jars, jar => Assert.Equal("Player", jar.GetProperty("Value").GetProperty("SyncType").GetString()));
    }

    [Fact]
    public void GrubSceneListStaysAStringList() {
        Assert.Contains("scenesGrubRescued", Root.GetProperty("stringListVariables").EnumerateArray().Select(e => e.GetString()));
    }
}
