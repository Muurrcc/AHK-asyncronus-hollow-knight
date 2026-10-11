# Development notes

Technical notes for people who want to build, debug or port this project. The user-facing description is in the [README](../README.md).

- Base: [HKMP](https://github.com/Extremelyd1/HKMP), commit `f7c35d96f84c3d7b671fc4283795a07b4df97227` (HKMP 2.4.3+ / 3.0.0 line), plus the changes in `src/`.
- Game: Hollow Knight 1.5.12459, Unity 6000.0.61f1, Steam build id 22529139.

## Modding API for Unity 6

The official Modding API does not support this game build. The port used here is [leesiuuuu/HK-Modding-API](https://github.com/leesiuuuu/HK-Modding-API), branch `unity6-support` (PR [hk-modding/api#174](https://github.com/hk-modding/api/pull/174), unmerged), pinned to commit `c6d6079684dd79ed60924b10a3902d74c7fd9f6b`. ModLog reports `Modding API: 0.0.0.0-77`.

```bash
git clone -b unity6-support https://github.com/leesiuuuu/HK-Modding-API.git modapi
cd modapi && git checkout c6d6079684dd79ed60924b10a3902d74c7fd9f6b
cp -r "<game>/hollow_knight_Data/Managed" Vanilla     # ORIGINAL (unmodded) Managed folder
dotnet build PrePatcher -c Release
# The first Assembly-CSharp build fails (MSB3073 / 9009) because PrePatcher/Output is evaluated
# before it exists. Simply repeat it:
dotnet build Assembly-CSharp -c Release --runtime win-x64 -p:CopyDir=
dotnet build Assembly-CSharp -c Release --runtime win-x64 -p:CopyDir=
```

- `-p:CopyDir=` is **mandatory**: otherwise the csproj copies `OutputFinal` into `C:\Program Files (x86)\Steam\...\Managed`.
- `--runtime win-x64` makes it copy the native `unityscenerepacker.dll`.
- Output (`OutputFinal`): patched `Assembly-CSharp.dll`, `MMHOOK_*` (Assembly-CSharp forwards to the TeamCherry.* ones), Mono.Cecil, MonoMod.*, unityscenerepacker.
- The `override/mscorlib.dll` is a compile-time reference only and is **not** installed.
- Always compile against the *original* `Managed` folder, never one that is already patched.
- Building with SDK 8 works. The installer used to build on the user's PC; one friend's machine (SDK 9/10, no .NET Framework reference assemblies) failed with `CS0518: ReadOnlySpan`, root cause not confirmed, which is why the MSI ships a binary delta instead.

## What Unity 6 broke in HKMP, and the fixes

| Problem | Fix |
|---|---|
| `tk2d` types moved to `TeamCherry.TK2D` / `MMHOOK_TeamCherry.TK2D` (CS1069/CS1070) | new references in `HKMP.csproj` |
| TextMeshPro moved to `Unity.TextMeshPro.dll` | new reference |
| The game ships Newtonsoft.Json 13.0.0.0 | version-less reference |
| Unity 6 modules are compiled against netstandard 2.1 (CS1705) | `UseGameCoreLibs` target in `HKMP.csproj` swaps the netstandard 2.0 facade for the game's `Managed\netstandard.dll` before `CoreCompile`. Only netstandard: changing mscorlib/System too breaks the server (.NET Framework). The resulting `HKMP.dll` works for both game and server |
| `ImageConversion.LoadImage` has `ReadOnlySpan` overloads (CS0518) | `Util/TextureUtil.cs` calls the `byte[]` overload by reflection |
| New global class `FontManager` shadows `Hkmp.Ui.Resources.FontManager` (CS0117) | renamed to `HkmpFontManager` |
| IL hooks: `heroInPosition.Invoke(bool)` is now `HeroController::SendHeroInPosition(bool)`; compiler-generated nested types renumbered; `EnterSceneDreamGate` moved to `DoEnterSceneDreamGate` | `CustomHooks.cs`: pattern chosen by what exists, nested types looked up by prefix, manual `ILHook` with fallback |
| No "TMP Settings" resource: `AddComponent<TextMeshPro>()` throws | `HkmpFontManager.EnsureTmpSettings()` creates one at runtime |
| `activeSceneChanged` gives an empty old scene name (`Opening_Sequence` → `Tutorial_01`) → `SetLeftScene(null)` spam | `ClientManager.OnSceneChange` falls back to the last scene and never sends null |
| Server: the game's Newtonsoft.Json fails strong-name validation on .NET Framework | `HKMPServer.csproj` uses the NuGet package 13.0.3 |

## Enemy sync notes

- The network protocol was changed **without bumping the version number**: client and server must come from the same build.
- `FullSynchronisation` defaults to `true` in both `ModSettings` and `ConsoleSettings`. A saved setting with `false` wins.
- Death persistence bug: the client copy `X(Clone)` stored its `PersistentBoolItem` under a different id. Fixed with `Entity.SyncPersistentBoolId`, and the server stores `Death`/`Health`.
- Health, max health, lifeblood and soul are per player: `SaveDataMapping.LocalOnlyPlayerDataFields` (never synced) and `PlayerSpecificDataFields` (forced `SyncType.Player`).
- `SpellFluke.DoDamage` does not go through `HealthManager.Hit`, so Flukenest hits were never forwarded to the host: `On.SpellFluke.DoDamage` hook + `RemoteEffectMarker`.
- Soul Master phase 2: the fake quake orb is a scene trigger simulated only by the scene host.
- Harmless warning: `Tiktik could not update active, client object is null`.

## Steam bridge

Code in `src/HKMP/Networking/SteamBridge/`. Pure logic (code, protocol, relay cores) has no game dependencies and is covered by `tests/HKMP.SteamBridge.Tests`. Steam-specific classes use the game's own `com.rlabrecque.steamworks.net.dll`; the game already calls `SteamAPI.RunCallbacks`, so the bridge does not.

- **Join code:** 32-bit account id + 3 version bits, 7 base32 characters (no `0/1/O/I`) + 1 check digit, formatted `XXXX-XXXX`.
- **Handshake:** one reliable bridge message carrying the optional password; the host answers and closes on mismatch. HKMP's protocol is untouched.
- **Limits:** 16 connections + 8 pending, 15 s handshake, 30 s connect, 60 s idle.
- A closed connection's local socket stays open 15 s (otherwise the server log fills with `ConnectionReset`).
- All clients reach the server from `127.0.0.1`, so IP bans affect everyone; ban by key.

## Developer launch arguments

Inactive unless passed on the command line: `-hkmp-menu`, `-hkmp-connect IP:PORT`, `-hkmp-name NAME`, `-hkmp-test`, `-hkmp-test-health`, `-hkmp-test-attacks`, and for the Steam bridge `-hkmp-steam-pair`, `-hkmp-steam-host-to`, `-hkmp-steam-join`, `-hkmp-steam-host-test`, `-hkmp-menu-shot`. `tests/SteamBridgeHarness` is a manual harness that needs an `HKMPServer` and Steam running.

## Two game instances on one PC

Both share ModLog, saves and settings under `LocalLow\Team Cherry`. To isolate them, patch the company name in `hollow_knight_Data\globalgamemanagers` to another string **of the same length** (it is preceded by an int32 length). Data then goes to `LocalLow\<name>\Hollow Knight` and PlayerPrefs to `HKCU\Software\<name>\Hollow Knight`; copy the original PlayerPrefs with `reg copy` or the game stays on the language picker and mods do not load. Each copy needs `steam_appid.txt` (`367520`) and Steam open. Use `-logFile` for separate Player logs. Instances keep running without focus. Both instances share one Steam account, so Steam P2P between them is not testable.

## Not verified

Godhome, Colosseum, level 2 spells, Sharp Shadow, Dung/Spore/Womb/Weaversong/Grimmchild/Dreamshield charms, real bench and Focus (simulated), PvP enabled, a real two-account Steam relay connection, long sessions.
