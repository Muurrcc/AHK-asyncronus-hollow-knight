using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Hkmp.Logging;
using Hkmp.Networking.SteamBridge;
using Hkmp.SteamBridge.Tests;
using Steamworks;

namespace SteamBridgeHarness;

/// <summary>
/// Console logger for the bridge code compiled into this harness.
/// </summary>
internal class ConsoleLogger : ILogger {
    private static void Write(string level, string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");

    public void Info(string message) => Write("INFO", message);
    public void Fine(string message) => Write("FINE", message);
    public void Debug(string message) => Write("DEBUG", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
}

/// <summary>
/// HKMP's real client (Hkmp.Networking.Client.NetClient from HKMP.dll), driven by reflection.
/// </summary>
internal class HkmpClient {
    private static Assembly _hkmp;

    private readonly object _netClient;
    private readonly PropertyInfo _statusProperty;

    public string Name { get; }

    public HkmpClient(string name) {
        Name = name;
        _hkmp ??= Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HKMP.dll"));

        var packetManager = Activator.CreateInstance(_hkmp.GetType("Hkmp.Networking.Packet.PacketManager", true), true);
        var netClientType = _hkmp.GetType("Hkmp.Networking.Client.NetClient", true);
        _netClient = Activator.CreateInstance(
            netClientType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [packetManager],
            null
        );
        _statusProperty = netClientType.GetProperty("ConnectionStatus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    public string Status => _statusProperty.GetValue(_netClient).ToString();

    public void Connect(string address, int port) {
        var authKey = (string) _hkmp.GetType("Hkmp.Util.AuthUtil", true)
            .GetMethod("GenerateAuthKey", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Invoke(null, null);
        var addonDataType = _hkmp.GetType("Hkmp.Networking.Packet.Data.AddonData", true);
        var addons = (IList) Activator.CreateInstance(typeof(List<>).MakeGenericType(addonDataType));

        _netClient.GetType().GetMethod("Connect").Invoke(_netClient, [address, port, Name, authKey, addons]);
    }

    public void Disconnect() {
        _netClient.GetType().GetMethod("Disconnect").Invoke(_netClient, null);
    }
}

internal static class Program {
    private static int _failures;

    /// <summary>
    /// Outside Unity HKMP's NetClient never reaches "Connected": it raises ConnectEvent through
    /// Hkmp.Util.ThreadUtil (a MonoBehaviour) right before setting the status. So the harness only checks that the
    /// connection did not fail; the acceptance itself is verified in the HKMP server log
    /// ("Connection request for client ID N was accepted").
    /// </summary>
    private static bool NotFailed(string status) => status is "Connecting" or "Connected";

    private static void Check(bool condition, string what) {
        Console.WriteLine($"HARNESS {(condition ? "PASS" : "FAIL")} {what}");
        if (!condition) {
            _failures++;
        }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs, Action tick = null) {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs) {
            tick?.Invoke();
            if (condition()) {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private static int Main(string[] args) {
        Logger.AddLogger(new ConsoleLogger());
        if (Environment.GetEnvironmentVariable("HARNESS_FIRST_CHANCE") == "1") {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => {
                if (e.Exception is not System.Net.Sockets.SocketException) {
                    Console.WriteLine($"FIRST CHANCE: {e.Exception.GetType().Name}: {e.Exception.Message}");
                }
            };
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.WriteLine($"UNHANDLED: {e.ExceptionObject}");

        if (args.Length < 2) {
            Console.WriteLine("usage: SteamBridgeHarness memory|steam-pair|steam-self <hkmpServerPort> [clients]");
            return 2;
        }

        var mode = args[0];
        var serverPort = int.Parse(args[1]);
        var clients = args.Length > 2 ? int.Parse(args[2]) : 2;

        switch (mode) {
            case "memory":
                RunMemory(serverPort, clients);
                break;
            case "direct":
                var direct = new HkmpClient("DirectTest");
                direct.Connect("127.0.0.1", serverPort);
                Check(WaitFor(() => direct.Status == "Connected", 15000), $"direct HKMP connection without bridge ({direct.Status})");
                direct.Disconnect();
                break;
            case "steam-pair":
                RunSteamPair(serverPort);
                break;
            case "steam-self":
                RunSteamSelf(serverPort);
                break;
            default:
                Console.WriteLine("unknown mode");
                return 2;
        }

        Console.WriteLine($"HARNESS SUMMARY failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Host bridge + N client bridges joined by in-memory links, real HKMP clients and a real HKMP server.
    /// </summary>
    private static void RunMemory(int serverPort, int clientCount) {
        var host = new SteamBridgeHostCore(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, serverPort), "pw");
        var hostWorker = new SteamBridgeWorker("host", host);
        hostWorker.Start();

        var cores = new List<SteamBridgeClientCore>();
        var workers = new List<SteamBridgeWorker>();
        var hkmpClients = new List<HkmpClient>();

        for (var i = 0; i < clientCount; i++) {
            var (clientLink, hostLink) = MemoryLink.CreatePair("mem" + i);
            host.TryAdd(hostLink);
            var core = new SteamBridgeClientCore(clientLink, "pw");
            var worker = new SteamBridgeWorker("client" + i, core);
            worker.Start();
            cores.Add(core);
            workers.Add(worker);
        }

        Check(WaitFor(() => cores.All(c => c.State == SteamBridgeClientState.Ready), 5000), "all client bridges accepted by host bridge");

        // One after the other: the HKMP server's DTLS accept loop handles a single handshake at a time
        // (concurrent handshakes from two end points break it, also without the bridge)
        for (var i = 0; i < clientCount; i++) {
            var client = new HkmpClient("BridgeTest" + i);
            client.Connect("127.0.0.1", cores[i].LocalPort);
            hkmpClients.Add(client);
            WaitFor(() => client.Status == "Connected", 15000);
            Console.WriteLine($"{client.Name}: {client.Status}");
        }

        Check(
            WaitFor(() => hkmpClients.All(c => c.Status == "Connected"), 5000) || hkmpClients.All(c => NotFailed(c.Status)),
            $"{clientCount} HKMP NetClients connecting through the bridge without failure (see server log): {string.Join(", ", hkmpClients.Select(c => c.Name + "=" + c.Status))}"
        );

        // Keep the connections alive for a while (HKMP heartbeats/updates flow through the bridge)
        Thread.Sleep(8000);
        Check(hkmpClients.All(c => NotFailed(c.Status)), "no HKMP timeout/failure after 8 s");
        for (var i = 0; i < cores.Count; i++) {
            Console.WriteLine($"client {i}: to host {cores[i].ForwardedToHost}, to local {cores[i].ForwardedToLocal}");
            Check(cores[i].ForwardedToHost > 50 && cores[i].ForwardedToLocal > 50, $"client {i} traffic flows both ways");
        }

        Check(host.ActiveCount == clientCount, $"host bridge has {host.ActiveCount} active connections");

        // Wrong password is refused before reaching the server
        var (badLink, badHostLink) = MemoryLink.CreatePair("bad");
        host.TryAdd(badHostLink);
        var badCore = new SteamBridgeClientCore(badLink, "nope");
        var badWorker = new SteamBridgeWorker("bad", badCore);
        badWorker.Start();
        Check(WaitFor(() => badCore.State == SteamBridgeClientState.Failed, 5000), $"wrong password refused: {badCore.FailureMessage}");
        badWorker.Stop();

        // (HkmpClient.Disconnect is not called: outside Unity HKMP's DtlsClient receive thread can throw an
        // unhandled exception on disconnect, which kills a plain .NET process; the bridge drop below is ungraceful)
        foreach (var worker in workers) {
            worker.Stop();
        }

        // Keep the host bridge running until the HKMP server timed the clients out: their local sockets linger,
        // so the server should not log ConnectionReset errors meanwhile
        WaitFor(() => host.TotalCount == 0, 3000);
        Check(host.TotalCount == 0, "host bridge cleaned up all connections");
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} clients gone, lingering sockets {host.LingeringSocketCount}; waiting 8 s");
        Thread.Sleep(8000);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} stopping host bridge");
        hostWorker.Stop();
    }

    private static bool InitSteam() {
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "steam_appid.txt"), "367520");
        var ok = SteamAPI.Init();
        Check(ok, "SteamAPI.Init");
        if (ok) {
            Console.WriteLine($"Steam user {SteamUser.GetSteamID().m_SteamID}, code {SteamJoinCode.Encode(SteamUser.GetSteamID().m_SteamID)}");
        }

        return ok;
    }

    /// <summary>
    /// Real Steam networking connections (CreateSocketPair over the network loopback) wrapped in
    /// <see cref="SteamBridgeLink"/>, with a real HKMP client and server.
    /// </summary>
    private static void RunSteamPair(int serverPort) {
        if (!InitSteam()) {
            return;
        }

        try {
            var identity1 = new SteamNetworkingIdentity();
            var identity2 = new SteamNetworkingIdentity();
            identity1.SetSteamID(SteamUser.GetSteamID());
            identity2.SetSteamID(SteamUser.GetSteamID());
            var ok = SteamNetworkingSockets.CreateSocketPair(out var connection1, out var connection2, true, ref identity1, ref identity2);
            Check(ok, "CreateSocketPair(network loopback)");
            if (!ok) {
                return;
            }

            for (var i = 0; i < 3; i++) {
                SteamAPI.RunCallbacks();
                var infoOk = SteamNetworkingSockets.GetConnectionInfo(connection1, out var info);
                SteamNetworkingSockets.GetDetailedConnectionStatus(connection1, out var detail, 2048);
                Console.WriteLine(
                    $"debug: conn1={connection1.m_HSteamNetConnection} infoOk={infoOk} state={info.m_eState} " +
                    $"sizeof={System.Runtime.InteropServices.Marshal.SizeOf(typeof(SteamNetConnectionInfo_t))}\n{detail}"
                );
                Thread.Sleep(200);
            }

            var host = new SteamBridgeHostCore(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, serverPort), "");
            host.TryAdd(new SteamBridgeLink(connection2, "pair:host"));
            var core = new SteamBridgeClientCore(new SteamBridgeLink(connection1, "pair:client"), "");
            var hostWorker = new SteamBridgeWorker("host", host);
            var clientWorker = new SteamBridgeWorker("client", core);
            hostWorker.Start();
            clientWorker.Start();

            Check(WaitFor(() => core.State == SteamBridgeClientState.Ready, 10000, SteamAPI.RunCallbacks), $"bridge handshake over Steam socket pair ({core.State} {core.FailureMessage})");

            var client = new HkmpClient("SteamPairTest");
            client.Connect("127.0.0.1", core.LocalPort);
            WaitFor(() => client.Status == "Connected", 5000, SteamAPI.RunCallbacks);
            Check(NotFailed(client.Status), $"HKMP NetClient through Steam sockets did not fail ({client.Status}, see server log)");
            WaitFor(() => false, 6000, SteamAPI.RunCallbacks);
            Check(NotFailed(client.Status), "no HKMP timeout/failure after 6 s");
            Console.WriteLine($"to host {core.ForwardedToHost}, to local {core.ForwardedToLocal}");
            Check(core.ForwardedToHost > 50 && core.ForwardedToLocal > 50, "traffic flows both ways over Steam sockets");

            clientWorker.Stop();
            WaitFor(() => host.TotalCount == 0, 3000, SteamAPI.RunCallbacks);
            Check(host.TotalCount == 0, "host side noticed the closed Steam connection");
            hostWorker.Stop();
        } finally {
            SteamAPI.Shutdown();
        }
    }

    /// <summary>
    /// Full Steam host (listen socket P2P + callback) and client (ConnectP2P) to our own SteamID.
    /// </summary>
    private static void RunSteamSelf(int serverPort) {
        if (!InitSteam()) {
            return;
        }

        try {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var self = SteamUser.GetSteamID().m_SteamID;
            using var host = SteamBridgeHost.Start(serverPort, "pw");
            SteamBridgeClient client = null;
            try {
                client = SteamBridgeClient.Connect(self, "pw", (state, message) => Console.WriteLine($"client state {state}: {message}"));
            } catch (Exception e) {
                Console.WriteLine($"ConnectP2P to self threw: {e.Message}");
                Check(false, "ConnectP2P to self");
                return;
            }

            var done = WaitFor(
                () => client.State is SteamBridgeClientState.Ready or SteamBridgeClientState.Failed or SteamBridgeClientState.Closed,
                40000,
                SteamAPI.RunCallbacks
            );
            Console.WriteLine($"self connection result: {client.State} {client.Core.FailureMessage} (done={done})");
            Check(client.State == SteamBridgeClientState.Ready, "ConnectP2P to own SteamID through listen socket");

            if (client.State == SteamBridgeClientState.Ready) {
                var hkmp = new HkmpClient("SteamSelfTest");
                hkmp.Connect("127.0.0.1", client.LocalPort);
                WaitFor(() => false, 8000, SteamAPI.RunCallbacks);
                Check(NotFailed(hkmp.Status), $"HKMP via ConnectP2P to self did not fail ({hkmp.Status}, see server log)");
                Check(client.Core.ForwardedToHost > 50 && client.Core.ForwardedToLocal > 50,
                    $"traffic both ways via ConnectP2P (to host {client.Core.ForwardedToHost}, to local {client.Core.ForwardedToLocal})");
            }

            client.Dispose();

            // Wrong password over the real Steam path
            var bad = SteamBridgeClient.Connect(self, "wrong", (state, message) => Console.WriteLine($"bad client state {state}: {message}"));
            WaitFor(() => bad.State is SteamBridgeClientState.Failed or SteamBridgeClientState.Ready, 20000, SteamAPI.RunCallbacks);
            Check(
                bad.State == SteamBridgeClientState.Failed && bad.Core.HostResult == SteamBridgeResult.WrongPassword,
                $"wrong password refused over Steam ({bad.State}, {bad.Core.HostResult})"
            );
            bad.Dispose();
            WaitFor(() => host.Core.TotalCount == 0, 3000, SteamAPI.RunCallbacks);
            Check(host.Core.TotalCount == 0, "host bridge cleaned up after Steam clients left");
        } finally {
            SteamAPI.Shutdown();
        }
    }
}
