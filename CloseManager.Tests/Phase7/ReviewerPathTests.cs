using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace CloseManager.Tests.Phase7;

/// <summary>
/// Phase 7 integration tests: reviewer-path stored procedures.
/// Requires Docker. Run with: dotnet test --filter Phase7
/// PeriodSps.sql, PreparerSps.sql, and ReviewerSps.sql must all be applied.
/// </summary>
public class ReviewerPathTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private AppDbContext _db = null!;
    private string _connString = string.Empty;

    // Seeded actors
    private User _preparer = null!;
    private User _reviewer1 = null!;
    private User _reviewer2 = null!;

    // Seeded roles
    private Role _preparerRole = null!;
    private Role _reviewerRole1 = null!;
    private Role _reviewerRole2 = null!;

    private long _workstreamId;

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

    // ══════════════════════════════════════════════════════════════════════════
    // sp_AdvanceStage
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AdvanceStage_WhenAllItemsApproved_AdvancesToNextStage()
    {
        await SubmitFromPreparerAsync();

        // Reviewer 1 acquires lock
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });

        // Approve all stage-1 items
        await ApproveAllCurrentStageItemsAsync(_reviewer1);

        // Advance
        await ExecSpAsync("sp_AdvanceStage", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            ActorEntraOid = _reviewer1.EntraObjectId
        });

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.CurrentStageIndex.Should().Be(2, "should be at stage 2 after advance");
        ws.Status.Should().Be("InProgress");
        ws.LockedByUserId.Should().BeNull("lock released on advance");

        var stage1 = await _db.WorkstreamStages
            .SingleAsync(s => s.WorkstreamId == _workstreamId && s.OrderIndex == 1);
        stage1.Outcome.Should().Be("Advanced");
        stage1.CompletedAtUtc.Should().NotBeNull();
        stage1.CompletedByUserId.Should().Be(_reviewer1.UserId);

        var stage2 = await _db.WorkstreamStages
            .SingleAsync(s => s.WorkstreamId == _workstreamId && s.OrderIndex == 2);
        stage2.EnteredAtUtc.Should().NotBeNull("stage 2 entered");

        var evt = await _db.AuditEvents
            .FirstAsync(e => e.Action == "StageAdvanced" && e.WorkstreamId == _workstreamId);
        evt.Notes.Should().Contain("\"fromStage\":1");
        evt.Notes.Should().Contain("\"toStage\":2");
    }

    [Fact]
    public async Task AdvanceStage_WithUnresolvedItems_Throws50044()
    {
        await SubmitFromPreparerAsync();

        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });

        // Do NOT approve items — just try to advance
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_AdvanceStage",
            new { WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
                  ActorEntraOid = _reviewer1.EntraObjectId },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50044, "unresolved items → throw 50044");

        // Status should be unchanged
        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.CurrentStageIndex.Should().Be(1, "should still be at stage 1");
    }

    [Fact]
    public async Task AdvanceStage_OnFinalStage_Throws50043()
    {
        // Advance to the final stage
        await SubmitFromPreparerAsync();
        await AdvanceToStageAsync(_reviewer1, 2);

        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer2.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer2);

        // Try sp_AdvanceStage on the final stage (should use sp_ApproveFinal instead)
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_AdvanceStage",
            new { WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
                  ActorEntraOid = _reviewer2.EntraObjectId },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50043, "final stage → throw 50043, use sp_ApproveFinal");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // sp_ApproveFinal
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApproveFinal_OnFinalStage_SetsStatusApproved()
    {
        await SubmitFromPreparerAsync();
        await AdvanceToStageAsync(_reviewer1, 2);

        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer2.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer2);

        await ExecSpAsync("sp_ApproveFinal", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            ActorEntraOid = _reviewer2.EntraObjectId
        });

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.Status.Should().Be("Approved");
        ws.ApprovedAtUtc.Should().NotBeNull();
        ws.ApprovedByUserId.Should().Be(_reviewer2.UserId);
        ws.LockedByUserId.Should().BeNull("lock released on final approval");

        var evt = await _db.AuditEvents
            .FirstAsync(e => e.Action == "FinalApproved" && e.WorkstreamId == _workstreamId);
        evt.Should().NotBeNull();
    }

    [Fact]
    public async Task ApproveFinal_OnNonFinalStage_Throws50062()
    {
        await SubmitFromPreparerAsync();

        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer1);

        // Stage 1 is NOT the final stage — sp_ApproveFinal must throw
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_ApproveFinal",
            new { WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
                  ActorEntraOid = _reviewer1.EntraObjectId },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50062, "non-final stage → throw 50062");
    }

    [Fact]
    public async Task ApproveFinal_WithUnresolvedItems_Throws50063()
    {
        await SubmitFromPreparerAsync();
        await AdvanceToStageAsync(_reviewer1, 2);

        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer2.EntraObjectId
        });

        // Do NOT approve items
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_ApproveFinal",
            new { WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
                  ActorEntraOid = _reviewer2.EntraObjectId },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50063, "unresolved items → throw 50063");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // sp_SendBackToStage
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SendBackToStage_DecrementsStageAndIncrementsRound()
    {
        await SubmitFromPreparerAsync();

        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });

        await ExecSpAsync("sp_SendBackToStage", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            ActorEntraOid = _reviewer1.EntraObjectId,
            Reason = "Cash balance doesn't reconcile"
        });

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.CurrentStageIndex.Should().Be(0, "sent back to stage 0");
        ws.Round.Should().Be(2, "round incremented");
        ws.Status.Should().Be("NeedsRevision");
        ws.LockedByUserId.Should().BeNull("lock released");

        // Stage 1 stamped SentBack
        var stage1 = await _db.WorkstreamStages
            .SingleAsync(s => s.WorkstreamId == _workstreamId && s.OrderIndex == 1);
        stage1.Outcome.Should().Be("SentBack");

        // Stage 0 outcome cleared for clean re-entry
        var stage0 = await _db.WorkstreamStages
            .SingleAsync(s => s.WorkstreamId == _workstreamId && s.OrderIndex == 0);
        stage0.Outcome.Should().BeNull("prior stage outcome cleared for re-entry");
        stage0.CompletedAtUtc.Should().BeNull();

        var evt = await _db.AuditEvents
            .FirstAsync(e => e.Action == "SentBack" && e.WorkstreamId == _workstreamId);
        evt.Notes.Should().Contain("Cash balance doesn't reconcile");
        evt.Notes.Should().Contain("\"fromStage\":1");
        evt.Notes.Should().Contain("\"toStage\":0");
    }

    [Fact]
    public async Task SendBackToStage_AtStage0_Throws50052()
    {
        // Workstream is at stage 0 (NotStarted/InProgress) — cannot send back from there
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _preparer.UserId,
            LockMinutes = 15, ActorEntraOid = _preparer.EntraObjectId
        });

        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_SendBackToStage",
            new { WorkstreamId = _workstreamId, UserId = _preparer.UserId,
                  ActorEntraOid = _preparer.EntraObjectId, Reason = "test" },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50052, "stage 0 → throw 50052");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Full 3-stage happy path
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullThreeStageHappyPath_ReachesApprovedStatus()
    {
        // Stage 0: preparer submits
        await SubmitFromPreparerAsync();

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.CurrentStageIndex.Should().Be(1);

        // Stage 1: reviewer 1 approves all items, advances
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer1);
        await ExecSpAsync("sp_AdvanceStage", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            ActorEntraOid = _reviewer1.EntraObjectId
        });

        await _db.Entry(ws).ReloadAsync();
        ws.CurrentStageIndex.Should().Be(2);
        ws.Status.Should().Be("InProgress");

        // Stage 2: reviewer 2 approves all items, finalizes
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer2.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer2);
        await ExecSpAsync("sp_ApproveFinal", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            ActorEntraOid = _reviewer2.EntraObjectId
        });

        await _db.Entry(ws).ReloadAsync();
        ws.Status.Should().Be("Approved");
        ws.ApprovedAtUtc.Should().NotBeNull();
        ws.ApprovedByUserId.Should().Be(_reviewer2.UserId);
        ws.LockedByUserId.Should().BeNull();

        // Assert audit trail has the full sequence
        var events = await _db.AuditEvents
            .Where(e => e.WorkstreamId == _workstreamId)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => e.Action)
            .ToListAsync();

        events.Should().Contain("Submitted");
        events.Should().Contain("StageAdvanced");
        events.Should().Contain("FinalApproved");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Send-back and re-advance cycle
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SendBackThenReAdvance_RoundIncrementsAndStatusReturnsInProgress()
    {
        await SubmitFromPreparerAsync();

        // Reviewer 1 sends back
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });
        await ExecSpAsync("sp_SendBackToStage", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            ActorEntraOid = _reviewer1.EntraObjectId, Reason = "Needs correction"
        });

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        ws.CurrentStageIndex.Should().Be(0);
        ws.Round.Should().Be(2);
        ws.Status.Should().Be("NeedsRevision");

        // Preparer re-acquires and re-submits
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _preparer.UserId,
            LockMinutes = 15, ActorEntraOid = _preparer.EntraObjectId
        });
        await ExecSpAsync("sp_SubmitWorkstream", new
        {
            WorkstreamId = _workstreamId, UserId = _preparer.UserId,
            ActorEntraOid = _preparer.EntraObjectId, Note = "Corrected"
        });

        await _db.Entry(ws).ReloadAsync();
        ws.CurrentStageIndex.Should().Be(1, "back at stage 1 after re-submit");
        ws.Status.Should().Be("InProgress");
        ws.Round.Should().Be(2, "round unchanged by re-submit — only send-back increments it");

        // Reviewer 1 advances again, reviewer 2 finalizes
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer1);
        await ExecSpAsync("sp_AdvanceStage", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            ActorEntraOid = _reviewer1.EntraObjectId
        });

        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer2.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer2);
        await ExecSpAsync("sp_ApproveFinal", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            ActorEntraOid = _reviewer2.EntraObjectId
        });

        await _db.Entry(ws).ReloadAsync();
        ws.Status.Should().Be("Approved");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // sp_AssertPeriodOpen guards all reviewer SPs
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AdvanceStage_OnClosedPeriod_Throws50050()
    {
        await SubmitFromPreparerAsync();
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer1);

        // Close the period
        await ExecSpAsync("sp_ClosePeriod", new
        {
            Period = "202501", ActorUserId = _preparer.UserId,
            ActorEntraOid = _preparer.EntraObjectId, Reason = "Closing for test"
        });

        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_AdvanceStage",
            new { WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
                  ActorEntraOid = _reviewer1.EntraObjectId },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50050, "closed period → sp_AssertPeriodOpen throws 50050");
    }

    [Fact]
    public async Task ApproveFinal_OnClosedPeriod_Throws50050()
    {
        await SubmitFromPreparerAsync();
        await AdvanceToStageAsync(_reviewer1, 2);
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer2.EntraObjectId
        });
        await ApproveAllCurrentStageItemsAsync(_reviewer2);

        await ExecSpAsync("sp_ClosePeriod", new
        {
            Period = "202501", ActorUserId = _preparer.UserId,
            ActorEntraOid = _preparer.EntraObjectId, Reason = "Closing"
        });

        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_ApproveFinal",
            new { WorkstreamId = _workstreamId, UserId = _reviewer2.UserId,
                  ActorEntraOid = _reviewer2.EntraObjectId },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50050);
    }

    [Fact]
    public async Task SendBackToStage_OnClosedPeriod_Throws50050()
    {
        await SubmitFromPreparerAsync();
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
            LockMinutes = 15, ActorEntraOid = _reviewer1.EntraObjectId
        });

        await ExecSpAsync("sp_ClosePeriod", new
        {
            Period = "202501", ActorUserId = _preparer.UserId,
            ActorEntraOid = _preparer.EntraObjectId, Reason = "Closing"
        });

        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        var act = async () => await conn.ExecuteAsync("sp_SendBackToStage",
            new { WorkstreamId = _workstreamId, UserId = _reviewer1.UserId,
                  ActorEntraOid = _reviewer1.EntraObjectId, Reason = "test" },
            commandType: System.Data.CommandType.StoredProcedure);

        await act.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Number == 50050);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Preparer acquires lock, uploads a primary file (mock), and submits → stage 1.
    /// </summary>
    private async Task SubmitFromPreparerAsync()
    {
        await ExecSpAsync("sp_AcquireLock", new
        {
            WorkstreamId = _workstreamId, UserId = _preparer.UserId,
            LockMinutes = 15, ActorEntraOid = _preparer.EntraObjectId
        });

        _db.WorkstreamFiles.Add(new WorkstreamFile
        {
            WorkstreamId = _workstreamId, FileRole = "Primary",
            SpDriveId = "d1", SpItemId = Guid.NewGuid().ToString(), SpWebUrl = "http://sp/file",
            SpRelativePath = "E1/202501/CASH/file.xlsx",
            FileName = "cash.xlsx", UploadedAtUtc = DateTime.UtcNow,
            UploadedByUserId = _preparer.UserId
        });
        await _db.SaveChangesAsync();

        await ExecSpAsync("sp_SubmitWorkstream", new
        {
            WorkstreamId = _workstreamId, UserId = _preparer.UserId,
            ActorEntraOid = _preparer.EntraObjectId, Note = (string?)null
        });
    }

    /// <summary>
    /// Approve all checklist items for the current stage as the given user.
    /// </summary>
    private async Task ApproveAllCurrentStageItemsAsync(User actor)
    {
        await _db.Entry(await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId))
                 .ReloadAsync();

        var ws = await _db.Workstreams.SingleAsync(w => w.WorkstreamId == _workstreamId);
        var stageId = await _db.WorkstreamStages
            .Where(s => s.WorkstreamId == _workstreamId && s.OrderIndex == ws.CurrentStageIndex)
            .Select(s => s.WorkstreamStageId)
            .SingleAsync();

        var items = await _db.ChecklistItems
            .Where(ci => ci.WorkstreamId == _workstreamId
                      && ci.WorkstreamStageId == stageId
                      && ci.ReviewerStatus != "Approved"
                      && !ci.IsDeleted)
            .Select(ci => ci.ChecklistItemId)
            .ToListAsync();

        foreach (var id in items)
        {
            await ExecSpAsync("sp_ApproveChecklistItem", new
            {
                ChecklistItemId = id, UserId = actor.UserId,
                ActorEntraOid = actor.EntraObjectId
            });
        }
    }

    /// <summary>
    /// Drive workstream from current stage to targetStage by acquiring lock,
    /// approving all items, and advancing. Uses reviewer1 for stage 1, reviewer2 for stage 2.
    /// </summary>
    private async Task AdvanceToStageAsync(User actor, int targetStage)
    {
        while (true)
        {
            var ws = await _db.Workstreams.AsNoTracking()
                .SingleAsync(w => w.WorkstreamId == _workstreamId);
            if (ws.CurrentStageIndex >= targetStage) break;

            var stageReviewer = ws.CurrentStageIndex == 1 ? _reviewer1 : _reviewer2;
            await ExecSpAsync("sp_AcquireLock", new
            {
                WorkstreamId = _workstreamId, UserId = stageReviewer.UserId,
                LockMinutes = 15, ActorEntraOid = stageReviewer.EntraObjectId
            });
            await ApproveAllCurrentStageItemsAsync(stageReviewer);
            await ExecSpAsync("sp_AdvanceStage", new
            {
                WorkstreamId = _workstreamId, UserId = stageReviewer.UserId,
                ActorEntraOid = stageReviewer.EntraObjectId
            });
        }
    }

    private async Task SeedAsync()
    {
        // ── Users ────────────────────────────────────────────────────────────
        _preparer  = new User { EntraObjectId = Guid.NewGuid(), Upn = "prep@t.com",
                                DisplayName = "Preparer",  CreatedAtUtc = DateTime.UtcNow };
        _reviewer1 = new User { EntraObjectId = Guid.NewGuid(), Upn = "rev1@t.com",
                                DisplayName = "Reviewer 1", CreatedAtUtc = DateTime.UtcNow };
        _reviewer2 = new User { EntraObjectId = Guid.NewGuid(), Upn = "rev2@t.com",
                                DisplayName = "Reviewer 2", CreatedAtUtc = DateTime.UtcNow };
        _db.Users.AddRange(_preparer, _reviewer1, _reviewer2);

        // ── Entity type + roles ──────────────────────────────────────────────
        var et = new EntityType { Code = "RE", Name = "Real estate",
                                   IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.EntityTypes.Add(et);

        _preparerRole  = new Role { Code = "Preparer",  Name = "Preparer",
                                     IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _reviewerRole1 = new Role { Code = "Treasury",  Name = "Treasury",
                                     IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _reviewerRole2 = new Role { Code = "Senior",    Name = "Senior",
                                     IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.AddRange(_preparerRole, _reviewerRole1, _reviewerRole2);
        await _db.SaveChangesAsync();

        // ── Entity ───────────────────────────────────────────────────────────
        var entity = new Entity { EntityTypeId = et.EntityTypeId, Code = "E1",
                                   Name = "Entity One", IsActive = true,
                                   CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _preparer.UserId };
        _db.Entities.Add(entity);
        await _db.SaveChangesAsync();

        // ── Role assignments (all three users on the entity) ─────────────────
        _db.EntityRoleAssignments.AddRange(
            new EntityRoleAssignment { EntityId = entity.EntityId, RoleId = _preparerRole.RoleId,
                UserId = _preparer.UserId, AssignedAtUtc = DateTime.UtcNow,
                AssignedByUserId = _preparer.UserId },
            new EntityRoleAssignment { EntityId = entity.EntityId, RoleId = _reviewerRole1.RoleId,
                UserId = _reviewer1.UserId, AssignedAtUtc = DateTime.UtcNow,
                AssignedByUserId = _preparer.UserId },
            new EntityRoleAssignment { EntityId = entity.EntityId, RoleId = _reviewerRole2.RoleId,
                UserId = _reviewer2.UserId, AssignedAtUtc = DateTime.UtcNow,
                AssignedByUserId = _preparer.UserId }
        );

        // ── Template: 3-stage chain (Prepare → Treasury → Senior[final]) ─────
        var tmpl = new WorkflowTemplate { EntityTypeId = et.EntityTypeId, Version = 1,
            IsCurrent = true, Notes = "3-stage test template",
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _preparer.UserId };
        _db.WorkflowTemplates.Add(tmpl);
        await _db.SaveChangesAsync();

        var def = new WorkstreamDef { WorkflowTemplateId = tmpl.WorkflowTemplateId,
            Code = "CASH", Name = "Cash reconciliation", OrderIndex = 0 };
        _db.WorkstreamDefs.Add(def);
        await _db.SaveChangesAsync();

        var defStage0 = new WorkstreamDefStage { WorkstreamDefId = def.WorkstreamDefId,
            OrderIndex = 0, RoleId = _preparerRole.RoleId, StageKind = "Prepare",
            DisplayName = "Prepare", IsFinalApproval = false, StuckThresholdHours = 24 };
        var defStage1 = new WorkstreamDefStage { WorkstreamDefId = def.WorkstreamDefId,
            OrderIndex = 1, RoleId = _reviewerRole1.RoleId, StageKind = "Review",
            DisplayName = "Treasury review", IsFinalApproval = false, StuckThresholdHours = 48 };
        var defStage2 = new WorkstreamDefStage { WorkstreamDefId = def.WorkstreamDefId,
            OrderIndex = 2, RoleId = _reviewerRole2.RoleId, StageKind = "Review",
            DisplayName = "Senior review", IsFinalApproval = true, StuckThresholdHours = 72 };
        _db.WorkstreamDefStages.AddRange(defStage0, defStage1, defStage2);
        await _db.SaveChangesAsync();

        // Checklist items for each review stage
        _db.WorkstreamDefChecklistItems.AddRange(
            new WorkstreamDefChecklistItem { WorkstreamDefStageId = defStage1.WorkstreamDefStageId,
                OrderIndex = 0, Text = "Cash tie-out to bank statement", IsRequired = true },
            new WorkstreamDefChecklistItem { WorkstreamDefStageId = defStage1.WorkstreamDefStageId,
                OrderIndex = 1, Text = "Outstanding items < 30 days", IsRequired = true },
            new WorkstreamDefChecklistItem { WorkstreamDefStageId = defStage2.WorkstreamDefStageId,
                OrderIndex = 0, Text = "Overall reasonableness check", IsRequired = true },
            new WorkstreamDefChecklistItem { WorkstreamDefStageId = defStage2.WorkstreamDefStageId,
                OrderIndex = 1, Text = "Prior period variances explained", IsRequired = true }
        );
        await _db.SaveChangesAsync();

        // ── Open period → instantiates the workstream ────────────────────────
        await ExecSpAsync("sp_OpenPeriod", new
        {
            EntityId = entity.EntityId, Period = "202501",
            ActorUserId = _preparer.UserId, ActorEntraOid = _preparer.EntraObjectId
        });

        _workstreamId = await _db.Workstreams
            .Where(w => w.Period == "202501" && !w.IsDeleted)
            .Select(w => w.WorkstreamId)
            .FirstAsync();
    }

    private async Task ExecSpAsync(string sp, object param)
    {
        using var conn = new SqlConnection(_connString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sp, param,
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30);
    }

    private async Task RunSpFilesAsync()
    {
        foreach (var file in new[] { "PeriodSps.sql", "PreparerSps.sql", "ReviewerSps.sql" })
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
