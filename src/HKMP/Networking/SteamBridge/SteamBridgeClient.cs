using System;
using Hkmp.Logging;
using Steamworks;

namespace Hkmp.Networking.SteamBridge;

/// <summary>
/// Steam side of the client bridge: a P2P connection to the host (through the Steam relays) and a local UDP port
/// for the HKMP client. Does not need Steam callbacks: the connection state is polled.
/// </summary>
internal sealed class SteamBridgeClient : IDisposable {
    private readonly SteamBridgeClientCore _core;
    private readonly SteamBridgeWorker _worker;
    private SteamBridgeClientState _lastReportedState;
    private bool _disposed;

    /// <summary>
    /// Invoked on the worker thread whenever <see cref="SteamBridgeClientCore.State"/> changes.
    /// </summary>
    public event Action<SteamBridgeClientState, string> StateChanged;

    /// <summary>
    /// The core, exposed for diagnostics.
    /// </summary>
    public SteamBridgeClientCore Core => _core;

    /// <summary>
    /// The local UDP port on 127.0.0.1 that the HKMP client should connect to.
    /// </summary>
    public int LocalPort => _core.LocalPort;

    /// <summary>
    /// The current state.
    /// </summary>
    public SteamBridgeClientState State => _core.State;

    private SteamBridgeClient(SteamBridgeClientCore core) {
        _core = core;
        _lastReportedState = core.State;
        _worker = new SteamBridgeWorker("HKMP Steam bridge client", core) {
            AfterPump = CheckStateChange
        };
    }

    /// <summary>
    /// Start connecting to the host with the given SteamID64.
    /// </summary>
    /// <param name="hostSteamId">The SteamID64 of the host.</param>
    /// <param name="password">Optional password.</param>
    /// <param name="stateChanged">Handler for state changes (invoked on the worker thread).</param>
    /// <param name="limits">Optional limits.</param>
    /// <returns>The client bridge.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the connection could not be created.</exception>
    public static SteamBridgeClient Connect(
        ulong hostSteamId,
        string password,
        Action<SteamBridgeClientState, string> stateChanged,
        SteamBridgeLimits limits = null
    ) {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(hostSteamId);

        var connection = SteamNetworkingSockets.ConnectP2P(ref identity, SteamBridgeHost.VirtualPort, 0, null);
        if (connection == HSteamNetConnection.Invalid) {
            throw new InvalidOperationException("ConnectP2P failed");
        }

        var link = new SteamBridgeLink(connection, $"steam:{hostSteamId}");
        SteamBridgeClientCore core;
        try {
            core = new SteamBridgeClientCore(link, password, limits);
        } catch {
            link.Close(SteamBridgeProtocol.CloseReasonBase, "local socket error");
            throw;
        }

        var client = new SteamBridgeClient(core);
        client.StateChanged += stateChanged;
        client._worker.Start();

        Logger.Info($"Steam bridge client: connecting to {hostSteamId}, local port {core.LocalPort}");
        return client;
    }

    private void CheckStateChange() {
        var state = _core.State;
        if (state == _lastReportedState) {
            return;
        }

        _lastReportedState = state;
        try {
            StateChanged?.Invoke(state, _core.FailureMessage);
        } catch (Exception e) {
            Logger.Error($"Steam bridge client: exception in state handler:\n{e}");
        }
    }

    /// <summary>
    /// Stop forwarding and close the connection.
    /// </summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        StateChanged = null;

        if (_worker.IsRunning) {
            if (!_worker.Stop()) {
                Logger.Warn("Steam bridge client: worker did not stop in time");
            }
        } else {
            _core.Dispose();
        }

        Logger.Info("Steam bridge client: stopped");
    }
}
