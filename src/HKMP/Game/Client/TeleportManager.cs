using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Settings;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Ui;
using Hkmp.Util;
using Modding.Utils;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client;

/// <summary>
/// Client side of teleporting to another player: sends the request to the server and, when the server answers with
/// the scene and position of the target, moves the local hero there (changing scene first if needed).
/// </summary>
internal class TeleportManager {
    /// <summary>
    /// Entry gate used for the scene change. It is the game's own dream gate entry, which puts the hero in the scene
    /// without needing the name of a transition gate of the target scene; the position is then overridden.
    /// </summary>
    private const string EntryGate = "dreamGate";

    private readonly NetClient _netClient;
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;
    private readonly ServerSettings _serverSettings;

    private ushort? _lastPickedId;
    private string _pendingScene;
    private Vector3 _pendingPosition;

    public TeleportManager(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        ServerSettings serverSettings
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _serverSettings = serverSettings;
    }

    /// <summary>
    /// Whether teleporting is possible right now (connected and allowed by the server).
    /// </summary>
    public bool IsAvailable => _netClient.IsConnected && _serverSettings.AllowTeleport;

    /// <summary>
    /// Request a teleport to the next other player (cycles through the players on repeated calls) or, if a name is
    /// given, to that player.
    /// </summary>
    /// <param name="name">Optional player name.</param>
    public void Request(string name = null) {
        if (!_netClient.IsConnected) {
            return;
        }

        if (!_serverSettings.AllowTeleport) {
            UiManager.InternalChatBox.AddMessage("Teleporting is disabled on this server");
            return;
        }

        var players = _playerData.Values.Select(p => (p.Id, p.Username)).ToList();
        ushort targetId;
        string targetName;
        if (string.IsNullOrWhiteSpace(name)) {
            if (!TeleportTargetSelector.TryPickNext(players, _lastPickedId, out var picked)) {
                UiManager.InternalChatBox.AddMessage("There are no other players");
                return;
            }

            targetId = picked.Id;
            targetName = picked.Name;
        } else {
            if (!TeleportTargetSelector.TryFindByName(players, name, out targetId)) {
                UiManager.InternalChatBox.AddMessage($"No unique player found for \"{name}\"");
                return;
            }

            targetName = _playerData[targetId].Username;
        }

        _lastPickedId = targetId;
        UiManager.InternalChatBox.AddMessage($"Teleporting to {targetName}...");
        _netClient.UpdateManager.SetTeleportRequest(targetId);
    }

    /// <summary>
    /// Callback for when the server tells us where to teleport to.
    /// </summary>
    /// <param name="teleport">The packet data.</param>
    public void OnPlayerTeleport(PlayerTeleport teleport) {
        var gm = global::GameManager.instance;
        if (!gm || gm.IsInSceneTransition || !HeroController.instance) {
            Logger.Info("Teleport ignored: game is not in a state to teleport");
            return;
        }

        var position = new Vector3(teleport.X, teleport.Y, HeroController.instance.transform.position.z);

        if (SceneUtil.GetCurrentSceneName() == teleport.SceneName) {
            MoveHero(position);
            return;
        }

        _pendingScene = teleport.SceneName;
        _pendingPosition = position;
        CustomHooks.AfterEnterSceneHeroTransformed -= OnHeroEnteredScene;
        CustomHooks.AfterEnterSceneHeroTransformed += OnHeroEnteredScene;

        gm.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
            SceneName = teleport.SceneName,
            EntryGateName = EntryGate,
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = global::GameManager.SceneLoadVisualizations.Default,
            AlwaysUnloadUnusedAssets = false
        });
    }

    private void OnHeroEnteredScene() {
        CustomHooks.AfterEnterSceneHeroTransformed -= OnHeroEnteredScene;

        var scene = _pendingScene;
        _pendingScene = null;
        if (scene == null || SceneUtil.GetCurrentSceneName() != scene) {
            return;
        }

        MoveHero(_pendingPosition);
    }

    private static void MoveHero(Vector3 position) {
        var hero = HeroController.instance;
        if (!hero) {
            return;
        }

        hero.transform.SetPosition2D(position.x, position.y);
        var body = hero.GetComponent<Rigidbody2D>();
        if (body) {
            body.linearVelocity = Vector2.zero;
        }
    }

    /// <summary>
    /// Forget pending state on disconnect.
    /// </summary>
    public void OnDisconnect() {
        _pendingScene = null;
        _lastPickedId = null;
        CustomHooks.AfterEnterSceneHeroTransformed -= OnHeroEnteredScene;
    }
}
