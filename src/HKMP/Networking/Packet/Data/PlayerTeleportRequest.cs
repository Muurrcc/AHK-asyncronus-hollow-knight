namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Packet data for a client asking the server to teleport it to another player.
/// </summary>
internal class PlayerTeleportRequest : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;

    /// <summary>
    /// The ID of the player to teleport to.
    /// </summary>
    public ushort TargetId { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(TargetId);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        TargetId = packet.ReadUShort();
    }
}
