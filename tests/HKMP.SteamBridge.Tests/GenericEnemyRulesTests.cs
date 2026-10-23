using Hkmp.Game.Client.Entity;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Fallback registration of enemies without a registry entry. Bug: a scene client hitting the Entombed Husks of
/// RestingGrounds_10 saw the hit locally, but the scene host's husk never lost health: the husks carry a networked
/// 'Dream Gate Set Lock' child and were skipped, so every instance simulated its own unsynchronised copy.
/// </summary>
public class GenericEnemyRulesTests {
    [Fact]
    public void EnemyWithNetworkedDescendantIsRegistered() {
        Assert.Equal(
            GenericEnemyRules.Decision.Register,
            GenericEnemyRules.Decide(hasHealthManager: true, managedSelfOrAncestor: false, managedDescendant: true)
        );
    }

    [Fact]
    public void PlainEnemyIsRegistered() {
        Assert.Equal(
            GenericEnemyRules.Decision.Register,
            GenericEnemyRules.Decide(hasHealthManager: true, managedSelfOrAncestor: false, managedDescendant: false)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartOfAnEntityIsNotRegisteredTwice(bool managedDescendant) {
        Assert.Equal(
            GenericEnemyRules.Decision.SkipManaged,
            GenericEnemyRules.Decide(hasHealthManager: true, managedSelfOrAncestor: true, managedDescendant)
        );
    }

    [Fact]
    public void ObjectWithoutHealthManagerIsNotAnEnemy() {
        Assert.Equal(
            GenericEnemyRules.Decision.SkipNoHealthManager,
            GenericEnemyRules.Decide(hasHealthManager: false, managedSelfOrAncestor: false, managedDescendant: true)
        );
    }
}
