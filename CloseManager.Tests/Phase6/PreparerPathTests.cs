using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace CloseManager.Tests.Phase6;

/// <summary>
/// Phase 6 integration tests: preparer-path stored procedures.
/// Requires Docker. Run with: dotnet test --filter Phase6
/// Both PeriodSps.sql and PreparerSps.sql must be applied to the test DB.
/// </summary>
public class PreparerPathTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private AppDbContext _db = null!;
    private string _connString = string.Empty;

    private User _actor = null!;
    private long _workstreamId;
    private long _checklistItemId;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        _connString = _sql.GetConnectionString();

        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connString).Options;
        _db = new AppDbContext(opts);
        await _db.Database.MigrateAsync();
        await RunSpFilesAsync();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    // ── sp_AcquireLock ────────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireLock_Succeeds_WhenUserHasPreparerRoleAndNoLockExists()
    {
        var rows = await ExecSpRowsAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            LockMinutes = 15, ActorEntraOid = _actor.EntraObjectId
        });

        rows.Should().Be(1, "lock should be acquired");

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.LockedByUserId.Should().Be(_actor.UserId);
        ws.LockExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task AcquireLock_ReturnsZero_WhenAnotherUserHoldsActiveLock()
    {
        // Give actor the lock
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            LockMinutes = 15, ActorEntraOid = _actor.EntraObjectId
        });

        // Create a second user
        var other = new User { EntraObjectId = Guid.NewGuid(), Upn = "other@t.com",
                               DisplayName = "Other", CreatedAtUtc = DateTime.UtcNow };
        _db.Users.Add(other);
        await _db.SaveChangesAsync();

        // Other user tries to acquire — should fail
        var rows = await ExecSpRowsAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = other.UserId,
            LockMinutes = 15, ActorEntraOid = other.EntraObjectId
        });

        rows.Should().Be(0, "lock is held by another user");
    }

    // ── sp_SubmitWorkstream ───────────────────────────────────────────────────

    [Fact]
    public async Task SubmitWorkstream_WithPrimaryFile_AdvancesStage()
    {
        // Acquire lock
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            LockMinutes = 15, ActorEntraOid = _actor.EntraObjectId
        });

        // Seed a primary file directly (SharePoint mocked)
        _db.WorkstreamFiles.Add(new WorkstreamFile
        {
            WorkstreamId = _workstreamId, FileRole = "Primary",
            SpDriveId = "d1", SpItemId = "i1", SpWebUrl = "http://sp/file",
            SpRelativePath = "E1/202501/CASH/file.xlsx",
            FileName = "file.xlsx", UploadedAtUtc = DateTime.UtcNow,
            UploadedByUserId = _actor.UserId
        });
        await _db.SaveChangesAsync();

        // Submit
        await ExecSpAsync("sp_SubmitWorkstream", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            ActorEntraOid = _actor.EntraObjectId, Note = "Submitted for review"
        });

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.CurrentStageIndex.Should().Be(1, "should advance to stage 1");
        ws.Status.Should().Be("InProgress");
        ws.LockedByUserId.Should().BeNull("lock released on submit");

        var stage0 = await _db.WorkstreamStages
            .SingleAsync(s => s.WorkstreamId == _workstreamId && s.OrderIndex == 0);
        stage0.Outcome.Should().Be("Advanced");
        stage0.CompletedAtUtc.Should().NotBeNull();

        var evt = await _db.AuditEvents
            .FirstAsync(e => e.Action == "Submitted" && e.WorkstreamId == _workstreamId);
        evt.Notes.Should().Be("Submitted for review");
    }

    [Fact]
    public async Task SubmitWorkstream_WithoutPrimaryFile_Throws50010()
    {
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            LockMinutes = 15, ActorEntraOid = _actor.EntraObjectId
        });

        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_SubmitWorkstream",
            new { WorkstreamId = _workstreamId, UserId = _actor.UserId,
                  ActorEntraOid = _actor.EntraObjectId, Note = (string?)null },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50010, "no primary file → throw 50010");
    }

    // ── sp_ApproveChecklistItem ───────────────────────────────────────────────

    [Fact]
    public async Task ApproveChecklistItem_AtStage0_SetsPreparerStatusReady()
    {
        await ExecSpAsync("sp_ApproveChecklistItem", new
        {
            ChecklistItemId = _checklistItemId, UserId = _actor.UserId,
            ActorEntraOid = _actor.EntraObjectId
        });

        var item = await _db.ChecklistItems.SingleAsync(c => c.ChecklistItemId == _checklistItemId);
        item.PreparerStatus.Should().Be("Ready");
        item.PreparerMarkedByUserId.Should().Be(_actor.UserId);

        var evt = await _db.AuditEvents
            .FirstAsync(e => e.Action == "ChecklistItemMarkedReady"
                          && e.TargetId == _checklistItemId);
        evt.Should().NotBeNull();
    }

    // ── sp_FlagChecklistItemWithComment ───────────────────────────────────────

    [Fact]
    public async Task FlagChecklistItem_AtStage1_SetsNeedsRevision_AndCreatesComment()
    {
        // First submit to reach stage 1
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            LockMinutes = 15, ActorEntraOid = _actor.EntraObjectId
        });
        _db.WorkstreamFiles.Add(new WorkstreamFile
        {
            WorkstreamId = _workstreamId, FileRole = "Primary",
            SpDriveId = "d1", SpItemId = "i2", SpWebUrl = "http://sp/f2",
            SpRelativePath = "p", FileName = "f.xlsx",
            UploadedAtUtc = DateTime.UtcNow, UploadedByUserId = _actor.UserId
        });
        await _db.SaveChangesAsync();
        await ExecSpAsync("sp_SubmitWorkstream", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            ActorEntraOid = _actor.EntraObjectId, Note = (string?)null
        });

        // Re-acquire lock as stage 1 (same actor for simplicity — in real use it's the reviewer)
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _actor.UserId,
            LockMinutes = 15, ActorEntraOid = _actor.EntraObjectId
        });

        // Flag the stage-1 checklist item
        var stage1Item = await _db.ChecklistItems
            .Where(ci => ci.WorkstreamId == _workstreamId)
            .Join(_db.WorkstreamStages.Where(ws => ws.OrderIndex == 1),
                ci => ci.WorkstreamStageId, ws => ws.WorkstreamStageId, (ci, _) => ci)
            .FirstAsync();

        await ExecSpAsync("sp_FlagChecklistItemWithComment", new
        {
            ChecklistItemId = stage1Item.ChecklistItemId, UserId = _actor.UserId,
            ActorEntraOid = _actor.EntraObjectId,
            CommentBody = "Please verify the interest calculation"
        });

        var reloaded = await _db.ChecklistItems.SingleAsync(c => c.ChecklistItemId == stage1Item.ChecklistItemId);
        reloaded.ReviewerStatus.Should().Be("NeedsRevision");

        var comment = await _db.Comments
            .FirstAsync(c => c.ChecklistItemId == stage1Item.ChecklistItemId);
        comment.Body.Should().Be("Please verify the interest calculation");

        var evt = await _db.AuditEvents
            .FirstAsync(e => e.Action == "ChecklistItemFlagged" && e.WorkstreamId == _workstreamId);
        evt.Should().NotBeNull();
    }

    // ── Lock contention ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentLockAcquisition_ExactlyOneSucceeds()
    {
        // Open a second workstream for this test
        var ws2 = new Workstream
        {
            ClosePeriodId = _db.ClosePeriods.First().ClosePeriodId,
            Period = "202501", EntityId = _db.Entities.First().EntityId,
            WorkstreamDefId = _db.WorkstreamDefs.First().WorkstreamDefId,
            Code = "DEBT", Name = "Debt service", OrderIndex = 1,
            Status = "NotStarted", Round = 1, CurrentStageIndex = 0,
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId
        };
        _db.Workstreams.Add(ws2);

        // Add second user and role assignment
        var user2 = new User { EntraObjectId = Guid.NewGuid(), Upn = "u2@t.com",
                               DisplayName = "User 2", CreatedAtUtc = DateTime.UtcNow };
        _db.Users.Add(user2);
        await _db.SaveChangesAsync();

        var era2 = new EntityRoleAssignment
        {
            EntityId = ws2.EntityId, RoleId = _db.Roles.First().RoleId,
            UserId = user2.UserId,
            AssignedAtUtc = DateTime.UtcNow, AssignedByUserId = _actor.UserId
        };
        _db.EntityRoleAssignments.Add(era2);
        await _db.SaveChangesAsync();

        // Concurrent lock attempts
        var t1 = ExecSpRowsAsync("sp_AcquireLock", new
        {
            WorkstreamId = ws2.WorkstreamId, UserId = _actor.UserId,
            LockMinutes = 15, ActorEntraOid = _actor.EntraObjectId
        });
        var t2 = ExecSpRowsAsync("sp_AcquireLock", new
        {
            WorkstreamId = ws2.WorkstreamId, UserId = user2.UserId,
            LockMinutes = 15, ActorEntraOid = user2.EntraObjectId
        });

        var results = await Task.WhenAll(t1, t2);
        results.Sum().Should().Be(1, "exactly one lock acquisition should succeed");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        _actor = new User { EntraObjectId = Guid.NewGuid(), Upn = "a@t.com",
                            DisplayName = "Actor", CreatedAtUtc = DateTime.UtcNow };
        _db.Users.Add(_actor);

        var et = new EntityType { Code = "RE", Name = "Real estate",
                                   IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.EntityTypes.Add(et);

        var role = new Role { Code = "Preparer", Name = "Preparer",
                              IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        var entity = new Entity { EntityTypeId = et.EntityTypeId, Code = "E1",
                                   Name = "Entity One", IsActive = true,
                                   CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId };
        _db.Entities.Add(entity);
        await _db.SaveChangesAsync();

        var era = new EntityRoleAssignment
        {
            EntityId = entity.EntityId, RoleId = role.RoleId, UserId = _actor.UserId,
            AssignedAtUtc = DateTime.UtcNow, AssignedByUserId = _actor.UserId
        };
        _db.EntityRoleAssignments.Add(era);

        var tmpl = new WorkflowTemplate { EntityTypeId = et.EntityTypeId, Version = 1,
            IsCurrent = true, Notes = "T", CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = _actor.UserId };
        _db.WorkflowTemplates.Add(tmpl);
        await _db.SaveChangesAsync();

        var def = new WorkstreamDef { WorkflowTemplateId = tmpl.WorkflowTemplateId,
            Code = "CASH", Name = "Cash", OrderIndex = 0 };
        _db.WorkstreamDefs.Add(def);
        await _db.SaveChangesAsync();

        var stage0 = new WorkstreamDefStage { WorkstreamDefId = def.WorkstreamDefId,
            OrderIndex = 0, RoleId = role.RoleId, StageKind = "Prepare",
            IsFinalApproval = false, StuckThresholdHours = 24 };
        var stage1 = new WorkstreamDefStage { WorkstreamDefId = def.WorkstreamDefId,
            OrderIndex = 1, RoleId = role.RoleId, StageKind = "Review",
            IsFinalApproval = true, StuckThresholdHours = 24 };
        _db.WorkstreamDefStages.AddRange(stage0, stage1);
        await _db.SaveChangesAsync();

        var defItem = new WorkstreamDefChecklistItem { WorkstreamDefStageId = stage1.WorkstreamDefStageId,
            OrderIndex = 0, Text = "Verify balance", IsRequired = true };
        _db.WorkstreamDefChecklistItems.Add(defItem);
        await _db.SaveChangesAsync();

        // Open period
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = entity.EntityId, Period = "202501",
            ActorUserId = _actor.UserId, ActorEntraOid = _actor.EntraObjectId
        });

        _workstreamId = await _db.Workstreams
            .Where(w => w.Period == "202501").Select(w => w.WorkstreamId).FirstAsync();

        _checklistItemId = await _db.ChecklistItems
            .Where(ci => ci.WorkstreamId == _workstreamId)
            .Join(_db.WorkstreamStages.Where(ws => ws.OrderIndex == 0),
                ci => ci.WorkstreamStageId, ws => ws.WorkstreamStageId, (ci, _) => ci.ChecklistItemId)
            .FirstOrDefaultAsync();

        // If no stage-0 checklist item, use any item
        if (_checklistItemId == 0)
            _checklistItemId = await _db.ChecklistItems
                .Where(ci => ci.WorkstreamId == _workstreamId)
                .Select(ci => ci.ChecklistItemId).FirstAsync();
    }

    private async Task ExecSpAsync(string sp, object param)
    {
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sp, param,
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30);
    }

    private async Task<int> ExecSpRowsAsync(string sp, object param)
    {
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        return await conn.ExecuteAsync(sp, param,
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30);
    }

    private async Task RunSpFilesAsync()
    {
        foreach (var file in new[] { "PeriodSps.sql", "PreparerSps.sql" })
        {
            var path = FindSpFile(file);
            var sql = await File.ReadAllTextAsync(path);
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync();
            foreach (var batch in sql.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
            {
                var t = batch.Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    await conn.ExecuteAsync(t, commandTimeout: 60);
            }
        }
    }

    private static string FindSpFile(string filename)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "CloseManager.Web", "Data", "StoredProcedures", filename),
            Path.Combine(AppContext.BaseDirectory, "Data", "StoredProcedures", filename)
        };
        return candidates.First(File.Exists);
    }
}
