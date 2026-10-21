using Hkmp.Util;
using Xunit;

namespace Hkmp.SteamBridge.Tests;

/// <summary>
/// Steam profile name to username conversion and username validity.
/// </summary>
public class UsernameRulesTests {
    [Theory]
    [InlineData("Marci", "Marci")]
    [InlineData("  Marci  B  ", "Marci_B")]
    [InlineData("Dark   Knight", "Dark_Knight")]
    [InlineData("Cañón", "Cañón")]
    [InlineData("[TAG] Name!", "TAG_Name")]
    [InlineData("a\tb\nc", "a_b_c")]
    [InlineData("x-y_z", "x-y_z")]
    public void FromProfileName_ProducesValidUsername(string raw, string expected) {
        var result = UsernameRules.FromProfileName(raw);
        Assert.Equal(expected, result);
        Assert.True(UsernameRules.IsValid(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\U0001F600\U0001F600")]
    [InlineData("!!! ???")]
    public void FromProfileName_ReturnsNullWhenNothingUsable(string raw) {
        Assert.Null(UsernameRules.FromProfileName(raw));
    }

    [Fact]
    public void FromProfileName_TruncatesWithoutTrailingSeparator() {
        var result = UsernameRules.FromProfileName("12345678901234567 abcdefghij");
        Assert.True(result.Length <= UsernameRules.MaxLength);
        Assert.False(result.EndsWith("_"));
        Assert.True(UsernameRules.IsValid(result));

        var exact = UsernameRules.FromProfileName(new string('a', 40));
        Assert.Equal(UsernameRules.MaxLength, exact.Length);
    }

    [Theory]
    [InlineData("Player1", true)]
    [InlineData("a_b-c", true)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("123456789012345678901", false)]
    [InlineData("bad!", false)]
    public void IsValid_FollowsServerRules(string name, bool valid) {
        Assert.Equal(valid, UsernameRules.IsValid(name));
    }

    [Fact]
    public void ToDisplayName_ShowsUnderscoresAsSpaces() {
        Assert.Equal("Dark Knight", UsernameRules.ToDisplayName("Dark_Knight"));
        Assert.Equal("", UsernameRules.ToDisplayName(null));
    }
}
