using Hkmp.Game.Client;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

public class TeleportRulesTests {
    /// <summary>
    /// A normal playable state: in game, playing, not paused, hero with input.
    /// </summary>
    private static TeleportGameState Playing() => new() {
        HeroExists = true,
        StatePlaying = true,
        HeroAcceptingInput = true,
        InputHandlerAcceptingInput = true
    };

    [Fact]
    public void Classify_SameScene() {
        Assert.Equal(TeleportKind.SameScene, TeleportRules.Classify("Town", "Town", true));
    }

    [Fact]
    public void Classify_OtherLoadableScene() {
        Assert.Equal(TeleportKind.OtherScene, TeleportRules.Classify("Town", "Room_Ouiji", true));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Not_A_Scene", false)]
    public void Classify_InvalidScene(string target, bool loadable) {
        Assert.Equal(TeleportKind.InvalidScene, TeleportRules.Classify("Town", target, loadable));
    }

    [Fact]
    public void Playing_CanTeleportAndHasControl() {
        Assert.Null(TeleportRules.BlockReason(Playing()));
        Assert.True(TeleportRules.IsControlRestored(Playing()));
        Assert.False(TeleportRules.NeedsUnpause(Playing()));
    }

    [Fact]
    public void Paused_NeedsUnpauseAndBlocksUntilUnpaused() {
        var paused = Playing();
        paused.StatePlaying = false;
        paused.StatePaused = true;
        paused.IsPaused = true;
        Assert.True(TeleportRules.NeedsUnpause(paused));
        Assert.Equal("paused", TeleportRules.BlockReason(paused));
    }

    [Fact]
    public void HalfPaused_IsPausedFlagAloneNeedsUnpauseAndIsNotControl() {
        // The state the old pause-menu button left: menu hidden, gameState PLAYING, but GameManager.isPaused set
        var halfPaused = Playing();
        halfPaused.IsPaused = true;
        Assert.True(TeleportRules.NeedsUnpause(halfPaused));
        Assert.False(TeleportRules.IsControlRestored(halfPaused));
    }

    [Theory]
    [InlineData("hero")]
    [InlineData("sceneTransition")]
    [InlineData("transitioning")]
    [InlineData("dead")]
    [InlineData("hazard")]
    [InlineData("ability")]
    [InlineData("bench")]
    [InlineData("relinquished")]
    [InlineData("notPlaying")]
    public void BlockingStates(string which) {
        var state = Playing();
        switch (which) {
            case "hero": state.HeroExists = false; break;
            case "sceneTransition": state.InSceneTransition = true; break;
            case "transitioning": state.Transitioning = true; break;
            case "dead": state.Dead = true; break;
            case "hazard": state.HazardRespawning = true; break;
            case "ability": state.UsingMovementAbility = true; break;
            case "bench": state.AtBench = true; break;
            case "relinquished": state.ControlRelinquished = true; break;
            case "notPlaying": state.StatePlaying = false; break;
        }

        Assert.NotNull(TeleportRules.BlockReason(state));
    }

    [Theory]
    [InlineData("timeStopped")]
    [InlineData("heroInput")]
    [InlineData("inputHandler")]
    [InlineData("relinquished")]
    [InlineData("transitioning")]
    [InlineData("hazard")]
    [InlineData("sceneTransition")]
    [InlineData("notPlaying")]
    public void ControlNotRestored(string which) {
        var state = Playing();
        switch (which) {
            case "timeStopped": state.TimeStopped = true; break;
            case "heroInput": state.HeroAcceptingInput = false; break;
            case "inputHandler": state.InputHandlerAcceptingInput = false; break;
            case "relinquished": state.ControlRelinquished = true; break;
            case "transitioning": state.Transitioning = true; break;
            case "hazard": state.HazardRespawning = true; break;
            case "sceneTransition": state.InSceneTransition = true; break;
            case "notPlaying": state.StatePlaying = false; break;
        }

        Assert.False(TeleportRules.IsControlRestored(state));
    }
}
