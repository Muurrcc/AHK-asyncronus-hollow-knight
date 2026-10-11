using System;
using System.Text;

namespace Hkmp.Networking.SteamBridge;

/// <summary>
/// Reason why a join code could not be decoded.
/// </summary>
internal enum SteamJoinCodeError {
    /// <summary>The code was decoded successfully.</summary>
    None,
    /// <summary>The input was empty.</summary>
    Empty,
    /// <summary>The input did not contain the expected number of characters.</summary>
    WrongLength,
    /// <summary>The input contained a character that is not part of the code alphabet.</summary>
    InvalidCharacter,
    /// <summary>The check character did not match (typo in the code).</summary>
    ChecksumMismatch,
    /// <summary>The code is structurally valid but uses an unknown version or an invalid account.</summary>
    Unsupported
}

/// <summary>
/// Short, human readable join code derived from the SteamID64 of the hosting player.
/// <para>
/// Only individual Steam accounts in the public universe (desktop instance) are supported, which means the
/// SteamID64 is fully described by its 32-bit account ID. The account ID (plus 3 version bits, currently 0) is
/// written as 7 base-32 characters followed by 1 Luhn mod 32 check character, formatted as "XXXX-XXXX".
/// The alphabet omits the ambiguous characters 0, 1, O and I.
/// </para>
/// This class has no game or Steam dependencies so it can be unit tested.
/// </summary>
internal static class SteamJoinCode {
    /// <summary>
    /// The 32 characters used in the code, in value order.
    /// </summary>
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>
    /// SteamID64 of account ID 0 for an individual account (universe public, type individual, instance desktop).
    /// </summary>
    public const ulong IndividualAccountBase = 76561197960265728UL;

    /// <summary>
    /// Number of characters that carry data.
    /// </summary>
    private const int DataLength = 7;

    /// <summary>
    /// Number of significant characters in a code (data + check character).
    /// </summary>
    public const int CodeLength = DataLength + 1;

    /// <summary>
    /// The current code version, stored in the 3 bits above the account ID.
    /// </summary>
    private const int Version = 0;

    /// <summary>
    /// Whether the given SteamID64 is an individual account that can be represented by a join code.
    /// </summary>
    /// <param name="steamId64">The SteamID64.</param>
    /// <returns>True if the ID can be encoded, false otherwise.</returns>
    public static bool IsEncodable(ulong steamId64) {
        if (steamId64 <= IndividualAccountBase) {
            return false;
        }

        return steamId64 - IndividualAccountBase <= uint.MaxValue;
    }

    /// <summary>
    /// Try to encode the given SteamID64 into a join code.
    /// </summary>
    /// <param name="steamId64">The SteamID64 of an individual account.</param>
    /// <param name="code">The formatted code ("XXXX-XXXX") or null if the ID cannot be encoded.</param>
    /// <returns>True if the ID was encoded, false otherwise.</returns>
    public static bool TryEncode(ulong steamId64, out string code) {
        code = null;
        if (!IsEncodable(steamId64)) {
            return false;
        }

        var value = ((ulong) Version << 32) | (steamId64 - IndividualAccountBase);

        var chars = new char[CodeLength];
        for (var i = DataLength - 1; i >= 0; i--) {
            chars[i] = Alphabet[(int) (value & 31UL)];
            value >>= 5;
        }

        chars[DataLength] = Alphabet[ComputeCheckValue(chars, DataLength)];

        code = Format(new string(chars));
        return true;
    }

    /// <summary>
    /// Encode the given SteamID64 into a join code.
    /// </summary>
    /// <param name="steamId64">The SteamID64 of an individual account.</param>
    /// <returns>The formatted code.</returns>
    /// <exception cref="ArgumentException">Thrown if the ID is not an individual account ID.</exception>
    public static string Encode(ulong steamId64) {
        if (!TryEncode(steamId64, out var code)) {
            throw new ArgumentException("SteamID is not an individual account ID", nameof(steamId64));
        }

        return code;
    }

    /// <summary>
    /// Try to decode a join code into a SteamID64. Case, spaces and dashes are ignored.
    /// </summary>
    /// <param name="input">The code as typed by the user.</param>
    /// <param name="steamId64">The decoded SteamID64, or 0 if decoding failed.</param>
    /// <param name="error">The reason decoding failed, or <see cref="SteamJoinCodeError.None"/>.</param>
    /// <returns>True if the code was valid, false otherwise.</returns>
    public static bool TryDecode(string input, out ulong steamId64, out SteamJoinCodeError error) {
        steamId64 = 0;

        var normalized = Normalize(input);
        if (normalized.Length == 0) {
            error = SteamJoinCodeError.Empty;
            return false;
        }

        if (normalized.Length != CodeLength) {
            error = SteamJoinCodeError.WrongLength;
            return false;
        }

        foreach (var c in normalized) {
            if (Alphabet.IndexOf(c) < 0) {
                error = SteamJoinCodeError.InvalidCharacter;
                return false;
            }
        }

        var chars = normalized.ToCharArray();
        if (Alphabet[ComputeCheckValue(chars, DataLength)] != chars[DataLength]) {
            error = SteamJoinCodeError.ChecksumMismatch;
            return false;
        }

        ulong value = 0;
        for (var i = 0; i < DataLength; i++) {
            value = (value << 5) | (uint) Alphabet.IndexOf(chars[i]);
        }

        var version = (int) (value >> 32);
        var accountId = value & uint.MaxValue;
        if (version != Version || accountId == 0) {
            error = SteamJoinCodeError.Unsupported;
            return false;
        }

        steamId64 = IndividualAccountBase + accountId;
        error = SteamJoinCodeError.None;
        return true;
    }

    /// <summary>
    /// Try to decode a join code into a SteamID64.
    /// </summary>
    /// <param name="input">The code as typed by the user.</param>
    /// <param name="steamId64">The decoded SteamID64, or 0 if decoding failed.</param>
    /// <returns>True if the code was valid, false otherwise.</returns>
    public static bool TryDecode(string input, out ulong steamId64) {
        return TryDecode(input, out steamId64, out _);
    }

    /// <summary>
    /// Normalize user input: upper case and strip everything that is not a letter or digit.
    /// </summary>
    /// <param name="input">The raw input, may be null.</param>
    /// <returns>The normalized string (never null).</returns>
    public static string Normalize(string input) {
        if (string.IsNullOrEmpty(input)) {
            return string.Empty;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var c in input) {
            if (c == '-' || c == ' ' || c == '_' || char.IsWhiteSpace(c)) {
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Format a normalized code of <see cref="CodeLength"/> characters as "XXXX-XXXX".
    /// </summary>
    /// <param name="normalized">The normalized code.</param>
    /// <returns>The formatted code.</returns>
    public static string Format(string normalized) {
        if (normalized == null || normalized.Length != CodeLength) {
            return normalized;
        }

        return normalized.Substring(0, 4) + "-" + normalized.Substring(4);
    }

    /// <summary>
    /// Compute the Luhn mod N check value over the first <paramref name="length"/> characters.
    /// Detects all single character errors and transpositions of adjacent characters.
    /// </summary>
    private static int ComputeCheckValue(char[] chars, int length) {
        const int n = 32;
        var factor = 2;
        var sum = 0;

        for (var i = length - 1; i >= 0; i--) {
            var addend = factor * Alphabet.IndexOf(chars[i]);
            factor = factor == 2 ? 1 : 2;
            addend = addend / n + addend % n;
            sum += addend;
        }

        return (n - sum % n) % n;
    }
}
