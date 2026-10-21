using Hkmp.Game.Client;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

public class TeleportTargetSelectorTests {
    private static readonly (ushort Id, string Name)[] Players = [(3, "Alice"), (1, "Bob"), (7, "Alberto")];

    [Fact]
    public void FindByName_ExactIgnoresCase() {
        Assert.True(TeleportTargetSelector.TryFindByName(Players, "alice", out var id));
        Assert.Equal(3, id);
    }

    [Fact]
    public void FindByName_UniquePrefix() {
        Assert.True(TeleportTargetSelector.TryFindByName(Players, "Bo", out var id));
        Assert.Equal(1, id);
    }

    [Fact]
    public void FindByName_AmbiguousPrefixOrUnknownOrEmptyFails() {
        Assert.False(TeleportTargetSelector.TryFindByName(Players, "Al", out _));
        Assert.False(TeleportTargetSelector.TryFindByName(Players, "Zed", out _));
        Assert.False(TeleportTargetSelector.TryFindByName(Players, " ", out _));
    }

    [Fact]
    public void PickNext_CyclesByIdAndWraps() {
        Assert.True(TeleportTargetSelector.TryPickNext(Players, null, out var first));
        Assert.Equal(1, first.Id);
        Assert.True(TeleportTargetSelector.TryPickNext(Players, first.Id, out var second));
        Assert.Equal(3, second.Id);
        Assert.True(TeleportTargetSelector.TryPickNext(Players, second.Id, out var third));
        Assert.Equal(7, third.Id);
        Assert.True(TeleportTargetSelector.TryPickNext(Players, third.Id, out var wrapped));
        Assert.Equal(1, wrapped.Id);
    }

    [Fact]
    public void PickNext_NoPlayers() {
        Assert.False(TeleportTargetSelector.TryPickNext([], null, out _));
    }
}
