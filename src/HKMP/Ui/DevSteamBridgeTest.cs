using System;
using System.Collections;
using System.Net;
using Hkmp.Networking.SteamBridge;
using Hkmp.Util;
using Steamworks;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui;

/// <summary>
/// Development/test helper for the Steam bridge (inactive unless one of the options is present). Two instances of
/// the game on one Steam account cannot connect to each other through Steam, so these options exercise the bridge
/// inside a single game:
/// <list type="bullet">
/// <item><c>-hkmp-steam-pair host:port</c>: create a Steam networking socket pair (network loopback, encrypted like
/// a real connection), put the host bridge on one end (forwarding to the HKMP server at host:port) and the client
/// bridge on the other; <c>-hkmp-connect</c> then connects the game's HKMP client to the client bridge port.</item>
/// <item><c>-hkmp-steam-host-test</c>: open (and after 10 s close) the real P2P listen socket of the host bridge.</item>
/// </list>
/// Log lines start with <c>HKMP_STEAM</c>.
/// </summary>
internal static class DevSteamBridgeTest {
    private static SteamBridgeHostCore _host;
    private static SteamBridgeClientCore _client;
    private static SteamBridgeWorker _hostWorker;
    private static SteamBridgeWorker _clientWorker;

    /// <summary>
    /// Whether the socket pair bridge was requested.
    /// </summary>
    public static bool PairRequested => GetOption("-hkmp-steam-pair") != null;

    /// <summary>
    /// The client bridge state of the socket pair, if running.
    /// </summary>
    public static SteamBridgeClientState? PairState => _client?.State;

    /// <summary>
    /// Start the requested tests.
    /// </summary>
    /// <param name="steamBridge">The Steam bridge service.</param>
    /// <returns>The local port of the client bridge if the socket pair was started, otherwise -1.</returns>
    public static int TryStart(SteamBridgeService steamBridge) {
        var shot = GetOption("-hkmp-menu-shot");
        if (shot != null) {
            MonoBehaviourUtil.Instance.StartCoroutine(MenuShot(shot));
        }

        var hostTo = GetOption("-hkmp-steam-host-to");
        if (hostTo != null && int.TryParse(hostTo, out var hostPort)) {
            // Host bridge in front of an external HKMP server (e.g. HKMPServer.exe) without hosting in-game
            var started = steamBridge.StartHost(hostPort, GetOption("-hkmp-steam-password") ?? "", out var code, out var error);
            Logger.Info($"HKMP_STEAM: host bridge to port {hostPort}: {started}, code {code}, error {error}");
        }

        if (Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals("-hkmp-steam-host-test", StringComparison.OrdinalIgnoreCase))) {
            MonoBehaviourUtil.Instance.StartCoroutine(HostTest(steamBridge));
        }

        var pair = GetOption("-hkmp-steam-pair");
        if (pair == null) {
            return -1;
        }

        try {
            var colon = pair.LastIndexOf(':');
            var target = new IPEndPoint(IPAddress.Parse(pair.Substring(0, colon)), int.Parse(pair.Substring(colon + 1)));

            var identity1 = new SteamNetworkingIdentity();
            var identity2 = new SteamNetworkingIdentity();
            identity1.SetSteamID(SteamUser.GetSteamID());
            identity2.SetSteamID(SteamUser.GetSteamID());
            if (!SteamNetworkingSockets.CreateSocketPair(out var clientConnection, out var hostConnection, true, ref identity1, ref identity2)) {
                Logger.Error("HKMP_STEAM: CreateSocketPair failed");
                return -1;
            }

            _host = new SteamBridgeHostCore(target, "dev");
            _host.TryAdd(new SteamBridgeLink(hostConnection, "pair:host"));
            _client = new SteamBridgeClientCore(new SteamBridgeLink(clientConnection, "pair:client"), "dev");
            _hostWorker = new SteamBridgeWorker("HKMP Steam pair host", _host);
            _clientWorker = new SteamBridgeWorker("HKMP Steam pair client", _client);
            _hostWorker.Start();
            _clientWorker.Start();

            Logger.Info($"HKMP_STEAM: socket pair bridge to {target}, local port {_client.LocalPort}");
            MonoBehaviourUtil.Instance.StartCoroutine(ReportPair());
            return _client.LocalPort;
        } catch (Exception e) {
            Logger.Error($"HKMP_STEAM: could not start socket pair bridge:\n{e}");
            return -1;
        }
    }

    private static IEnumerator ReportPair() {
        var lastState = _client.State;
        Logger.Info($"HKMP_STEAM: pair state {lastState}");
        while (true) {
            for (var i = 0; i < 10 && _client.State == lastState; i++) {
                yield return new WaitForSecondsRealtime(1f);
            }

            lastState = _client.State;
            Logger.Info(
                $"HKMP_STEAM: pair state {lastState}, datagrams client->host {_client.ForwardedToHost}, " +
                $"host->client {_client.ForwardedToLocal}, host active {_host.ActiveCount}"
            );

            if (lastState is SteamBridgeClientState.Failed or SteamBridgeClientState.Closed) {
                yield break;
            }
        }
    }

    /// <summary>
    /// Take a screenshot of the HKMP connect menu (<c>-hkmp-menu-shot file.png</c>, used with <c>-hkmp-menu</c>).
    /// </summary>
    private static IEnumerator MenuShot(string path) {
        yield return new WaitUntil(() =>
            UIManager.instance != null && UIManager.instance.menuState == GlobalEnums.MainMenuState.DYNAMIC_MENU
        );
        yield return new WaitForSecondsRealtime(2f);
        ScreenCapture.CaptureScreenshot(path);
        Logger.Info($"HKMP_STEAM: menu screenshot {path}");
    }

    private static IEnumerator HostTest(SteamBridgeService steamBridge) {
        yield return new WaitForSecondsRealtime(2f);

        var started = steamBridge.StartHost(26999, "dev", out var code, out var error);
        Logger.Info($"HKMP_STEAM: host bridge start: {started}, code {code}, error {error}");
        yield return new WaitForSecondsRealtime(10f);
        steamBridge.StopHost();
        Logger.Info($"HKMP_STEAM: host bridge stopped, hosting={steamBridge.IsHosting}");
    }

    private static string GetOption(string option) {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++) {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase)) {
                return args[i + 1];
            }
        }

        return null;
    }
}
