using System;
using System.Net;
using Hkmp.Logging;
using Steamworks;

namespace Hkmp.Networking.SteamBridge;

/// <summary>
/// Steam side of the host bridge: a P2P listen socket (virtual port 0) whose incoming connections are handed to a
/// <see cref="SteamBridgeHostCore"/> that forwards them to the local HKMP server.
/// Create and dispose from the thread that runs the Steam callbacks (the Unity main thread in the game).
/// </summary>
internal sealed class SteamBridgeHost : IDisposable {
    /// <summary>
    /// The Steam virtual port used by the bridge.
    /// </summary>
    public const int VirtualPort = 0;

    private readonly HSteamListenSocket _listenSocket;
    private readonly SteamBridgeHostCore _core;
    private readonly SteamBridgeWorker _worker;
    private Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback;
    private bool _disposed;

    /// <summary>
    /// The core, exposed for diagnostics.
    /// </summary>
    public SteamBridgeHostCore Core => _core;

    private SteamBridgeHost(HSteamListenSocket listenSocket, SteamBridgeHostCore core) {
        _listenSocket = listenSocket;
        _core = core;
        _worker = new SteamBridgeWorker("HKMP Steam bridge host", core);
    }

    /// <summary>
    /// Open the listen socket and start forwarding to the HKMP server on the given local port.
    /// </summary>
    /// <param name="hkmpPort">The UDP port of the local HKMP server.</param>
    /// <param name="password">Optional password.</param>
    /// <param name="limits">Optional limits.</param>
    /// <returns>The running host bridge.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the listen socket could not be created.</exception>
    public static SteamBridgeHost Start(int hkmpPort, string password, SteamBridgeLimits limits = null) {
        var listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
        if (listenSocket == HSteamListenSocket.Invalid) {
            throw new InvalidOperationException("CreateListenSocketP2P failed");
        }

        var core = new SteamBridgeHostCore(new IPEndPoint(IPAddress.Loopback, hkmpPort), password, limits);
        var host = new SteamBridgeHost(listenSocket, core);

        try {
            host._statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(host.OnStatusChanged);
            host._worker.Start();
        } catch {
            host.Dispose();
            throw;
        }

        Logger.Info(
            $"Steam bridge host: listening (virtual port {VirtualPort}) for HKMP server on port {hkmpPort}, " +
            $"password {(string.IsNullOrEmpty(SteamBridgeProtocol.TrimPassword(password)) ? "off" : "on")}"
        );
        return host;
    }

    /// <summary>
    /// Callback for connection state changes. Steam reports every connection of the process here, so only
    /// connections on our listen socket are handled.
    /// </summary>
    private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t data) {
        try {
            if (_disposed || data.m_info.m_hListenSocket != _listenSocket) {
                return;
            }

            if (data.m_info.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting ||
                data.m_eOldState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None) {
                // Other transitions are observed by the link itself
                return;
            }

            var connection = data.m_hConn;
            var remote = data.m_info.m_identityRemote.GetSteamID64();
            var link = new SteamBridgeLink(connection, $"steam:{remote}");

            if (!_core.TryAdd(link)) {
                Logger.Info($"Steam bridge host: refusing connection from {remote}, too many connections");
                SteamNetworkingSockets.CloseConnection(
                    connection,
                    SteamBridgeProtocol.CloseReasonBase + (int) SteamBridgeResult.ServerFull,
                    "Host is full",
                    false
                );
                return;
            }

            var result = SteamNetworkingSockets.AcceptConnection(connection);
            if (result != EResult.k_EResultOK) {
                // The core sees the closed state and cleans up the link
                Logger.Warn($"Steam bridge host: AcceptConnection for {remote} failed: {result}");
            }
        } catch (Exception e) {
            Logger.Error($"Steam bridge host: exception in status callback:\n{e}");
        }
    }

    /// <summary>
    /// Stop forwarding, close all connections and the listen socket.
    /// </summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;

        try {
            _statusCallback?.Dispose();
        } catch (Exception e) {
            Logger.Warn($"Steam bridge host: error unregistering callback: {e.Message}");
        }

        _statusCallback = null;

        if (_worker.IsRunning) {
            if (!_worker.Stop()) {
                Logger.Warn("Steam bridge host: worker did not stop in time");
            }
        } else {
            // Never started: dispose the core ourselves
            _core.Dispose();
        }

        try {
            SteamNetworkingSockets.CloseListenSocket(_listenSocket);
        } catch (Exception e) {
            Logger.Warn($"Steam bridge host: error closing listen socket: {e.Message}");
        }

        Logger.Info("Steam bridge host: stopped");
    }
}
