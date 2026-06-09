using FluentAssertions;
using Microsoft.Extensions.Options;
using Observability.Domain.Identity;
using Observability.Infrastructure.Authentication;
using Xunit;

namespace Observability.UnitTests;

public class AccessTokenServiceTests
{
    private static AccessTokenService Service(int lifetimeMinutes = 480, string key = "unit-test-signing-key-0123456789abcdef") =>
        new(Options.Create(new AccessTokenOptions { SigningKey = key, LifetimeMinutes = lifetimeMinutes }));

    private static User NewUser(Role role = Role.Developer) =>
        new() { Email = "dev@example.com", Role = role };

    [Fact]
    public void Issue_ThenValidate_RoundTripsClaims()
    {
        var svc = Service();
        var user = NewUser(Role.Admin);

        var (token, expiresAt) = svc.Issue(user);
        expiresAt.Should().BeAfter(DateTime.UtcNow);

        var claims = svc.Validate(token);
        claims.Should().NotBeNull();
        claims!.UserId.Should().Be(user.Id);
        claims.Email.Should().Be(user.Email);
        claims.Role.Should().Be(Role.Admin);
    }

    [Fact]
    public void Validate_RejectsTamperedSignature()
    {
        var svc = Service();
        var (token, _) = svc.Issue(NewUser());

        // Flip the last character of the signature segment.
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        svc.Validate(tampered).Should().BeNull();
    }

    [Fact]
    public void Validate_RejectsTokenSignedWithDifferentKey()
    {
        var (token, _) = Service(key: "key-one-0123456789abcdef0123456789").Issue(NewUser());
        Service(key: "key-two-0123456789abcdef0123456789").Validate(token).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only.two")]
    public void Validate_RejectsMalformedToken(string token)
    {
        Service().Validate(token).Should().BeNull();
    }

    [Fact]
    public void Constructor_ThrowsWhenSigningKeyMissing()
    {
        var act = () => new AccessTokenService(Options.Create(new AccessTokenOptions { SigningKey = "" }));
        act.Should().Throw<InvalidOperationException>();
    }
}
