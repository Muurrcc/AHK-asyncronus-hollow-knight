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
    public void GrubProgress_IsSharedBetweenPlayers(string field) {
        var props = Root.GetProperty("playerData").GetProperty(field);
        Assert.Equal("Server", props.GetProperty("SyncType").GetString());
        Assert.True(props.GetProperty("Additive").GetBoolean());
    }

    [Fact]
    public void GrubJars_AreSharedBetweenPlayers() {
        var jars = Root.GetProperty("persistentBoolItems").EnumerateArray()
            .Where(i => i.GetProperty("Key").GetProperty("id").GetString().Contains("Grub") &&
                        i.GetProperty("Key").GetProperty("id").GetString().Contains("Bottle"))
            .ToList();

        Assert.NotEmpty(jars);
        Assert.All(jars, jar => Assert.Equal("Server", jar.GetProperty("Value").GetProperty("SyncType").GetString()));
    }

    [Fact]
    public void GrubSceneListStaysAStringList() {
        Assert.Contains("scenesGrubRescued", Root.GetProperty("stringListVariables").EnumerateArray().Select(e => e.GetString()));
    }
}
