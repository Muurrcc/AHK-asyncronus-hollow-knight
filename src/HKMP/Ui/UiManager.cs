using System;
using System.Collections;
using System.Collections.Generic;
using GlobalEnums;
using Hkmp.Api.Client;
using Hkmp.Game.Settings;
using Hkmp.Networking.Client;
using Hkmp.Networking.SteamBridge;
using Hkmp.Ui.Chat;
using Hkmp.Ui.Component;
using Hkmp.Util;
using Modding;
using Modding.Menu;
using Modding.Menu.Config;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Logger = Hkmp.Logging.Logger;
using Object = UnityEngine.Object;

namespace Hkmp.Ui;

/// <inheritdoc />
internal class UiManager : IUiManager {
    #region Internal UI manager variables and properties

    /// <summary>
    /// The font size of header text.
    /// </summary>
    public const int HeaderFontSize = 34;

    /// <summary>
    /// The font size of normal text.
    /// </summary>
    public const int NormalFontSize = 24;

    /// <summary>
    /// The font size of the chat text.
    /// </summary>
    public const int ChatFontSize = 22;

    /// <summary>
    /// The font size of sub text.
    /// </summary>
    public const int SubTextFontSize = 22;
    
    /// <summary>
    /// The address to connect to the local device.
    /// </summary>
    private const string LocalhostAddress = "127.0.0.1";

    /// <summary>
    /// Expression for the GameManager instance.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static GameManager GM => GameManager.instance;
    
    /// <summary>
    /// Expression for the UIManager instance.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static UIManager UM => UIManager.instance;
    
    /// <summary>
    /// Expression for the InputHandler instance.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static InputHandler IH => InputHandler.Instance;

    /// <summary>
    /// The global GameObject in which all UI is created.
    /// </summary>
    internal static GameObject UiGameObject;

    /// <summary>
    /// The chat box instance.
    /// </summary>
    internal static ChatBox InternalChatBox;
    
    /// <summary>
    /// Event that is fired when a server is requested to be hosted from the UI.
    /// </summary>
    public event Action<int> RequestServerStartHostEvent;

    /// <summary>
    /// Event that is fired when a server is requested to be stopped.
    /// </summary>
    public event Action RequestServerStopHostEvent;

    /// <summary>
    /// Event that is fired when a connection is requested with the given username, IP, port and whether it was a
    /// connection from hosting.
    /// </summary>
    public event Action<string, int, string, bool> RequestClientConnectEvent;

    /// <summary>
    /// Event that is fired when a disconnect is requested.
    /// </summary>
    public event Action RequestClientDisconnectEvent;

    /// <summary>
    /// Event that is executed when the teleport button of the pause menu is pressed.
    /// </summary>
    public event Action RequestTeleportEvent;

    /// <summary>
    /// The component group with the teleport button, only shown while the game is paused.
    /// </summary>
    private ComponentGroup _teleportGroup;

    /// <summary>
    /// Whether the server allows teleporting (and we are connected).
    /// </summary>
    private bool _teleportAvailable;

    /// <summary>
    /// The mod settings for HKMP.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The net client to check if we are connected to a server or not.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The Steam bridge for joining with a code.
    /// </summary>
    private readonly SteamBridgeService _steamBridge;

    /// <summary>
    /// The connect interface.
    /// </summary>
    private ConnectInterface _connectInterface;

    /// <summary>
    /// The menu screen for the connection UI.
    /// </summary>
    private MenuScreen _connectMenu;
    
    /// <summary>
    /// The ping interface.
    /// </summary>
    private PingInterface _pingInterface;
    
    /// <summary>
    /// The group that controls the connection UI.
    /// </summary>
    private ComponentGroup _connectGroup;

    /// <summary>
    /// List of event trigger entries for the original back triggers for the save selection screen. These triggers are
    /// stored when they are override to get back to our connection menu.
    /// </summary>
    private List<EventTrigger.Entry> _originalBackTriggers;

    /// <summary>
    /// Callback action to execute when save slot selection is finished. The boolean parameter indicates whether a
    /// save slot was selected (true) or the menu was exited through the back button (false). 
    /// </summary>
    private Action<bool> _saveSlotSelectedAction;

    #endregion

    #region IUiManager properties

    /// <inheritdoc />
    public IChatBox ChatBox => InternalChatBox;

    #endregion

    /// <summary>
    /// The password entered in the connect menu for the Steam bridge when hosting (empty if none).
    /// </summary>
    public string SteamHostPassword => _connectInterface?.SteamPassword ?? string.Empty;

    /// <summary>
    /// The Steam bridge service (used by the development test helpers).
    /// </summary>
    internal SteamBridgeService SteamBridge => _steamBridge;

    public UiManager(ModSettings modSettings, NetClient netClient, SteamBridgeService steamBridge) {
        _modSettings = modSettings;
        _netClient = netClient;
        _steamBridge = steamBridge;
    }
    
    /// <summary>
    /// Initialize the UI manager by creating UI and register various hooks.
    /// </summary>
    public void Initialize() {
        // First we create a gameObject that will hold all other objects of the UI
        UiGameObject = new GameObject();

        // Create event system object
        var eventSystemObj = new GameObject("EventSystem");

        var eventSystem = eventSystemObj.AddComponent<EventSystem>();
        eventSystem.sendNavigationEvents = true;
        eventSystem.pixelDragThreshold = 10;

        eventSystemObj.AddComponent<StandaloneInputModule>();

        Object.DontDestroyOnLoad(eventSystemObj);

        // Make sure that our UI is an overlay on the screen
        UiGameObject.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        // Also scale the UI with the screen size
        var canvasScaler = UiGameObject.AddComponent<CanvasScaler>();
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

        UiGameObject.AddComponent<GraphicRaycaster>();

        Object.DontDestroyOnLoad(UiGameObject);

        var uiGroup = new ComponentGroup();

        _connectGroup = new ComponentGroup(false);

        _connectInterface = new ConnectInterface(
            _modSettings,
            _connectGroup
        );

        var inGameGroup = new ComponentGroup(parent: uiGroup);

        var infoBoxGroup = new ComponentGroup(parent: inGameGroup);

        InternalChatBox = new ChatBox(infoBoxGroup, _modSettings);

        var pingGroup = new ComponentGroup(parent: inGameGroup);

        _pingInterface = new PingInterface(
            pingGroup,
            _modSettings,
            _netClient
        );

        _teleportGroup = new ComponentGroup(false, uiGroup);
        var teleportButton = new ButtonComponent(
            _teleportGroup,
            new Vector2(1920f - 160f, 1080f - 60f),
            "Teleport / TP"
        );
        teleportButton.SetOnPress(OnTeleportPressed);

        // Register callbacks to make sure the UI is hidden and shown at correct times
        On.UIManager.SetState += (orig, self, state) => {
            orig(self, state);

            _teleportGroup.SetActive(state == UIState.PAUSED && _teleportAvailable && _netClient.IsConnected);

            if (state == UIState.PAUSED) {
                inGameGroup.SetActive(false);
            } else {
                // Only show chat box UI in gameplay scenes
                if (!SceneUtil.IsNonGameplayScene(SceneUtil.GetCurrentSceneName())) {
                    inGameGroup.SetActive(true);
                }
            }
        };
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (_, newScene) => {
            var isNonGamePlayScene = SceneUtil.IsNonGameplayScene(newScene.name);
            
            eventSystem.enabled = !isNonGamePlayScene;
            inGameGroup.SetActive(!isNonGamePlayScene);
        };

        ModHooks.LanguageGetHook += (key, sheet, orig) => {
            if (key == "StartMultiplayerBtn" && sheet == "MainMenu") {
                return "Start Multiplayer";
            }
            
            if (key == "MODAL_PROGRESS" && sheet == "MainMenu" && _netClient.IsConnected) {
                return "You will be disconnected";
            }

            return orig;
        };
        
        On.UIManager.UIGoToMainMenu += (orig, self) => {
            orig(self);

            TryAddMultiplayerScreen();
        };
        
        TryAddMultiplayerScreen();

        _connectInterface.StartHostButtonPressed += (username, port) => {
            OpenSaveSlotSelection(saveSelected => {
                if (!saveSelected) {
                    return;
                }

                RequestServerStartHostEvent?.Invoke(port);
                RequestClientConnectEvent?.Invoke(LocalhostAddress, port, username, true);
            });
        };

        _connectInterface.ConnectButtonPressed += (address, port, username) => {
            RequestClientConnectEvent?.Invoke(address, port, username, false);
        };

        _connectInterface.JoinWithCodeButtonPressed += OnJoinWithCodePressed;
        _connectInterface.CopyCodeButtonPressed += OnCopyCodePressed;
        RefreshOwnSteamCode();

        On.UIManager.ReturnToMainMenu += (orig, self) => {
            RequestClientDisconnectEvent?.Invoke();
            RequestServerStopHostEvent?.Invoke();
            
            return orig(self);
        };

        // Hook to make sure that after game completion cutscenes we do not head to the main menu, but stay hosting/
        // connected to the server. Otherwise, if the host would go to the main menu, every other player would be
        // disconnected
        On.CutsceneHelper.DoSceneLoad += (orig, self) => {
            if (!_netClient.IsConnected) {
                orig(self);
                return;
            }

            var sceneName = self.gameObject.scene.name;
            
            Logger.Debug($"DoSceneLoad of CutsceneHelper for next scene type: {self.nextSceneType}, scene name: {sceneName}");

            var toMainMenu = self.nextSceneType.Equals(CutsceneHelper.NextScene.MainMenu) 
                             || self.nextSceneType.Equals(CutsceneHelper.NextScene.MainMenuNoSave);
            if (self.nextSceneType.Equals(CutsceneHelper.NextScene.PermaDeathUnlock)) {
                toMainMenu |= GM.GetStatusRecordInt("RecPermadeathMode") != 0;
            }

            if (toMainMenu) {
                if (PlayerData.instance.GetInt("permadeathMode") != 0) {
                    // We are running Steel Soul mode, so we disconnect and go to main menu instead of reloading to
                    // the last save point
                    Logger.Debug("  NextSceneType is main menu, disconnecting because of Steel Soul");
                    
                    RequestClientDisconnectEvent?.Invoke();
                    RequestServerStopHostEvent?.Invoke();
                    
                    orig(self);
                    return;
                }
                
                Logger.Debug("  NextSceneType is main menu, transitioning to last save point instead");
                
                GameManager.instance.ContinueGame();
                return;
            }

            orig(self);
        };

        // Test/dev automation through command line arguments (no-op without -hkmp-menu / -hkmp-connect)
        DevAutoConnect.TryStart(this, _netClient, _modSettings.Username);
    }

    /// <summary>
    /// Open the HKMP multiplayer menu from the main menu (used by <see cref="DevAutoConnect"/>).
    /// </summary>
    internal IEnumerator OpenMultiplayerMenu() {
        if (_connectMenu == null) {
            TryAddMultiplayerScreen();
        }

        return GoToMultiplayerMenu();
    }

    /// <summary>
    /// Request a connection to the given server as if the connect button was pressed (used by
    /// <see cref="DevAutoConnect"/>).
    /// </summary>
    internal void RequestConnect(string address, int port, string username) {
        RequestClientConnectEvent?.Invoke(address, port, username, false);
    }

    /// <summary>
    /// Join through the Steam bridge as if the join with code button was pressed (used by
    /// <see cref="DevAutoConnect"/>).
    /// </summary>
    internal void DevJoinWithCode(string code, string password, string username) {
        OnJoinWithCodePressed(code, password, username);
    }

    /// <summary>
    /// Enter the game with the current PlayerData from the multiplayer menu. This assumes that the PlayerData
    /// instance is populated with values already.
    /// </summary>
    public void EnterGameFromMultiplayerMenu(bool newGame) {
        IH.StopUIInput();

        _connectGroup.SetActive(false);

        UM.uiAudioPlayer.PlayStartGame();
        if (MenuStyles.Instance) {
            MenuStyles.Instance.StopAudio();
        }

        if (newGame) {
            Logger.Debug("Entering game from MP menu for new game");
            GM.StartCoroutine(GM.RunStartNewGame());
        } else {
            Logger.Debug("Entering game from MP menu for a continued game");
            GM.ContinueGame();
        }
    }
    
    /// <summary>
    /// Start hosting from the main menu on the given save slot as if the "Start Hosting" button was pressed and the
    /// slot was picked in the save menu (continue if the slot has a save, new game otherwise). Used by
    /// <see cref="DevSaveTest"/>.
    /// </summary>
    internal void DevStartHost(string username, int port, int slot, bool slotHasSave) {
        OpenSaveSlotSelection(saveSelected => {
            if (!saveSelected) {
                return;
            }

            RequestServerStartHostEvent?.Invoke(port);
            RequestClientConnectEvent?.Invoke(LocalhostAddress, port, username, true);
        });

        UM.StartCoroutine(DevPickSlot(slot, slotHasSave));
    }

    private IEnumerator DevPickSlot(int slot, bool slotHasSave) {
        var waited = 0f;
        yield return new WaitUntil(() => {
            waited += Time.unscaledDeltaTime;
            return UM.menuState == MainMenuState.SAVE_PROFILES || waited > 8f;
        });
        yield return new WaitForSecondsRealtime(2f);
        Logger.Info($"DevPickSlot: menuState {UM.menuState}, slot {slot}, has save {slotHasSave}");

        if (slotHasSave) {
            GM.LoadGameFromUI(slot);
        } else {
            GM.profileID = slot;
            // Skip the first-run overscan/brightness screens of a fresh game profile
            Logger.Info(
                $"DevPickSlot: overscanAdjusted {GM.gameSettings.overscanAdjusted}, " +
                $"brightnessAdjusted {GM.gameSettings.brightnessAdjusted}"
            );
            GM.gameSettings.overscanAdjusted = 1;
            GM.gameSettings.brightnessAdjusted = 1;
            UM.StartNewGame();
        }
    }

    /// <summary>
    /// Callback for the teleport button of the pause menu. Resumes the game the same way as the pause menu's own
    /// resume button: only hiding the menu (UIClosePauseMenu) left GameManager.isPaused set and the input handler
    /// stopped, so the hero could not move nor attack anymore, even after changing scene.
    /// </summary>
    private void OnTeleportPressed() {
        RequestTeleportEvent?.Invoke();

        try {
            UM.TogglePauseGame();
        } catch (Exception e) {
            Logger.Warn($"Could not resume the game after teleport request: {e.Message}");
        }
    }

    /// <summary>
    /// Test helper: press the teleport button of the pause menu.
    /// </summary>
    internal void DevPressTeleport() {
        OnTeleportPressed();
    }

    /// <summary>
    /// Disconnect from the server and return to the main menu without saving (used by <see cref="DevAutoConnect"/>
    /// in the automated tests).
    /// </summary>
    internal void DevDisconnectToMainMenu() {
        RequestClientDisconnectEvent?.Invoke();
        ReturnToMainMenuFromGame();
    }

    /// <summary>
    /// Return to the main menu from in-game. Used whenever the player disconnects from the current server.
    /// </summary>
    public void ReturnToMainMenuFromGame() {
        IH.StopUIInput();
        
        UM.StartCoroutine(GM.ReturnToMainMenu(
            GameManager.ReturnToMainMenuSaveModes.DontSave,
            _ => {
                IH.StartUIInput();
                UM.returnMainMenuPrompt.HighlightDefault();
            }
        ));
    }

    /// <summary>
    /// Open the save slot selection screen (from the multiplayer connect menu) and execute the given callback when
    /// a save is selected.
    /// </summary>
    /// <param name="callback">The action to execute when save slot selection is finished. The boolean parameter
    /// indicates whether a save slot is selected (true) or the back button was pressed (false). Can be null, in which
    /// case no callback is executed.</param>
    public void OpenSaveSlotSelection(Action<bool> callback = null) {
        _saveSlotSelectedAction = SaveSlotSelectedCallback;

        On.GameManager.StartNewGame += OnStartNewGame;
        On.GameManager.ContinueGame += OnContinueGame;

        UM.StartCoroutine(GoToSaveMenu());
        return;

        void SaveSlotSelectedCallback(bool saveSelected) {
            callback?.Invoke(saveSelected);

            On.GameManager.StartNewGame -= OnStartNewGame;
            On.GameManager.ContinueGame -= OnContinueGame;
        }
    }
    
    /// <summary>
    /// Callback method for when a new game is started. This is used to check when to start a hosted server from
    /// the save menu.
    /// </summary>
    private void OnStartNewGame(On.GameManager.orig_StartNewGame orig, GameManager self, bool permaDeathMode, bool bossRushMode) {
        orig(self, permaDeathMode, bossRushMode);
        _saveSlotSelectedAction.Invoke(true);
    }

    /// <summary>
    /// Callback method for when a save file is continued. This is used to check when to start a hosted server from
    /// the save menu.
    /// </summary>
    private void OnContinueGame(On.GameManager.orig_ContinueGame orig, GameManager self) {
        orig(self);
        _saveSlotSelectedAction.Invoke(true);
    }

    /// <summary>
    /// Try to add the "Start Multiplayer" button and multiplayer menu screen to the main menu. Will not add the button
    /// or screen if they already exist.
    /// </summary>
    private void TryAddMultiplayerScreen() {
        var btnParent = UM.mainMenuButtons.gameObject;
        if (!btnParent) {
            return;
        }

        var startMultiBtn = btnParent.FindGameObjectInChildren("StartMultiplayerButton");
        if (startMultiBtn) {
            FixMultiplayerButtonNavigation(startMultiBtn);
            
            return;
        }

        var startGameBtn = UM.mainMenuButtons.startButton.gameObject;
        if (!startGameBtn) {
            return;
        }

        startMultiBtn = Object.Instantiate(startGameBtn, btnParent.transform);
        if (!startMultiBtn) {
            return;
        }

        startMultiBtn.name = "StartMultiplayerButton";
        startMultiBtn.transform.SetSiblingIndex(1);

        var autoLocalize = startMultiBtn.GetComponent<AutoLocalizeTextUI>();
        autoLocalize.textKey = "StartMultiplayerBtn";
        autoLocalize.RefreshTextFromLocalization();

        var eventTrigger = startMultiBtn.GetComponent<EventTrigger>();
        eventTrigger.triggers.Clear();
        
        ChangeBtnTriggers(eventTrigger, () => UM.StartCoroutine(GoToMultiplayerMenu()));
        
        // Fix navigation now and when the IH can accept input again
        FixMultiplayerButtonNavigation(startMultiBtn);
        
        UM.StartCoroutine(WaitForInput());
        IEnumerator WaitForInput() {
            yield return new WaitUntil(() => IH.acceptingInput);
            
            FixMultiplayerButtonNavigation(startMultiBtn);   
        }
        
        _connectMenu = MenuUtils.CreateMenuBuilder("HKMP").AddControls(
            new SingleContentLayout(
                new AnchoredPosition(
                    new Vector2(0.5f, 0.5f), 
                    new Vector2(0.5f, 0.5f), 
                    new Vector2(0.0f, -64f)
                )
            ),
            c => {
                Action<MenuSelectable> returnAction = _ => {
                    UM.StartCoroutine(UM.HideCurrentMenu());
                    UM.UIGoToMainMenu();
                    
                    _connectGroup.SetActive(false);
                };
                c.AddMenuButton("BackButton", new MenuButtonConfig {
                    Label = Language.Language.Get("NAV_BACK", "MainMenu"),
                    CancelAction = returnAction,
                    SubmitAction = returnAction,
                    Proceed = true
                }, out _);
            }
        ).Build();
    }
    
    /// <summary>
    /// Fix the navigation for the multiplayer button that is added to the main menu.
    /// </summary>
    /// <param name="multiBtnObject">The game object for the multiplayer button.</param>
    private void FixMultiplayerButtonNavigation(GameObject multiBtnObject) {
        // Fix navigation for buttons
        var startMultiBtnMenuBtn = multiBtnObject.GetComponent<MenuButton>();
        if (startMultiBtnMenuBtn) {
            var nav = UM.mainMenuButtons.startButton.navigation;
            nav.selectOnDown = startMultiBtnMenuBtn;
            UM.mainMenuButtons.startButton.navigation = nav;

            nav = UM.mainMenuButtons.optionsButton.navigation;
            nav.selectOnUp = startMultiBtnMenuBtn;
            UM.mainMenuButtons.optionsButton.navigation = nav;

            nav = startMultiBtnMenuBtn.navigation;
            nav.selectOnUp = UM.mainMenuButtons.startButton;
            startMultiBtnMenuBtn.navigation = nav;
        }
    }

    /// <summary>
    /// Coroutine to go to the multiplayer menu of the main menu. 
    /// </summary>
    private IEnumerator GoToMultiplayerMenu() {
        IH.StopUIInput();

        if (UM.menuState == MainMenuState.MAIN_MENU) {
            UM.StartCoroutine(ReflectionHelper.CallMethod<UIManager, IEnumerator>(UM, "FadeOutSprite", UM.gameTitle));
            UM.subtitleFSM.SendEvent("FADE OUT");
            yield return UM.StartCoroutine(UM.FadeOutCanvasGroup(UM.mainMenuScreen));
        } else if (UM.menuState == MainMenuState.SAVE_PROFILES) {
            yield return UM.StartCoroutine(UM.HideSaveProfileMenu());
        }
        
        IH.StartUIInput();

        yield return UM.StartCoroutine(UM.ShowMenu(_connectMenu));

        UM.currentDynamicMenu = _connectMenu;
        UM.menuState = MainMenuState.DYNAMIC_MENU;

        RefreshOwnSteamCode();
        _connectGroup.SetActive(true);
    }

    /// <summary>
    /// Update the text that shows the Steam join code of this player.
    /// </summary>
    private void RefreshOwnSteamCode() {
        if (!_steamBridge.IsEnabled) {
            _connectInterface.SetOwnCodeText("Steam code disabled in settings\nCódigo Steam desactivado");
            return;
        }

        if (_steamBridge.TryGetOwnCode(out var code, out var error)) {
            _connectInterface.SetOwnCodeText($"Your code / Tu código:\n{code}");
        } else {
            _connectInterface.SetOwnCodeText(error);
        }
    }

    /// <summary>
    /// Callback for when the join with code button is pressed: connect the Steam bridge, then connect HKMP to the
    /// local bridge port.
    /// </summary>
    private void OnJoinWithCodePressed(string code, string password, string username) {
        // Disconnect first, so that the disconnect callback does not stop the new bridge later
        if (_netClient.IsConnected) {
            RequestClientDisconnectEvent?.Invoke();
        }

        var started = _steamBridge.Join(
            code,
            password,
            port => {
                _connectInterface.ShowFeedback(Color.white, "Steam OK, connecting...\nConectando...");
                RequestClientConnectEvent?.Invoke(LocalhostAddress, port, username, false);
            },
            message => {
                _connectInterface.ShowFeedback(Color.red, $"Failed to connect:\n{message}");
                _connectInterface.ResetJoinCodeButton();
            },
            out var error
        );

        if (!started) {
            _connectInterface.ShowFeedback(Color.red, $"Failed to connect:\n{error}");
            _connectInterface.ResetJoinCodeButton();
            return;
        }

        _connectInterface.ShowFeedback(Color.white, "Contacting host via Steam...\nContactando por Steam...");
    }

    /// <summary>
    /// Callback for when the copy code button is pressed.
    /// </summary>
    private void OnCopyCodePressed() {
        if (!_steamBridge.TryGetOwnCode(out var code, out var error)) {
            _connectInterface.ShowFeedback(Color.red, error);
            return;
        }

        _connectInterface.ShowFeedback(
            Color.green,
            SteamBridgeService.CopyToClipboard(code)
                ? $"Copied / Copiado: {code}"
                : $"Your code / Tu código: {code}"
        );
    }

    /// <summary>
    /// Callback for when the Steam bridge host was started or failed to start, after a server was started.
    /// </summary>
    /// <param name="success">Whether the bridge is running.</param>
    /// <param name="codeOrError">The join code, or the reason it failed.</param>
    public void OnSteamBridgeHostResult(bool success, string codeOrError) {
        if (success) {
            var copied = SteamBridgeService.CopyToClipboard(codeOrError);
            InternalChatBox.AddMessage(
                $"Steam code / Código: {codeOrError}{(copied ? " (copied / copiado)" : "")}"
            );
        } else if (_steamBridge.IsEnabled) {
            InternalChatBox.AddMessage($"Steam code unavailable / Código no disponible: {codeOrError}");
        }
    }

    /// <summary>
    /// Coroutine to go to the saves menu from the multiplayer menu. Used whenever the user selects to host a server.
    /// </summary>
    private IEnumerator GoToSaveMenu() {
        _connectGroup.SetActive(false);

        yield return UM.HideCurrentMenu();
        yield return UM.GoToProfileMenu();
        
        var saveProfilesBackBtn = UM.saveProfileControls.gameObject.FindGameObjectInChildren("BackButton");
        if (!saveProfilesBackBtn) {
            Logger.Info("saveProfilesBackBtn is null");
            yield break;
        }

        var eventTrigger = saveProfilesBackBtn.GetComponent<EventTrigger>();
        _originalBackTriggers = eventTrigger.triggers;

        eventTrigger.triggers = [];
        ChangeBtnTriggers(eventTrigger, () => {
            On.GameManager.StartNewGame -= OnStartNewGame;
            On.GameManager.ContinueGame -= OnContinueGame;

            _saveSlotSelectedAction.Invoke(false);
            
            UM.StartCoroutine(GoToMultiplayerMenu());
            
            eventTrigger.triggers = _originalBackTriggers;
        });
    }

    /// <summary>
    /// Change the triggers on a button with the given event trigger.
    /// </summary>
    /// <param name="eventTrigger">The event trigger of the button to change.</param>
    /// <param name="action">The action that should be executed whenever the button is triggered.</param>
    private void ChangeBtnTriggers(EventTrigger eventTrigger, Action action) {
        var entry = new EventTrigger.Entry {
            eventID = EventTriggerType.Submit
        };
        entry.callback.AddListener(_ => action.Invoke());
        eventTrigger.triggers.Add(entry);
        
        var entry2 = new EventTrigger.Entry {
            eventID = EventTriggerType.PointerClick
        };
        entry2.callback.AddListener(_ => action.Invoke());
        eventTrigger.triggers.Add(entry2);
    }
    
    #region Internal UI manager methods

    /// <summary>
    /// Callback method for when the client successfully connects.
    /// </summary>
    public void OnSuccessfulConnect() {
        _connectInterface.OnSuccessfulConnect();
        _pingInterface.SetEnabled(true);
    }

    /// <summary>
    /// Callback method for when the client fails to connect.
    /// </summary>
    /// <param name="result">The result of the failed connection.</param>
    public void OnFailedConnect(ConnectionFailedResult result) {
        _steamBridge.StopClient();
        _connectInterface.OnFailedConnect(result);
    }

    /// <summary>
    /// Callback method for when the client disconnects.
    /// </summary>
    public void OnClientDisconnect() {
        _steamBridge.StopClient();
        SetTeleportAvailable(false);
        _connectInterface.OnClientDisconnect();
        _pingInterface.SetEnabled(false);
    }

    /// <summary>
    /// Set whether teleporting to other players is available (server allows it and we are connected). Controls
    /// whether the teleport button shows up in the pause menu.
    /// </summary>
    /// <param name="available">Whether teleporting is available.</param>
    public void SetTeleportAvailable(bool available) {
        _teleportAvailable = available;

        if (!available) {
            _teleportGroup?.SetActive(false);
        }
    }

    #endregion

    #region IUiManager methods

    /// <inheritdoc />
    public void DisableTeamSelection() {
    }

    /// <inheritdoc />
    public void EnableTeamSelection() {
    }

    /// <inheritdoc />
    public void DisableSkinSelection() {
        // SettingsInterface.OnAddonSetSkinSelection(false);
    }

    /// <inheritdoc />
    public void EnableSkinSelection() {
        // SettingsInterface.OnAddonSetSkinSelection(true);
    }

    #endregion
}
