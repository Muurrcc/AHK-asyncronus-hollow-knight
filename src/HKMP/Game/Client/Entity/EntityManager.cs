using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using HutongGames.PlayMaker.Actions;
using Modding;
using UnityEngine;
using UnityEngine.SceneManagement;
using FindGameObject = On.HutongGames.PlayMaker.Actions.FindGameObject;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Manager class that handles entity creation, updating, networking and destruction.
/// </summary>
internal class EntityManager {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// Dictionary mapping entity IDs to their respective entity instances.
    /// </summary>
    private readonly Dictionary<ushort, Entity> _entities;

    /// <summary>
    /// Whether the scene host is determined for this scene locally.
    /// </summary>
    public bool IsSceneHostDetermined { get; private set; }

    /// <summary>
    /// Whether the client user is the scene host.
    /// </summary>
    public bool IsSceneHost { get; private set; }

    /// <summary>
    /// Queue of entity updates that have not been applied yet because of a missing entity.
    /// Usually this occurs because the entities are loaded later than the updates are received when the local player
    /// enters a new scene.
    /// </summary>
    private readonly Queue<QueuedUpdate> _receivedUpdates;

    /// <summary>
    /// Maximum number of times a queued update is checked against the registered entities before it is dropped.
    /// </summary>
    private const int MaxQueuedUpdateChecks = 8;

    /// <summary>
    /// Maximum number of queued updates; the oldest are dropped beyond this.
    /// </summary>
    private const int MaxQueuedUpdates = 4096;

    /// <summary>
    /// An entity update that could not be applied yet.
    /// </summary>
    private class QueuedUpdate {
        public BaseEntityUpdate Update;
        public bool AlreadyInScene;
        public int Checks;
    }

    /// <summary>
    /// The last created instance (used by development tests).
    /// </summary>
    public static EntityManager Instance { get; private set; }

    /// <summary>
    /// The registered entities (read-only view, used by development tests).
    /// </summary>
    public IReadOnlyDictionary<ushort, Entity> Entities => _entities;

    /// <summary>
    /// The number of updates waiting for their entity (used by development tests).
    /// </summary>
    public int QueuedUpdateCount => _receivedUpdates.Count;

    public EntityManager(NetClient netClient) {
        _netClient = netClient;
        _entities = new Dictionary<ushort, Entity>();
        _receivedUpdates = new Queue<QueuedUpdate>();

        Instance = this;
    }

    /// <summary>
    /// Store an update that could not be applied yet.
    /// </summary>
    private void EnqueueUpdate(BaseEntityUpdate update, bool alreadyInScene, int checks = 0) {
        while (_receivedUpdates.Count >= MaxQueuedUpdates) {
            _receivedUpdates.Dequeue();
        }

        _receivedUpdates.Enqueue(new QueuedUpdate {
            Update = update,
            AlreadyInScene = alreadyInScene,
            Checks = checks
        });
    }

    /// <summary>
    /// Initialize the entity manager by initializing the processor and action hooks.
    /// </summary>
    public void Initialize() {
        EntityProcessor.Initialize(_entities, _netClient);
        AwakenChildrenTracker.Initialize();
    }

    /// <summary>
    /// Register the hooks for entity-related operations.
    /// </summary>
    public void RegisterHooks() {
        FsmActionHooks.RegisterHooks();
        MusicComponent.RegisterHooks();

        EntityFsmActions.EntitySpawnEvent += OnGameObjectSpawned;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;

        FindGameObject.Find += OnFindGameObject;

        On.GameManager.ResetSemiPersistentItems += OnResetSemiPersistentItems;
    }

    /// <summary>
    /// Callback for when the local player rests at a bench, which resets semi-persistent items (enemies). Notifies the
    /// server so it forgets the stored semi-persistent enemy deaths of empty scenes.
    /// </summary>
    private void OnResetSemiPersistentItems(On.GameManager.orig_ResetSemiPersistentItems orig, global::GameManager self) {
        orig(self);

        if (!_netClient.IsConnected) {
            return;
        }

        Logger.Info("Semi-persistent items reset (bench), notifying server");
        _netClient.UpdateManager.AddEntityData(ushort.MaxValue, new EntityNetworkData {
            Type = EntityComponentType.ResetSemiPersistent
        });
    }

    /// <summary>
    /// Deregister the hooks for entity-related operations.
    /// </summary>
    public void DeregisterHooks() {
        FsmActionHooks.DeregisterHooks();
        MusicComponent.DeregisterHooks();

        EntityFsmActions.EntitySpawnEvent -= OnGameObjectSpawned;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnSceneChanged;

        FindGameObject.Find -= OnFindGameObject;

        On.GameManager.ResetSemiPersistentItems -= OnResetSemiPersistentItems;

        ClearEntities();
    }

    /// <summary>
    /// Initializes the entity manager if we are the scene host.
    /// </summary>
    public void InitializeSceneHost() {
        Logger.Info("We are scene host, releasing control of all registered entities");

        IsSceneHost = true;

        foreach (var entity in _entities.Values) {
            entity.InitializeHost();
        }

        IsSceneHostDetermined = true;

        CheckReceivedUpdates();
    }

    /// <summary>
    /// Initializes the entity manager if we are a scene client.
    /// </summary>
    public void InitializeSceneClient() {
        Logger.Info("We are scene client, taking control of all registered entities");

        IsSceneHost = false;

        foreach (var entity in _entities.Values) {
            entity.InitializeClient();
        }

        IsSceneHostDetermined = true;

        CheckReceivedUpdates();
    }

    /// <summary>
    /// Updates the entity manager if we become the scene host.
    /// </summary>
    public void BecomeSceneHost() {
        Logger.Info("Becoming scene host");

        IsSceneHost = true;

        foreach (var entity in _entities.Values) {
            entity.MakeHost();
        }

        CheckReceivedUpdates();
    }

    /// <summary>
    /// Spawn an entity with the given ID and type, that was spawned from the given entity type.
    /// </summary>
    /// <param name="id">The ID of the entity.</param>
    /// <param name="spawningType">The type of the entity that spawned the new entity.</param>
    /// <param name="spawnedType">The type of the spawned entity.</param>
    public void SpawnEntity(ushort id, EntityType spawningType, EntityType spawnedType) {
        Logger.Info($"Trying to spawn entity with ID {id} with types: {spawningType}, {spawnedType}");

        // If an entity with the new ID already exists, we return
        if (_entities.ContainsKey(id)) {
            Logger.Info($"  Entity with ID {id} already exists, assuming it has been spawned by action");
            return;
        }

        // Find an entity that has the same type as the spawning type. Doesn't matter if it is the correct instance,
        // because the FSMs and components will be identical
        var spawningEntity = _entities.Values.FirstOrDefault(
            e => e.Type == spawningType
        );

        if (spawningEntity == null) {
            Logger.Warn("Could not find entity with same type for spawning");
            return;
        }

        var spawnedObject = EntitySpawner.SpawnEntityGameObject(
            spawningType,
            spawnedType,
            spawningEntity.Object.Client,
            spawningEntity.GetClientFsms()
        );

        var processor = new EntityProcessor {
            GameObject = spawnedObject,
            IsSceneHost = IsSceneHost,
            IsSceneHostDetermined = IsSceneHostDetermined,
            LateLoad = true,
            SpawnedId = id
        }.Process();

        if (!processor.Success) {
            Logger.Warn($"Could not process game object of spawned entity: {spawnedObject.name}");
            return;
        }

        CheckReceivedUpdates();
    }

    /// <summary>
    /// Method for handling received entity updates.
    /// </summary>
    /// <param name="entityUpdate">The entity update to handle.</param>
    /// <param name="alreadyInSceneUpdate">Whether this is the update from the already in scene packet.</param>
    /// <param name="enqueueOnFailure">Whether to store the update if it cannot be applied yet.</param>
    public bool HandleEntityUpdate(
        EntityUpdate entityUpdate,
        bool alreadyInSceneUpdate = false,
        bool enqueueOnFailure = true
    ) {
        if (IsSceneHost) {
            return true;
        }

        if (!_entities.TryGetValue(entityUpdate.Id, out var entity) || !IsSceneHostDetermined) {
            if (IsSceneHostDetermined) {
                Logger.Debug($"Could not find entity ({entityUpdate.Id}) to apply update for; storing update for now");
            } else {
                Logger.Debug("Scene host is not determined yet to apply update; storing update for now");
            }

            if (enqueueOnFailure) {
                EnqueueUpdate(entityUpdate, alreadyInSceneUpdate);
            }

            return false;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Position)) {
            entity.UpdatePosition(entityUpdate.Position);
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Scale)) {
            entity.UpdateScale(entityUpdate.Scale);
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Animation)) {
            entity.UpdateAnimation(
                entityUpdate.AnimationId,
                (tk2dSpriteAnimationClip.WrapMode) entityUpdate.AnimationWrapMode,
                alreadyInSceneUpdate
            );
        }

        return true;
    }

    /// <summary>
    /// Method for handling received reliable entity updates.
    /// </summary>
    /// <param name="entityUpdate">The reliable entity update to handle.</param>
    /// <param name="alreadyInSceneUpdate">Whether this is the update from the already in scene packet.</param>
    /// <param name="enqueueOnFailure">Whether to store the update if it cannot be applied yet.</param>
    public bool HandleReliableEntityUpdate(
        ReliableEntityUpdate entityUpdate,
        bool alreadyInSceneUpdate = false,
        bool enqueueOnFailure = true
    ) {
        if (!_entities.TryGetValue(entityUpdate.Id, out var entity) || !IsSceneHostDetermined) {
            if (IsSceneHostDetermined) {
                Logger.Debug($"Could not find entity ({entityUpdate.Id}) to apply update for; storing update for now");
            } else {
                Logger.Debug("Scene host is not determined yet to apply update; storing update for now");
            }

            if (enqueueOnFailure) {
                EnqueueUpdate(entityUpdate, alreadyInSceneUpdate);
            }

            return false;
        }

        // Check if we are not the scene host for processing this data, active state and host FSM data should only
        // be applied if we not the scene host, while data type below should always be applied
        if (!IsSceneHost) {
            if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Active)) {
                entity.UpdateIsActive(entityUpdate.IsActive);
            }

            if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
                entity.UpdateHostFsmData(entityUpdate.HostFsmData);
            }
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Data)) {
            entity.UpdateData(entityUpdate.GenericData, alreadyInSceneUpdate);
        }

        return true;
    }

    /// <summary>
    /// Callback method for when a game object is spawned from an existing entity.
    /// </summary>
    /// <param name="details">The entity spawn details containing how the entity was spawned.</param>
    /// <returns>Whether an entity was registered from this spawn.</returns>
    private bool OnGameObjectSpawned(EntitySpawnDetails details) {
        if (_entities.Values.Any(existingEntity => existingEntity.Object.Host == details.GameObject)) {
            Logger.Debug("Spawned object was already a registered entity");
            return true;
        }

        var processor = new EntityProcessor {
            GameObject = details.GameObject,
            IsSceneHost = IsSceneHost,
            IsSceneHostDetermined = IsSceneHostDetermined,
            LateLoad = true
        }.Process();

        if (!processor.Success) {
            return false;
        }

        if (!IsSceneHost) {
            Logger.Warn("Game object was spawned while not scene host, this shouldn't happen");
            return false;
        }

        string spawningObjectName;
        EntityType spawningType;
        var topLevelEntity = processor.Entities[0];

        if (details.Type == EntitySpawnType.FsmAction) {
            spawningObjectName = details.Action.Fsm.GameObject.name;
            if (EntityRegistry.TryGetEntry(details.Action.Fsm.GameObject, out var entry)) {
                spawningType = entry.Type;
            } else {
                Logger.Warn("Could not find registry entry for spawning type of object");
                return false;
            }
        } else if (details.Type == EntitySpawnType.EnemySpawnerComponent) {
            spawningObjectName = "Vengefly Summon";
            spawningType = EntityType.VengeflySummon;
        } else if (details.Type == EntitySpawnType.SpawnJarComponent) {
            spawningObjectName = "Spawn Jar";
            spawningType = EntityType.CollectorJar;
        } else {
            Logger.Error($"Invalid EntitySpawnDetails type: {details.Type}");
            return false;
        }

        Logger.Info(
            $"Notifying server of entity ({spawningObjectName}, {spawningType}) spawning entity ({details.GameObject.name}, {topLevelEntity.Type}) with ID {topLevelEntity.Id}");
        _netClient.UpdateManager.SetEntitySpawn(
            topLevelEntity.Id,
            spawningType,
            topLevelEntity.Type
        );

        return true;
    }

    /// <summary>
    /// Check to see if there are received un-applied entity updates.
    /// </summary>
    /// <remarks>
    /// Each queued update is looked at exactly once per call: applied if its entity exists (and the scene host is
    /// determined), otherwise re-queued until it has been checked <see cref="MaxQueuedUpdateChecks"/> times. The
    /// previous implementation peeked at the head of the queue in a <c>while</c> loop without ever dequeuing an update
    /// whose entity did not exist, which never terminated (game freeze) as soon as one orphan update was queued.
    /// </remarks>
    public void CheckReceivedUpdates() {
        if (!IsSceneHostDetermined) {
            return;
        }

        var count = _receivedUpdates.Count;
        var applied = 0;
        var dropped = 0;

        for (var i = 0; i < count && _receivedUpdates.Count > 0; i++) {
            var queued = _receivedUpdates.Dequeue();
            var update = queued.Update;

            bool handled;
            if (!_entities.ContainsKey(update.Id)) {
                handled = false;
            } else if (update is EntityUpdate entityUpdate) {
                handled = HandleEntityUpdate(entityUpdate, queued.AlreadyInScene, false);
            } else if (update is ReliableEntityUpdate reliableEntityUpdate) {
                handled = HandleReliableEntityUpdate(reliableEntityUpdate, queued.AlreadyInScene, false);
            } else {
                // Unknown update type, drop it
                dropped++;
                continue;
            }

            if (handled) {
                applied++;
                continue;
            }

            if (queued.Checks + 1 >= MaxQueuedUpdateChecks) {
                dropped++;
                continue;
            }

            EnqueueUpdate(update, queued.AlreadyInScene, queued.Checks + 1);
        }

        if (count > 0) {
            Logger.Info($"HKMP_QUEUE checked {count} queued entity updates: applied {applied}, dropped {dropped}, remaining {_receivedUpdates.Count}");
        }
    }

    /// <summary>
    /// Callback method for when the scene changes. Will clear existing entities and start checking for
    /// new entities.
    /// </summary>
    /// <param name="oldScene">The old scene.</param>
    /// <param name="newScene">The new scene.</param>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        Logger.Info("Scene changed, clearing registered entities");

        ClearEntities();

        if (!_netClient.IsConnected) {
            return;
        }

        IsSceneHostDetermined = false;

        FindEntitiesInScene(newScene, false);

        // Since we have tried finding entities in the scene, we also check whether there are un-applied updates for
        // those entities
        CheckReceivedUpdates();
    }

    /// <summary>
    /// Clears all the registered entities, and resets static components.
    /// </summary>
    private void ClearEntities() {
        foreach (var entity in _entities.Values) {
            entity.Destroy();
        }

        // Clear the list of entities and the queue of received updates that have not been applied yet
        _entities.Clear();
        _receivedUpdates.Clear();

        MusicComponent.ClearInstance();
    }

    /// <summary>
    /// Callback method for when a scene is loaded.
    /// </summary>
    /// <param name="scene">The scene that is loaded.</param>
    /// <param name="mode">The load scene mode.</param>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        var currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        // If this scene is a boss or boss-defeated scene it starts with the same name, so we skip all other
        // loaded scenes
        if (!scene.name.StartsWith(currentSceneName) || scene.name.Equals(currentSceneName)) {
            return;
        }

        Logger.Info($"Additional scene loaded ({scene.name}), looking for entities");

        FindEntitiesInScene(scene, true);

        // Since we have tried finding entities in the scene, we also check whether there are un-applied updates for
        // those entities
        CheckReceivedUpdates();
    }

    /// <summary>
    /// Find entities to register in the given scene.
    /// </summary>
    /// <param name="scene">The scene to find entities in.</param>
    /// <param name="lateLoad">Whether this scene was loaded late.</param>
    private void FindEntitiesInScene(Scene scene, bool lateLoad) {
        // Find all EnemyDeathEffects components
        var objectsToCheck = Object.FindObjectsOfType<EnemyDeathEffects>()
            // Filter out EnemyDeathEffects components not in the current scene
            .Where(e => e.gameObject.scene == scene)
            // Project each death effect to their GameObject and the corpse of the pre-instantiated EnemyDeathEffects
            // component
            .SelectMany(enemyDeathEffects => {
                try {
                    enemyDeathEffects.PreInstantiate();
                } catch (Exception) {
                    // If we get an exception it might not be possible to pre-instantiate the enemy death effects
                    // This can happen when the object uses a PersonalObjectPool, which can't be pre-instantiated
                    // this early, so we return only the original gameobject
                    return new[] { enemyDeathEffects.gameObject };
                }

                var corpse = ReflectionHelper.GetField<EnemyDeathEffects, GameObject>(
                    enemyDeathEffects,
                    "corpse"
                );

                return new[] { enemyDeathEffects.gameObject, corpse };
            })
            // Concatenate all GameObjects for PlayMakerFSM components in the current scene, and check whether it is the
            // FSM for a Colosseum Cage, in which case we pre-instantiate the enemy inside and concatenate it as well
            .Concat(Object.FindObjectsOfType<PlayMakerFSM>(true)
                .Where(fsm => fsm.gameObject.scene == scene)
                .SelectMany(fsm => {
                    if (!fsm.name.StartsWith("Colosseum Cage Small") &&
                        !fsm.name.StartsWith("Colosseum Cage Large") &&
                        !fsm.name.StartsWith("Colosseum Cage Zote")) {
                        return new[] { fsm.gameObject };
                    }

                    if (!fsm.Fsm.Name.Equals("Spawn") &&
                        !fsm.Fsm.Name.Equals("Control")
                    ) {
                        return new[] { fsm.gameObject };
                    }

                    var createAction = fsm.GetFirstAction<CreateObject>("Init");
                    EntityFsmActions.ApplyNetworkDataFromAction(null, createAction);

                    createAction.Enabled = false;

                    var createdObject = createAction.storeObject.Value;
                    if (createdObject == null) {
                        return new[] { fsm.gameObject };
                    }

                    var fsmTransform = fsm.gameObject.transform;

                    createdObject.transform.position = fsmTransform.position;
                    createdObject.transform.rotation = Quaternion.Euler(fsmTransform.eulerAngles);
                    createdObject.SetActive(false);

                    return new[] { fsm.gameObject, createdObject };
                })
            )
            // Project each GameObject into its children including itself
            .SelectMany(obj => obj == null ? Array.Empty<GameObject>() : obj.GetChildren().Prepend(obj))
            // Concatenate all GameObjects for Climber components (Tiktiks)
            .Concat(Object.FindObjectsOfType<Climber>(true).Select(climber => climber.gameObject))
            // Concatenate all GameObjects for Walker components (Amblooms)
            .Concat(Object.FindObjectsOfType<Walker>(true).Select(walker => walker.gameObject))
            // Concatenate all GameObjects for BigCentipede components (Garpedes)
            .Concat(Object.FindObjectsOfType<BigCentipede>(true).Select(centipede => centipede.gameObject))
            // Concatenate all GameObjects for CameraLockArea components
            .Concat(Object.FindObjectsOfType<CameraLockArea>(true).Select(cameraLockArea => cameraLockArea.gameObject))
            // Concatenate all GameObjects for FlipPlatform components
            .Concat(Object.FindObjectsOfType<FlipPlatform>(true).Select(flipPlatform => flipPlatform.gameObject))
            // Concatenate all GameObjects for DreamPlatform components
            .Concat(Object.FindObjectsOfType<DreamPlatform>(true).Select(dreamPlatform => dreamPlatform.gameObject))
            // Filter out GameObjects not in the current scene
            .Where(obj => obj.scene == scene)
            .Distinct();

        foreach (var obj in objectsToCheck) {
            new EntityProcessor {
                GameObject = obj,
                IsSceneHost = IsSceneHost,
                IsSceneHostDetermined = IsSceneHostDetermined,
                LateLoad = lateLoad
            }.Process();
        }

        RegisterGenericEnemies(scene, lateLoad);
    }

    /// <summary>
    /// Register every game object with a HealthManager in the given scene that is not covered by the entity registry
    /// (directly, as child of a registered entity or as one of the client copies) as a generic entity. This way all
    /// enemies get position/animation/activity/HP/death synchronisation, even if their FSM actions are not supported.
    /// </summary>
    private void RegisterGenericEnemies(Scene scene, bool lateLoad) {
        var managedObjects = new HashSet<GameObject>();
        foreach (var entity in _entities.Values) {
            if (entity.Object.Host != null) {
                managedObjects.Add(entity.Object.Host);
            }

            if (entity.Object.Client != null) {
                managedObjects.Add(entity.Object.Client);
            }
        }

        bool IsManagedOrUnderManaged(Transform transform) {
            while (transform != null) {
                if (managedObjects.Contains(transform.gameObject)) {
                    return true;
                }

                transform = transform.parent;
            }

            return false;
        }

        string GetPath(Transform transform) {
            var path = transform.name;
            while (transform.parent != null) {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }

            return path;
        }

        var healthManagers = Object.FindObjectsOfType<HealthManager>(true)
            .Where(hm => hm != null && hm.gameObject.scene == scene)
            .ToList();

        // An enemy whose descendant is a networked entity (e.g. the Entombed Husks of RestingGrounds_10 carry a
        // 'Dream Gate Set Lock' battle gate) must still be registered itself: it used to be skipped, so it stayed a
        // local object on every instance and hits of scene clients never reached the scene host (see
        // GenericEnemyRules). The managed descendant keeps its own entity; the client copy of the enemy drops it
        // (Entity.DestroyManagedChildren).
        var candidates = healthManagers
            .Select(hm => hm.gameObject)
            .Select(go => (
                Go: go,
                Path: GetPath(go.transform),
                Decision: GenericEnemyRules.Decide(
                    true,
                    IsManagedOrUnderManaged(go.transform),
                    go.GetComponentsInChildren<Transform>(true).Any(t => managedObjects.Contains(t.gameObject))
                )
            ))
            .Where(c => c.Decision == GenericEnemyRules.Decision.Register)
            // Sort for a deterministic ID assignment between clients
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => c.Go.transform.position.x)
            .ThenBy(c => c.Go.transform.position.y)
            .Select(c => c.Go)
            .ToList();

        var registered = 0;
        foreach (var go in candidates) {
            // Skip candidates that were already handled as part of a previous generic candidate
            if (IsManagedOrUnderManaged(go.transform)) {
                continue;
            }

            var managedDescendants = go.GetComponentsInChildren<Transform>(true)
                .Where(t => t.gameObject != go && managedObjects.Contains(t.gameObject))
                .Select(t => t.name)
                .ToList();
            if (managedDescendants.Count > 0) {
                Logger.Info(
                    $"HKMP_GENERIC '{GetPath(go.transform)}' contains networked entities ({string.Join(", ", managedDescendants)}), registering it as well");
            }

            var processor = new EntityProcessor {
                GameObject = go,
                IsSceneHost = IsSceneHost,
                IsSceneHostDetermined = IsSceneHostDetermined,
                LateLoad = lateLoad,
                AllowGeneric = true
            }.Process();

            foreach (var entity in processor.Entities) {
                registered++;
                managedObjects.Add(entity.Object.Host);
                managedObjects.Add(entity.Object.Client);
                Logger.Info($"HKMP_GENERIC registered generic enemy '{go.name}' ({GetPath(go.transform)}) as entity {entity.Id}, type {entity.Type}");
            }
        }

        Logger.Info(
            $"HKMP_GENERIC scene {scene.name}: {healthManagers.Count} HealthManager objects (incl. client copies), " +
            $"{registered} registered as generic, {_entities.Count} entities total");
    }

    /// <summary>
    /// Callback method for when the find method of FindGameObject is called. This is to look for objects that are
    /// normally not found by the action due to our entity system making certain objects inactive. If we notice that
    /// the find failed, but the name to look for was one of our host objects in the entity system, we set that object
    /// instead.
    /// </summary>
    private void OnFindGameObject(FindGameObject.orig_Find orig, HutongGames.PlayMaker.Actions.FindGameObject self) {
        orig(self);

        // If the object was found after the method executed, we skip
        if (self.store.Value != null) {
            return;
        }

        // Check for specific instances where we don't want to manually find the entity, because it messes
        // with the logic of the FSM
        if (self.State.Name.Equals("Can Roller?") && self.Fsm.Name.Equals("Blocker Control")) {
            return;
        }

        Logger.Debug($"OnFindGameObject, find failed: looking for '{self.objectName.Value}'");

        // If the object to find is tagged we skip, since this doesn't happen in our case
        if (self.withTag.Value != "Untagged") {
            return;
        }

        // Check if the name we are looking for is one of our registered entity's host objects
        foreach (var entity in _entities.Values) {
            var obj = entity.Object.Host;
            if (obj != null && obj.name == self.objectName.Value) {
                // The host object of the entity matches the name the action was looking for, so we set the variable
                self.store.Value = obj;

                Logger.Debug($"  Name matches host object of entity: ({entity.Id}, {entity.Type})");
                return;
            }
        }

        Logger.Debug("  Name did not match any entity");
    }
}
