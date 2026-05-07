using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;
using CloseManager.Web.Data.Services;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace CloseManager.Tests.Phase5;

/// <summary>
/// Phase 5 integration tests: period lifecycle stored procedures via real SQL Server.
/// Requires Docker. Run with: dotnet test --filter Phase5
///
/// Note: The SPs must be created on the test database before running.
/// After EF migrations, run CloseManager.Web/Data/StoredProcedures/PeriodSps.sql.
/// </summary>
public class PeriodLifecycleTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private AppDbContext _db = null!;
    private string _connString = string.Empty;

    // Seed data IDs
    private User _actor = null!;
    private EntityType _entityType = null!;
    private Entity _entity1 = null!;
    private Entity _entity2 = null!;
    private Role _role = null!;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        _connString = _sql.GetConnectionString();

        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connString).Options;
        _db = new AppDbContext(opts);
        await _db.Database.MigrateAsync();

        // Create SPs
        await RunSqlFileAsync();

        // Seed reference data
        _actor = new User { EntraObjectId = Guid.NewGuid(), Upn = "a@t.com",
                            DisplayName = "Actor", CreatedAtUtc = DateTime.UtcNow };
        _db.Users.Add(_actor);

        _entityType = new EntityType { Code = "RETYPE", Name = "Real estate",
                                        IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.EntityTypes.Add(_entityType);

        _role = new Role { Code = "SENR", Name = "Senior",
                           IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(_role);
        await _db.SaveChangesAsync();

        _entity1 = new Entity { EntityTypeId = _entityType.EntityTypeId, Code = "E1",
                                 Name = "Entity One", IsActive = true,
                                 CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId };
        _entity2 = new Entity { EntityTypeId = _entityType.EntityTypeId, Code = "E2",
                                 Name = "Entity Two", IsActive = true,
                                 CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId };
        _db.Entities.AddRange(_entity1, _entity2);
        await _db.SaveChangesAsync();

        // Seed a minimal workflow template so sp_OpenPeriod can materialise workstreams
        await SeedTemplateAsync();
    }

    private async Task SeedTemplateAsync()
    {
        var tmpl = new WorkflowTemplate
        {
            EntityTypeId = _entityType.EntityTypeId, Version = 1, IsCurrent = true,
            Notes = "Test", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId
        };
        _db.WorkflowTemplates.Add(tmpl);
        await _db.SaveChangesAsync();

        var def = new WorkstreamDef
        {
            WorkflowTemplateId = tmpl.WorkflowTemplateId,
            Code = "CASH", Name = "Cash review", OrderIndex = 0
        };
        _db.WorkstreamDefs.Add(def);
        await _db.SaveChangesAsync();

        var stage = new WorkstreamDefStage
        {
            WorkstreamDefId = def.WorkstreamDefId, OrderIndex = 1,
            RoleId = _role.RoleId, StageKind = "Review",
            IsFinalApproval = true, StuckThresholdHours = 24
        };
        _db.WorkstreamDefStages.Add(stage);
        await _db.SaveChangesAsync();

        _db.WorkstreamDefChecklistItems.Add(new WorkstreamDefChecklistItem
        {
            WorkstreamDefStageId = stage.WorkstreamDefStageId, OrderIndex = 0,
            Text = "Verify balance", IsRequired = true
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    // ── sp_OpenPeriod ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenPeriod_CreatesClosePeriod_Workstream_Stage_ChecklistItems()
    {
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity1.EntityId, Period = "202510",
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });

        // ClosePeriod row
        var cp = await _db.ClosePeriods
            .SingleAsync(c => c.EntityId == _entity1.EntityId && c.Period == "202510");
        cp.ClosedAtUtc.Should().BeNull("period should be open");

        // Workstream materialised
        var ws = await _db.Workstreams
            .SingleAsync(w => w.ClosePeriodId == cp.ClosePeriodId);
        ws.Code.Should().Be("CASH");
        ws.Status.Should().Be("NotStarted");
        ws.CurrentStageIndex.Should().Be(0);

        // WorkstreamStage (both stage 0 implicit and stage 1)
        var stages = await _db.WorkstreamStages
            .Where(s => s.WorkstreamId == ws.WorkstreamId).ToListAsync();
        stages.Should().HaveCount(1, "one explicit stage (preparer is implicit)");
        stages[0].IsFinalApproval.Should().BeTrue();

        // ChecklistItem cloned
        var items = await _db.ChecklistItems
            .Where(i => i.WorkstreamId == ws.WorkstreamId).ToListAsync();
        items.Should().HaveCount(1, "one checklist item from template");
        items[0].Text.Should().Be("Verify balance");

        // Audit event
        var evt = await _db.AuditEvents
            .FirstAsync(e => e.Action == "WorkstreamInstantiated" && e.EntityId == _entity1.EntityId);
        evt.Period.Should().Be("202510");
    }

    [Fact]
    public async Task OpenPeriod_IsIdempotent_DoesNotDuplicateOnSecondCall()
    {
        var period = "202511";
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity1.EntityId, Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });
        // Call again — should silently skip
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity1.EntityId, Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });

        var cpCount = await _db.ClosePeriods
            .CountAsync(c => c.EntityId == _entity1.EntityId && c.Period == period);
        cpCount.Should().Be(1, "idempotent: second call is a no-op");
    }

    [Fact]
    public async Task OpenPeriod_TwoEntities_BothMaterialise_Independently()
    {
        var period = "202512";
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity1.EntityId, Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity2.EntityId, Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });

        var cpRows = await _db.ClosePeriods
            .Where(c => c.Period == period).ToListAsync();
        cpRows.Should().HaveCount(2, "one ClosePeriod row per entity");

        var wsRows = await _db.Workstreams
            .Where(w => w.Period == period).ToListAsync();
        wsRows.Should().HaveCount(2, "one Workstream per entity per workstreamDef");
    }

    // ── sp_ClosePeriod ────────────────────────────────────────────────────────

    [Fact]
    public async Task ClosePeriod_StampsClosedAtUtc_OnAllEntityRows()
    {
        var period = "202601";
        await OpenBothEntitiesAsync(period);

        await ExecSpAsync("sp_ClosePeriod", new
        {
            Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId,
            Reason = "Test close"
        });

        var rows = await _db.ClosePeriods.Where(c => c.Period == period).ToListAsync();
        rows.Should().AllSatisfy(r => r.ClosedAtUtc.Should().NotBeNull());
    }

    [Fact]
    public async Task ClosePeriod_WritesOneAuditEventPerEntity()
    {
        var period = "202602";
        await OpenBothEntitiesAsync(period);

        await ExecSpAsync("sp_ClosePeriod", new
        {
            Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId,
            Reason = "Month end"
        });

        var events = await _db.AuditEvents
            .Where(e => e.Period == period && e.Action == "PeriodClosed").ToListAsync();
        events.Should().HaveCount(2, "one PeriodClosed event per entity");
        events.Should().AllSatisfy(e => e.Notes.Should().Be("Month end"));
    }

    // ── sp_AssertPeriodOpen ───────────────────────────────────────────────────

    [Fact]
    public async Task AssertPeriodOpen_ThrowsError50050_WhenPeriodClosed()
    {
        var period = "202603";
        await OpenBothEntitiesAsync(period);
        await ExecSpAsync("sp_ClosePeriod", new
        {
            Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId,
            Reason = "Closing for test"
        });

        var ws = await _db.Workstreams.FirstAsync(w => w.Period == period);

        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_AssertPeriodOpen",
            new { WorkstreamId = ws.WorkstreamId },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50050,
                "sp_AssertPeriodOpen throws error 50050 when period is closed");
    }

    // ── sp_ReopenPeriod ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReopenPeriod_ClearsClosedAtUtc_AndWritesAuditEvents()
    {
        var period = "202604";
        await OpenBothEntitiesAsync(period);
        await ExecSpAsync("sp_ClosePeriod", new
        {
            Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId,
            Reason = "Close for reopen test"
        });

        await ExecSpAsync("sp_ReopenPeriod", new
        {
            Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId,
            Reason = "Reopen for additional work"
        });

        var rows = await _db.ClosePeriods.Where(c => c.Period == period).ToListAsync();
        rows.Should().AllSatisfy(r => r.ClosedAtUtc.Should().BeNull("should be null after reopen"));

        var reopenEvents = await _db.AuditEvents
            .Where(e => e.Period == period && e.Action == "PeriodReopened").ToListAsync();
        reopenEvents.Should().HaveCount(2);
        reopenEvents.Should().AllSatisfy(e => e.Notes.Should().Be("Reopen for additional work"));

        // AssertPeriodOpen should no longer throw
        var ws = await _db.Workstreams.FirstAsync(w => w.Period == period);
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_AssertPeriodOpen",
            new { WorkstreamId = ws.WorkstreamId },
            commandType: System.Data.CommandType.StoredProcedure);
        await act.Should().NotThrowAsync("period is now open again");
    }

    // ── sp_CloseEntityInPeriod ────────────────────────────────────────────────

    [Fact]
    public async Task CloseEntityInPeriod_OnlyClosesTargetEntity()
    {
        var period = "202605";
        await OpenBothEntitiesAsync(period);

        var cp1 = await _db.ClosePeriods
            .SingleAsync(c => c.EntityId == _entity1.EntityId && c.Period == period);

        await ExecSpAsync("sp_CloseEntityInPeriod", new
        {
            ClosePeriodId = cp1.ClosePeriodId,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId,
            Reason = "Early close"
        });

        var reloaded1 = await _db.ClosePeriods.SingleAsync(c => c.ClosePeriodId == cp1.ClosePeriodId);
        var cp2 = await _db.ClosePeriods
            .SingleAsync(c => c.EntityId == _entity2.EntityId && c.Period == period);

        reloaded1.ClosedAtUtc.Should().NotBeNull("entity 1 was closed early");
        cp2.ClosedAtUtc.Should().BeNull("entity 2 remains open");
    }

    // ── sp_ExpireLocks ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpireLocks_ClearsExpiredLocks_AndWritesAuditEvent()
    {
        var period = "202606";
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity1.EntityId, Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });

        var ws = await _db.Workstreams.FirstAsync(w => w.Period == period);

        // Manually set an expired lock
        ws.LockedByUserId = _actor.UserId;
        ws.LockedAtUtc = DateTime.UtcNow.AddMinutes(-20);
        ws.LockExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5); // expired
        await _db.SaveChangesAsync();

        await ExecSpAsync("sp_ExpireLocks", new { });

        var reloaded = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == ws.WorkstreamId);
        reloaded.LockedByUserId.Should().BeNull("lock should be cleared");
        reloaded.LockExpiresAtUtc.Should().BeNull();

        var expiredEvent = await _db.AuditEvents
            .FirstAsync(e => e.Action == "LockExpired" && e.WorkstreamId == ws.WorkstreamId);
        expiredEvent.Notes.Should().Contain(_actor.UserId.ToString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task OpenBothEntitiesAsync(string period)
    {
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity1.EntityId, Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = _entity2.EntityId, Period = period,
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });
    }

    private async Task ExecSpAsync(string spName, object param)
    {
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(spName, param,
            commandType: System.Data.CommandType.StoredProcedure,
            commandTimeout: 60);
    }

    /// <summary>
    /// Creates the period SPs on the test database from the SQL file.
    /// Splits on GO statements and executes each batch.
    /// </summary>
    private async Task RunSqlFileAsync()
    {
        var sqlPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "CloseManager.Web", "Data", "StoredProcedures", "PeriodSps.sql");

        if (!File.Exists(sqlPath))
        {
            // Try relative path for CI
            sqlPath = Path.Combine(
                AppContext.BaseDirectory, "Data", "StoredProcedures", "PeriodSps.sql");
        }

        if (!File.Exists(sqlPath))
            throw new FileNotFoundException($"PeriodSps.sql not found. Expected at: {sqlPath}");

        var sql = await File.ReadAllTextAsync(sqlPath);
        var batches = sql.Split("\nGO", StringSplitOptions.RemoveEmptyEntries);

        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                await conn.ExecuteAsync(trimmed, commandTimeout: 60);
        }
    }
}
