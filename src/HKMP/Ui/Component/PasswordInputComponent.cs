using UnityEngine;
using UnityEngine.UI;

namespace Hkmp.Ui.Component;

/// <summary>
/// Input component that masks its content (used for the optional Steam bridge password).
/// </summary>
internal class PasswordInputComponent : InputComponent {
    public PasswordInputComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        string defaultValue,
        string placeholderText,
        int characterLimit
    ) : base(
        componentGroup,
        position,
        defaultValue,
        placeholderText,
        characterLimit
    ) {
        InputField.contentType = InputField.ContentType.Password;
        InputField.asteriskChar = '*';
    }
}
