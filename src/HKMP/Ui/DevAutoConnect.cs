using System;
using System.Collections;
using GlobalEnums;
using Hkmp.Networking.Client;
using Hkmp.Networking.SteamBridge;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui;

/// <summary>
/// Development/test helper that drives the HKMP main-menu flow without clicking, configured from the command line
/// (or environment variables). Inactive unless one of the options is present.
/// <para>Options (command line, case-insensitive) / environment variable equivalents:</para>
/// <list type="bullet">
/// <item><c>-hkmp-menu</c> / <c>HKMP_MENU=1</c>: open the HKMP multiplayer menu once the main menu is ready.</item>
/// <item><c>-hkmp-connect host[:port]</c> / <c>HKMP_CONNECT</c>: open the menu and connect to the server. With
/// FullSynchronisation enabled on the server the client receives the server save and enters the game by itself.</item>
/// <item><c>-hkmp-name name</c> / <c>HKMP_NAME</c>: username to connect with (default: ModSettings.Username or
/// "Player").</item>
/// </list>
/// </summary>
internal static class DevAutoConnect {
    /// <summary>
    /// Default HKMP port.
    /// </summary>
    private const int DefaultPort = 26950;

    /// <summary>
    /// Maximum number of connection attempts.
    /// </summary>
    private const int MaxAttempts = 12;

    /// <summary>
    /// Seconds to wait between connection attempts.
    /// </summary>
    private const float RetryDelay = 5f;

    /// <summary>
    /// Start the auto-connect logic if it has been requested through the command line or environment.
    /// </summary>
    /// <param name="uiManager">The HKMP UI manager.</param>
    /// <param name="netClient">The net client to check the connection status.</param>
    /// <param name="defaultUsername">Username to use if none was given.</param>
    public static void TryStart(UiManager uiManager, NetClient netClient, string defaultUsername) {
        // Automated entity synchronisation test (no-op without -hkmp-test)
        DevEntityTest.TryStart();

        // Steam bridge tests (no-op without -hkmp-steam-pair / -hkmp-steam-host-test)
        var steamPairPort = DevSteamBridgeTest.TryStart(uiManager.SteamBridge);

        // Save/quit test of a hosted game (no-op without -hkmp-test-save)
        DevSaveTest.TryStart(uiManager, netClient);

        // Teleport test (no-op without -hkmp-test-tp)
        DevTeleportTest.TryStart(uiManager, netClient);

        // Grub jar sync test (no-op without -hkmp-test-grub)
        DevGrubTest.TryStart(netClient);

        // Shared progress test: masks, soul vessels, spells, story (no-op without -hkmp-test-shared)
        DevSharedProgressTest.TryStart(netClient);

        var openMenu = HasFlag("-hkmp-menu") || Environment.GetEnvironmentVariable("HKMP_MENU") == "1";
        var connect = GetOption("-hkmp-connect") ?? Environment.GetEnvironmentVariable("HKMP_CONNECT");
        var username = GetOption("-hkmp-name") ?? Environment.GetEnvironmentVariable("HKMP_NAME");

        // Join through the Steam bridge with a code (-hkmp-steam-join CODE)
        _steamJoinCode = GetOption("-hkmp-steam-join");
        _steamPassword = GetOption("-hkmp-steam-password") ?? "";
        if (_steamJoinCode != null) {
            SteamBridgeService.AllowOwnCode = true;
            connect = "steam:" + _steamJoinCode;
        }

        if (!openMenu && string.IsNullOrEmpty(connect)) {
            return;
        }

        if (string.IsNullOrEmpty(username)) {
            username = string.IsNullOrEmpty(defaultUsername) ? "Player" : defaultUsername;
        }

        string address = null;
        var port = DefaultPort;
        if (!string.IsNullOrEmpty(connect)) {
            address = connect;
            var colonIndex = connect.LastIndexOf(':');
            if (colonIndex > 0 && int.TryParse(connect.Substring(colonIndex + 1), out var parsedPort)) {
                address = connect.Substring(0, colonIndex);
                port = parsedPort;
            }
        }

        if (steamPairPort > 0) {
            // Connect through the Steam socket pair bridge instead of directly
            address = "127.0.0.1";
            port = steamPairPort;
        }

        Logger.Info(
            $"HKMP_AUTO: requested (menu: {openMenu}, connect: {address ?? "none"}:{port}, name: {username})"
        );

        _uiManager = uiManager;
        _netClient = netClient;
        _address = address;
        _port = port;
        _username = username;

        MonoBehaviourUtil.Instance.StartCoroutine(Run(uiManager, netClient, address, port, username));
    }

    private static UiManager _uiManager;
    private static NetClient _netClient;
    private static string _address;
    private static int _port;
    private static string _username;
    private static string _steamJoinCode;
    private static string _steamPassword;

    /// <summary>
    /// Test helper: disconnect from the server and return to the main menu without saving, like a player quitting
    /// to the menu. Returns false if auto-connect was not configured.
    /// </summary>
    internal static bool DisconnectToMenu() {
        if (_uiManager == null || _address == null) {
            return false;
        }

        Logger.Info("HKMP_AUTO: disconnecting and returning to main menu");
        _uiManager.DevDisconnectToMainMenu();
        return true;
    }

    /// <summary>
    /// Test helper: from the main menu, open the HKMP menu and connect again with the original options.
    /// </summary>
    internal static IEnumerator Reconnect() {
        if (_uiManager == null || _address == null) {
            yield break;
        }

        yield return Run(_uiManager, _netClient, _address, _port, _username);
    }

    /// <summary>
    /// Coroutine that waits for the main menu, opens the HKMP menu and optionally connects.
    /// </summary>
    private static IEnumerator Run(
        UiManager uiManager,
        NetClient netClient,
        string address,
        int port,
        string username
    ) {
        // Wait for the main menu to be fully shown and accepting input
        yield return new WaitUntil(() =>
            UIManager.instance != null &&
            UIManager.instance.menuState == MainMenuState.MAIN_MENU &&
            InputHandler.Instance != null &&
            InputHandler.Instance.acceptingInput
        );
        // Small grace period so the main menu fade-in finishes
        yield return new WaitForSeconds(1f);

        Logger.Info("HKMP_AUTO: main menu ready, opening HKMP menu");
        yield return uiManager.OpenMultiplayerMenu();
        Logger.Info($"HKMP_AUTO: HKMP menu opened (menuState: {UIManager.instance.menuState})");

        if (address == null) {
            yield break;
        }

        if (DevSteamBridgeTest.PairRequested) {
            yield return new WaitUntil(() => DevSteamBridgeTest.PairState is not SteamBridgeClientState.Connecting
                and not SteamBridgeClientState.Handshaking);
            Logger.Info($"HKMP_AUTO: Steam pair bridge {DevSteamBridgeTest.PairState}");
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            if (_steamJoinCode != null) {
                Logger.Info($"HKMP_AUTO: connect attempt {attempt} with Steam code {_steamJoinCode} as {username}");
                uiManager.DevJoinWithCode(_steamJoinCode, _steamPassword, username);
                // Wait for the bridge (up to its connect timeout) before HKMP starts connecting
                var bridgeWait = 0f;
                yield return new WaitUntil(() => {
                    bridgeWait += Time.unscaledDeltaTime;
                    return netClient.ConnectionStatus != ClientConnectionStatus.NotConnected || bridgeWait > 45f;
                });
            } else {
                Logger.Info($"HKMP_AUTO: connect attempt {attempt} to {address}:{port} as {username}");
                uiManager.RequestConnect(address, port, username);
            }

            // Wait until the connection attempt resolves
            var waited = 0f;
            yield return new WaitUntil(() => {
                waited += Time.unscaledDeltaTime;
                return netClient.ConnectionStatus != ClientConnectionStatus.Connecting || waited > 30f;
            });

            if (netClient.IsConnected) {
                Logger.Info("HKMP_AUTO: connected");
                yield break;
            }

            Logger.Info($"HKMP_AUTO: connect attempt {attempt} failed (status: {netClient.ConnectionStatus})");
            yield return new WaitForSecondsRealtime(RetryDelay);
        }

        Logger.Warn("HKMP_AUTO: giving up connecting");
    }

    /// <summary>
    /// Whether the given flag is present on the command line.
    /// </summary>
    private static bool HasFlag(string flag) {
        foreach (var arg in Environment.GetCommandLineArgs()) {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get the value following the given option on the command line, or null if not present.
    /// </summary>
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
