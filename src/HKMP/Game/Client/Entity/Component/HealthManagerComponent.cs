using System;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using Modding;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the <see cref="HealthManager"/> component of the entity.
/// <para>Networking model:</para>
/// <list type="bullet">
/// <item>Hits: a non-host player that hits the client copy of the entity forwards the <see cref="HitInstance"/> to the
/// scene host (<see cref="EntityComponentType.Hit"/>), which applies it to the real (host) health manager. On the
/// scene host, hits on the host object coming from the replicated attacks of remote players are ignored, because the
/// attacking player forwards them itself (otherwise they would count twice).</item>
/// <item>Health: the scene host sends the HP of the host health manager whenever it changes
/// (<see cref="EntityComponentType.Health"/>); the server stores the last value so late joiners and new scene hosts
/// get the right HP.</item>
/// <item>Death: the scene host sends <see cref="EntityComponentType.Death"/>; the server stores it so players that
/// enter the scene later (or become scene host) get the enemy already dead. The persistent bool of the enemy
/// (semi-persistent "killed" flag of the scene data) is written on every client so it stays dead on reload.</item>
/// </list>
internal class HealthManagerComponent : EntityComponent {
    /// <summary>
    /// Name prefix of the remote player containers created by HKMP.
    /// </summary>
    private const string RemotePlayerContainerPrefix = "Player Container";

    /// <summary>
    /// Whether a hit forwarded by a remote player is currently being applied on the host object.
    /// </summary>
    private static bool _applyingRemoteHit;

    /// <summary>
    /// Leaf game object used as the source of hits forwarded by remote players. It is the grandchild of a root object
    /// that is not the knight, so the HKMP IL patches treat it as a remote hit (no soul gain, no recoil/shake).
    /// </summary>
    private static GameObject _remoteHitSource;

    /// <summary>
    /// Statistics for development tests: hits forwarded to the scene host (count and total damage), hits applied as
    /// scene host, and hits from replicated remote attacks that were ignored as scene host.
    /// </summary>
    public static int HitsSent, HitsSentDamage, HitsApplied, ReplicaHitsIgnored, SpellHitsSent, SpellHitsApplied;

    /// <summary>
    /// Statistics for development tests: hits forwarded to the scene host per entity ID (count, total damage).
    /// </summary>
    public static readonly System.Collections.Generic.Dictionary<ushort, (int Count, int Damage)> SentByEntity = new();

    /// <summary>
    /// Test counters on the scene host: hits forwarded by remote players and applied, per entity.
    /// </summary>
    public static readonly System.Collections.Generic.Dictionary<ushort, (int Count, int Damage)> AppliedByEntity = new();

    /// <summary>
    /// Test counters on the scene host: other hits that reached the host object (own player, environment, or
    /// remote effects that were not recognised as replicated, which would count twice), per entity.
    /// </summary>
    public static readonly System.Collections.Generic.Dictionary<ushort, (int Count, int Damage)> OtherHostHitsByEntity = new();

    /// <summary>
    /// Host-client pair of health manager components of the entity.
    /// </summary>
    private readonly HostClientPair<HealthManager> _healthManager;

    /// <summary>
    /// Boolean indicating whether the health manager of the client entity is allowed to die.
    /// </summary>
    private bool _allowDeath;

    /// <summary>
    /// The last value for the "invincible" variable of the health manager.
    /// </summary>
    private bool _lastInvincible;

    /// <summary>
    /// The last value for the "invincibleFromDirection" variable of the health manager.
    /// </summary>
    private int _lastInvincibleFromDirection;

    /// <summary>
    /// The last HP of the host health manager that was sent (scene host only).
    /// </summary>
    private int _lastSentHp;

    /// <summary>
    /// The last HP received from the scene host, or null if none was received.
    /// </summary>
    private int? _syncedHp;

    /// <summary>
    /// Whether this entity is known to be dead (through local death or networked death).
    /// </summary>
    public bool IsDead { get; private set; }

    /// <summary>
    /// The host health manager (may be null if destroyed).
    /// </summary>
    public HealthManager HostHealthManager => _healthManager.Host;

    /// <summary>
    /// The client health manager (may be null if destroyed).
    /// </summary>
    public HealthManager ClientHealthManager => _healthManager.Client;

    public HealthManagerComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<HealthManager> healthManager
    ) : base(netClient, entityId, gameObject) {
        _healthManager = healthManager;

        _lastInvincible = healthManager.Host.IsInvincible;
        _lastInvincibleFromDirection = healthManager.Host.InvincibleFromDirection;
        _lastSentHp = healthManager.Host.hp;

        On.HealthManager.Die += HealthManagerOnDie;
        On.HealthManager.Hit += HealthManagerOnHit;
        On.SpellFluke.DoDamage += SpellFlukeOnDoDamage;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Flukes (Flukenest) damage enemies with HealthManager.SubtractHealth/Die instead of HealthManager.Hit, so
    /// they need their own forwarding: the local player's flukes hitting the client copy are forwarded as a Spell
    /// hit; on the host object, flukes of remote players (replicated by HKMP) are ignored because the attacker
    /// forwards them.
    /// </summary>
    private void SpellFlukeOnDoDamage(On.SpellFluke.orig_DoDamage orig, SpellFluke self, GameObject obj, int upwardRecursionAmount, bool burst) {
        var hm = obj != null ? obj.GetComponent<HealthManager>() : null;
        if (hm == null || (hm != _healthManager.Host && hm != _healthManager.Client)) {
            orig(self, obj, upwardRecursionAmount, burst);
            return;
        }

        var damage = ReflectionHelper.GetField<SpellFluke, int>(self, "damage");
        var remote = IsRemotePlayerSource(self.gameObject);

        if (hm == _healthManager.Client) {
            if (!IsControlled) {
                orig(self, obj, upwardRecursionAmount, burst);
                return;
            }

            if (!remote && !hm.GetIsDead() && !hm.IsInvincible && damage > 0) {
                SendHit(new HitInstance {
                    Source = self.gameObject,
                    AttackType = AttackTypes.Spell,
                    DamageDealt = damage,
                    Direction = 0f,
                    MagnitudeMultiplier = 1f,
                    Multiplier = 1f,
                    IgnoreInvulnerable = true
                });
            }

            var hpBefore = hm.hp;
            orig(self, obj, upwardRecursionAmount, burst);
            if (!hm.GetIsDead()) {
                hm.hp = _syncedHp ?? hpBefore;
            }

            return;
        }

        // Host object
        if (IsControlled || remote) {
            if (remote && !IsControlled) {
                ReplicaHitsIgnored++;
                Logger.Info($"Ignoring fluke damage from replicated remote player on host entity ({EntityId}), the attacker forwards it");
            }

            if (burst) {
                typeof(SpellFluke).GetMethod("Burst", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(self, null);
            }

            return;
        }

        OtherHostHitsByEntity.TryGetValue(EntityId, out var other);
        OtherHostHitsByEntity[EntityId] = (other.Count + 1, other.Damage + damage);
        orig(self, obj, upwardRecursionAmount, burst);
    }

    /// <summary>
    /// Get a short description of the HP state of this entity for logging.
    /// </summary>
    public string Describe() {
        var host = _healthManager.Host;
        var client = _healthManager.Client;
        return $"hostHp={(host == null ? "null" : host.hp.ToString())} " +
               $"hostDead={(host == null ? "null" : host.GetIsDead().ToString())} " +
               $"hostActive={(host == null ? "null" : host.gameObject.activeInHierarchy.ToString())} " +
               $"clientHp={(client == null ? "null" : client.hp.ToString())} " +
               $"clientDead={(client == null ? "null" : client.GetIsDead().ToString())} " +
               $"clientActive={(client == null ? "null" : client.gameObject.activeInHierarchy.ToString())} " +
               $"syncedHp={(_syncedHp.HasValue ? _syncedHp.Value.ToString() : "none")} netDead={IsDead}";
    }

    /// <summary>
    /// Callback method for when the health manager dies.
    /// </summary>
    private void HealthManagerOnDie(
        On.HealthManager.orig_Die orig,
        HealthManager self,
        float? attackDirection,
        AttackTypes attackType,
        bool ignoreEvasion
    ) {
        if (self != _healthManager.Host && self != _healthManager.Client) {
            orig(self, attackDirection, attackType, ignoreEvasion);
            return;
        }

        if (self == _healthManager.Client) {
            if (!_allowDeath) {
                Logger.Info($"HealthManager Die was called on client entity ({EntityId})");
            } else {
                Logger.Info($"HealthManager Die was called on client entity ({EntityId}), but it is allowed death");

                orig(self, attackDirection, attackType, ignoreEvasion);

                _allowDeath = false;
            }

            return;
        }

        // The host object of a client (non-host) is never simulated, so do not let it die (or send deaths)
        if (IsControlled) {
            Logger.Info($"HealthManager Die was called on host object of controlled entity ({EntityId}), ignoring");
            return;
        }

        var wasDead = self.GetIsDead();

        Logger.Info($"HealthManager Die was called on host entity ({EntityId})");

        orig(self, attackDirection, attackType, ignoreEvasion);

        // Special deaths (e.g. bosses with a death FSM) do not set isDead and should not be networked as death
        if (wasDead || !self.GetIsDead()) {
            Logger.Info($"  Entity ({EntityId}) did not die (special death or already dead), not sending death");
            return;
        }

        IsDead = true;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Death
        };

        if (attackDirection.HasValue) {
            data.Packet.Write(true);
            data.Packet.Write(attackDirection.Value);
        } else {
            data.Packet.Write(false);
        }

        data.Packet.Write((byte) attackType);
        data.Packet.Write(ignoreEvasion);
        data.Packet.Write(self.gameObject.name);

        // Trailing bytes read by the server: whether the enemy is semi-persistent (respawns after resting at a
        // bench) and whether it has a persistent bool at all
        var pbi = self.GetComponent<PersistentBoolItem>();
        data.Packet.Write(pbi != null && pbi.semiPersistent);
        data.Packet.Write(pbi != null);

        Logger.Info($"HKMP_SYNC death sent: entity {EntityId} '{self.name}' persistent={pbi != null} semi={pbi != null && pbi.semiPersistent}");

        SendData(data);
    }

    /// <summary>
    /// Callback method for when a health manager is hit.
    /// </summary>
    private void HealthManagerOnHit(On.HealthManager.orig_Hit orig, HealthManager self, HitInstance hitInstance) {
        if (self != _healthManager.Host && self != _healthManager.Client) {
            orig(self, hitInstance);
            return;
        }

        if (self == _healthManager.Client) {
            if (!IsControlled) {
                // We are scene host, the client object should not be in use
                orig(self, hitInstance);
                return;
            }

            var source = hitInstance.Source;
            var canBeHit = !self.GetIsDead() &&
                           hitInstance.DamageDealt > 0 &&
                           ReflectionHelper.GetField<HealthManager, float>(self, "evasionByHitRemaining") <= 0f;

            if (canBeHit && IsLocalPlayerHit(hitInstance)) {
                SendHit(hitInstance);
            } else {
                Logger.Info(
                    $"HKMP_SYNC client hit not forwarded: entity {EntityId} '{self.name}' canBeHit={canBeHit} type={hitInstance.AttackType} dmg={hitInstance.DamageDealt} source='{(source != null ? source.name : "null")}' layer={(source != null ? source.layer : -1)}");
            }

            // Run the original for the local feedback (flash, effects, soul), but keep the HP authoritative from the
            // scene host. Death of the client object is blocked in the Die hook.
            var hpBefore = self.hp;
            orig(self, hitInstance);
            if (!self.GetIsDead()) {
                self.hp = _syncedHp ?? hpBefore;
            }

            return;
        }

        // self is the host object
        if (IsControlled) {
            // We are not scene host, the host object is not simulated
            return;
        }

        if (!_applyingRemoteHit && IsRemotePlayerSource(hitInstance.Source)) {
            ReplicaHitsIgnored++;
            Logger.Info($"Ignoring hit from replicated remote player attack on host entity ({EntityId}), the attacker forwards it");
            return;
        }

        if (!_applyingRemoteHit) {
            OtherHostHitsByEntity.TryGetValue(EntityId, out var other);
            OtherHostHitsByEntity[EntityId] = (other.Count + 1, other.Damage + hitInstance.DamageDealt);
            Logger.Info($"HKMP_SYNC host hit (not forwarded): entity {EntityId} '{self.name}' type={hitInstance.AttackType} dmg={hitInstance.DamageDealt} source='{(hitInstance.Source != null ? hitInstance.Source.name : "null")}'");
        }

        orig(self, hitInstance);
    }

    /// <summary>
    /// Whether the hit was dealt by the local player (nail, spells, charms), as opposed to the environment or the
    /// replicated attacks of remote players.
    /// </summary>
    private static bool IsLocalPlayerHit(HitInstance hitInstance) {
        var source = hitInstance.Source;
        if (source == null) {
            return false;
        }

        if (IsRemotePlayerSource(source)) {
            return false;
        }

        var hero = HeroController.instance != null ? HeroController.instance.gameObject : null;
        var transform = source.transform;
        while (transform != null) {
            if (hero != null && transform.gameObject == hero) {
                return true;
            }

            transform = transform.parent;
        }

        // Spells and charm attacks of the local player are spawned at the scene root (e.g. fireballs), they are on
        // the "Attack" layer (17) or have a player attack type
        return source.layer == 17 ||
               hitInstance.AttackType is AttackTypes.Spell or AttackTypes.SharpShadow or AttackTypes.NailBeam;
    }

    /// <summary>
    /// Whether the given hit source is part of a remote player (replicated attack effects of HKMP).
    /// </summary>
    private static bool IsRemotePlayerSource(GameObject source) {
        if (source == null) {
            return false;
        }

        if (source == _remoteHitSource) {
            return true;
        }

        var transform = source.transform;
        while (transform != null) {
            var go = transform.gameObject;
            if (go.name.StartsWith(RemotePlayerContainerPrefix)) {
                return true;
            }

            // Effects spawned at the root by HKMP for remote players carry an HKMP component (e.g. Fireball)
            foreach (var behaviour in go.GetComponents<MonoBehaviour>()) {
                if (behaviour == null) {
                    continue;
                }

                var ns = behaviour.GetType().Namespace;
                if (ns != null && ns.StartsWith("Hkmp")) {
                    return true;
                }
            }

            transform = transform.parent;
        }

        return false;
    }

    /// <summary>
    /// Send the given hit instance to the scene host.
    /// </summary>
    private void SendHit(HitInstance hit) {
        var data = new EntityNetworkData {
            Type = EntityComponentType.Hit
        };

        var sourcePos = hit.Source != null ? hit.Source.transform.position : _healthManager.Client.transform.position;

        data.Packet.Write((byte) hit.AttackType);
        data.Packet.Write(hit.DamageDealt);
        data.Packet.Write(hit.Direction);
        data.Packet.Write(hit.MagnitudeMultiplier);
        data.Packet.Write(hit.Multiplier);
        data.Packet.Write(hit.MoveAngle);
        data.Packet.Write(hit.IgnoreInvulnerable);
        data.Packet.Write(hit.CircleDirection);
        data.Packet.Write(hit.MoveDirection);
        data.Packet.Write(hit.IsExtraDamage);
        data.Packet.Write((byte) hit.SpecialType);
        data.Packet.Write(sourcePos.x);
        data.Packet.Write(sourcePos.y);

        Logger.Info(
            $"HKMP_SYNC hit sent: entity {EntityId} '{_healthManager.Client.name}' type={hit.AttackType} dmg={hit.DamageDealt} mult={hit.Multiplier} localHp={_healthManager.Client.hp}");

        HitsSent++;
        HitsSentDamage += hit.DamageDealt;
        if (hit.AttackType == AttackTypes.Spell) {
            SpellHitsSent++;
        }
        SentByEntity.TryGetValue(EntityId, out var sent);
        SentByEntity[EntityId] = (sent.Count + 1, sent.Damage + hit.DamageDealt);

        SendData(data);
    }

    /// <summary>
    /// Apply a hit received from a remote (non-host) player on the host health manager.
    /// </summary>
    private void ApplyRemoteHit(EntityNetworkData data) {
        var attackType = (AttackTypes) data.Packet.ReadByte();
        var damage = data.Packet.ReadInt();
        var direction = data.Packet.ReadFloat();
        var magnitude = data.Packet.ReadFloat();
        var multiplier = data.Packet.ReadFloat();
        var moveAngle = data.Packet.ReadFloat();
        var ignoreInvulnerable = data.Packet.ReadBool();
        var circleDirection = data.Packet.ReadBool();
        var moveDirection = data.Packet.ReadBool();
        var isExtraDamage = data.Packet.ReadBool();
        var specialType = (SpecialTypes) data.Packet.ReadByte();
        var sourceX = data.Packet.ReadFloat();
        var sourceY = data.Packet.ReadFloat();

        var host = _healthManager.Host;
        if (host == null) {
            Logger.Info($"HKMP_SYNC hit received for entity {EntityId} but host health manager is gone");
            return;
        }

        if (host.GetIsDead()) {
            Logger.Info($"HKMP_SYNC hit received for entity {EntityId} but it is already dead");
            return;
        }

        var source = GetRemoteHitSource();
        source.transform.position = new Vector3(sourceX, sourceY, host.transform.position.z);

        var hit = new HitInstance {
            Source = source,
            AttackType = attackType,
            DamageDealt = damage,
            Direction = direction,
            MagnitudeMultiplier = magnitude,
            Multiplier = multiplier,
            MoveAngle = moveAngle,
            IgnoreInvulnerable = ignoreInvulnerable,
            CircleDirection = circleDirection,
            MoveDirection = moveDirection,
            IsExtraDamage = isExtraDamage,
            SpecialType = specialType
        };

        var hpBefore = host.hp;

        // The attacker already respected the invulnerability window of its own copy, so don't drop the hit here
        // because of network timing
        ReflectionHelper.SetField(host, "evasionByHitRemaining", 0f);

        HitsApplied++;
        AppliedByEntity.TryGetValue(EntityId, out var applied);
        AppliedByEntity[EntityId] = (applied.Count + 1, applied.Damage + damage);
        if (attackType == AttackTypes.Spell) {
            SpellHitsApplied++;
        }

        _applyingRemoteHit = true;
        try {
            host.Hit(hit);
        } catch (Exception e) {
            Logger.Error($"Exception while applying remote hit on entity {EntityId}:\n{e}");
        } finally {
            _applyingRemoteHit = false;
        }

        Logger.Info(
            $"HKMP_SYNC hit applied on host: entity {EntityId} '{host.name}' type={attackType} dmg={damage} hp {hpBefore} -> {host.hp} dead={host.GetIsDead()}");
    }

    /// <summary>
    /// Get (or create) the game object used as source of forwarded hits.
    /// </summary>
    private static GameObject GetRemoteHitSource() {
        if (_remoteHitSource != null) {
            return _remoteHitSource;
        }

        var root = new GameObject("HKMP Remote Hit Root");
        Object.DontDestroyOnLoad(root);
        var middle = new GameObject("HKMP Remote Hit Attacks");
        middle.transform.SetParent(root.transform);
        _remoteHitSource = new GameObject("HKMP Remote Hit Source");
        _remoteHitSource.transform.SetParent(middle.transform);

        return _remoteHitSource;
    }

    /// <summary>
    /// Callback method for updates to check whether invincibility or health changes.
    /// </summary>
    private void OnUpdate() {
        if (_healthManager.Host == null) {
            return;
        }

        var data = new EntityNetworkData {
            Type = EntityComponentType.Invincibility
        };

        var shouldSend = false;

        var newInvincible = _healthManager.Host.IsInvincible;
        if (newInvincible != _lastInvincible) {
            _lastInvincible = newInvincible;
            shouldSend = true;
        }
        data.Packet.Write(newInvincible);

        var newInvincibleFromDir = _healthManager.Host.InvincibleFromDirection;
        if (newInvincibleFromDir != _lastInvincibleFromDirection) {
            _lastInvincibleFromDirection = newInvincibleFromDir;
            shouldSend = true;
        }
        data.Packet.Write((byte) newInvincibleFromDir);

        if (shouldSend && !IsControlled) {
            SendData(data);
        }

        if (IsControlled) {
            return;
        }

        var hp = _healthManager.Host.hp;
        if (hp != _lastSentHp) {
            _lastSentHp = hp;

            var healthData = new EntityNetworkData {
                Type = EntityComponentType.Health
            };
            healthData.Packet.Write(hp);

            Logger.Info($"HKMP_SYNC health sent: entity {EntityId} '{_healthManager.Host.name}' hp={hp}");

            SendData(healthData);
        }
    }

    /// <summary>
    /// Called when the local player becomes scene host and the host object takes over from the client object.
    /// Restores the synchronised HP (and death) on the host health manager.
    /// </summary>
    public void OnMakeHost() {
        var host = _healthManager.Host;
        if (host == null) {
            return;
        }

        if (_syncedHp.HasValue) {
            host.hp = _syncedHp.Value;
        }

        if (IsDead) {
            host.SetIsDead(true);
        }

        _lastSentHp = host.hp;

        Logger.Info($"HKMP_SYNC became host for entity {EntityId} '{host.name}': hp={host.hp} dead={host.GetIsDead()}");
    }

    /// <inheritdoc />
    public override void InitializeHost() {
        if (_healthManager.Host != null) {
            _lastSentHp = _healthManager.Host.hp;
        }
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (data.Type == EntityComponentType.Hit) {
            if (IsControlled) {
                // Only the scene host applies hits
                return;
            }

            ApplyRemoteHit(data);
            return;
        }

        if (data.Type == EntityComponentType.Health) {
            var hp = data.Packet.ReadInt();

            // As scene host we are authoritative, except for the state of the scene when we enter it
            if (!IsControlled && !alreadyInSceneUpdate) {
                return;
            }

            _syncedHp = hp;

            if (_healthManager.Client != null && !_healthManager.Client.GetIsDead()) {
                _healthManager.Client.hp = hp;
            }

            if (_healthManager.Host != null && !_healthManager.Host.GetIsDead()) {
                _healthManager.Host.hp = hp;
                if (!IsControlled) {
                    _lastSentHp = hp;
                }
            }

            Logger.Info($"HKMP_SYNC health received: entity {EntityId} hp={hp} alreadyInScene={alreadyInSceneUpdate}");
            return;
        }

        if (data.Type == EntityComponentType.Death) {
            if (!IsControlled && !alreadyInSceneUpdate) {
                return;
            }

            var attackDirection = new float?();
            if (data.Packet.ReadBool()) {
                attackDirection = data.Packet.ReadFloat();
            }

            var attackType = (AttackTypes) data.Packet.ReadByte();
            var ignoreEvasion = data.Packet.ReadBool();
            var objectName = data.Packet.ReadString();

            if (IsDead) {
                return;
            }

            var localName = _healthManager.Host != null
                ? _healthManager.Host.gameObject.name
                : _healthManager.Client != null ? _healthManager.Client.gameObject.name.Replace("(Clone)", "") : null;
            if (localName != null && objectName != localName) {
                Logger.Warn($"HKMP_SYNC death for entity {EntityId} is for '{objectName}' but local object is '{localName}' (entity IDs differ between clients), ignoring");
                return;
            }

            IsDead = true;

            Logger.Info($"HKMP_SYNC death received: entity {EntityId} alreadyInScene={alreadyInSceneUpdate} controlled={IsControlled}");

            if (alreadyInSceneUpdate || !IsControlled) {
                // The enemy died before we entered the scene: make it dead without death effects/drops
                ApplySilentDeath();
                return;
            }

            // Mark the (inactive) host object dead as well, in case we become scene host later
            if (_healthManager.Host != null) {
                _healthManager.Host.SetIsDead(true);
            }

            SaveDeathToSceneData();

            if (_healthManager.Client != null) {
                // Set a boolean to indicate that the client health manager is allowed to execute the Die method
                _allowDeath = true;
                _healthManager.Client.Die(attackDirection, attackType, ignoreEvasion);
            }

            return;
        }

        if (!IsControlled) {
            return;
        }

        if (data.Type == EntityComponentType.Invincibility) {
            var newInvincible = data.Packet.ReadBool();
            var newInvincibleFromDir = data.Packet.ReadByte();

            if (_healthManager.Host != null) {
                _healthManager.Host.IsInvincible = newInvincible;
                _healthManager.Host.InvincibleFromDirection = newInvincibleFromDir;
            }

            if (_healthManager.Client != null) {
                _healthManager.Client.IsInvincible = newInvincible;
                _healthManager.Client.InvincibleFromDirection = newInvincibleFromDir;
            }
        }
    }

    /// <summary>
    /// Make the entity dead without running the death effects: used when the death happened before the local player
    /// entered the scene.
    /// </summary>
    private void ApplySilentDeath() {
        SaveDeathToSceneData();

        foreach (var hm in new[] { _healthManager.Host, _healthManager.Client }) {
            if (hm == null) {
                continue;
            }

            hm.SetIsDead(true);
            hm.gameObject.SetActive(false);
        }

        Logger.Info($"HKMP_SYNC applied silent death to entity {EntityId}");
    }

    /// <summary>
    /// Write the persistent bool ("killed") of the enemy to the local scene data, with the ID of the original (host)
    /// object, so the enemy stays dead when the scene is loaded again (same as for the player that killed it).
    /// </summary>
    private void SaveDeathToSceneData() {
        var host = _healthManager.Host;
        if (host == null) {
            return;
        }

        var pbi = host.GetComponent<PersistentBoolItem>();
        if (pbi == null) {
            return;
        }

        var id = GetPersistentId(pbi, host.gameObject);
        var sceneName = global::GameManager.GetBaseSceneName(host.gameObject.scene.name);

        SceneData.instance.SaveMyState(new PersistentBoolData {
            id = id,
            sceneName = sceneName,
            activated = true,
            semiPersistent = pbi.semiPersistent
        });

        Logger.Info($"HKMP_SYNC saved death to scene data: ({id}, {sceneName}) semi={pbi.semiPersistent}");
    }

    /// <summary>
    /// Get the persistent ID used by the given persistent bool item.
    /// </summary>
    public static string GetPersistentId(PersistentBoolItem pbi, GameObject owner) {
        var data = ReflectionHelper.GetField<PersistentBoolItem, PersistentBoolData>(pbi, "persistentBoolData");
        if (data != null && !string.IsNullOrEmpty(data.id)) {
            return data.id;
        }

        return owner.name;
    }

    /// <inheritdoc />
    public override void Destroy() {
        On.HealthManager.Die -= HealthManagerOnDie;
        On.HealthManager.Hit -= HealthManagerOnHit;
        On.SpellFluke.DoDamage -= SpellFlukeOnDoDamage;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
