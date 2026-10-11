using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

/// <summary>
/// Class for a generalizable part of an entity that requires networking for a specific feature.
/// </summary>
internal abstract class EntityComponent {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;
    /// <summary>
    /// The ID of the entity.
    /// </summary>
    private readonly ushort _entityId;

    /// <summary>
    /// Host-client pair of the game objects of the entity.
    /// </summary>
    protected readonly HostClientPair<GameObject> GameObject;

    /// <summary>
    /// The ID of the entity this component belongs to.
    /// </summary>
    protected ushort EntityId => _entityId;

    /// <summary>
    /// Whether the entity is controlled.
    /// </summary>
    public bool IsControlled { get; set; }

    protected EntityComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) {
        _netClient = netClient;
        _entityId = entityId;

        GameObject = gameObject;

        IsControlled = true;
    }

    /// <summary>
    /// Send the given <see cref="EntityNetworkData"/>.
    /// </summary>
    /// <param name="data">The data to send.</param>
    protected void SendData(EntityNetworkData data) {
        _netClient.UpdateManager.AddEntityData(_entityId, data);
    }

    /// <summary>
    /// Initializes the entity component when the client user is the scene host.
    /// </summary>
    public abstract void InitializeHost();

    /// <summary>
    /// Update the entity component with the given data.
    /// </summary>
    /// <param name="data">The data to update with.</param>
    /// <param name="alreadyInSceneUpdate">Whether this data is from an already in scene packet.</param>
    public abstract void Update(EntityNetworkData data, bool alreadyInSceneUpdate);
    /// <summary>
    /// Destroy the entity component.
    /// </summary>
    public abstract void Destroy();
}

/// <summary>
/// Enum for data types.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum EntityComponentType : ushort {
    Fsm = 0,
    Death,
    Invincibility,
    Rotation,
    Collider,
    DamageHero,
    MeshRenderer,
    Velocity,
    GravityScale,
    ZPosition,
    Climber,
    EnemySpawner,
    ChildrenActivation,
    SpawnJar,
    SpriteRenderer,
    ChallengePrompt,
    Music,
    FlipPlatform,
    DreamPlatform,
    HazardRespawn,
    /// <summary>
    /// A hit (HitInstance) dealt by a non-host player to the client copy of an entity, forwarded to the scene host.
    /// Never persisted by the server.
    /// </summary>
    Hit,
    /// <summary>
    /// The current HP of the (host) health manager of an entity, sent by the scene host whenever it changes.
    /// </summary>
    Health,
    /// <summary>
    /// Sent (with entity ID <see cref="ushort.MaxValue"/>) when the local player rests at a bench, which resets
    /// semi-persistent enemies. Lets the server forget stored semi-persistent deaths of unoccupied scenes.
    /// </summary>
    ResetSemiPersistent
}
