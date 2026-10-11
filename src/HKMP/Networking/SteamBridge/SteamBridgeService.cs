using System;
using Hkmp.Game.Settings;
using Hkmp.Logging;
using Hkmp.Util;
using Steamworks;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Networking.SteamBridge;

/// <summary>
/// Entry point of the Steam bridge for the mod (main thread API). Lets players connect to a host through the Steam
/// relay network using a short join code instead of an IP address and an open port, without changing the HKMP
/// protocol: the bridges only forward HKMP's UDP datagrams between local sockets and a Steam connection.
/// <para>
/// Steam callbacks (needed to accept incoming connections) are dispatched by the game itself:
/// <c>SteamOnlineSubsystem.Update</c> calls <c>SteamAPI.RunCallbacks</c> every frame.
/// </para>
/// </summary>
internal class SteamBridgeService {
    /// <summary>
    /// The mod settings (for <see cref="ModSettings.SteamBridgeEnabled"/>).
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The running host bridge, if any.
    /// </summary>
    private SteamBridgeHost _host;

    /// <summary>
    /// The running client bridge, if any.
    /// </summary>
    private SteamBridgeClient _client;

    /// <summary>
    /// Incremented for every client bridge, so that late callbacks of an old one are ignored.
    /// </summary>
    private int _clientGeneration;

    /// <summary>
    /// Whether relay network access was requested already.
    /// </summary>
    private bool _relayInitialized;

    /// <summary>
    /// Development only: allow joining the own code (two game instances on one Steam account).
    /// </summary>
    internal static bool AllowOwnCode { get; set; }

    /// <summary>
    /// Whether the Steam bridge is enabled in the settings.
    /// </summary>
    public bool IsEnabled => _modSettings.SteamBridgeEnabled;

    /// <summary>
    /// Whether the host bridge is running.
    /// </summary>
    public bool IsHosting => _host != null;

    public SteamBridgeService(ModSettings modSettings) {
        _modSettings = modSettings;
    }

    /// <summary>
    /// Initialize: start relay network access early so hosting/joining is faster later. No-op if disabled or if
    /// Steam is not available.
    /// </summary>
    public void Initialize() {
        if (!IsEnabled) {
            Logger.Info("Steam bridge: disabled in settings");
            return;
        }

        if (TryGetOwnCode(out var code, out var error)) {
            Logger.Info($"Steam bridge: available, join code of this account: {code}");
            EnsureRelayAccess();
        } else {
            Logger.Info($"Steam bridge: not available ({error})");
        }
    }

    /// <summary>
    /// Try to get the SteamID64 of the logged in user.
    /// </summary>
    /// <param name="steamId">The SteamID64.</param>
    /// <param name="error">Reason if not available.</param>
    /// <returns>True if Steam is running and initialized by the game.</returns>
    public static bool TryGetOwnSteamId(out ulong steamId, out string error) {
        steamId = 0;
        try {
            if (!SteamAPI.IsSteamRunning()) {
                error = "Steam is not running / Steam no está abierto";
                return false;
            }

            var id = SteamUser.GetSteamID();
            if (!id.IsValid()) {
                error = "Not logged in to Steam / Sin sesión en Steam";
                return false;
            }

            steamId = id.m_SteamID;
            error = null;
            return true;
        } catch (Exception e) {
            // InvalidOperationException if the game did not initialize Steam, DllNotFoundException without steam_api
            error = "Steam not available / Steam no disponible";
            Logger.Debug($"Steam bridge: Steam not available: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Try to get the join code of the logged in user.
    /// </summary>
    /// <param name="code">The formatted code.</param>
    /// <param name="error">Reason if not available.</param>
    /// <returns>True if a code is available.</returns>
    public bool TryGetOwnCode(out string code, out string error) {
        code = null;
        if (!TryGetOwnSteamId(out var steamId, out error)) {
            return false;
        }

        if (!SteamJoinCode.TryEncode(steamId, out code)) {
            error = "Unsupported Steam account / Cuenta de Steam no compatible";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Request access to the Steam relay network (once).
    /// </summary>
    private void EnsureRelayAccess() {
        if (_relayInitialized) {
            return;
        }

        try {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _relayInitialized = true;
            Logger.Info("Steam bridge: relay network access requested");
        } catch (Exception e) {
            Logger.Warn($"Steam bridge: InitRelayNetworkAccess failed: {e.Message}");
        }
    }

    /// <summary>
    /// Start the host bridge for a HKMP server that runs on the given local port.
    /// </summary>
    /// <param name="hkmpPort">The UDP port of the local HKMP server.</param>
    /// <param name="password">Optional password.</param>
    /// <param name="code">The join code to share.</param>
    /// <param name="error">Reason if it could not be started.</param>
    /// <returns>True if the host bridge is running.</returns>
    public bool StartHost(int hkmpPort, string password, out string code, out string error) {
        StopHost();

        code = null;
        if (!IsEnabled) {
            error = "Steam bridge disabled / Puente Steam desactivado";
            return false;
        }

        if (!TryGetOwnCode(out code, out error)) {
            return false;
        }

        EnsureRelayAccess();

        try {
            _host = SteamBridgeHost.Start(hkmpPort, password);
            Logger.Info($"Steam bridge: hosting with join code {code}");
            return true;
        } catch (Exception e) {
            Logger.Error($"Steam bridge: could not start host bridge:\n{e}");
            error = "Could not open Steam socket / No se pudo abrir el socket de Steam";
            _host = null;
            return false;
        }
    }

    /// <summary>
    /// Stop the host bridge if it is running.
    /// </summary>
    public void StopHost() {
        if (_host == null) {
            return;
        }

        var host = _host;
        _host = null;

        try {
            host.Dispose();
        } catch (Exception e) {
            Logger.Error($"Steam bridge: error stopping host bridge:\n{e}");
        }
    }

    /// <summary>
    /// Connect the client bridge to the host with the given join code.
    /// </summary>
    /// <param name="code">The join code entered by the user.</param>
    /// <param name="password">Optional password.</param>
    /// <param name="onReady">Invoked on the main thread with the local port once the host accepted.</param>
    /// <param name="onFailed">Invoked on the main thread with a message if connecting failed.</param>
    /// <param name="error">Reason if connecting could not even start.</param>
    /// <returns>True if connecting started.</returns>
    public bool Join(string code, string password, Action<int> onReady, Action<string> onFailed, out string error) {
        StopClient();

        if (!IsEnabled) {
            error = "Steam bridge disabled / Puente Steam desactivado";
            return false;
        }

        if (!SteamJoinCode.TryDecode(code, out var hostSteamId, out var codeError)) {
            error = codeError switch {
                SteamJoinCodeError.Empty => "Enter a code / Introduce un código",
                SteamJoinCodeError.ChecksumMismatch => "Code has a typo / El código tiene un error",
                _ => "Invalid code / Código no válido"
            };
            return false;
        }

        if (!TryGetOwnSteamId(out var ownSteamId, out error)) {
            return false;
        }

        if (ownSteamId == hostSteamId && !AllowOwnCode) {
            error = "That is your own code / Es tu propio código";
            return false;
        }

        EnsureRelayAccess();

        var generation = ++_clientGeneration;
        try {
            _client = SteamBridgeClient.Connect(hostSteamId, password, (state, message) => {
                // Worker thread: hand over to the main thread
                ThreadUtil.RunActionOnMainThread(() => OnClientStateChanged(generation, state, message, onReady, onFailed));
            });
        } catch (Exception e) {
            Logger.Error($"Steam bridge: could not start client bridge:\n{e}");
            error = "Could not open Steam connection / No se pudo abrir la conexión de Steam";
            _client = null;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Handle a state change of the client bridge on the main thread.
    /// </summary>
    private void OnClientStateChanged(
        int generation,
        SteamBridgeClientState state,
        string message,
        Action<int> onReady,
        Action<string> onFailed
    ) {
        if (generation != _clientGeneration || _client == null) {
            return;
        }

        switch (state) {
            case SteamBridgeClientState.Ready:
                onReady?.Invoke(_client.LocalPort);
                break;
            case SteamBridgeClientState.Failed:
                StopClient();
                onFailed?.Invoke(message);
                break;
            case SteamBridgeClientState.Closed:
                // After the HKMP connection was established: HKMP notices the lost connection itself (timeout)
                Logger.Info($"Steam bridge: connection to host closed: {message}");
                StopClient();
                break;
        }
    }

    /// <summary>
    /// Stop the client bridge if it is running.
    /// </summary>
    public void StopClient() {
        if (_client == null) {
            return;
        }

        var client = _client;
        _client = null;
        _clientGeneration++;

        try {
            client.Dispose();
        } catch (Exception e) {
            Logger.Error($"Steam bridge: error stopping client bridge:\n{e}");
        }
    }

    /// <summary>
    /// Stop everything (on application quit).
    /// </summary>
    public void Shutdown() {
        StopClient();
        StopHost();
    }

    /// <summary>
    /// Copy text to the system clipboard.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>True if it was copied.</returns>
    public static bool CopyToClipboard(string text) {
        try {
            GUIUtility.systemCopyBuffer = text;
            return true;
        } catch (Exception e) {
            Logger.Warn($"Steam bridge: could not copy to clipboard: {e.Message}");
            return false;
        }
    }
}
