using System.Collections.Generic;
using Hkmp.Game.Server.Save;
using Hkmp.Game.Settings;
using Hkmp.Logging;
using Hkmp.Menu;
using Hkmp.Util;
using Modding;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp;

/// <summary>
/// Mod class for the HKMP mod.
/// </summary>
internal class HkmpMod : Mod, IGlobalSettings<ModSettings>, ILocalSettings<ModSaveFile>, ICustomMenuMod {
    /// <summary>
    /// Dictionary containing preloaded objects by scene name and object path.
    /// </summary>
    public static Dictionary<string, Dictionary<string, GameObject>> PreloadedObjects;
    
    /// <summary>
    /// Statically create Settings object, so it can be accessed early.
    /// </summary>
    private ModSettings _modSettings = new ModSettings();

    /// <summary>
    /// The game manager instance.
    /// </summary>
    private Game.GameManager _gameManager;

    /// <summary>
    /// The HKMP mod menu.
    /// </summary>
    private ModMenu _modMenu;

    /// <summary>
    /// Construct the HKMP mod.
    /// </summary>
    public HkmpMod() : base("HKMP") {
        // Keep save slots in the format the game can load (the Unity 6 Modding API writes them unencrypted).
        // Registered as early as possible, the main menu reads the save slot stats before Initialize.
        Game.Client.Save.SaveFormatGuard.Register();
    }

    /// <inheritdoc />
    public override string GetVersion() {
        return Version.String;
    }

    /// <inheritdoc />
    public override List<(string, string)> GetPreloadNames() {
        return [
            ("GG_Sly", "Battle Scene/Sly Boss/Cyclone Tink"),
            ("GG_Sly", "Battle Scene/Sly Boss/S1")
        ];
    }

    /// <inheritdoc />
    public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects) {
        PreloadedObjects = preloadedObjects;
        
        // Add the logger that logs to the ModLog
        Logger.AddLogger(new ModLogger());

        Logger.Info($"Initializing HKMP v{Version.String}");

        // Create a persistent gameObject where we can add the MonoBehaviourUtil to
        var gameObject = new GameObject("HKMP Persistent GameObject");
        Object.DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<MonoBehaviourUtil>();

        _gameManager = new Game.GameManager(_modSettings);

        _modMenu = new ModMenu(
            _modSettings,
            _gameManager.ClientManager,
            _gameManager.ServerManager,
            _gameManager.NetClient
        );
        _modMenu.Initialize();
    }

    /// <inheritdoc />
    public void OnLoadGlobal(ModSettings modSettings) {
        _modSettings = modSettings ?? new ModSettings();
    }

    /// <inheritdoc />
    public ModSettings OnSaveGlobal() {
        return _modSettings;
    }

    /// <inheritdoc />
    public void OnLoadLocal(ModSaveFile modSaveFile) {
        _gameManager?.ServerManager?.OnLoadLocal(modSaveFile);
    }
    
    /// <inheritdoc />
    public ModSaveFile OnSaveLocal() {
        // Never throw while the game saves: a failure here must not take the vanilla save down with it
        try {
            return _gameManager?.ServerManager?.OnSaveLocal() ?? new ModSaveFile();
        } catch (System.Exception e) {
            Logging.Logger.Error($"Could not serialize the mod save data, writing an empty one: {e}");
            return new ModSaveFile();
        }
    }

    /// <inheritdoc />
    public MenuScreen GetMenuScreen(MenuScreen modListMenu, ModToggleDelegates? toggleDelegates) {
        return _modMenu.CreateMenu(modListMenu);
    }

    /// <inheritdoc />
    public bool ToggleButtonInsideMenu => true;
}
