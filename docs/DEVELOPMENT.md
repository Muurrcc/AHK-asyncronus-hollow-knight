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
| New `AwakenChildren` component: activates its inactive targets in `Awake` and deactivates them again in `Start`. HKMP registers entities in between, so e.g. the dream warriors (inactive in their `Warrior` folder until challenged) were taken as active: on the scene host they attacked at once, on clients they were shown | `Entity/AwakenChildrenTracker.cs` (hook on `AwakenChildren.Start`, reads the recorded state) + `EntityActivationRules.cs` (pure, `DreamProgressTests`): an entity woken only temporarily starts inactive |
| No "TMP Settings" resource: `AddComponent<TextMeshPro>()` throws | `HkmpFontManager.EnsureTmpSettings()` creates one at runtime |
| `activeSceneChanged` gives an empty old scene name (`Opening_Sequence` → `Tutorial_01`) → `SetLeftScene(null)` spam | `ClientManager.OnSceneChange` falls back to the last scene and never sends null |
| Server: the game's Newtonsoft.Json fails strong-name validation on .NET Framework | `HKMPServer.csproj` uses the NuGet package 13.0.3 |
| The Unity 6 Modding API (`unity6-support`, `Patches/GameManager.cs` `SaveGame`) writes `userN.dat` as plain JSON, but the vanilla loader and save-menu stats expect the encrypted BinaryFormatter format (`useSaveEncryption`), so every slot saved with the mod (quit to menu, bench) shows as corrupted and Continue fails | `SaveFormatGuard` (hooks `DesktopPlatform.WriteSaveSlot`/`ReadSaveSlot`, registered in the `HkmpMod` constructor) encrypts plain JSON on write and reads already-broken plain JSON slots; format in `Util/SaveFileFormat.cs` (unit tested). Real fix belongs in the API |

## Enemy sync notes

- The network protocol was changed **without bumping the version number**: client and server must come from the same build.
- `FullSynchronisation` defaults to `true` in both `ModSettings` and `ConsoleSettings`. A saved setting with `false` wins.
- Death persistence bug: the client copy `X(Clone)` stored its `PersistentBoolItem` under a different id. Fixed with `Entity.SyncPersistentBoolId`, and the server stores `Death`/`Health`.
- Current health, lifeblood and soul are per player: `SharedProgressRules.LocalOnlyPlayerDataFields` (never synced) and `PlayerSpecificDataFields` (forced `SyncType.Player`). Max masks and soul vessels are shared, see "Shared progress".
- `SpellFluke.DoDamage` does not go through `HealthManager.Hit`, so Flukenest hits were never forwarded to the host: `On.SpellFluke.DoDamage` hook + `RemoteEffectMarker`.
- Soul Master phase 2: the fake quake orb is a scene trigger simulated only by the scene host.
- Enemy targets (`EnemiesTargetNearestPlayer`, default on): only the scene host runs enemy AI, and it only knows the local knight. `Game/Client/Entity/EnemyTargetManager.cs`, for locally simulated entities with a `HealthManager` (and their child FSMs): (1) most enemy actions use the PlayMaker **global** `Hero` directly (shared by all FSMs, so never changed): the action fields (FsmGameObject / FsmOwnerDefault) that reference it are swapped for a per-enemy proxy variable that holds the chosen target; actions named `Set*`, `Send*`, `Call*`, `Invoke*`, `Activate*`, `Destroy*`, `Add*`, `GetFsm*`, `Broadcast*` keep the real knight (they act on the object itself, e.g. roar lock); (2) local variables filled by `GetHero` / `FindGameObject` are pointed at the target; (3) `LineOfSightDetector.CanSeeHero` is also true for a remote player in line of sight (needed to wake e.g. Vengeflies, husks); (4) `Walker` turns towards a remote target. The remote player is its `Player Prefab` object (synchronised position). Choice and hysteresis (20% and 1.5 units closer, 1 s hold, ties by lowest id) in `EnemyTargetSelector.cs` (unit tested). Not handled: other C# components reading `HeroController.instance` (e.g. `ScuttlerControl`), triggers filtered on the `Player` tag (remote player objects are untagged). Dev test: `-hkmp-test p1|p2 -hkmp-test-scenes none -hkmp-test-target Scene:gate` (`Ui/DevEnemyTargetTest.cs`); verified on Tutorial_01 (Vengefly) and Crossroads_01 (Wandering Husk).
- Generic enemies (1.2.3): `EntityManager.RegisterGenericEnemies` used to skip any object with a `HealthManager` that had an already synced child. Entombed Husks (`Grave Zombie`, `RestingGrounds_10`) carry a `Dream Gate Set Lock` battle gate entity, so they stayed local copies and scene clients could not damage the host's enemy. Now only the object itself or an ancestor being networked is a reason to skip (`GenericEnemyRules`, `GenericEnemyRulesTests`). Dev test: `-hkmp-test-dmg p1|p2 [-hkmp-test-dmg-scenes Scene:gate,...]` (`Ui/DevDamageTest.cs`), used for a client-hits-host sweep over 284 rooms.
- Harmless warning: `Tiktik could not update active, client object is null`.

## Shared progress (save sync)

What one player obtains is given to every player of the server. Field lists and merge rules: `Game/Client/Save/SharedProgressRules.cs` (pure, unit tested in `SharedProgressTests`); `SaveDataMapping` forces them on load, `save-data.json` holds the same values. Criterion per PlayerData field:

| Category | Sync | Fields (examples) | Why |
|---|---|---|---|
| Live combat state, derived values | never (`LocalOnlyPlayerDataFields`, 16) | `health`, `healthBlue`, `MPCharge`, `MPReserve`, `maxHealth`, `maxMP`, `MPReserveCap`, hazard respawn | changes every second; the game derives `maxHealth` from `maxHealthBase` + charms |
| Personal state | `Player` (`PlayerSpecificDataFields`, 69, plus the upstream per-player entries left as they were) | position/respawn, shade, dreamgate, `geo`, `geoPool`, pale ore, simple keys, rancid eggs, relics, essence (`dreamOrbs`), nail upgrades, charms (owned, equipped, notches, Salubra, Leg Eater, Divine), map markers, banker, Grey Mourner's flower | each player spends/carries it; equipped charms decide your own build |
| Masks | `Server` | `maxHealthBase`, `heartPieces`, `heartPieceCollected`, `heartPieceMax`, Sly shard purchases | progress |
| Soul vessels | `Server` | `MPReserveMax`, `vesselFragments`, `vesselFragmentCollected/Max`, Sly vessel purchases, stag nest and Basin fountain (`fountainGeo` additive) | progress |
| Spells | `Server` | `hasSpell`, `fireballLevel`, `quakeLevel`, `screamLevel` | progress |
| Abilities and key items | `Server` | dash, walljump, double jump, superdash, shadow dash, acid armour, dream nail/gate/upgrade, nail arts, lantern, King's Brand, tram pass, keys, journal, Hunter's Mark, Godfinder | progress / story gating |
| Story, maps, zones | `Server` | NPC dialogues and encounters, Seer rewards, colosseum, Grimm troupe dialogues, maps, quill, pins, `visited*`, trams, Godhome unlocks | progress; bosses and world state were already `Server` |

- Shared fields use `IgnoreSceneHost: true`: any player may obtain them, not only the scene host. Grub fields (`grub*`, jars, Grubfather rewards incl. the `Heart Piece` at `Crossroads_38`) are handled by the grub sync, not here.
- Scene pickups that give shared progress are shared too (`SharedProgressRules.SharedPickups`): every `Heart Piece` and `Vessel Fragment`, Vengeful Spirit, Mothwing Cloak, Mantis Claw, Isma's Tear, King's Brand, Shade Cloak, Godfinder, Hunter's Mark. Other shinies (charms, relics) stay per player.
- Live apply (`SaveChanges.ApplySharedMaxChange`): a received `maxHealthBase` change moves `maxHealth` by the same amount (not with Joni's Blessing, as vanilla); a character at full health stays full, a damaged one keeps its health (receiving a mask does not heal, unlike picking it up). HUD: `MAX HP UP` broadcast + `HeroController.AddHealth(0)`. A received `MPReserveMax` clamps `MPReserve`, HUD: `NEW SOUL ORB` broadcast + `MP RESERVE UP`. Abilities and spell levels are read live by `HeroController`/FSMs.
- Ability gates (1.2.3): the game keeps a gate flag next to some abilities (`canDash`, `canWallJump`, `canShadowDash`, `canSuperDash`) and `HeroController` only reads the gate. Only `has*` is networked. Before 1.2.3 any received `hasDash` (also `false`, which the full world state sends on join) opened `canDash`, so a client could dash without the Mothwing Cloak. Now the gate copies the received `has*` value (`AbilityGateRules`, `AbilityGateTests`), and `SaveManager.RepairAbilityGates` closes stale gates left in local saves on join and on every scene change; an unlocked ability is never taken away.
- Server guard: `maxHealthBase`, `MPReserveMax` and spell levels never go down; a lower value (outdated client) is ignored and the stored value is sent back to that client.
- Join/conflict: the host never receives `CurrentSave` (its loaded save is the global data); a joining client gets global + its own `Player` entries and its local slot is only backed up. Progress that older versions stored per player under now-shared fields is merged into the global data when that player joins (`ServerSaveData.MergePlayerProgressIntoGlobal`: OR for flags, server value for used-up keys, max for levels, union for lists, masks+shards and vessels+fragments as one pair, larger total wins), removed from the player entry and broadcast live to connected players.
- Dev test: `-hkmp-test-shared p1|p2 -hkmp-test-dir DIR` (`Ui/DevSharedProgressTest.cs`, `HKMP_SHARED` lines): p1 joins first, p2 joins with seeded per-player progress; masks (with HUD), vessels, spells (Vengeful Spirit cast before/after), abilities, story flags both ways, geo stays individual, p2 reconnect with wiped local data.
- Dream bosses: the dream warrior states (`xeroDefeated`... 0/1/2), dream boss defeats (`falseKnightDreamDefeated`...) and warrior pins are shared from any player (`IgnoreSceneHost`, `SharedProgressRules.DreamWarriorFields`/`DreamBossFields`): the essence (state 2) is set by the local ghost NPC of whoever dream nails it, which is often not the scene host, and was dropped. The server never lowers the warrior states nor turns a dream boss defeat off (`MonotonicIntFields`/`MonotonicBoolFields`).
- Dev test: `-hkmp-test-dream p1|p2 -hkmp-test-dir DIR` (`Ui/DevDreamTest.cs`, `HKMP_DREAM` lines; p2 must be started after p1 signals `p1-in-dream`): late joiner in the same dream, leaving it, dream boss defeat, return to the world, Dream Gate warp, dream warrior Xero asleep/challenged on host and client with essence collected by the non-host, death inside a dream. `-hkmp-test-dream-dump Scene:gate,...` logs the FSMs/components of dream and boss related objects.
- `DevAttackTest` sets spell levels back to 0 after casting; with the server guard that reset is corrected back by the server.

## Teleport

`Game/Client/TeleportManager.cs` (client), `TeleportRules.cs` (pure rules, unit tested in `TeleportRulesTests` and `TeleportEdgeRulesTests`), `ServerManager.OnPlayerTeleportRequest` (server).
- Flow: request (throttled: 1.5 s between requests, 4 s while unanswered; refused at once when dead, on a bench, changing scene or in a blocked scene) -> server checks `AllowTeleport`, target exists and is in a scene, neither player is in a blocked scene, and neither is already teleporting (`TeleportFlights`, 5 s or until the next enter-scene: two players teleporting to each other would swap places) -> client unpauses, waits up to 3 s for a safe state (dive, super dash, focus, hazard respawn, short loss of control), then moves (same scene) or does a hazard-respawn scene change.
- Blocked scenes (`TeleportRules.SceneBlockReason`): menus/cutscenes (`Cinematic_*`, `Menu_Title`, `Opening_Sequence`...), Godhome (`GG_*` except `GG_Waterways`, `GG_Pipeway`, `GG_Lurker`), dreams (`Dream_*`, `White_Palace_*`), Colosseum trials (`Room_Colosseum_Bronze/Silver/Gold`). Entering them without their own entry broke them (wrong scene, black screen, hero stuck).
- Landing: when the target scene is set up (`GameManager.BeginScene` hook) the target is checked: no ground within 50 units below, a hazard (`DamageHero.hazardType` 2-5; acid ignored with Isma's Tear) or a non-door `TransitionPoint` touching the knight standing there (target just entered through it) -> closest safe `HazardRespawnMarker` of the scene. Before this a target in the air above spikes killed the teleporter in a spike loop with a black screen, a target above the Dirtmouth well dropped it into Crossroads, and a target that had just entered a scene bounced the teleporter back through the gate.
- Recovery: any failure/timeout ends in `Recover` (unpause, game state, input, hero state, camera, fade; keeps `isInvincible`), but never during a scene load (it waits for the load to end, then also tells HKMP about the scene). Watchdog 120 s, scene load timeout 90 s. Pause-menu button: ignores a second press within 1 s and only resumes if the menu is still open.
- Dev test: `-hkmp-test-tp p1|p2` (`Ui/DevTeleportTest.cs`, basic cases) and `-hkmp-test-tp p1|p2|p3 -hkmp-test-tp-suite edge [-hkmp-test-tp-players 3] [-hkmp-test-tp-unsafe 1]` (`Ui/DevTeleportEdgeTest.cs`: spikes, pit, boss arena with closed gates, target changing scene, repeated requests/double click, mutual teleport, air/pause/dive/super dash/focus/bench/dialogue/half-pause/stuck arrival/dead, blocked scenes forged and refused by the server, three players). Both mute the game (`AudioListener.volume = 0`).

## Steam bridge

Code in `src/HKMP/Networking/SteamBridge/`. Pure logic (code, protocol, relay cores) has no game dependencies and is covered by `tests/HKMP.SteamBridge.Tests`. Steam-specific classes use the game's own `com.rlabrecque.steamworks.net.dll`; the game already calls `SteamAPI.RunCallbacks`, so the bridge does not.

- **Join code:** 32-bit account id + 3 version bits, 7 base32 characters (no `0/1/O/I`) + 1 check digit, formatted `XXXX-XXXX`.
- **Handshake:** one reliable bridge message carrying the optional password; the host answers and closes on mismatch. HKMP's protocol is untouched.
- **Limits:** 16 connections + 8 pending, 15 s handshake, 30 s connect, 60 s idle.
- A closed connection's local socket stays open 15 s (otherwise the server log fills with `ConnectionReset`).
- All clients reach the server from `127.0.0.1`, so IP bans affect everyone; ban by key.

## Developer launch arguments

Inactive unless passed on the command line: `-hkmp-menu`, `-hkmp-connect IP:PORT`, `-hkmp-name NAME`, `-hkmp-test`, `-hkmp-test-health`, `-hkmp-test-attacks`, and for the Steam bridge `-hkmp-steam-pair`, `-hkmp-steam-host-to`, `-hkmp-steam-join`, `-hkmp-steam-host-test`, `-hkmp-menu-shot`. Save/quit test: `-hkmp-test-save host|client` (`Ui/DevSaveTest.cs`, with `-hkmp-test-dir`, `-hkmp-test-save-slot`, `-hkmp-test-save-port`; the client also needs `-hkmp-connect`): hosts a slot, both change data, quit to the menu with save as client and as host, continue the slot again and check the data (`HKMP_SAVE` lines in the ModLog). `tests/SteamBridgeHarness` is a manual harness that needs an `HKMPServer` and Steam running.

## Two game instances on one PC

Both share ModLog, saves and settings under `LocalLow\Team Cherry`. To isolate them, patch the company name in `hollow_knight_Data\globalgamemanagers` to another string **of the same length** (it is preceded by an int32 length). Data then goes to `LocalLow\<name>\Hollow Knight` and PlayerPrefs to `HKCU\Software\<name>\Hollow Knight`; copy the original PlayerPrefs with `reg copy` or the game stays on the language picker and mods do not load. Each copy needs `steam_appid.txt` (`367520`) and Steam open. Use `-logFile` for separate Player logs. Instances keep running without focus. Both instances share one Steam account, so Steam P2P between them is not testable.

## Not verified

Godhome and Colosseum gameplay (teleport into them is refused), teleport into a real Godhome boss fight or dream by the other player (only covered by the server/client scene checks), teleport with a real dive/super dash/focus/bench/dialogue (simulated states), level 2 spells, Sharp Shadow, Dung/Spore/Womb/Weaversong/Grimmchild/Dreamshield charms, real bench and Focus (simulated), PvP enabled, a real two-account Steam relay connection, long sessions.

## Linux

Installer, requirements and portability review in [LINUX.md](LINUX.md). Unverified on a real Linux machine.
