using System;
using System.Collections;
using Hkmp.Game.Server;
using Hkmp.Game.Settings;
using Hkmp.Networking.SteamBridge;
using Hkmp.Networking.Client;
using Hkmp.Ui.Component;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui;

/// <summary>
/// Class for creating and managing the connect interface.
/// </summary>
internal class ConnectInterface {
    /// <summary>
    /// The indent of some text elements.
    /// </summary>
    private const float TextIndentWidth = 5f;

    /// <summary>
    /// The text of the connection button if not connected.
    /// </summary>
    private const string ConnectText = "Connect";

    /// <summary>
    /// The text of the connection button while connecting.
    /// </summary>
    private const string ConnectingText = "Connecting...";

    /// <summary>
    /// The text of the host button while not hosting.
    /// </summary>
    private const string StartHostingText = "Start Hosting";

    /// <summary>
    /// The text of the join with code button while not joining.
    /// </summary>
    private const string JoinWithCodeText = "Join / Unirse";

    /// <summary>
    /// The text of the join with code button while the Steam connection is being established.
    /// </summary>
    private const string JoiningWithCodeText = "Steam...";

    /// <summary>
    /// Horizontal distance of the two columns (IP/port and Steam code) from the center.
    /// </summary>
    private const float ColumnOffset = 150f;

    /// <summary>
    /// The time in seconds to hide the feedback text after it appeared.
    /// </summary>
    private const float FeedbackTextHideTime = 10f;

    /// <summary>
    /// The mod settings.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The component group of the connect UI.
    /// </summary>
    private readonly ComponentGroup _connectGroup;

    // /// <summary>
    // /// The component group of the client settings UI.
    // /// </summary>
    // private readonly ComponentGroup _settingsGroup;

    /// <summary>
    /// The username input component.
    /// </summary>
    private IInputComponent _usernameInput;

    /// <summary>
    /// The address input component.
    /// </summary>
    private IInputComponent _addressInput;

    /// <summary>
    /// The port input component.
    /// </summary>
    private IInputComponent _portInput;

    /// <summary>
    /// The connection (connect or disconnect) button component.
    /// </summary>
    private IButtonComponent _connectionButton;

    /// <summary>
    /// The server host button component.
    /// </summary>
    private IButtonComponent _serverButton;

    /// <summary>
    /// The Steam join code input component.
    /// </summary>
    private IInputComponent _codeInput;

    /// <summary>
    /// The optional Steam bridge password input component (used for both hosting and joining).
    /// </summary>
    private IInputComponent _passwordInput;

    /// <summary>
    /// The join with code button component.
    /// </summary>
    private IButtonComponent _joinCodeButton;

    /// <summary>
    /// The text component that shows the join code of this player.
    /// </summary>
    private ITextComponent _ownCodeText;

    /// <summary>
    /// The feedback text component.
    /// </summary>
    private ITextComponent _feedbackText;

    /// <summary>
    /// The coroutine that hides the feedback text after a delay.
    /// </summary>
    private Coroutine _feedbackHideCoroutine;

    /// <summary>
    /// Event that is executed when the connect button is pressed.
    /// </summary>
    public event Action<string, int, string> ConnectButtonPressed;

    /// <summary>
    /// Event that is executed when the start hosting button is pressed.
    /// </summary>
    public event Action<string, int> StartHostButtonPressed;

    /// <summary>
    /// Event that is executed when the join with code button is pressed, with code, password and username.
    /// </summary>
    public event Action<string, string, string> JoinWithCodeButtonPressed;

    /// <summary>
    /// Event that is executed when the copy code button is pressed.
    /// </summary>
    public event Action CopyCodeButtonPressed;

    /// <summary>
    /// The password entered for the Steam bridge (empty if none).
    /// </summary>
    public string SteamPassword => _passwordInput.GetInput() ?? string.Empty;

    public ConnectInterface(
        ModSettings modSettings,
        ComponentGroup connectGroup
        // ComponentGroup settingsGroup
    ) {
        _modSettings = modSettings;

        _connectGroup = connectGroup;
        // _settingsGroup = settingsGroup;

        CreateConnectUi();
    }

    /// <summary>
    /// Callback method for when the client disconnects.
    /// </summary>
    public void OnClientDisconnect() {
        _connectionButton.SetText(ConnectText);
        _connectionButton.SetOnPress(OnConnectButtonPressed);
        _connectionButton.SetInteractable(true);

        ResetJoinCodeButton();
    }

    /// <summary>
    /// Reset the join with code button to its idle state.
    /// </summary>
    public void ResetJoinCodeButton() {
        _joinCodeButton.SetText(JoinWithCodeText);
        _joinCodeButton.SetInteractable(true);
    }

    /// <summary>
    /// Set the text that shows the join code of this player.
    /// </summary>
    /// <param name="text">The text.</param>
    public void SetOwnCodeText(string text) {
        _ownCodeText.SetText(text);
    }

    /// <summary>
    /// Show a feedback message below the buttons.
    /// </summary>
    /// <param name="color">The color of the text.</param>
    /// <param name="text">The content of the text.</param>
    public void ShowFeedback(Color color, string text) {
        SetFeedbackText(color, text);
    }

    /// <summary>
    /// Callback method for when the client successfully connects.
    /// </summary>
    public void OnSuccessfulConnect() {
        // Let the user know that the connection was successful
        SetFeedbackText(Color.green, "Successfully connected");

        // Reset the connection button with the disconnect text
        _connectionButton.SetText(ConnectText);
        _connectionButton.SetInteractable(true);

        ResetJoinCodeButton();
    }

    /// <summary>
    /// Callback method for when the client fails to connect.
    /// </summary>
    /// <param name="result">The result of the failed connection.</param>
    public void OnFailedConnect(ConnectionFailedResult result) {
        // Let the user know that the connection failed based on the result
        switch (result.Reason) {
            case ConnectionFailedReason.InvalidAddons:
                SetFeedbackText(Color.red, "Failed to connect:\nInvalid addons");
                break;
            case ConnectionFailedReason.SocketException:
            case ConnectionFailedReason.IOException:
                SetFeedbackText(Color.red, "Failed to connect:\nInternal error");
                break;
            case ConnectionFailedReason.TimedOut:
                SetFeedbackText(Color.red, "Failed to connect:\nConnection timed out");
                break;
            case ConnectionFailedReason.Other:
                var message = ((ConnectionFailedMessageResult) result).Message;
                SetFeedbackText(Color.red, $"Failed to connect:\n{message}");
                break;
            default:
                SetFeedbackText(Color.red, $"Failed to connect:\nUnknown reason");
                break;
        }

        // Enable the connect button again
        _connectionButton.SetText(ConnectText);
        _connectionButton.SetInteractable(true);

        ResetJoinCodeButton();
    }

    /// <summary>
    /// Create the connection UI.
    /// </summary>
    private void CreateConnectUi() {
        // Now we can start adding individual components to our UI
        // Keep track of current x and y of objects we want to place
        var centerX = 1920f / 2f;
        var x = centerX - ColumnOffset;
        var y = 1080f - 400f;

        const float labelHeight = 20f;

        // ReSharper disable once ObjectCreationAsStatement
        new TextComponent(
            _connectGroup,
            new Vector2(x + TextIndentWidth, y),
            new Vector2(212f, labelHeight),
            "Username",
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );

        y -= labelHeight + 14f;

        _usernameInput = new InputComponent(
            _connectGroup,
            new Vector2(x, y),
            _modSettings.Username,
            "Username",
            characterLimit: 20,
            onValidateInput: (_, _, addedChar) => UsernameRules.IsValidChar(addedChar) ? addedChar : '\0'
        );

        // Default to the Steam profile name (the typed/saved username is the fallback without Steam)
        if (_modSettings.UseSteamName) {
            var steamUsername = SteamBridgeService.GetSteamUsername();
            if (steamUsername != null) {
                _usernameInput.SetInput(steamUsername);
            }
        }

        y -= InputComponent.DefaultHeight + 20f;

        // ReSharper disable once ObjectCreationAsStatement
        new TextComponent(
            _connectGroup,
            new Vector2(x + TextIndentWidth, y),
            new Vector2(212f, labelHeight),
            "Server IP and port",
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );

        y -= labelHeight + 14f;

        _addressInput = new IpInputComponent(
            _connectGroup,
            new Vector2(x, y),
            _modSettings.ConnectAddress,
            "IP Address"
        );

        y -= InputComponent.DefaultHeight + 8f;

        var joinPort = _modSettings.ConnectPort;
        _portInput = new PortInputComponent(
            _connectGroup,
            new Vector2(x, y),
            joinPort == -1 ? "" : joinPort.ToString(),
            "Port"
        );

        y -= InputComponent.DefaultHeight + 20f;

        _connectionButton = new ButtonComponent(
            _connectGroup,
            new Vector2(x, y),
            ConnectText
        );
        _connectionButton.SetOnPress(OnConnectButtonPressed);

        y -= ButtonComponent.DefaultHeight + 8f;

        _serverButton = new ButtonComponent(
            _connectGroup,
            new Vector2(x, y),
            StartHostingText
        );
        _serverButton.SetOnPress(OnStartButtonPressed);

        y -= ButtonComponent.DefaultHeight + 8f;

        var steamColumnBottom = CreateSteamColumn(centerX + ColumnOffset, 1080f - 400f, labelHeight);
        y = Mathf.Min(y, steamColumnBottom);

        _feedbackText = new TextComponent(
            _connectGroup,
            new Vector2(centerX, y),
            new Vector2(540f, labelHeight),
            new Vector2(0.5f, 1f),
            "",
            UiManager.SubTextFontSize,
            alignment: TextAnchor.UpperCenter
        );
        _feedbackText.SetActive(false);
    }

    /// <summary>
    /// Create the Steam join code column of the connect UI.
    /// </summary>
    /// <param name="x">The x coordinate of the column center.</param>
    /// <param name="y">The y coordinate of the top of the column.</param>
    /// <param name="labelHeight">The height of labels.</param>
    /// <returns>The y coordinate below the last element.</returns>
    private float CreateSteamColumn(float x, float y, float labelHeight) {
        // ReSharper disable once ObjectCreationAsStatement
        new TextComponent(
            _connectGroup,
            new Vector2(x + TextIndentWidth, y),
            new Vector2(240f, labelHeight),
            "Steam code / Código",
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );

        y -= labelHeight + 14f;

        _codeInput = new InputComponent(
            _connectGroup,
            new Vector2(x, y),
            _modSettings.SteamJoinCode ?? "",
            "ABCD-EFGH",
            characterLimit: SteamJoinCode.CodeLength + 1,
            onValidateInput: (_, _, addedChar) => {
                if (addedChar == '-') {
                    return addedChar;
                }

                return char.IsLetterOrDigit(addedChar) ? char.ToUpperInvariant(addedChar) : '\0';
            }
        );

        y -= InputComponent.DefaultHeight + 8f;

        _passwordInput = new PasswordInputComponent(
            _connectGroup,
            new Vector2(x, y),
            "",
            "Password / Contraseña",
            SteamBridgeProtocol.MaxPasswordLength
        );

        y -= InputComponent.DefaultHeight + 20f;

        _joinCodeButton = new ButtonComponent(
            _connectGroup,
            new Vector2(x, y),
            JoinWithCodeText
        );
        _joinCodeButton.SetOnPress(OnJoinWithCodeButtonPressed);

        y -= ButtonComponent.DefaultHeight + 8f;

        var copyButton = new ButtonComponent(
            _connectGroup,
            new Vector2(x, y),
            "Copy my code / Copiar"
        );
        copyButton.SetOnPress(() => CopyCodeButtonPressed?.Invoke());

        y -= ButtonComponent.DefaultHeight + 8f;

        _ownCodeText = new TextComponent(
            _connectGroup,
            new Vector2(x, y),
            new Vector2(280f, labelHeight * 2f + 8f),
            new Vector2(0.5f, 1f),
            "",
            UiManager.SubTextFontSize,
            alignment: TextAnchor.UpperCenter
        );

        y -= labelHeight * 2f + 16f;

        return y;
    }

    /// <summary>
    /// Callback method for when the join with code button is pressed.
    /// </summary>
    private void OnJoinWithCodeButtonPressed() {
        var code = _codeInput.GetInput();
        if (!SteamJoinCode.TryDecode(code, out _, out var error)) {
            SetFeedbackText(
                Color.red,
                error == SteamJoinCodeError.ChecksumMismatch
                    ? "Code has a typo / El código tiene un error"
                    : "Invalid code / Código no válido"
            );
            return;
        }

        if (!ValidateUsername(out var username)) {
            return;
        }

        var normalized = SteamJoinCode.Format(SteamJoinCode.Normalize(code));
        _codeInput.SetInput(normalized);
        _modSettings.SteamJoinCode = normalized;
        _modSettings.Username = username;

        _joinCodeButton.SetText(JoiningWithCodeText);
        _joinCodeButton.SetInteractable(false);

        JoinWithCodeButtonPressed?.Invoke(normalized, SteamPassword, username);
    }

    /// <summary>
    /// Callback method for when the connect button is pressed.
    /// </summary>
    private void OnConnectButtonPressed() {
        var address = _addressInput.GetInput();

        if (address.Length == 0) {
            // Let the user know that the address is empty
            SetFeedbackText(Color.red, "Failed to connect:\nYou must enter an address");

            return;
        }

        var portString = _portInput.GetInput();

        var parsedPort = int.TryParse(portString, out var port);
        if (!parsedPort || port == 0) {
            // Let the user know that the entered port is incorrect
            SetFeedbackText(Color.red, "Failed to connect:\nYou must enter a valid port");

            return;
        }

        Logger.Debug($"Connect button pressed, address: {address}:{port}");

        if (!ValidateUsername(out var username)) {
            return;
        }

        // Input values were valid, so we can store them in the settings
        Logger.Debug($"Saving join address {address} in global settings");
        Logger.Debug($"Saving join port {port} in global settings");
        Logger.Debug($"Saving join username {username} in global settings");
        _modSettings.ConnectAddress = address;
        _modSettings.ConnectPort = port;
        _modSettings.Username = username;

        // Disable the connect button while we are trying to establish a connection
        _connectionButton.SetText(ConnectingText);
        _connectionButton.SetInteractable(false);

        ConnectButtonPressed?.Invoke(address, port, username);
    }

    /// <summary>
    /// Callback method for when the start hosting button is pressed.
    /// </summary>
    private void OnStartButtonPressed() {
        var portString = _portInput.GetInput();

        var parsedPort = int.TryParse(portString, out var port);
        if (!parsedPort || port == 0) {
            // Let the user know that the entered port is incorrect
            SetFeedbackText(Color.red, "Failed to host:\nYou must enter a valid port");

            return;
        }
        
        if (!ValidateUsername(out var username)) {
            return;
        }

        // Start the server in networkManager
        StartHostButtonPressed?.Invoke(username, port);
    }

    /// <summary>
    /// Set the feedback text with the given color and content.
    /// </summary>
    /// <param name="color">The color of the text.</param>
    /// <param name="text">The content of the text.</param>
    private void SetFeedbackText(Color color, string text) {
        _feedbackText.SetColor(color);
        _feedbackText.SetText(text);
        _feedbackText.SetActive(true);

        if (_feedbackHideCoroutine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(_feedbackHideCoroutine);
        }

        _feedbackHideCoroutine = MonoBehaviourUtil.Instance.StartCoroutine(WaitHideFeedbackText());
    }

    /// <summary>
    /// Coroutine for hiding the feedback text after a delay.
    /// </summary>
    /// <returns>An enumerator for the coroutine.</returns>
    private IEnumerator WaitHideFeedbackText() {
        yield return new WaitForSeconds(FeedbackTextHideTime);

        _feedbackText.SetActive(false);
    }

    private bool ValidateUsername(out string username) {
        username = _usernameInput.GetInput();

        if (_modSettings.UseSteamName) {
            var steamUsername = SteamBridgeService.GetSteamUsername();
            if (steamUsername != null) {
                username = steamUsername;
                _usernameInput.SetInput(username);
            }
        }

        if (username.Length == 0) {
            SetFeedbackText(Color.red, "Failed to connect:\nYou must enter a username");
            return false;
        }

        if (username.Length > ServerManager.UsernameMaxLength) {
            SetFeedbackText(Color.red, "Failed to connect:\nUsername is too long");
            return false;
        }

        return true;
    }
}
