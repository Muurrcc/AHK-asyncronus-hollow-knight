# Linux

Asyncronus on Linux: Steam with **Proton** (Windows build of the game, recommended) or the **native Linux build**. Steam Deck / SteamOS included.

> **Status: not tested on a real Linux machine yet.** The installer script was run end to end (patch, hook generation, install, uninstall, byte-identical restore) against a copy of the original `Managed` folder, using the same .NET 8 runtime on Windows through Git Bash. Nothing has been run inside Hollow Knight on Linux. See [Not verified](#not-verified).

## Requirements

| What | Detail |
|---|---|
| Game | Hollow Knight on Steam, build **1.5.12459** (Unity 6), no beta branch (*Properties > Betas > None*) |
| Steam | Installed and logged in. Native packages, Flatpak (`com.valvesoftware.Steam`) and Snap are looked up automatically |
| .NET | .NET **8 or newer** runtime or SDK, `dotnet` in `PATH` (or in `~/.dotnet`). Only used while installing (patch + hook generation) |
| Tools | `bash` 4+, `sha256sum`, `tar` (all standard) |
| Proton (recommended) | *Properties > Compatibility > Force the use of a specific Steam Play compatibility tool*, e.g. **Proton 9+** or Proton Experimental. Run the game once so Steam downloads the Windows build |

Why Proton is recommended: the binary patch is made for the **Windows** `Assembly-CSharp.dll` (sha256 `e9048ef6...`). If the native Linux build ships a different file, the installer detects it (hash check), changes nothing and tells you to switch to Proton.

### Installing .NET

| Distro | Command |
|---|---|
| Arch / SteamOS (with `steamos-readonly disable` or user install, see below) | `sudo pacman -S dotnet-runtime dotnet-sdk` |
| Debian / Ubuntu | `sudo apt install dotnet-sdk-8.0` |
| Fedora | `sudo dnf install dotnet-sdk-8.0` |
| Any, no root (**Steam Deck**) | `curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && bash dotnet-install.sh --channel 8.0 --runtime dotnet` (installs in `~/.dotnet`; the installer finds it) |

On Steam Deck do **not** unlock the read-only system partition: use the no-root line.

## Install

1. Close Hollow Knight (and make sure it is not "Running" in Steam).
2. Download `Asyncronus-linux-X.Y.Z.tar.gz` from the [Releases](https://github.com/Muurrcc/AHK-asyncronus-hollow-knight/releases) page (or build it, see below).
3. In a terminal:

```bash
tar xzf Asyncronus-linux-*.tar.gz
cd Asyncronus-linux-*/
./install.sh
```

It finds the game by itself (`~/.local/share/Steam`, `~/.steam/steam`, Flatpak, Snap and every library in `libraryfolders.vdf`). If it cannot, or you have several copies, pass the folder:

```bash
./install.sh --game-dir "/mnt/games/SteamLibrary/steamapps/common/Hollow Knight"
```

Other options: `--status` (shows where the game is and the state of `Assembly-CSharp.dll`, changes nothing), `--force` (skip the "game is running" check), `--keep-work` (keep the temporary folder for debugging).

What it does, in order: checks the sha256 of `Assembly-CSharp.dll` (original -> continues; already patched -> only updates the mod; anything else -> aborts without touching anything), applies the binary delta, generates the four `MMHOOK_*.dll` on your machine with PrePatcher + MonoMod HookGen, backs up what it overwrites into `hollow_knight_Data/Managed-vanilla-backup/` (plus `asyncronus-backup.txt`, the log of changes), copies the API and the mod to `Managed/Mods/Asyncronus/`, and verifies the final hash. If anything fails it reverts. It takes 15-30 seconds. **To update**, extract the newer package and run `./install.sh` again.

## Launch the game

No Steam launch options and no wrapper are needed: the patched `Assembly-CSharp.dll` loads the mod by itself.

1. Open Steam and start **Hollow Knight** normally (Play).
2. The main menu shows **Start Multiplayer**. If it does not, see [Troubleshooting](#troubleshooting).

## Host and join with a code

Same as on Windows (Steam must be open and logged in on both sides):

1. **Host:** *Start Multiplayer > Start Hosting* (optional password). Copy and share the `XXXX-XXXX` code shown.
2. **Guest:** *Start Multiplayer > Join / Unirse*, type the code (and password), connect.

The code is derived from the host's Steam account; connections go through Steam's relay network, so no port forwarding or VPN is needed. Both players need the **same mod version**.

## Uninstall

```bash
./uninstall.sh                 # or: ./uninstall.sh --game-dir "/path/to/Hollow Knight"
```

It restores `Assembly-CSharp.dll` from `Managed-vanilla-backup`, removes the files it added (API libraries, `MMHOOK_*`) and `Managed/Mods/Asyncronus`, and deletes its backup folder. It never touches other mods or your saves. For certainty you can also use *Properties > Installed files > Verify integrity of local files* in Steam.

## Where things are

| | Proton | Native |
|---|---|---|
| Game | `<library>/steamapps/common/Hollow Knight/` | same |
| Mod | `hollow_knight_Data/Managed/Mods/Asyncronus/` | same |
| Saves and `ModLog.txt` | `<library>/steamapps/compatdata/367520/pfx/drive_c/users/steamuser/AppData/LocalLow/Team Cherry/Hollow Knight/` | `~/.config/unity3d/Team Cherry/Hollow Knight/` |
| Installer error log | `~/.local/state/asyncronus-error-log.txt` (only when a tool fails) | same |

## Troubleshooting

- **"Hollow Knight not found"**: use `--game-dir`. For Flatpak Steam, a library outside your home needs permission: `flatpak override --user --filesystem=/path/to/library com.valvesoftware.Steam`. The installer itself runs outside the sandbox and only needs write access to the game folder.
- **".NET not found / runtime 8 or newer is required"**: install .NET (above). With only .NET 9 or 10 it works too (the tools are forced to roll forward).
- **"Your Hollow Knight is not the version Asyncronus needs"**: *Verify integrity of local files*, *Betas > None*. On the native build, force Proton (see Requirements) and verify integrity again. Lumafly or another Modding API on the same install is not compatible.
- **Hook generation fails**: the full output is saved in `~/.local/state/asyncronus-error-log.txt`; attach it to an issue. If it says a game file is damaged, verify integrity in Steam.
- **No "Start Multiplayer" in the menu**: check `ModLog.txt` (see table). It must list `Asyncronus`/`HKMP` among the loaded mods and `Modding API: 0.0.0.0-77`.
- **Cannot join by code**: both players need Steam open and logged in, same mod version, and the host must be in *Start Hosting*. A direct IP connection needs UDP port 26950 reachable (firewall/router).
- **Copy-code button does nothing**: Unity's clipboard needs X11/XWayland; type the code by hand.

## Linux notes (technical)

- **Portability review of the code.** No change was needed in `src/`. The mod has no `DllImport`, registry or Windows path code; its paths use `Path.Combine`, `Application.dataPath` and the mod's own folder (`Assembly.Location`). The Steam bridge only uses Steamworks.NET from the game (`com.rlabrecque.steamworks.net.dll`: `ISteamNetworkingSockets`, `SteamUser`) and loopback UDP sockets.
- **Steamworks.** On the native build the game's Steamworks.NET resolves `libsteam_api.so` (shipped in `hollow_knight_Data/Plugins`) and talks to the running Steam client; under Proton the Windows `steam_api64.dll` goes through Proton's `lsteamclient` to the native client. The game already calls `SteamAPI.RunCallbacks` every frame, so the bridge does not. Steam networking sockets (relay, `InitRelayNetworkAccess`) need an up-to-date Steam client.
- **The Windows build tools run on Linux without Mono.** `PrePatcher.exe` and `MonoMod.RuntimeDetour.HookGen.exe` are plain IL assemblies; the script runs them with `dotnet X.exe` after writing a `X.runtimeconfig.json` next to them. Output was checked on .NET 8: the four `MMHOOK_*` files have exactly the sizes recorded in `payload.ini`.
- **Delta.** `installer/linux/delta-tool` is a net8 wrapper around the same `Delta.cs` as the MSI (custom `ASYNPAT1` format, not stock `bspatch`), so the Linux and Windows results are the same file (sha256 `e13818bb...`).
- **Backup format** is identical to the MSI's (`Managed-vanilla-backup`, `asyncronus-backup.txt`).
- **`unityscenerepacker.dll`** (a native Windows DLL used by the Modding API) is copied as on Windows. It is fine under Proton; on the native build it cannot be loaded, which only matters for features that call it.

## Not verified

- Anything running **inside the game on Linux** (mod loading, UI, synchronization), under Proton or native.
- That the native Linux `Assembly-CSharp.dll` matches the Windows hash (if not, use Proton).
- The Steam relay (P2P) under Proton and with `libsteam_api.so`; the Windows two-account test is also still pending (see README, *Known limitations*).
- Flatpak/Snap Steam layouts and Steam Deck game mode (script written from the usual paths, never run there).
- `shellcheck` and `bash -n` are clean.

## Building the Linux package

On a machine with the .NET SDK 8+, after `installer/build.ps1` has produced `installer/build/payload` (or copying that folder):

```bash
installer/linux/make-package.sh            # -> dist/Asyncronus-linux-X.Y.Z.tar.gz
```

It contains `install.sh`, `uninstall.sh`, `lib.sh`, `tools/AsyncronusDelta.dll` and `payload/` (the same payload as the MSI: delta, third-party libraries, mod, HookGen tools). No game code.
