using CloseManager.Web.Auth;
using System.Security.Claims;
using Xunit;
using FluentAssertions;

namespace CloseManager.Tests.Phase1;

/// <summary>
/// Phase 1 smoke tests — no database or running server required.
/// Database-dependent tests (UserSyncService upsert) are in Phase 2 integration tests.
/// </summary>
public class UserSyncServiceTests
{
    [Fact]
    public void GetEntraObjectId_ReturnsGuid_WhenOidClaimPresent()
    {
        var expectedGuid = Guid.NewGuid();
        var principal = MakePrincipal(new Claim("oid", expectedGuid.ToString()));

        var result = UserSyncService.GetEntraObjectId(principal);

        result.Should().Be(expectedGuid);
    }

    [Fact]
    public void GetEntraObjectId_ReturnsEmpty_WhenOidClaimMissing()
    {
        var principal = MakePrincipal();

        var result = UserSyncService.GetEntraObjectId(principal);

        result.Should().Be(Guid.Empty);
    }

    [Fact]
    public void GetEntraObjectId_ReturnsEmpty_WhenOidClaimMalformed()
    {
        var principal = MakePrincipal(new Claim("oid", "not-a-guid"));

        var result = UserSyncService.GetEntraObjectId(principal);

        result.Should().Be(Guid.Empty);
    }

    [Theory]
    [InlineData("Maya Rodriguez", "MR")]
    [InlineData("Sam", "SA")]
    [InlineData("Daniel Ochoa Reyes", "DR")]
    [InlineData("X", "X")]
    public void Initials_DerivedCorrectly(string displayName, string expected)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length >= 2
            ? $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
            : displayName.Length >= 2 ? displayName[..2].ToUpperInvariant() : displayName.ToUpperInvariant();

        initials.Should().Be(expected);
    }

    private static ClaimsPrincipal MakePrincipal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }
}
