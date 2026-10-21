using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Save;
using Hkmp.Logging;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;

namespace Hkmp.Game.Server.Save;

/// <summary>
/// Class that holds save data from a server. This consists of global data relating to the world and individual
/// data specific to each player. This class is only used for storing the save data while the server is running;
/// serialization of this data to the save file is done with <see cref="ModSaveFile"/>.
/// </summary>
internal class ServerSaveData {
    /// <summary>
    /// Name of the variable in PlayerData that denotes a Steel Soul save file.
    /// </summary>
    private const string SteelSoulVarName = "permadeathMode";
    /// <summary>
    /// Name of the variable in PlayerData that denotes a Godseeker save file.
    /// </summary>
    private const string GodseekerVarName = "bossRushMode";
    
    /// <summary>
    /// The file path of the embedded resource file for the Godseeker overrides.
    /// </summary>
    private const string GodseekerFilePath = "Hkmp.Resource.save-data-godseeker.json";
    
    /// <summary>
    /// Save data that is the basis for a Godseeker file and should override player save data when initializing new
    /// save data.
    /// </summary>
    private static readonly Dictionary<ushort, byte[]> GodseekerOverrides;

    /// <summary>
    /// The index that corresponds with the Steel Soul variable.
    /// </summary>
    private static readonly ushort SteelSoulIndex;
    /// <summary>
    /// The index that corresponds with the Godseeker variable.
    /// </summary>
    private static readonly ushort GodseekerIndex;
    
    /// <summary>
    /// The global save data for the server. E.g. broken walls, open doors, etc.
    /// </summary>
    public Dictionary<ushort, byte[]> GlobalSaveData { get; set; }

    /// <summary>
    /// The player specific save data mapped to player's auth keys.
    /// </summary>
    public Dictionary<string, Dictionary<ushort, byte[]>> PlayerSaveData { get; set; }

    /// <summary>
    /// Static constructor for initializing the indices for the Steel Soul and Godseeker variables and the Godseeker
    /// overrides.
    /// </summary>
    static ServerSaveData() {
        if (!SaveDataMapping.Instance.PlayerDataIndices.TryGetValue(SteelSoulVarName, out SteelSoulIndex)) {
            Logger.Warn("Could not find index for steel soul variable");
        }
        
        if (!SaveDataMapping.Instance.PlayerDataIndices.TryGetValue(GodseekerVarName, out GodseekerIndex)) {
            Logger.Warn("Could not find index for godseeker variable");
        }
        
        var deserializedOverrides = FileUtil.LoadObjectFromEmbeddedJson<ModSaveFile.PlayerDataEntries>(GodseekerFilePath);
        GodseekerOverrides = EncodeUtil.ConvertToServerSaveData(new ModSaveFile.SaveData {
            PlayerDataEntries = deserializedOverrides
        });
    }
    
    public ServerSaveData() {
        GlobalSaveData = new Dictionary<ushort, byte[]>();
        PlayerSaveData = new Dictionary<string, Dictionary<ushort, byte[]>>();
    }

    /// <summary>
    /// Get save data that contains global save data and player specific save data for the player with the given auth
    /// key. 
    /// </summary>
    /// <param name="authKey">The auth key that corresponds to the player for the player specific data.</param>
    /// <returns>A dictionary mapping save data indices to byte encoded values.</returns>
    public CurrentSave GetCurrentSaveData(string authKey) {
        var currentSave = new CurrentSave();
        
        if (!PlayerSaveData.TryGetValue(authKey, out var playerSaveData)) {
            if (IsGodseeker()) {
                Logger.Debug("Global server data indicates Godseeker mode, adding overrides for new player data");
                // Obtain a new dictionary with the Godseeker overrides
                playerSaveData = new Dictionary<ushort, byte[]>(GodseekerOverrides);

                // And immediately store it in the player save data dictionary for future use
                PlayerSaveData[authKey] = playerSaveData;
            } else {
                playerSaveData = new Dictionary<ushort, byte[]>();
                currentSave.NewForPlayer = true;

                Logger.Debug("No save data for player yet, marking in CurrentSave");
            }
        }

        var saveData = new Dictionary<ushort, byte[]>(GlobalSaveData);
        foreach (var data in playerSaveData) {
            saveData[data.Key] = data.Value;
        }

        currentSave.SaveData = saveData;

        return currentSave;
    }

    /// <summary>
    /// Move the progress stored for the player with the given auth key under fields that are now shared
    /// (<see cref="SaveDataMapping.SyncType.Server"/>, e.g. masks, vessels, spells and story flags that older
    /// versions stored per player) into the global save data, merging it with the progress already there
    /// (<see cref="SharedProgressRules"/>: OR for flags, max for levels, union for lists, masks/shards and
    /// vessels/fragments as pairs). Without this, the stale per-player value would override the shared value for
    /// that player on join, and the player's own progress would be lost for the others.
    /// </summary>
    /// <param name="authKey">The auth key of the player.</param>
    /// <returns>The global entries that changed (index to encoded value), to broadcast to connected players.
    /// </returns>
    public Dictionary<ushort, byte[]> MergePlayerProgressIntoGlobal(string authKey) {
        var changed = new Dictionary<ushort, byte[]>();
        if (!PlayerSaveData.TryGetValue(authKey, out var playerSaveData) || playerSaveData.Count == 0) {
            return changed;
        }

        var mapping = SaveDataMapping.Instance;
        var sharedPlayerData = new Dictionary<string, ushort>();
        var sharedBools = new List<ushort>();
        foreach (var index in playerSaveData.Keys) {
            if (mapping.PlayerDataIndices.TryGetValue(index, out var name)) {
                if (mapping.PlayerDataVarProperties.TryGetValue(name, out var props) && props.Sync &&
                    props.SyncType == SaveDataMapping.SyncType.Server) {
                    sharedPlayerData[name] = index;
                }
            } else if (mapping.PersistentBoolIndices.TryGetValue(index, out var item)) {
                if (mapping.PersistentBoolVarProperties.TryGetValue(item, out var props) && props.Sync &&
                    props.SyncType == SaveDataMapping.SyncType.Server) {
                    sharedBools.Add(index);
                }
            }
        }

        if (sharedPlayerData.Count == 0 && sharedBools.Count == 0) {
            return changed;
        }

        void SetGlobal(ushort index, byte[] value) {
            if (GlobalSaveData.TryGetValue(index, out var current) && current.SequenceEqual(value)) {
                return;
            }

            GlobalSaveData[index] = value;
            changed[index] = value;
        }

        int? GetInt(Dictionary<ushort, byte[]> data, string name) {
            if (!mapping.PlayerDataIndices.TryGetValue(name, out var index) ||
                !data.TryGetValue(index, out var value)) {
                return null;
            }

            return EncodeUtil.DecodeSaveDataValue(name, value) as int?;
        }

        // Masks/shards and vessels/fragments are merged as pairs, so the result is a state one player really had
        void MergePair(string maxName, string piecesName, int defaultMax, Func<int, int, int> total) {
            if (!sharedPlayerData.ContainsKey(maxName) && !sharedPlayerData.ContainsKey(piecesName)) {
                return;
            }

            var playerMax = GetInt(playerSaveData, maxName);
            var playerPieces = GetInt(playerSaveData, piecesName);
            var serverMax = GetInt(GlobalSaveData, maxName);
            var serverPieces = GetInt(GlobalSaveData, piecesName);
            (int Max, int Pieces) merged;
            if (serverMax == null && serverPieces == null) {
                merged = (playerMax ?? defaultMax, playerPieces ?? 0);
            } else {
                merged = SharedProgressRules.MergePair(
                    (serverMax ?? defaultMax, serverPieces ?? 0),
                    (playerMax ?? serverMax ?? defaultMax, playerPieces ?? serverPieces ?? 0),
                    total
                );
            }

            SetGlobal(mapping.PlayerDataIndices[maxName], EncodeUtil.EncodeSaveDataValue(merged.Max));
            SetGlobal(mapping.PlayerDataIndices[piecesName], EncodeUtil.EncodeSaveDataValue(merged.Pieces));
            sharedPlayerData.Remove(maxName);
            sharedPlayerData.Remove(piecesName);
        }

        var moved = sharedPlayerData.Values.Concat(sharedBools).ToList();

        MergePair("maxHealthBase", "heartPieces", 5, SharedProgressRules.MaskShardTotal);
        MergePair("MPReserveMax", "vesselFragments", 0, SharedProgressRules.VesselFragmentTotal);

        foreach (var pair in sharedPlayerData) {
            var name = pair.Key;
            var index = pair.Value;
            try {
                var playerValue = EncodeUtil.DecodeSaveDataValue(name, playerSaveData[index]);
                var serverValue = GlobalSaveData.TryGetValue(index, out var serverEncoded)
                    ? EncodeUtil.DecodeSaveDataValue(name, serverEncoded)
                    : null;
                var merged = SharedProgressRules.MergeValue(name, serverValue, playerValue);
                SetGlobal(index, EncodeUtil.EncodeSaveDataValue(merged));
            } catch (Exception e) {
                Logger.Warn($"Could not merge stored player progress for '{name}': {e.Message}");
            }
        }

        foreach (var index in sharedBools) {
            var playerValue = playerSaveData[index];
            var activated = playerValue.Length > 0 && playerValue[0] != 0;
            if (GlobalSaveData.TryGetValue(index, out var serverValue) && serverValue.Length > 0 && serverValue[0] != 0) {
                continue;
            }

            if (activated || !GlobalSaveData.ContainsKey(index)) {
                SetGlobal(index, playerValue);
            }
        }

        foreach (var index in moved) {
            playerSaveData.Remove(index);
        }

        Logger.Info(
            $"Merged {moved.Count} stored per-player entries of shared progress into the global save " +
            $"({changed.Count} global values changed)"
        );

        return changed;
    }

    /// <summary>
    /// Whether the global save data in this instance is for Steel Soul mode.
    /// </summary>
    /// <returns>True if the save data is for Steel Soul mode, false otherwise.</returns>
    public bool IsSteelSoul() {
        if (!GlobalSaveData.TryGetValue(SteelSoulIndex, out var value)) {
            return false;
        }

        return value.Length > 0 && value[0] != 0;
    }

    /// <summary>
    /// Whether the global save data in this instance is for Godseeker mode.
    /// </summary>
    /// <returns>True if the save data is for Godseeker mode, false otherwise.</returns>
    private bool IsGodseeker() {
        if (!GlobalSaveData.TryGetValue(GodseekerIndex, out var value)) {
            return false;
        }

        return value.Length > 0 && value[0] == 1;
    }
}
