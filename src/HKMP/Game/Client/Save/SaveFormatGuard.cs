using System;
using Hkmp.Util;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Save;

/// <summary>
/// Keeps save slots loadable. The Unity 6 Modding API writes the save JSON unencrypted, but the game only reads
/// the encrypted format when save encryption is on (the PC default), so every slot saved with the mod (quitting to
/// the menu, benches) became "corrupted": the save menu cannot read its stats and Continue fails. This guard
/// encrypts plain JSON on its way to disk (so the files are in the vanilla format again, also readable without
/// mods) and lets the game read slots that were already written as plain JSON (repairing them on the next save).
/// See <see cref="SaveFileFormat"/>.
/// </summary>
internal static class SaveFormatGuard {
    private static bool _registered;

    /// <summary>
    /// Register the platform hooks (once).
    /// </summary>
    public static void Register() {
        if (_registered) {
            return;
        }

        _registered = true;
        On.DesktopPlatform.WriteSaveSlot += OnWriteSaveSlot;
        On.DesktopPlatform.ReadSaveSlot += OnReadSaveSlot;
    }

    /// <summary>
    /// Whether the game expects the encrypted format (same condition as GameManager.SaveGame/LoadGame).
    /// </summary>
    private static bool EncryptionExpected() {
        var gm = global::GameManager.instance;
        var platform = Platform.Current;
        if (!gm || gm.gameConfig == null || !platform) {
            // Without a game manager we cannot know, the PC default is encrypted
            return platform == null || !platform.IsFileSystemProtected;
        }

        return gm.gameConfig.useSaveEncryption && !platform.IsFileSystemProtected;
    }

    private static void OnWriteSaveSlot(
        On.DesktopPlatform.orig_WriteSaveSlot orig,
        DesktopPlatform self,
        int slotIndex,
        byte[] bytes,
        Action<bool> callback
    ) {
        try {
            if (SaveFileFormat.IsPlainJson(bytes) && EncryptionExpected()) {
                bytes = SaveFileFormat.EncryptPlainJson(bytes);
                Logger.Info($"Save slot {slotIndex}: writing the save in the encrypted game format");
            }
        } catch (Exception e) {
            Logger.Error($"Save slot {slotIndex}: could not convert the save to the game format, writing as is:\n{e}");
        }

        orig(self, slotIndex, bytes, callback);
    }

    private static void OnReadSaveSlot(
        On.DesktopPlatform.orig_ReadSaveSlot orig,
        DesktopPlatform self,
        int slotIndex,
        Action<byte[]> callback
    ) {
        orig(self, slotIndex, bytes => {
            try {
                if (SaveFileFormat.IsPlainJson(bytes) && EncryptionExpected()) {
                    Logger.Info($"Save slot {slotIndex}: was stored as plain JSON, reading it in the game format");
                    bytes = SaveFileFormat.EncryptPlainJson(bytes);
                }
            } catch (Exception e) {
                Logger.Error($"Save slot {slotIndex}: could not convert the plain JSON save:\n{e}");
            }

            callback?.Invoke(bytes);
        });
    }
}
