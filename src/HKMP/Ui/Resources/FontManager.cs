using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui.Resources;

/// <summary>
/// The font manager that stores fonts that are used in-game.
/// </summary>
internal static class HkmpFontManager {
    /// <summary>
    /// The font used for UI.
    /// </summary>
    public static Font UIFontRegular;

    /// <summary>
    /// Backing field for <see cref="InGameNameFont"/>.
    /// </summary>
    private static TMP_FontAsset _inGameNameFont;

    /// <summary>
    /// Whether the list of available TMP fonts has been logged already (to avoid spamming the log).
    /// </summary>
    private static bool _loggedMissingTmpFont;

    /// <summary>
    /// The font used for usernames above player objects. In the Unity 6 build (1.5.12459) the TMP font assets are
    /// not loaded yet when HKMP initializes in the main menu, so it is resolved lazily on first use.
    /// </summary>
    public static TMP_FontAsset InGameNameFont {
        get {
            if (_inGameNameFont == null) {
                FindInGameNameFont();
            }

            return _inGameNameFont;
        }
    }

    /// <summary>
    /// Load the fonts by trying to find them in the game through Unity.
    /// </summary>
    public static void LoadFonts() {
        foreach (var font in UnityEngine.Resources.FindObjectsOfTypeAll<Font>()) {
            switch (font.name) {
                case "Perpetua":
                    UIFontRegular = font;
                    break;
            }
        }

        if (UIFontRegular == null) {
            Logger.Error("UI font regular is missing!");
        }

        FindInGameNameFont();
        if (_inGameNameFont == null) {
            Logger.Info("In-game name font not loaded yet, will be resolved on first use");
        }
    }

    /// <summary>
    /// Try to find the TMP font asset used for usernames.
    /// </summary>
    private static void FindInGameNameFont() {
        var tmpFonts = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

        TMP_FontAsset fallback = null;
        foreach (var textMeshProFont in tmpFonts) {
            if (textMeshProFont.name == "TrajanPro-Bold SDF") {
                _inGameNameFont = textMeshProFont;
                return;
            }

            if (fallback == null && textMeshProFont.name.IndexOf("trajan", StringComparison.OrdinalIgnoreCase) >= 0) {
                fallback = textMeshProFont;
            }
        }

        if (fallback != null) {
            _inGameNameFont = fallback;
            Logger.Info($"In-game name font fallback: {fallback.name}");
            return;
        }

        if (tmpFonts.Length > 0 && !_loggedMissingTmpFont) {
            _loggedMissingTmpFont = true;

            var names = new List<string>();
            foreach (var textMeshProFont in tmpFonts) {
                names.Add(textMeshProFont.name);
            }

            Logger.Error($"In-game name font is missing! Available TMP fonts: {string.Join(", ", names)}");
        }
    }

    /// <summary>
    /// Make sure TextMeshPro has a settings instance. The Unity 6 build of the game does not ship the
    /// "TMP Settings" resource, so adding a <see cref="TextMeshPro"/> component at runtime throws a
    /// NullReferenceException in <c>TMP_Text.LoadDefaultSettings</c>. In that case an empty settings instance is
    /// created and injected.
    /// </summary>
    public static void EnsureTmpSettings() {
        var instanceField = typeof(TMP_Settings).GetField(
            "s_Instance",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        if (instanceField == null || instanceField.GetValue(null) as TMP_Settings != null) {
            return;
        }

        if (UnityEngine.Resources.Load<TMP_Settings>("TMP Settings") != null) {
            return;
        }

        Logger.Info("TMP Settings resource missing, creating a runtime TMP_Settings instance");

        var settings = ScriptableObject.CreateInstance<TMP_Settings>();
        settings.hideFlags = HideFlags.HideAndDontSave;

        // Make sure no list/array fields are null, TMP iterates some of them without null checks
        foreach (var field in typeof(TMP_Settings).GetFields(
                     BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance
                 )) {
            if (field.GetValue(settings) != null) {
                continue;
            }

            var fieldType = field.FieldType;
            if (fieldType.IsArray) {
                field.SetValue(settings, Array.CreateInstance(fieldType.GetElementType()!, 0));
            } else if (typeof(IList).IsAssignableFrom(fieldType) && !fieldType.IsAbstract && !fieldType.IsInterface) {
                field.SetValue(settings, Activator.CreateInstance(fieldType));
            }
        }

        var defaultFontField = typeof(TMP_Settings).GetField(
            "m_defaultFontAsset",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        if (defaultFontField != null && InGameNameFont != null) {
            defaultFontField.SetValue(settings, InGameNameFont);
        }

        instanceField.SetValue(null, settings);
    }
}
