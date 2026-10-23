namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Pure (game independent) rule for the fallback registration of enemies that have no entry in the entity registry
/// (EntityType.GenericEnemy). Every object with a HealthManager that is not already networked must
/// become an entity: otherwise each instance simulates its own local copy and the hits of a scene client only
/// damage that local copy, never the scene host's enemy. Linked into the unit tests.
/// </summary>
internal static class GenericEnemyRules {
    /// <summary>
    /// Outcome for one candidate object.
    /// </summary>
    public enum Decision {
        /// <summary>Register the object as a generic entity.</summary>
        Register,
        /// <summary>The object is a networked entity itself or part of one (its client copy carries it).</summary>
        SkipManaged,
        /// <summary>Nothing to synchronise: the object has no HealthManager.</summary>
        SkipNoHealthManager
    }

    /// <summary>
    /// Decide whether an object found on scene load is registered as a generic enemy.
    /// </summary>
    /// <param name="hasHealthManager">Whether the object has a HealthManager.</param>
    /// <param name="managedSelfOrAncestor">Whether the object or one of its ancestors is (part of) a registered
    /// entity.</param>
    /// <param name="managedDescendant">Whether one of its descendants is a registered entity. Deliberately not a
    /// reason to skip: e.g. the Entombed Husks ('Grave Zombie', RestingGrounds_10) carry a 'Dream Gate Set Lock'
    /// battle gate entity and were left unsynchronised, so scene clients could not damage them on the scene host.
    /// </param>
    // ReSharper disable once UnusedParameter.Global
    public static Decision Decide(bool hasHealthManager, bool managedSelfOrAncestor, bool managedDescendant) {
        if (!hasHealthManager) {
            return Decision.SkipNoHealthManager;
        }

        return managedSelfOrAncestor ? Decision.SkipManaged : Decision.Register;
    }
}
