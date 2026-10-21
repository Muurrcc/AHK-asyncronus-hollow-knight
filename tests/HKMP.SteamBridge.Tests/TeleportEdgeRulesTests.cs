using System.Linq;
using Hkmp.Game.Client;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Edge cases of the teleport rules: blocked scenes, request throttling, landing spot choice and the server side
/// flight tracking.
/// </summary>
public class TeleportEdgeRulesTests {
    private static TeleportGameState Playing() => new() {
        HeroExists = true,
        StatePlaying = true,
        HeroAcceptingInput = true,
        InputHandlerAcceptingInput = true
    };

    [Theory]
    [InlineData("GG_Atrium", "in Godhome")]
    [InlineData("GG_Workshop", "in Godhome")]
    [InlineData("GG_Hornet_1", "in Godhome")]
    [InlineData("GG_Hollow_Knight", "in Godhome")]
    [InlineData("Dream_01_False_Knight", "in a dream")]
    [InlineData("Dream_Nailcollection", "in a dream")]
    [InlineData("White_Palace_01", "in a dream")]
    [InlineData("Room_Colosseum_Bronze", "in a Colosseum trial")]
    [InlineData("Room_Colosseum_Silver", "in a Colosseum trial")]
    [InlineData("Room_Colosseum_Gold", "in a Colosseum trial")]
    [InlineData("Menu_Title", "in a menu or cutscene")]
    [InlineData("Quit_To_Menu", "in a menu or cutscene")]
    [InlineData("Opening_Sequence", "in a menu or cutscene")]
    [InlineData("Cinematic_Stag_travel", "in a menu or cutscene")]
    [InlineData("Cinematic_Ending_A", "in a menu or cutscene")]
    [InlineData("End_Credits", "in a menu or cutscene")]
    [InlineData("GG_Entrance_Cutscene", "in a menu or cutscene")]
    [InlineData("", "not in a scene")]
    [InlineData(null, "not in a scene")]
    public void SceneBlockReason_BlockedScenes(string scene, string reason) {
        Assert.Equal(reason, TeleportRules.SceneBlockReason(scene));
    }

    [Theory]
    [InlineData("Town")]
    [InlineData("Tutorial_01")]
    [InlineData("Crossroads_10")]
    [InlineData("Crossroads_10_boss")]
    [InlineData("Room_Colosseum_01")]
    [InlineData("Room_Colosseum_Spectate")]
    [InlineData("GG_Waterways")]
    [InlineData("GG_Pipeway")]
    [InlineData("GG_Lurker")]
    [InlineData("Abyss_15")]
    [InlineData("Room_Final_Boss_Core")]
    [InlineData("Fungus1_04_boss")]
    public void SceneBlockReason_NormalScenes(string scene) {
        Assert.Null(TeleportRules.SceneBlockReason(scene));
    }

    [Theory]
    [InlineData("Town", "GG_Atrium")]
    [InlineData("Town", "Dream_01_False_Knight")]
    [InlineData("Town", "Menu_Title")]
    [InlineData("GG_Hornet_1", "Town")]
    [InlineData("Room_Colosseum_Bronze", "Town")]
    [InlineData("Dream_Abyss", "Abyss_15")]
    public void Classify_BlockedSceneEitherWay(string current, string target) {
        Assert.Equal(TeleportKind.BlockedScene, TeleportRules.Classify(current, target, true));
    }

    [Fact]
    public void Classify_SameBlockedSceneIsStillBlocked() {
        // Two players in the same Godhome boss fight: no moving around inside it either
        Assert.Equal(TeleportKind.BlockedScene, TeleportRules.Classify("GG_Hornet_1", "GG_Hornet_1", true));
    }

    [Fact]
    public void Classify_UnknownSceneIsInvalidNotBlocked() {
        Assert.Equal(TeleportKind.InvalidScene, TeleportRules.Classify("Town", "Not_A_Scene", false));
    }

    [Fact]
    public void BlockReason_Focusing() {
        var state = Playing();
        state.Focusing = true;
        Assert.Equal("focusing", TeleportRules.BlockReason(state));
    }

    [Fact]
    public void RequestBlockReason_PlayingIsFine() {
        Assert.Null(TeleportRules.RequestBlockReason(Playing(), "Town"));
    }

    [Fact]
    public void RequestBlockReason_PausedIsFine() {
        // The pause menu button sends the request while paused: the teleport unpauses
        var state = Playing();
        state.StatePlaying = false;
        state.StatePaused = true;
        state.IsPaused = true;
        Assert.Null(TeleportRules.RequestBlockReason(state, "Town"));
    }

    [Theory]
    [InlineData("ability")]
    [InlineData("focus")]
    [InlineData("relinquished")]
    [InlineData("hazard")]
    public void RequestBlockReason_ShortStatesAreWaitedForLater(string which) {
        var state = Playing();
        switch (which) {
            case "ability": state.UsingMovementAbility = true; break;
            case "focus": state.Focusing = true; break;
            case "relinquished": state.ControlRelinquished = true; break;
            case "hazard": state.HazardRespawning = true; break;
        }

        Assert.Null(TeleportRules.RequestBlockReason(state, "Town"));
    }

    [Theory]
    [InlineData("hero", "not in game")]
    [InlineData("dead", "dead")]
    [InlineData("bench", "resting at a bench")]
    [InlineData("transition", "changing scene")]
    public void RequestBlockReason_LongStatesBlockAtOnce(string which, string reason) {
        var state = Playing();
        switch (which) {
            case "hero": state.HeroExists = false; break;
            case "dead": state.Dead = true; break;
            case "bench": state.AtBench = true; break;
            case "transition": state.InSceneTransition = true; break;
        }

        Assert.Equal(reason, TeleportRules.RequestBlockReason(state, "Town"));
    }

    [Fact]
    public void RequestBlockReason_FromBlockedScene() {
        Assert.Equal("you are in Godhome", TeleportRules.RequestBlockReason(Playing(), "GG_Atrium"));
    }

    [Fact]
    public void Throttle_FirstRequestAllowed() {
        Assert.Null(TeleportRules.ThrottleReason(false, double.PositiveInfinity, false));
    }

    [Fact]
    public void Throttle_BusyBlocks() {
        Assert.NotNull(TeleportRules.ThrottleReason(true, 100, false));
    }

    [Fact]
    public void Throttle_DoubleClickBlocked() {
        Assert.NotNull(TeleportRules.ThrottleReason(false, 0.2, false));
        Assert.NotNull(TeleportRules.ThrottleReason(false, TeleportRules.MinRequestInterval - 0.01, false));
        Assert.Null(TeleportRules.ThrottleReason(false, TeleportRules.MinRequestInterval + 0.01, false));
    }

    [Fact]
    public void Throttle_UnansweredRequestBlocksUntilTimeout() {
        Assert.NotNull(TeleportRules.ThrottleReason(false, TeleportRules.AnswerTimeout - 0.5, true));
        Assert.Null(TeleportRules.ThrottleReason(false, TeleportRules.AnswerTimeout + 0.5, true));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)] // normal enemy damage
    [InlineData(2, false, true)] // spikes
    [InlineData(3, false, true)] // acid
    [InlineData(3, true, false)] // acid with Isma's Tear
    [InlineData(4, true, true)] // lava
    [InlineData(5, false, true)] // pit
    public void IsHazard(int hazardType, bool acidArmour, bool expected) {
        Assert.Equal(expected, TeleportRules.IsHazard(hazardType, acidArmour));
    }

    [Fact]
    public void ChooseLanding_SafeTargetIsKept() {
        var target = new LandingSpot(10, 5, true, false);
        var result = TeleportRules.ChooseLanding(target, new[] { new LandingSpot(11, 5, true, false) }, out var moved);
        Assert.False(moved);
        Assert.Equal(10, result.X);
    }

    [Fact]
    public void ChooseLanding_AboveSpikesUsesClosestSafeMarker() {
        var target = new LandingSpot(10, 5, true, true);
        var markers = new[] {
            new LandingSpot(30, 5, true, false),
            new LandingSpot(12, 6, true, true), // closest, but on spikes too
            new LandingSpot(15, 5, true, false),
            new LandingSpot(11, 5, false, false) // no ground below
        };
        var result = TeleportRules.ChooseLanding(target, markers, out var moved);
        Assert.True(moved);
        Assert.Equal(15, result.X);
    }

    [Fact]
    public void ChooseLanding_InEntryGateUsesMarker() {
        // The target just entered the scene and stands in the gate: landing there walks back out
        var target = new LandingSpot(1.3f, 44.4f, true, false, true);
        Assert.False(target.Safe);
        var result = TeleportRules.ChooseLanding(target, new[] {
            new LandingSpot(1.5f, 44.4f, true, false, true),
            new LandingSpot(5f, 44.4f, true, false)
        }, out var moved);
        Assert.True(moved);
        Assert.Equal(5f, result.X);
    }

    [Fact]
    public void ChooseLanding_AbovePitUsesMarker() {
        var target = new LandingSpot(50, 40, false, false);
        var result = TeleportRules.ChooseLanding(target, new[] { new LandingSpot(45, 10, true, false) }, out var moved);
        Assert.True(moved);
        Assert.Equal(45, result.X);
        Assert.Equal(10, result.Y);
    }

    [Fact]
    public void ChooseLanding_NoSafeMarkerKeepsTarget() {
        var target = new LandingSpot(50, 40, false, false);
        var result = TeleportRules.ChooseLanding(target, new[] { new LandingSpot(45, 10, true, true) }, out var moved);
        Assert.False(moved);
        Assert.Equal(50, result.X);
        TeleportRules.ChooseLanding(target, null, out moved);
        Assert.False(moved);
        TeleportRules.ChooseLanding(target, Enumerable.Empty<LandingSpot>(), out moved);
        Assert.False(moved);
    }

    [Fact]
    public void Flights_MutualTeleportSecondIsRefused() {
        var flights = new TeleportFlights();
        Assert.Null(flights.RefuseReason(1, 2, "B", 0));
        flights.Start(1, 0);
        // B asks for A while A is on its way to B
        Assert.Equal("A is teleporting right now, try again in a moment", flights.RefuseReason(2, 1, "A", 0.3));
        // A asks again
        Assert.Equal("You are already teleporting", flights.RefuseReason(1, 2, "B", 0.5));
        // A third player can still go to B
        Assert.Null(flights.RefuseReason(3, 2, "B", 0.5));
    }

    [Fact]
    public void Flights_EndOnArrivalOrExpiry() {
        var flights = new TeleportFlights();
        flights.Start(1, 0);
        flights.End(1);
        Assert.False(flights.IsTeleporting(1, 0.1));

        flights.Start(1, 10);
        Assert.True(flights.IsTeleporting(1, 10 + TeleportFlights.FlightSeconds - 0.1));
        Assert.False(flights.IsTeleporting(1, 10 + TeleportFlights.FlightSeconds + 0.1));
        Assert.Null(flights.RefuseReason(2, 1, "A", 10 + TeleportFlights.FlightSeconds + 0.1));
    }

    [Fact]
    public void Flights_EndOfUnknownPlayerIsHarmless() {
        var flights = new TeleportFlights();
        flights.End(7);
        Assert.False(flights.IsTeleporting(7, 0));
    }
}
