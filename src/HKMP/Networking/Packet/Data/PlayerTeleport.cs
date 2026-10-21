namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Packet data from the server telling a client where to teleport to (scene and position of another player).
/// </summary>
internal class PlayerTeleport : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;

    /// <summary>
    /// The name of the scene of the target player.
    /// </summary>
    public string SceneName { get; set; }

    /// <summary>
    /// The X position of the target player.
    /// </summary>
    public float X { get; set; }

    /// <summary>
    /// The Y position of the target player.
    /// </summary>
    public float Y { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(SceneName);
        packet.Write(X);
        packet.Write(Y);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        SceneName = packet.ReadString();
        X = packet.ReadFloat();
        Y = packet.ReadFloat();
    }
}
