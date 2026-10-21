using System.Text;

namespace Hkmp.Util;

/// <summary>
/// Pure rules for player usernames (the name sent in the handshake and shown above the characters), and the
/// conversion of a Steam profile name into a valid username.
/// </summary>
internal static class UsernameRules {
    /// <summary>
    /// Maximum length of a username.
    /// </summary>
    public const int MaxLength = 20;

    /// <summary>
    /// Whether the given character is allowed in a username: letters, digits, '_' and '-'.
    /// </summary>
    public static bool IsValidChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    /// <summary>
    /// Whether the given username is valid for the server (non-empty, short enough, only valid characters).
    /// </summary>
    public static bool IsValid(string username) {
        if (string.IsNullOrEmpty(username) || username.Length > MaxLength) {
            return false;
        }

        foreach (var c in username) {
            if (!IsValidChar(c)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Convert a free-form name (for example a Steam profile name) to a valid username: whitespace runs become a
    /// single '_', other invalid characters (emoji, symbols, control characters) are dropped, leading and trailing
    /// '_' are removed and the result is truncated to <see cref="MaxLength"/>.
    /// </summary>
    /// <param name="raw">The raw name.</param>
    /// <returns>The valid username, or null if nothing usable remains.</returns>
    public static string FromProfileName(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return null;
        }

        var builder = new StringBuilder();
        var pendingSeparator = false;

        foreach (var c in raw) {
            if (char.IsWhiteSpace(c)) {
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (!IsValidChar(c)) {
                continue;
            }

            if (pendingSeparator) {
                builder.Append('_');
                pendingSeparator = false;
            }

            builder.Append(c);
        }

        var result = builder.ToString().Trim('_');
        if (result.Length > MaxLength) {
            result = result.Substring(0, MaxLength).Trim('_');
        }

        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// The text shown above a player: the username with '_' shown as spaces (so a Steam name keeps its look).
    /// </summary>
    public static string ToDisplayName(string username) => username?.Replace('_', ' ') ?? "";
}
