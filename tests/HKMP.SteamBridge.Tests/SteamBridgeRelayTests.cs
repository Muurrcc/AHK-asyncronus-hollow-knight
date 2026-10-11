using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Hkmp.Networking.SteamBridge;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// UDP server on loopback that echoes every datagram prefixed with the sender end point, like a HKMP server
/// would answer each client end point separately.
/// </summary>
internal sealed class EchoServer : IDisposable {
    private readonly Socket _socket;
    private readonly Thread _thread;
    private volatile bool _stop;

    public readonly ConcurrentDictionary<string, int> Senders = new();
    public int Port { get; }
    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    public EchoServer() {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _socket.ReceiveTimeout = 100;
        Port = ((IPEndPoint) _socket.LocalEndPoint).Port;
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
    }

    private void Run() {
        var buffer = new byte[70000];
        while (!_stop) {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try {
                length = _socket.ReceiveFrom(buffer, ref remote);
            } catch (SocketException) {
                continue;
            } catch (ObjectDisposedException) {
                return;
            }

            Senders.AddOrUpdate(remote.ToString(), 1, (_, n) => n + 1);
            var prefix = Encoding.ASCII.GetBytes(remote + "|");
            var reply = new byte[prefix.Length + length];
            prefix.CopyTo(reply, 0);
            Array.Copy(buffer, 0, reply, prefix.Length, length);
            try {
                _socket.SendTo(reply, remote);
            } catch (SocketException) {
                // ignore
            }
        }
    }

    public void Dispose() {
        _stop = true;
        _socket.Close();
        _thread.Join(1000);
    }
}

/// <summary>
/// Simulated HKMP client: a UDP socket that sends to the client bridge port.
/// </summary>
internal sealed class FakeHkmpClient : IDisposable {
    public readonly Socket Socket;
    public readonly IPEndPoint Target;

    public FakeHkmpClient(int bridgePort) {
        Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Socket.Blocking = false;
        Target = new IPEndPoint(IPAddress.Loopback, bridgePort);
    }

    public void Send(string text) {
        var data = Encoding.UTF8.GetBytes(text);
        Socket.SendTo(data, Target);
    }

    public void Send(byte[] data) {
        Socket.SendTo(data, Target);
    }

    public bool TryReceive(out byte[] data) {
        data = null;
        if (Socket.Available <= 0) {
            return false;
        }

        var buffer = new byte[70000];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var length = Socket.ReceiveFrom(buffer, ref remote);
        data = new byte[length];
        Array.Copy(buffer, data, length);
        return true;
    }

    public void Dispose() => Socket.Close();
}

public class SteamBridgeRelayTests {
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public SteamBridgeRelayTests(Xunit.Abstractions.ITestOutputHelper output) {
        _output = output;
    }
    private static void PumpUntil(Func<bool> condition, int timeoutMs, params ISteamBridgePump[] pumps) {
        var stopwatch = Stopwatch.StartNew();
        while (!condition()) {
            if (stopwatch.ElapsedMilliseconds > timeoutMs) {
                throw new TimeoutException("condition not reached");
            }

            foreach (var pump in pumps) {
                pump.Pump();
            }

            Thread.Sleep(1);
        }
    }

    private static void PumpFor(int ms, params ISteamBridgePump[] pumps) {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < ms) {
            foreach (var pump in pumps) {
                pump.Pump();
            }

            Thread.Sleep(1);
        }
    }

    private static string ReceiveText(FakeHkmpClient client, int timeoutMs, params ISteamBridgePump[] pumps) {
        byte[] data = null;
        PumpUntil(() => client.TryReceive(out data), timeoutMs, pumps);
        return Encoding.UTF8.GetString(data);
    }

    [Fact]
    public void Datagrams_AreForwardedBothWays() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, null);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        Assert.True(host.TryAdd(hostLink));
        var client = new SteamBridgeClientCore(clientLink, null);

        try {
            PumpUntil(() => client.State == SteamBridgeClientState.Ready, 2000, client, host);
            Assert.Equal(SteamBridgeResult.Accepted, client.HostResult);
            Assert.Equal(1, host.ActiveCount);

            using var hkmp = new FakeHkmpClient(client.LocalPort);
            for (var i = 0; i < 50; i++) {
                hkmp.Send($"packet {i}");
                var reply = ReceiveText(hkmp, 2000, client, host);
                Assert.EndsWith($"|packet {i}", reply);
                Assert.StartsWith("127.0.0.1:", reply);
            }

            // Large datagram (bigger than a typical MTU, smaller than the UDP maximum)
            var big = new byte[8000];
            new Random(1).NextBytes(big);
            hkmp.Send(big);
            byte[] bigReply = null;
            PumpUntil(() => hkmp.TryReceive(out bigReply), 2000, client, host);
            Assert.Equal(big, bigReply.Skip(bigReply.Length - big.Length).ToArray());

            Assert.Equal(51, client.ForwardedToHost);
            Assert.Equal(51, client.ForwardedToLocal);
        } finally {
            client.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void EachRemoteClient_GetsItsOwnServerEndPoint() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, "pw");
        var clients = new List<SteamBridgeClientCore>();
        var fakes = new List<FakeHkmpClient>();

        try {
            for (var i = 0; i < 3; i++) {
                var (clientLink, hostLink) = MemoryLink.CreatePair("c" + i);
                Assert.True(host.TryAdd(hostLink));
                clients.Add(new SteamBridgeClientCore(clientLink, "pw"));
            }

            var pumps = clients.Cast<ISteamBridgePump>().Append(host).ToArray();
            PumpUntil(() => clients.All(c => c.State == SteamBridgeClientState.Ready), 2000, pumps);

            var seenEndPoints = new HashSet<string>();
            for (var i = 0; i < clients.Count; i++) {
                var fake = new FakeHkmpClient(clients[i].LocalPort);
                fakes.Add(fake);
                fake.Send("hello " + i);
                var reply = ReceiveText(fake, 2000, pumps);
                Assert.EndsWith("|hello " + i, reply);
                seenEndPoints.Add(reply.Split('|')[0]);
            }

            Assert.Equal(3, seenEndPoints.Count);
            Assert.Equal(3, server.Senders.Count);
            Assert.Equal(3, host.ActiveCount);
        } finally {
            fakes.ForEach(f => f.Dispose());
            clients.ForEach(c => c.Dispose());
            host.Dispose();
        }
    }

    [Fact]
    public void WrongPassword_IsRejectedAndClosed() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, "correct");
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, "wrong");

        try {
            PumpUntil(() => client.State == SteamBridgeClientState.Failed, 2000, client, host);
            Assert.Equal(SteamBridgeResult.WrongPassword, client.HostResult);
            Assert.Contains("password", client.FailureMessage, StringComparison.OrdinalIgnoreCase);
            PumpUntil(() => host.TotalCount == 0, 2000, host);
            Assert.Equal(0, host.ActiveCount);
            Assert.Equal(SteamBridgeLinkState.Closed, hostLink.State);
            Assert.Equal(SteamBridgeProtocol.CloseReasonBase + (int) SteamBridgeResult.WrongPassword, hostLink.CloseReason);
            Assert.Empty(server.Senders);
        } finally {
            client.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void MissingPassword_IsRejected() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, "secret");
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, "");

        try {
            PumpUntil(() => client.State == SteamBridgeClientState.Failed, 2000, client, host);
            Assert.Equal(SteamBridgeResult.WrongPassword, client.HostResult);
        } finally {
            client.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void ConnectionLimit_RejectsExtraClients() {
        using var server = new EchoServer();
        var limits = new SteamBridgeLimits { MaxConnections = 1, MaxPending = 1 };
        var host = new SteamBridgeHostCore(server.EndPoint, null, limits);

        var (link1, hostLink1) = MemoryLink.CreatePair("a");
        var (link2, hostLink2) = MemoryLink.CreatePair("b");
        var (_, hostLink3) = MemoryLink.CreatePair("c");

        Assert.True(host.TryAdd(hostLink1));
        Assert.True(host.TryAdd(hostLink2));
        // Max + pending reached: refused before the handshake
        Assert.False(host.TryAdd(hostLink3));

        var client1 = new SteamBridgeClientCore(link1, null);
        var client2 = new SteamBridgeClientCore(link2, null);

        try {
            PumpUntil(() => client1.State == SteamBridgeClientState.Ready, 2000, client1, host);
            PumpUntil(() => client2.State == SteamBridgeClientState.Failed, 2000, client2, host);
            Assert.Equal(SteamBridgeResult.ServerFull, client2.HostResult);
            Assert.Equal(1, host.ActiveCount);

            // After the first client leaves, a new one fits again
            client1.Dispose();
            PumpUntil(() => host.TotalCount == 0, 2000, host);

            var (link4, hostLink4) = MemoryLink.CreatePair("d");
            Assert.True(host.TryAdd(hostLink4));
            var client4 = new SteamBridgeClientCore(link4, null);
            PumpUntil(() => client4.State == SteamBridgeClientState.Ready, 2000, client4, host);
            client4.Dispose();
        } finally {
            client1.Dispose();
            client2.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Host_ClosesConnectionWithoutHello_AfterTimeout() {
        long now = 0;
        var limits = new SteamBridgeLimits { HelloTimeoutMs = 1000 };
        var host = new SteamBridgeHostCore(new IPEndPoint(IPAddress.Loopback, 9), null, limits, () => now);
        var (_, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);

        host.Pump();
        now = 999;
        host.Pump();
        Assert.Equal(1, host.TotalCount);

        now = 1001;
        host.Pump();
        Assert.Equal(0, host.TotalCount);
        Assert.Equal(SteamBridgeLinkState.Closed, hostLink.State);
        Assert.Equal(SteamBridgeProtocol.CloseReasonBase + (int) SteamBridgeResult.TimedOut, hostLink.CloseReason);
        host.Dispose();
    }

    [Fact]
    public void Host_RejectsGarbageHello() {
        var host = new SteamBridgeHostCore(new IPEndPoint(IPAddress.Loopback, 9), null);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        clientLink.Send([1, 2, 3, 4, 5, 6, 7, 8, 9], 9, true);

        host.Pump();
        Assert.Equal(0, host.TotalCount);
        Assert.True(clientLink.TryReceive(out var response));
        Assert.True(SteamBridgeProtocol.TryParseResponse(response, out var result));
        Assert.Equal(SteamBridgeResult.Malformed, result);
        host.Dispose();
    }

    [Fact]
    public void Host_RejectsOtherVersion() {
        var host = new SteamBridgeHostCore(new IPEndPoint(IPAddress.Loopback, 9), null);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var hello = SteamBridgeProtocol.BuildHello("");
        hello[4] = 99;
        clientLink.Send(hello, hello.Length, true);

        host.Pump();
        Assert.True(clientLink.TryReceive(out var response));
        Assert.True(SteamBridgeProtocol.TryParseResponse(response, out var result));
        Assert.Equal(SteamBridgeResult.VersionMismatch, result);
        host.Dispose();
    }

    [Fact]
    public void Client_FailsIfLinkNeverConnects() {
        long now = 0;
        var (clientLink, _) = MemoryLink.CreatePair(connected: false);
        var client = new SteamBridgeClientCore(clientLink, null, new SteamBridgeLimits { ConnectTimeoutMs = 500 }, () => now);

        client.Pump();
        Assert.Equal(SteamBridgeClientState.Connecting, client.State);
        now = 501;
        client.Pump();
        Assert.Equal(SteamBridgeClientState.Failed, client.State);
        Assert.Equal(SteamBridgeLinkState.Closed, clientLink.State);
        client.Dispose();
    }

    [Fact]
    public void Client_FailsIfHostNeverAnswers() {
        long now = 0;
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        var client = new SteamBridgeClientCore(clientLink, null, new SteamBridgeLimits { ResponseTimeoutMs = 500 }, () => now);

        client.Pump();
        Assert.Equal(SteamBridgeClientState.Handshaking, client.State);
        Assert.True(hostLink.TryReceive(out var hello));
        Assert.True(SteamBridgeProtocol.TryParseHello(hello, out _, out _));

        now = 501;
        client.Pump();
        Assert.Equal(SteamBridgeClientState.Failed, client.State);
        client.Dispose();
    }

    [Fact]
    public void HostStop_ClosesClients() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, null);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, null);

        PumpUntil(() => client.State == SteamBridgeClientState.Ready, 2000, client, host);
        host.Dispose();
        client.Pump();

        Assert.Equal(SteamBridgeClientState.Closed, client.State);
        Assert.False(host.TryAdd(MemoryLink.CreatePair().host));
        client.Dispose();
    }

    [Fact]
    public void IdleConnections_AreClosed() {
        long now = 0;
        using var server = new EchoServer();
        var limits = new SteamBridgeLimits { IdleTimeoutMs = 5000 };
        var host = new SteamBridgeHostCore(server.EndPoint, null, limits, () => now);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, null, limits, () => now);

        PumpUntil(() => client.State == SteamBridgeClientState.Ready, 2000, client, host);
        now = 4000;
        host.Pump();
        Assert.Equal(1, host.ActiveCount);
        now = 6000;
        host.Pump();
        Assert.Equal(0, host.ActiveCount);
        client.Pump();
        Assert.Equal(SteamBridgeClientState.Closed, client.State);
        client.Dispose();
        host.Dispose();
    }

    [Fact]
    public void ClosedConnection_KeepsLocalSocketOpenForLingerTime() {
        long now = 0;
        using var server = new EchoServer();
        var limits = new SteamBridgeLimits { SocketLingerMs = 15000 };
        var host = new SteamBridgeHostCore(server.EndPoint, null, limits, () => now);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, null, limits, () => now);

        PumpUntil(() => client.State == SteamBridgeClientState.Ready, 2000, client, host);
        using var hkmp = new FakeHkmpClient(client.LocalPort);
        hkmp.Send("x");
        ReceiveText(hkmp, 2000, client, host);
        var serverSidePort = server.Senders.Keys.Single();

        client.Dispose();
        host.Pump();
        Assert.Equal(0, host.TotalCount);
        Assert.Equal(1, host.LingeringSocketCount);

        // The server can still send to the closed session's port without getting "port unreachable"
        using (var probe = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
            var parts = serverSidePort.Split(':');
            probe.SendTo(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
        }

        now = 14000;
        host.Pump();
        Assert.Equal(1, host.LingeringSocketCount);
        now = 15001;
        host.Pump();
        Assert.Equal(0, host.LingeringSocketCount);
        host.Dispose();
    }

    [Fact]
    public void Dispose_ClosesLingeringSockets() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, null);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, null);
        PumpUntil(() => client.State == SteamBridgeClientState.Ready, 2000, client, host);
        client.Dispose();
        host.Pump();
        Assert.Equal(1, host.LingeringSocketCount);
        host.Dispose();
        Assert.Equal(0, host.LingeringSocketCount);
    }

    [Fact]
    public void Client_IgnoresHostDataUntilLocalClientKnown_AndFollowsReconnect() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, null);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, null);

        try {
            PumpUntil(() => client.State == SteamBridgeClientState.Ready, 2000, client, host);

            // HKMP reconnects with a new socket (new local port): replies go to the latest sender
            using (var first = new FakeHkmpClient(client.LocalPort)) {
                first.Send("one");
                Assert.EndsWith("|one", ReceiveText(first, 2000, client, host));
            }

            using var second = new FakeHkmpClient(client.LocalPort);
            second.Send("two");
            Assert.EndsWith("|two", ReceiveText(second, 2000, client, host));
        } finally {
            client.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Workers_RunOnThreads_AndStopCleanly() {
        using var server = new EchoServer();
        var host = new SteamBridgeHostCore(server.EndPoint, null);
        var (clientLink, hostLink) = MemoryLink.CreatePair();
        host.TryAdd(hostLink);
        var client = new SteamBridgeClientCore(clientLink, null);

        var hostWorker = new SteamBridgeWorker("test host", host);
        var clientWorker = new SteamBridgeWorker("test client", client);
        hostWorker.Start();
        clientWorker.Start();

        var stopwatch = Stopwatch.StartNew();
        while (client.State != SteamBridgeClientState.Ready && stopwatch.ElapsedMilliseconds < 3000) {
            Thread.Sleep(5);
        }

        Assert.Equal(SteamBridgeClientState.Ready, client.State);

        using var hkmp = new FakeHkmpClient(client.LocalPort);
        var latencies = new List<double>();
        for (var i = 0; i < 200; i++) {
            var sw = Stopwatch.StartNew();
            hkmp.Send("ping " + i);
            byte[] reply = null;
            while (!hkmp.TryReceive(out reply) && sw.ElapsedMilliseconds < 2000) {
                Thread.SpinWait(50);
            }

            Assert.NotNull(reply);
            Assert.EndsWith("|ping " + i, Encoding.UTF8.GetString(reply));
            latencies.Add(sw.Elapsed.TotalMilliseconds);
        }

        latencies.Sort();
        var median = latencies[latencies.Count / 2];
        _output.WriteLine($"round trip through both bridges (2 worker threads): median {median:F2} ms, p95 {latencies[(int) (latencies.Count * 0.95)]:F2} ms");
        // Round trip passes 2 pumps in each direction; it must stay far below HKMP's update interval
        Assert.True(median < 20, $"median round trip {median:F2} ms");

        Assert.True(clientWorker.Stop());
        Assert.True(hostWorker.Stop());
        Assert.False(clientWorker.IsRunning);
        Assert.False(hostWorker.IsRunning);
        // Workers dispose their pumps
        Assert.Equal(SteamBridgeLinkState.Closed, clientLink.State);
        Assert.Equal(0, host.TotalCount);
    }

    private sealed class ThrowingPump : ISteamBridgePump {
        public int Pumps;
        public bool Disposed;

        public void Pump() {
            Interlocked.Increment(ref Pumps);
            throw new InvalidOperationException("boom");
        }

        public void Wait(int timeoutMicroseconds) {
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Worker_SurvivesExceptions_AndGivesUpAfterTooMany() {
        var pump = new ThrowingPump();
        var worker = new SteamBridgeWorker("throwing", pump);
        worker.Start();

        var stopwatch = Stopwatch.StartNew();
        while (worker.IsRunning && stopwatch.ElapsedMilliseconds < 5000) {
            Thread.Sleep(10);
        }

        Assert.False(worker.IsRunning);
        Assert.Equal(50, pump.Pumps);
        Assert.True(pump.Disposed);
    }
}
