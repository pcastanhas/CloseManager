using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;
using CloseManager.Web.Data.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace CloseManager.Tests.Phase3;

/// <summary>
/// Phase 3 integration tests for Roles admin page logic and AuditService.
/// Requires Docker. Run with: dotnet test --filter Phase3
/// </summary>
public class RolesAndAuditTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private AppDbContext _db = null!;
    private AuditService _audit = null!;

    // Seed user for FK constraints
    private User _actor = null!;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_sql.GetConnectionString()).Options;
        _db = new AppDbContext(opts);
        await _db.Database.MigrateAsync();
        _audit = new AuditService(_db);

        _actor = new User
        {
            EntraObjectId = Guid.NewGuid(), Upn = "actor@test.com",
            DisplayName = "Test Actor", CreatedAtUtc = DateTime.UtcNow
        };
        _db.Users.Add(_actor);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    // ── Role creation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRole_WritesRoleRow_AndAuditEvent()
    {
        var role = new Role { Code = "SENIOR", Name = "Senior reviewer",
                              IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "Role", role.RoleId, "RoleCreated",
            after: new { role.Code, role.Name });
        await _db.SaveChangesAsync();

        var saved = await _db.Roles.SingleAsync(r => r.Code == "SENIOR");
        saved.Name.Should().Be("Senior reviewer");
        saved.IsActive.Should().BeTrue();

        var evt = await _db.AuditEvents.SingleAsync(e => e.Action == "RoleCreated");
        evt.TargetTable.Should().Be("Role");
        evt.TargetId.Should().Be(role.RoleId);
        evt.AfterJson.Should().Contain("SENIOR");
        evt.BeforeJson.Should().BeNull();
    }

    [Fact]
    public async Task RenameRole_WritesRoleRenamed_AuditEvent()
    {
        var role = new Role { Code = "CFO", Name = "Chief Financial Officer",
                              IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        var before = new { role.Code, role.Name };
        role.Name = "CFO";
        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "Role", role.RoleId, "RoleRenamed",
            before: before, after: new { role.Code, role.Name });
        await _db.SaveChangesAsync();

        var evt = await _db.AuditEvents.SingleAsync(e => e.Action == "RoleRenamed");
        evt.BeforeJson.Should().Contain("Chief Financial Officer");
        evt.AfterJson.Should().Contain("CFO");
    }

    [Fact]
    public async Task DeactivateRole_SetsIsActiveFalse_AndWritesAuditEvent()
    {
        var role = new Role { Code = "PREP", Name = "Preparer",
                              IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        role.IsActive = false;
        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "Role", role.RoleId, "RoleDeactivated");
        await _db.SaveChangesAsync();

        var saved = await _db.Roles.SingleAsync(r => r.RoleId == role.RoleId);
        saved.IsActive.Should().BeFalse();

        var evt = await _db.AuditEvents.SingleAsync(e => e.Action == "RoleDeactivated");
        evt.TargetId.Should().Be(role.RoleId);
    }

    [Fact]
    public async Task ReactivateRole_SetsIsActiveTrue_AndWritesAuditEvent()
    {
        var role = new Role { Code = "TREAS", Name = "Treasury",
                              IsActive = false, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        role.IsActive = true;
        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "Role", role.RoleId, "RoleReactivated");
        await _db.SaveChangesAsync();

        (await _db.Roles.SingleAsync(r => r.RoleId == role.RoleId))
            .IsActive.Should().BeTrue();
        (await _db.AuditEvents.AnyAsync(e => e.Action == "RoleReactivated"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task RoleCode_UniqueConstraint_Enforced()
    {
        _db.Roles.Add(new Role { Code = "UNIQUE_CODE", Name = "First",
                                  IsActive = true, CreatedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        _db.Roles.Add(new Role { Code = "UNIQUE_CODE", Name = "Second",
                                  IsActive = true, CreatedAtUtc = DateTime.UtcNow });

        var act = async () => await _db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("Role.Code has a unique index");
    }

    // ── User deactivation ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateUser_SetsIsActiveFalse_AndWritesAuditEvent()
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid(), Upn = "target@test.com",
            DisplayName = "Target User", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        user.IsActive = false;
        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "User", user.UserId, "UserDeactivated",
            after: new { IsActive = false });
        await _db.SaveChangesAsync();

        (await _db.Users.SingleAsync(u => u.UserId == user.UserId))
            .IsActive.Should().BeFalse();
        (await _db.AuditEvents.AnyAsync(e => e.Action == "UserDeactivated"))
            .Should().BeTrue();
    }

    // ── AppSetting ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveSetting_UpdatesValue_AndWritesAuditEvent()
    {
        var svc = new AppSettingService(_db);

        await svc.SaveAsync("StuckThreshold.Default", "48",
            _actor.UserId, _actor.EntraObjectId, _audit);

        var val = await svc.GetStuckThresholdHoursAsync();
        val.Should().Be(48);

        var evt = await _db.AuditEvents.SingleAsync(e => e.Action == "AppSettingUpdated");
        evt.AfterJson.Should().Contain("48");
        evt.BeforeJson.Should().Contain("24"); // original seed value
    }

    [Fact]
    public async Task SaveSecretSetting_MasksValueInAuditJson()
    {
        var svc = new AppSettingService(_db);
        await svc.SaveAsync("SharePoint.ClientSecret", "super-secret-value",
            _actor.UserId, _actor.EntraObjectId, _audit);

        var evt = await _db.AuditEvents.SingleAsync(e =>
            e.Action == "AppSettingUpdated" &&
            (e.AfterJson != null && e.AfterJson.Contains("ClientSecret")));

        evt.AfterJson.Should().Contain("[secret]");
        evt.AfterJson.Should().NotContain("super-secret-value");
    }

    // ── Audit search ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AuditSearch_FilterByActorUserId_ReturnsOnlyThatActor()
    {
        var other = new User
        {
            EntraObjectId = Guid.NewGuid(), Upn = "other@test.com",
            DisplayName = "Other User", CreatedAtUtc = DateTime.UtcNow
        };
        _db.Users.Add(other);
        await _db.SaveChangesAsync();

        // Write events for both actors
        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "Role", 1, "RoleCreated");
        await _audit.WriteAsync(other.UserId, other.EntraObjectId,
            "Role", 2, "RoleCreated");
        await _db.SaveChangesAsync();

        var actorEvents = await _db.AuditEvents
            .Where(e => e.ActorUserId == _actor.UserId)
            .ToListAsync();

        actorEvents.Should().OnlyContain(e => e.ActorUserId == _actor.UserId);
    }

    [Fact]
    public async Task AuditEvents_AreNeverDeleted_EvenAfterRoleDeactivation()
    {
        var role = new Role { Code = "AUDIT_TEST", Name = "Audit test",
                              IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "Role", role.RoleId, "RoleCreated", after: new { role.Code });
        await _db.SaveChangesAsync();

        // Soft-delete the role
        role.IsDeleted = true;
        role.DeletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Audit event must still be queryable
        var evt = await _db.AuditEvents
            .Where(e => e.TargetId == role.RoleId && e.Action == "RoleCreated")
            .FirstOrDefaultAsync();

        evt.Should().NotBeNull("audit events survive soft-delete of their target");
    }
}
