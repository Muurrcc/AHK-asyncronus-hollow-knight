using System;
using Hkmp.Api.Command.Client;
using Hkmp.Game.Client;

namespace Hkmp.Game.Command.Client;

/// <summary>
/// Command for teleporting to another player: "/tp [name]". Without a name it cycles through the other players.
/// </summary>
internal class TeleportCommand : IClientCommand {
    /// <inheritdoc />
    public string Trigger => "/tp";

    /// <inheritdoc />
    public string[] Aliases => Array.Empty<string>();

    private readonly TeleportManager _teleportManager;

    public TeleportCommand(TeleportManager teleportManager) {
        _teleportManager = teleportManager;
    }

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        _teleportManager.Request(arguments.Length > 1 ? string.Join(" ", arguments, 1, arguments.Length - 1) : null);
    }
}
