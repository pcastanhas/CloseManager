using CloseManager.Web.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace CloseManager.Tests.Phase2;

/// <summary>
/// Phase 2 integration tests.
/// Spins up a real SQL Server container, applies EF migrations, and verifies
/// the schema and seed data are correct. Requires Docker.
///
/// Run with: dotnet test --filter Phase2
/// </summary>
public class SchemaTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _sqlContainer.StartAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_sqlContainer.GetConnectionString())
            .Options;

        _db = new AppDbContext(options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _sqlContainer.DisposeAsync();
    }

    [Fact]
    public async Task AllTables_Exist_AfterMigration()
    {
        var tables = await _db.Database.SqlQueryRaw<string>(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = 'dbo'"
        ).ToListAsync();

        var expected = new[]
        {
            "User", "EntityType", "Entity", "Role", "EntityRoleAssignment",
            "WorkflowTemplate", "WorkstreamDef", "WorkstreamDefStage", "WorkstreamDefChecklistItem",
            "ClosePeriod", "Workstream", "WorkstreamStage",
            "WorkstreamFile", "ChecklistItem", "Comment", "CommentAttachment",
            "AuditEvent", "AppSetting"
        };

        foreach (var table in expected)
            tables.Should().Contain(table, because: $"table [{table}] must exist");
    }

    [Fact]
    public async Task AppSettings_Seeded_WithAllEightKeys()
    {
        var keys = await _db.AppSettings.Select(s => s.Key).ToListAsync();

        keys.Should().Contain("StuckThreshold.Default");
        keys.Should().Contain("Lock.DurationMinutes");
        keys.Should().Contain("Period.CloseConfirmPhrase");
        keys.Should().Contain("SharePoint.TenantId");
        keys.Should().Contain("SharePoint.ClientId");
        keys.Should().Contain("SharePoint.ClientSecret");
        keys.Should().Contain("SharePoint.SiteId");
        keys.Should().Contain("SharePoint.DriveId");
        keys.Should().HaveCount(8);
    }

    [Fact]
    public async Task AppSetting_StuckThreshold_DefaultIs24()
    {
        var setting = await _db.AppSettings.SingleAsync(s => s.Key == "StuckThreshold.Default");
        setting.Value.Should().Be("24");
        setting.IsSecret.Should().BeFalse();
    }

    [Fact]
    public async Task AppSetting_ClientSecret_IsMarkedSecret()
    {
        var setting = await _db.AppSettings.SingleAsync(s => s.Key == "SharePoint.ClientSecret");
        setting.IsSecret.Should().BeTrue();
    }

    [Fact]
    public async Task EntityTypes_Seeded_WithFourTypes()
    {
        var codes = await _db.EntityTypes.Select(t => t.Code).OrderBy(c => c).ToListAsync();
        codes.Should().BeEquivalentTo(
            new[] { "HoldingCo", "InvestmentFund", "OperatingCo", "RealEstateAsset" });
    }

    [Fact]
    public async Task Roles_Seeded_WithExpectedRoles()
    {
        var codes = await _db.Roles.Select(r => r.Code).OrderBy(c => c).ToListAsync();
        codes.Should().Contain("Preparer");
        codes.Should().Contain("Senior");
        codes.Should().Contain("CFO");
        codes.Should().Contain("TreasuryRE");
        codes.Should().Contain("TreasuryInv");
    }

    [Fact]
    public async Task WorkflowTemplate_FilteredUniqueIndex_EnforcesOneCurrentPerEntityType()
    {
        // Seed: one User and one EntityType to satisfy FKs
        var user = new Web.Data.Entities.User
        {
            EntraObjectId = Guid.NewGuid(),
            Upn = "test@test.com",
            DisplayName = "Test",
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var et = await _db.EntityTypes.FirstAsync();

        _db.WorkflowTemplates.Add(new Web.Data.Entities.WorkflowTemplate
        {
            EntityTypeId = et.EntityTypeId,
            Version = 1,
            IsCurrent = true,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = user.UserId
        });
        await _db.SaveChangesAsync();

        // Attempting a second IsCurrent=1 row for the same EntityType should violate
        // the UX_WorkflowTemplate_Current filtered unique index
        _db.WorkflowTemplates.Add(new Web.Data.Entities.WorkflowTemplate
        {
            EntityTypeId = et.EntityTypeId,
            Version = 2,
            IsCurrent = true,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = user.UserId
        });

        var act = async () => await _db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            because: "filtered unique index UX_WorkflowTemplate_Current prevents two current rows per entity type");
    }

    [Fact]
    public async Task User_EntraObjectId_UniqueConstraint_Enforced()
    {
        var oid = Guid.NewGuid();

        _db.Users.Add(new Web.Data.Entities.User
        {
            EntraObjectId = oid, Upn = "a@test.com", DisplayName = "A", CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _db.Users.Add(new Web.Data.Entities.User
        {
            EntraObjectId = oid, Upn = "b@test.com", DisplayName = "B", CreatedAtUtc = DateTime.UtcNow
        });

        var act = async () => await _db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            because: "EntraObjectId must be unique across User rows");
    }
}
