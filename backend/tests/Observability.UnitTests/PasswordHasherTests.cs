using FluentAssertions;
using Observability.Infrastructure.Authentication;
using Xunit;

namespace Observability.UnitTests;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_ThenVerify_Succeeds()
    {
        var encoded = _hasher.Hash("correct horse battery staple");
        _hasher.Verify("correct horse battery staple", encoded).Should().BeTrue();
    }

    [Fact]
    public void Verify_WithWrongPassword_Fails()
    {
        var encoded = _hasher.Hash("s3cret");
        _hasher.Verify("not-the-password", encoded).Should().BeFalse();
    }

    [Fact]
    public void Hash_IsSaltedSoIdenticalPasswordsDiffer()
    {
        _hasher.Hash("same").Should().NotBe(_hasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-the-right-format")]
    [InlineData("pbkdf2$abc$salt$hash")]
    [InlineData("pbkdf2$100000$!!!notbase64$hash")]
    public void Verify_WithMalformedEncoded_ReturnsFalseAndDoesNotThrow(string encoded)
    {
        _hasher.Verify("any", encoded).Should().BeFalse();
    }
}
