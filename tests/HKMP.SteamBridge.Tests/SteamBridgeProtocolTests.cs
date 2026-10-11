using System;
using Hkmp.Networking.SteamBridge;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

public class SteamBridgeProtocolTests {
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("secret", "secret")]
    [InlineData("contraseña ñandú 漢字", "contraseña ñandú 漢字")]
    public void Hello_RoundTrip(string password, string expected) {
        var hello = SteamBridgeProtocol.BuildHello(password);

        Assert.True(SteamBridgeProtocol.TryParseHello(hello, out var version, out var parsed));
        Assert.Equal(SteamBridgeProtocol.Version, version);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void Hello_LongPasswordIsTrimmed() {
        var hello = SteamBridgeProtocol.BuildHello(new string('x', 500));
        Assert.True(SteamBridgeProtocol.TryParseHello(hello, out _, out var parsed));
        Assert.Equal(SteamBridgeProtocol.MaxPasswordLength, parsed.Length);
    }

    [Fact]
    public void Hello_RejectsMalformed() {
        Assert.False(SteamBridgeProtocol.TryParseHello(null, out _, out _));
        Assert.False(SteamBridgeProtocol.TryParseHello(new byte[3], out _, out _));
        Assert.False(SteamBridgeProtocol.TryParseHello(new byte[8], out _, out _));

        var hello = SteamBridgeProtocol.BuildHello("abc");
        var truncated = new byte[hello.Length - 1];
        Array.Copy(hello, truncated, truncated.Length);
        Assert.False(SteamBridgeProtocol.TryParseHello(truncated, out _, out _));

        var extended = new byte[hello.Length + 1];
        Array.Copy(hello, extended, hello.Length);
        Assert.False(SteamBridgeProtocol.TryParseHello(extended, out _, out _));

        // Invalid UTF-8 in the password
        var invalid = SteamBridgeProtocol.BuildHello("ab");
        invalid[8] = 0xFF;
        Assert.False(SteamBridgeProtocol.TryParseHello(invalid, out _, out _));

        // A response is not a hello
        Assert.False(SteamBridgeProtocol.TryParseHello(SteamBridgeProtocol.BuildResponse(SteamBridgeResult.Accepted), out _, out _));
    }

    [Theory]
    [InlineData((int) SteamBridgeResult.Accepted)]
    [InlineData((int) SteamBridgeResult.WrongPassword)]
    [InlineData((int) SteamBridgeResult.ServerFull)]
    [InlineData((int) SteamBridgeResult.VersionMismatch)]
    public void Response_RoundTrip(int resultValue) {
        var result = (SteamBridgeResult) resultValue;
        var response = SteamBridgeProtocol.BuildResponse(result);
        Assert.True(SteamBridgeProtocol.TryParseResponse(response, out var parsed));
        Assert.Equal(result, parsed);
    }

    [Fact]
    public void Response_RejectsMalformed() {
        Assert.False(SteamBridgeProtocol.TryParseResponse(null, out _));
        Assert.False(SteamBridgeProtocol.TryParseResponse(new byte[6], out _));
        Assert.False(SteamBridgeProtocol.TryParseResponse(SteamBridgeProtocol.BuildHello(""), out _));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "anything", true)]
    [InlineData("pw", "pw", true)]
    [InlineData("pw", "PW", false)]
    [InlineData("pw", "pw2", false)]
    [InlineData("pw2", "pw", false)]
    [InlineData("pw", "", false)]
    [InlineData("pw", null, false)]
    [InlineData("ñ", "ñ", true)]
    public void PasswordMatches(string expected, string received, bool matches) {
        Assert.Equal(matches, SteamBridgeProtocol.PasswordMatches(expected, received));
    }

    [Fact]
    public void Describe_IsBilingualForAllResults() {
        foreach (SteamBridgeResult result in Enum.GetValues(typeof(SteamBridgeResult))) {
            Assert.Contains(" / ", SteamBridgeProtocol.Describe(result));
        }
    }
}
