using System.Collections.Generic;
using Hkmp.Util;
using Modding;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Knows which objects are only temporarily active because an <c>AwakenChildren</c> component (Unity 6 build)
/// woke them up in its Awake and will deactivate them again in its Start. See <see cref="EntityActivationRules"/>.
/// </summary>
internal static class AwakenChildrenTracker {
    /// <summary>
    /// Instance IDs of the AwakenChildren components that already ran their Start.
    /// </summary>
    private static readonly HashSet<int> Started = new();

    private static bool _initialized;

    /// <summary>
    /// Register the hook that records when an AwakenChildren component ran its Start.
    /// </summary>
    public static void Initialize() {
        if (_initialized) {
            return;
        }

        _initialized = true;
        On.AwakenChildren.Start += (orig, self) => {
            Started.Add(self.GetInstanceID());
            orig(self);
        };
    }

    /// <summary>
    /// The active state of the given object as the scene defines it, and whether it is active in the hierarchy as
    /// the scene defines it (taking temporarily woken ancestors into account).
    /// </summary>
    /// <param name="obj">The host object of an entity.</param>
    /// <returns>The original activeSelf and activeInHierarchy.</returns>
    public static (bool ActiveSelf, bool ActiveInHierarchy) OriginalActive(GameObject obj) {
        var activeSelf = obj.activeSelf;
        var activeInHierarchy = obj.activeInHierarchy;

        AwakenChildren[] components;
        try {
            components = obj.GetComponentsInParent<AwakenChildren>(true);
        } catch (System.Exception) {
            return (activeSelf, activeInHierarchy);
        }

        foreach (var component in components) {
            if (component == null) {
                continue;
            }

            var started = Started.Contains(component.GetInstanceID());
            if (started) {
                continue;
            }

            var targets = ReflectionHelper.GetField<AwakenChildren, List<GameObject>>(component, "awakenTarget");
            var recorded = ReflectionHelper.GetField<AwakenChildren, bool[]>(component, "activeState");

            // The object itself, or one of its ancestors, may be a target that is only awake for now
            var current = obj.transform;
            while (current != null) {
                var recordedActive = EntityActivationRules.RecordedActive(targets, recorded, current.gameObject);
                if (recordedActive == false && current.gameObject.activeSelf) {
                    activeInHierarchy = false;
                    if (current.gameObject == obj) {
                        activeSelf = EntityActivationRules.OriginalActive(activeSelf, false, false);
                    }

                    Logger.Info(
                        $"Entity object '{obj.name}' is only awake temporarily (AwakenChildren on " +
                        $"'{component.gameObject.name}' woke '{current.gameObject.name}'), original active: " +
                        $"{activeSelf}, in hierarchy: false");
                }

                current = current.parent;
            }
        }

        return (activeSelf, activeInHierarchy);
    }
}
