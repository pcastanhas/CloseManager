using CloseManager.Web.Auth;
using CloseManager.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

namespace CloseManager.Tests.Phase1;

public class UserSyncServiceTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ClaimsPrincipal MakePrincipal(string oid,
        string upn = "test@company.com", string name = "Test User")
    {
        var claims = new[]
        {
            new Claim("oid", oid),
            new Claim("preferred_username", upn),
            new Claim("name", name),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public async Task SyncAsync_NewUser_CreatesUserRow()
    {
        await using var db = CreateDb();
        var svc = new UserSyncService(db, NullLogger<UserSyncService>.Instance);
        var oid = Guid.NewGuid().ToString();

        var userId = await svc.SyncAsync(MakePrincipal(oid, "maya@company.com", "Maya Rodriguez"));

        userId.Should().NotBeNull();
        var user = await db.Users.SingleAsync();
        user.DisplayName.Should().Be("Maya Rodriguez");
        user.Upn.Should().Be("maya@company.com");
        user.EntraObjectId.Should().Be(Guid.Parse(oid));
        user.IsActive.Should().BeTrue();
        user.LastSeenUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncAsync_ExistingUser_UpdatesDisplayNameAndUpn()
    {
        await using var db = CreateDb();
        var svc = new UserSyncService(db, NullLogger<UserSyncService>.Instance);
        var oid = Guid.NewGuid().ToString();

        await svc.SyncAsync(MakePrincipal(oid, "maya@company.com", "Maya Rodriguez"));
        await svc.SyncAsync(MakePrincipal(oid, "maya.r@company.com", "Maya R."));

        var users = await db.Users.ToListAsync();
        users.Should().HaveCount(1);
        users[0].DisplayName.Should().Be("Maya R.");
        users[0].Upn.Should().Be("maya.r@company.com");
    }

    [Fact]
    public async Task SyncAsync_DeactivatedUser_DoesNotReactivate()
    {
        await using var db = CreateDb();
        var svc = new UserSyncService(db, NullLogger<UserSyncService>.Instance);
        var oid = Guid.NewGuid().ToString();

        await svc.SyncAsync(MakePrincipal(oid));
        var user = await db.Users.SingleAsync();
        user.IsActive = false;
        await db.SaveChangesAsync();

        await svc.SyncAsync(MakePrincipal(oid));

        (await db.Users.SingleAsync()).IsActive.Should().BeFalse();
        (await svc.IsDeactivatedAsync(Guid.Parse(oid))).Should().BeTrue();
    }

    [Fact]
    public async Task SyncAsync_UnauthenticatedPrincipal_ReturnsNull()
    {
        await using var db = CreateDb();
        var svc = new UserSyncService(db, NullLogger<UserSyncService>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var userId = await svc.SyncAsync(principal);

        userId.Should().BeNull();
        db.Users.Should().BeEmpty();
    }
}
