using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace Hkmp.Networking.SteamBridge;

/// <summary>
/// <see cref="ISteamBridgeLink"/> over a Steam networking sockets connection (ISteamNetworkingSockets). Used only
/// from the bridge worker thread; the Steam networking sockets API is thread safe.
/// </summary>
internal sealed class SteamBridgeLink : ISteamBridgeLink {
    /// <summary>
    /// Minimum time between two connection state queries.
    /// </summary>
    private const long StatePollIntervalMs = 100;

    /// <summary>
    /// Maximum number of messages fetched from Steam at once.
    /// </summary>
    private const int ReceiveBatch = 64;

    /// <summary>
    /// The Steam connection handle.
    /// </summary>
    public HSteamNetConnection Connection { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public string CloseDescription { get; private set; }

    private readonly IntPtr[] _messagePointers = new IntPtr[ReceiveBatch];
    private readonly Queue<byte[]> _received = new();

    private IntPtr _sendBuffer;
    private int _sendBufferSize;

    private SteamBridgeLinkState _state = SteamBridgeLinkState.Connecting;
    private long _lastStatePoll = -StatePollIntervalMs; // (not long.MinValue: "now - MinValue" overflows)
    private bool _closed;

    public SteamBridgeLink(HSteamNetConnection connection, string description) {
        Connection = connection;
        Description = description;
    }

    /// <inheritdoc />
    public SteamBridgeLinkState State {
        get {
            if (_closed) {
                return SteamBridgeLinkState.Closed;
            }

            var now = SteamBridgeClock.NowMs();
            if (_state != SteamBridgeLinkState.Closed && now - _lastStatePoll >= StatePollIntervalMs) {
                _lastStatePoll = now;
                _state = QueryState();
            }

            return _state;
        }
    }

    private SteamBridgeLinkState QueryState() {
        if (!SteamNetworkingSockets.GetConnectionInfo(Connection, out var info)) {
            CloseDescription ??= "invalid connection";
            return SteamBridgeLinkState.Closed;
        }

        switch (info.m_eState) {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute:
                return SteamBridgeLinkState.Connecting;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                return SteamBridgeLinkState.Connected;
            default:
                CloseDescription ??= $"{info.m_eState}, reason {info.m_eEndReason}: {info.m_szEndDebug}";
                return SteamBridgeLinkState.Closed;
        }
    }

    /// <inheritdoc />
    public bool Send(byte[] buffer, int length, bool reliable) {
        if (_closed || length <= 0) {
            return false;
        }

        if (_sendBufferSize < length) {
            if (_sendBuffer != IntPtr.Zero) {
                Marshal.FreeHGlobal(_sendBuffer);
            }

            _sendBufferSize = System.Math.Max(length, 2048);
            _sendBuffer = Marshal.AllocHGlobal(_sendBufferSize);
        }

        Marshal.Copy(buffer, 0, _sendBuffer, length);

        var flags = reliable
            ? Steamworks.Constants.k_nSteamNetworkingSend_ReliableNoNagle
            : Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
        var result = SteamNetworkingSockets.SendMessageToConnection(
            Connection,
            _sendBuffer,
            (uint) length,
            flags,
            out _
        );

        return result == EResult.k_EResultOK;
    }

    /// <inheritdoc />
    public bool TryReceive(out byte[] data) {
        if (_received.Count == 0 && !_closed) {
            var count = SteamNetworkingSockets.ReceiveMessagesOnConnection(Connection, _messagePointers, ReceiveBatch);
            for (var i = 0; i < count; i++) {
                var pointer = _messagePointers[i];
                try {
                    var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                    var size = message.m_cbSize;
                    if (size >= 0 && size <= SteamBridgeProtocol.MaxDatagramSize) {
                        var bytes = new byte[size];
                        if (size > 0) {
                            Marshal.Copy(message.m_pData, bytes, 0, size);
                        }

                        _received.Enqueue(bytes);
                    }
                } finally {
                    SteamNetworkingMessage_t.Release(pointer);
                    _messagePointers[i] = IntPtr.Zero;
                }
            }
        }

        if (_received.Count > 0) {
            data = _received.Dequeue();
            return true;
        }

        data = null;
        return false;
    }

    /// <inheritdoc />
    public void Close(int reason, string debug) {
        if (_closed) {
            return;
        }

        _closed = true;
        CloseDescription ??= debug;

        try {
            // Linger so that a reliable handshake response is still delivered
            SteamNetworkingSockets.CloseConnection(Connection, reason, Truncate(debug), true);
        } finally {
            _received.Clear();
            if (_sendBuffer != IntPtr.Zero) {
                Marshal.FreeHGlobal(_sendBuffer);
                _sendBuffer = IntPtr.Zero;
                _sendBufferSize = 0;
            }
        }
    }

    private static string Truncate(string text) {
        if (string.IsNullOrEmpty(text)) {
            return string.Empty;
        }

        // k_cchSteamNetworkingMaxConnectionCloseReason = 128 (including terminator, UTF-8)
        var ascii = new char[System.Math.Min(text.Length, 100)];
        for (var i = 0; i < ascii.Length; i++) {
            ascii[i] = text[i] < 128 ? text[i] : '?';
        }

        return new string(ascii);
    }
}
