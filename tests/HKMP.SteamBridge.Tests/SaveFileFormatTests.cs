using System;
using System.Linq;
using System.Text;
using Hkmp.Util;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Tests for the save slot format conversion that keeps saves written by the Unity 6 Modding API (plain JSON)
/// loadable by the game (which expects the encrypted BinaryFormatter format).
/// </summary>
public class SaveFileFormatTests {
    private const string SmallJson = "{\"playerData\":{\"geo\":5}}";

    /// <summary>
    /// The encrypted bytes of <see cref="SmallJson"/>, produced by an independent implementation (Python,
    /// cryptography) that re-encodes a save written by the unmodded game byte for byte.
    /// </summary>
    private const string SmallJsonEncryptedHex =
        "0001000000ffffffff010000000000000006010000002c5446654972545a44514b736948353462434c6b464c636b5a5642682f69" +
        "55366d7767456c654942356d49553d0b";

    [Fact]
    public void Encrypt_MatchesTheGameFormat() {
        Assert.Equal(SmallJsonEncryptedHex, Convert.ToHexString(SaveFileFormat.Encrypt(SmallJson)).ToLowerInvariant());
    }

    [Fact]
    public void Decrypt_ReadsTheGameFormat() {
        Assert.True(SaveFileFormat.TryDecrypt(Convert.FromHexString(SmallJsonEncryptedHex), out var json));
        Assert.Equal(SmallJson, json);
    }

    [Fact]
    public void RoundTrip_LargeSaveUsesMultiByteLengthPrefix() {
        // Real saves are ~60 KB, so the 7-bit length prefix takes 3 bytes
        var json = "{\"playerData\":{\"name\":\"" + new string('x', 70000) + "\",\"n\":\"ñ€\"}}";

        var encrypted = SaveFileFormat.Encrypt(json);

        Assert.False(SaveFileFormat.IsPlainJson(encrypted));
        Assert.True(SaveFileFormat.TryDecrypt(encrypted, out var decrypted));
        Assert.Equal(json, decrypted);
    }

    [Fact]
    public void EncryptPlainJson_StripsBom() {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(SmallJson)).ToArray();

        Assert.True(SaveFileFormat.IsPlainJson(bytes));
        Assert.Equal(SmallJsonEncryptedHex, Convert.ToHexString(SaveFileFormat.EncryptPlainJson(bytes)).ToLowerInvariant());
    }

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("\r\n  {\"a\":1}", true)]
    [InlineData("[1,2]", false)]
    [InlineData("", false)]
    public void IsPlainJson_DetectsJsonObjects(string text, bool expected) {
        Assert.Equal(expected, SaveFileFormat.IsPlainJson(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void IsPlainJson_FalseForEncryptedAndNull() {
        Assert.False(SaveFileFormat.IsPlainJson(Convert.FromHexString(SmallJsonEncryptedHex)));
        Assert.False(SaveFileFormat.IsPlainJson(null));
    }

    [Fact]
    public void TryDecrypt_RejectsGarbage() {
        Assert.False(SaveFileFormat.TryDecrypt(Encoding.UTF8.GetBytes(SmallJson), out _));
        Assert.False(SaveFileFormat.TryDecrypt(null, out _));

        var truncated = Convert.FromHexString(SmallJsonEncryptedHex).Take(30).ToArray();
        Assert.False(SaveFileFormat.TryDecrypt(truncated, out _));
    }
}
