using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Unit.Identity;

public class TotpTests
{
    // RFC 6238 appendix B, SHA-1, secret "12345678901234567890". The RFC lists 8 digits; the last 6 are ours.
    private const string RfcSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void CodeMatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(expected, Totp.CodeAt(RfcSecretBase32, at));
    }

    [Fact]
    public void VerifyAcceptsTheCurrentAndAdjacentSteps()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        Assert.True(Totp.Verify(RfcSecretBase32, "005924", now));
        Assert.True(Totp.Verify(RfcSecretBase32, Totp.CodeAt(RfcSecretBase32, now.AddSeconds(-30)), now));
        Assert.True(Totp.Verify(RfcSecretBase32, Totp.CodeAt(RfcSecretBase32, now.AddSeconds(30)), now));
    }

    [Fact]
    public void VerifyRejectsOldCodesAndBadInput()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        Assert.False(Totp.Verify(RfcSecretBase32, Totp.CodeAt(RfcSecretBase32, now.AddSeconds(-90)), now));
        Assert.False(Totp.Verify(RfcSecretBase32, "000000", now));
        Assert.False(Totp.Verify(RfcSecretBase32, "12345", now));
        Assert.False(Totp.Verify(RfcSecretBase32, "abcdef", now));
        Assert.False(Totp.Verify(RfcSecretBase32, "", now));
    }

    [Fact]
    public void NewSecretsAreDistinctAndDecodable()
    {
        var a = Totp.NewSecret();
        var b = Totp.NewSecret();
        Assert.NotEqual(a, b);
        Assert.Equal(32, a.Length); // 20 random bytes
        Assert.Matches("^[A-Z2-7]+$", a);
        Assert.Equal(6, Totp.CodeAt(a, DateTimeOffset.UtcNow).Length);
    }

    [Fact]
    public void Base32RoundTrips()
    {
        byte[] data = [1, 2, 3, 250, 251, 252, 0, 9];
        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        Assert.Equal("MZXW6", Base32.Encode("foo"u8.ToArray())); // RFC 4648, no padding
    }
}
