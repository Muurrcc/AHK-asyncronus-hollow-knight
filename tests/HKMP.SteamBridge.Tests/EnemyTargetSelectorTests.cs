using Hkmp.Game.Client.Entity;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Tests for choosing the player an enemy goes after (nearest player with hysteresis).
/// </summary>
public class EnemyTargetSelectorTests {
    private const int Host = EnemyTargetSelector.LocalPlayerId;
    private const int Remote = 2;

    private static EnemyTargetCandidate[] Players(float hostX, float remoteX) => new[] {
        new EnemyTargetCandidate(Host, hostX, 0f),
        new EnemyTargetCandidate(Remote, remoteX, 0f)
    };

    [Fact]
    public void NoCandidatesMeansNoTarget() {
        Assert.Equal(EnemyTargetSelector.NoTarget,
            EnemyTargetSelector.Choose(EnemyTargetSelector.NoTarget, 10f, 0f, 0f, new EnemyTargetCandidate[0]));
        Assert.Equal(EnemyTargetSelector.NoTarget, EnemyTargetSelector.GetNearest(0f, 0f, null));
    }

    [Fact]
    public void OnlyHostIsAlwaysChosen() {
        var candidates = new[] { new EnemyTargetCandidate(Host, 50f, 0f) };
        Assert.Equal(Host, EnemyTargetSelector.Choose(EnemyTargetSelector.NoTarget, 0f, 0f, 0f, candidates));
    }

    [Fact]
    public void InitialTargetIsNearestEvenIfNotHost() {
        // Enemy at 0, host far away at 20, remote player close at 3
        Assert.Equal(Remote, EnemyTargetSelector.Choose(EnemyTargetSelector.NoTarget, 0f, 0f, 0f, Players(20f, 3f)));
        Assert.Equal(Host, EnemyTargetSelector.Choose(EnemyTargetSelector.NoTarget, 0f, 0f, 0f, Players(3f, 20f)));
    }

    [Fact]
    public void UsesTwoDimensionalDistance() {
        var candidates = new[] {
            new EnemyTargetCandidate(Host, 3f, 0f),
            new EnemyTargetCandidate(Remote, 0f, -2f)
        };
        Assert.Equal(Remote, EnemyTargetSelector.GetNearest(0f, 0f, candidates));
    }

    [Fact]
    public void EquidistantTieIsDeterministicLowestId() {
        var candidates = new[] {
            new EnemyTargetCandidate(5, 4f, 0f),
            new EnemyTargetCandidate(Remote, -4f, 0f),
            new EnemyTargetCandidate(Host, 0f, 4f)
        };
        Assert.Equal(Host, EnemyTargetSelector.GetNearest(0f, 0f, candidates));

        var reversed = new[] { candidates[2], candidates[1], candidates[0] };
        Assert.Equal(Host, EnemyTargetSelector.GetNearest(0f, 0f, reversed));
    }

    [Fact]
    public void KeepsCurrentTargetWhenOtherIsOnlySlightlyCloser() {
        // Current target (host) at 10, remote at 9: 10% closer, below the 20% ratio
        Assert.Equal(Host, EnemyTargetSelector.Choose(Host, 5f, 0f, 0f, Players(10f, -9f)));
    }

    [Fact]
    public void SwitchesWhenOtherIsClearlyCloser() {
        // Current target (host) at 10, remote at 7: 30% and 3 units closer
        Assert.Equal(Remote, EnemyTargetSelector.Choose(Host, 5f, 0f, 0f, Players(10f, -7f)));
        // And back the other way
        Assert.Equal(Host, EnemyTargetSelector.Choose(Remote, 5f, 0f, 0f, Players(7f, -10f)));
    }

    [Fact]
    public void RatioBoundaryIsInclusive() {
        // Exactly 80% of the current distance and 2 units closer
        Assert.Equal(Remote, EnemyTargetSelector.Choose(Host, 5f, 0f, 0f, Players(10f, -8f)));
        // Just above 80%
        Assert.Equal(Host, EnemyTargetSelector.Choose(Host, 5f, 0f, 0f, Players(10f, -8.1f)));
    }

    [Fact]
    public void RequiresAbsoluteMarginWhenBothAreClose() {
        // Host at 2, remote at 1: 50% closer but only 1 unit (margin is 1.5)
        Assert.Equal(Host, EnemyTargetSelector.Choose(Host, 5f, 0f, 0f, Players(2f, -1f)));
        // Host at 3, remote at 1: 2 units closer
        Assert.Equal(Remote, EnemyTargetSelector.Choose(Host, 5f, 0f, 0f, Players(3f, -1f)));
    }

    [Fact]
    public void HoldTimePreventsQuickSwitchBack() {
        Assert.Equal(Host, EnemyTargetSelector.Choose(Host, 0.5f, 0f, 0f, Players(10f, -1f)));
        Assert.Equal(Remote, EnemyTargetSelector.Choose(Host, EnemyTargetSelector.MinHoldSeconds, 0f, 0f,
            Players(10f, -1f)));
    }

    [Fact]
    public void SwitchesImmediatelyWhenCurrentTargetLeft() {
        var onlyHost = new[] { new EnemyTargetCandidate(Host, 30f, 0f) };
        // Held for 0 seconds, but the remote target is gone
        Assert.Equal(Host, EnemyTargetSelector.Choose(Remote, 0f, 0f, 0f, onlyHost));
    }

    [Fact]
    public void TrackerDoesNotOscillateBetweenEquidistantPlayers() {
        var tracker = new EnemyTargetTracker();
        var changes = 0;

        // Enemy oscillates slightly around the middle of two players at -5 and 5 for 10 seconds
        for (var i = 0; i < 100; i++) {
            var time = i * 0.1f;
            var enemyX = (i % 2 == 0 ? 0.4f : -0.4f);
            if (tracker.Update(time, enemyX, 0f, Players(-5f, 5f))) {
                changes++;
            }
        }

        // Only the initial choice
        Assert.Equal(1, changes);
    }

    [Fact]
    public void TrackerFollowsEnemyMovingTowardsOtherPlayer() {
        var tracker = new EnemyTargetTracker();
        var candidates = Players(0f, 20f);

        Assert.True(tracker.Update(0f, 1f, 0f, candidates));
        Assert.Equal(Host, tracker.CurrentId);

        // Enemy walks from x=1 to x=19 over 9 seconds
        for (var i = 1; i <= 90; i++) {
            tracker.Update(i * 0.1f, 1f + i * 0.2f, 0f, candidates);
        }

        Assert.Equal(Remote, tracker.CurrentId);
    }

    [Fact]
    public void TrackerIsDeterministic() {
        int Run() {
            var tracker = new EnemyTargetTracker();
            var hash = 17;
            for (var i = 0; i < 200; i++) {
                var x = (float) System.Math.Sin(i * 0.37) * 12f;
                tracker.Update(i * 0.05f, x, 0f, Players(-6f, 6f));
                hash = hash * 31 + tracker.CurrentId;
            }

            return hash;
        }

        Assert.Equal(Run(), Run());
    }
}
