using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Unit.Identity;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new(iterations: 2000);

    [Fact]
    public void VerifiesTheRightPasswordOnly()
    {
        var stored = _hasher.Hash("correct horse battery");
        Assert.True(_hasher.Verify("correct horse battery", stored));
        Assert.False(_hasher.Verify("correct horse batterY", stored));
        Assert.False(_hasher.Verify("", stored));
    }

    [Fact]
    public void SamePasswordGivesDifferentHashes()
    {
        Assert.NotEqual(_hasher.Hash("same password 1"), _hasher.Hash("same password 1"));
    }

    [Fact]
    public void StoredFormatCarriesItsOwnIterationCount()
    {
        var stored = new PasswordHasher(iterations: 1500).Hash("another password");
        Assert.StartsWith("v1.1500.", stored, StringComparison.Ordinal);
        // A hasher configured differently still verifies old hashes.
        Assert.True(_hasher.Verify("another password", stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1.abc.def.ghi")]
    [InlineData("v9.1000.AAAA.AAAA")]
    public void MalformedStoredValuesNeverVerify(string stored)
    {
        Assert.False(_hasher.Verify("whatever password", stored));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("exactly12chr", true)]
    [InlineData("a much longer passphrase", true)]
    public void PolicyRequiresTwelveCharacters(string password, bool ok)
    {
        Assert.Equal(ok, PasswordHasher.MeetsPolicy(password));
    }
}
