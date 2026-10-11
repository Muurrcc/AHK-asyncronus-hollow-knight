using System;
using System.Linq;
using System.Text.RegularExpressions;
using Hkmp.Networking.SteamBridge;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

public class SteamJoinCodeTests {
    private const ulong Base = SteamJoinCode.IndividualAccountBase;

    private static readonly Regex CodePattern = new("^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$");

    [Fact]
    public void Alphabet_Has32UniqueUnambiguousChars() {
        Assert.Equal(32, SteamJoinCode.Alphabet.Length);
        Assert.Equal(32, SteamJoinCode.Alphabet.Distinct().Count());
        foreach (var c in "01OI") {
            Assert.DoesNotContain(c, SteamJoinCode.Alphabet);
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(12345678UL)]
    [InlineData(0x7FFFFFFFUL)]
    [InlineData(0xFFFFFFFFUL)]
    public void RoundTrip_EdgeAccountIds(ulong accountId) {
        var steamId = Base + accountId;
        var code = SteamJoinCode.Encode(steamId);

        Assert.Matches(CodePattern, code);
        Assert.True(SteamJoinCode.TryDecode(code, out var decoded, out var error));
        Assert.Equal(SteamJoinCodeError.None, error);
        Assert.Equal(steamId, decoded);
    }

    [Fact]
    public void RoundTrip_RandomAccountIds() {
        var random = new Random(1234);
        for (var i = 0; i < 20000; i++) {
            var accountId = (ulong) (uint) random.Next(1, int.MaxValue) | ((ulong) (uint) random.Next(0, 2) << 31);
            if (accountId == 0) {
                continue;
            }

            var steamId = Base + accountId;
            var code = SteamJoinCode.Encode(steamId);
            Assert.Matches(CodePattern, code);
            Assert.True(SteamJoinCode.TryDecode(code, out var decoded));
            Assert.Equal(steamId, decoded);
        }
    }

    [Fact]
    public void Encode_IsStable() {
        // Known vector: protects against accidental format changes (codes are shared between players and builds)
        // account 1 -> data "2222223", Luhn mod 32: sum = 2*1 = 2 -> check value 30 = 'Y'
        Assert.Equal("2222-223Y", SteamJoinCode.Encode(Base + 1));
        var code = SteamJoinCode.Encode(76561198000000000UL);
        Assert.Equal(code, SteamJoinCode.Encode(76561198000000000UL));
        Assert.True(SteamJoinCode.TryDecode(code, out var id));
        Assert.Equal(76561198000000000UL, id);
    }

    [Fact]
    public void Decode_IgnoresCaseDashesAndSpaces() {
        var steamId = 76561198012345678UL;
        var code = SteamJoinCode.Encode(steamId);
        var compact = code.Replace("-", "");

        foreach (var variant in new[] {
                     code.ToLowerInvariant(),
                     compact,
                     " " + compact.Substring(0, 2) + " " + compact.Substring(2) + " ",
                     code.Replace("-", "_")
                 }) {
            Assert.True(SteamJoinCode.TryDecode(variant, out var decoded, out var error), variant);
            Assert.Equal(SteamJoinCodeError.None, error);
            Assert.Equal(steamId, decoded);
        }
    }

    [Theory]
    [InlineData(null, (int) SteamJoinCodeError.Empty)]
    [InlineData("", (int) SteamJoinCodeError.Empty)]
    [InlineData(" - ", (int) SteamJoinCodeError.Empty)]
    [InlineData("ABC", (int) SteamJoinCodeError.WrongLength)]
    [InlineData("ABCD-EFGHJ", (int) SteamJoinCodeError.WrongLength)]
    [InlineData("ABCD-EFG0", (int) SteamJoinCodeError.InvalidCharacter)]
    [InlineData("ABCD-EFG1", (int) SteamJoinCodeError.InvalidCharacter)]
    [InlineData("ABCD-EFGO", (int) SteamJoinCodeError.InvalidCharacter)]
    [InlineData("ABCD-EFGI", (int) SteamJoinCodeError.InvalidCharacter)]
    [InlineData("ABCD-EF#H", (int) SteamJoinCodeError.InvalidCharacter)]
    public void Decode_RejectsMalformedInput(string input, int expectedError) {
        var expected = (SteamJoinCodeError) expectedError;
        Assert.False(SteamJoinCode.TryDecode(input, out var id, out var error));
        Assert.Equal(expected, error);
        Assert.Equal(0UL, id);
    }

    [Fact]
    public void Decode_DetectsEverySingleCharacterError() {
        var random = new Random(42);
        for (var n = 0; n < 200; n++) {
            var code = SteamJoinCode.Normalize(SteamJoinCode.Encode(Base + (ulong) random.Next(1, int.MaxValue)));
            for (var position = 0; position < code.Length; position++) {
                foreach (var replacement in SteamJoinCode.Alphabet) {
                    if (replacement == code[position]) {
                        continue;
                    }

                    var chars = code.ToCharArray();
                    chars[position] = replacement;
                    Assert.False(SteamJoinCode.TryDecode(new string(chars), out _), $"{code} -> {new string(chars)}");
                }
            }
        }
    }

    [Fact]
    public void Decode_DetectsAdjacentTranspositions_ExceptLuhnBlindPair() {
        // Luhn mod 32 detects every adjacent transposition except swapping the values 0 and 31 ('2' and 'Z')
        var random = new Random(7);
        var checkedSwaps = 0;
        for (var n = 0; n < 2000; n++) {
            var code = SteamJoinCode.Normalize(SteamJoinCode.Encode(Base + (ulong) random.Next(1, int.MaxValue)));
            for (var i = 0; i < code.Length - 1; i++) {
                var a = code[i];
                var b = code[i + 1];
                if (a == b || (a == '2' && b == 'Z') || (a == 'Z' && b == '2')) {
                    continue;
                }

                var chars = code.ToCharArray();
                chars[i] = b;
                chars[i + 1] = a;
                Assert.False(SteamJoinCode.TryDecode(new string(chars), out _), $"{code} swap at {i}");
                checkedSwaps++;
            }
        }

        Assert.True(checkedSwaps > 10000);
    }

    [Fact]
    public void Decode_ReportsChecksumMismatch() {
        var code = SteamJoinCode.Normalize(SteamJoinCode.Encode(76561198012345678UL)).ToCharArray();
        var last = SteamJoinCode.Alphabet.IndexOf(code[7]);
        code[7] = SteamJoinCode.Alphabet[(last + 1) % 32];

        Assert.False(SteamJoinCode.TryDecode(new string(code), out _, out var error));
        Assert.Equal(SteamJoinCodeError.ChecksumMismatch, error);
    }

    [Fact]
    public void Decode_RejectsUnknownVersionAndAccountZero() {
        // Build codes with a valid check character but version bits set / account 0
        Assert.False(SteamJoinCode.TryDecode(BuildRaw(1UL << 32 | 5), out _, out var error));
        Assert.Equal(SteamJoinCodeError.Unsupported, error);

        Assert.False(SteamJoinCode.TryDecode(BuildRaw(0), out _, out error));
        Assert.Equal(SteamJoinCodeError.Unsupported, error);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(76561197960265728UL)] // account 0
    [InlineData(76561197960265728UL + 0x100000000UL)] // other instance
    [InlineData(103582791429521408UL)] // clan / group ID
    [InlineData(90071992547409920UL)] // anonymous game server
    public void Encode_RejectsNonIndividualIds(ulong steamId) {
        Assert.False(SteamJoinCode.IsEncodable(steamId));
        Assert.False(SteamJoinCode.TryEncode(steamId, out var code));
        Assert.Null(code);
        Assert.Throws<ArgumentException>(() => SteamJoinCode.Encode(steamId));
    }

    /// <summary>
    /// Independent implementation of the encoding of an arbitrary 35-bit value (for negative tests).
    /// </summary>
    private static string BuildRaw(ulong value) {
        var alphabet = SteamJoinCode.Alphabet;
        var chars = new char[8];
        for (var i = 6; i >= 0; i--) {
            chars[i] = alphabet[(int) (value & 31)];
            value >>= 5;
        }

        var factor = 2;
        var sum = 0;
        for (var i = 6; i >= 0; i--) {
            var addend = factor * alphabet.IndexOf(chars[i]);
            factor = factor == 2 ? 1 : 2;
            sum += addend / 32 + addend % 32;
        }

        chars[7] = alphabet[(32 - sum % 32) % 32];
        return new string(chars);
    }
}
