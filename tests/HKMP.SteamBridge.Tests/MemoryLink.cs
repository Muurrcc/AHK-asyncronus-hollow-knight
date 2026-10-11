using System.Collections.Concurrent;
using System.Threading;
using Hkmp.Networking.SteamBridge;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// In-memory replacement of a Steam connection for tests. Messages sent before a close can still be received by
/// the peer after the close (like Steam with linger).
/// </summary>
internal sealed class MemoryLink : ISteamBridgeLink {
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private MemoryLink _peer;
    private volatile int _state = (int) SteamBridgeLinkState.Connecting;
    private volatile string _closeDescription;

    public string Description { get; }

    public int SentReliable;
    public int SentUnreliable;
    public int? CloseReason;

    private MemoryLink(string description) {
        Description = description;
    }

    public static (MemoryLink client, MemoryLink host) CreatePair(string name = "mem", bool connected = true) {
        var client = new MemoryLink(name + ":client");
        var host = new MemoryLink(name + ":host");
        client._peer = host;
        host._peer = client;
        if (connected) {
            client.SetConnected();
            host.SetConnected();
        }

        return (client, host);
    }

    public void SetConnected() {
        Interlocked.CompareExchange(ref _state, (int) SteamBridgeLinkState.Connected, (int) SteamBridgeLinkState.Connecting);
    }

    public SteamBridgeLinkState State => (SteamBridgeLinkState) _state;

    public string CloseDescription => _closeDescription;

    public bool Send(byte[] buffer, int length, bool reliable) {
        if (State != SteamBridgeLinkState.Connected) {
            return false;
        }

        if (reliable) {
            Interlocked.Increment(ref SentReliable);
        } else {
            Interlocked.Increment(ref SentUnreliable);
        }

        var copy = new byte[length];
        System.Array.Copy(buffer, copy, length);
        _peer._inbox.Enqueue(copy);
        return true;
    }

    public bool TryReceive(out byte[] data) {
        return _inbox.TryDequeue(out data);
    }

    public void Close(int reason, string debug) {
        if (State == SteamBridgeLinkState.Closed) {
            return;
        }

        CloseReason = reason;
        _closeDescription = debug;
        _state = (int) SteamBridgeLinkState.Closed;

        if (_peer.State != SteamBridgeLinkState.Closed) {
            _peer._closeDescription = "closed by peer: " + debug;
            _peer._state = (int) SteamBridgeLinkState.Closed;
        }
    }
}
