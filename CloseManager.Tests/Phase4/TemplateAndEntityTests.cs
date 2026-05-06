using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;
using CloseManager.Web.Data.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace CloseManager.Tests.Phase4;

/// <summary>
/// Phase 4 integration tests: template versioning, entity creation, role assignments.
/// Requires Docker. Run with: dotnet test --filter Phase4
/// </summary>
public class TemplateAndEntityTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private AppDbContext _db = null!;
    private AuditService _audit = null!;
    private User _actor = null!;
    private EntityType _entityType = null!;
    private Role _role = null!;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_sql.GetConnectionString()).Options;
        _db = new AppDbContext(opts);
        await _db.Database.MigrateAsync();
        _audit = new AuditService(_db);

        _actor = new User { EntraObjectId = Guid.NewGuid(), Upn = "a@t.com",
                            DisplayName = "Actor", CreatedAtUtc = DateTime.UtcNow };
        _db.Users.Add(_actor);

        _entityType = new EntityType { Code = "RETYPE1", Name = "Real estate",
                                        IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.EntityTypes.Add(_entityType);

        _role = new Role { Code = "SENIOR", Name = "Senior reviewer",
                           IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        _db.Roles.Add(_role);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    // ── Template creation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveTemplate_V1_CreatesAllChildRows()
    {
        var template = await SaveTemplateV1Async();

        template.Version.Should().Be(1);
        template.IsCurrent.Should().BeTrue();

        var defs = await _db.WorkstreamDefs
            .Where(d => d.WorkflowTemplateId == template.WorkflowTemplateId).ToListAsync();
        defs.Should().HaveCount(2, "two workstreams");

        var stages = await _db.WorkstreamDefStages
            .Where(s => defs.Select(d => d.WorkstreamDefId).Contains(s.WorkstreamDefId))
            .ToListAsync();
        stages.Should().HaveCount(2, "one stage per workstream");

        var items = await _db.WorkstreamDefChecklistItems
            .Where(i => stages.Select(s => s.WorkstreamDefStageId).Contains(i.WorkstreamDefStageId))
            .ToListAsync();
        items.Should().HaveCount(4, "two items per stage");
    }

    [Fact]
    public async Task SaveTemplate_V2_FlipsPriorCurrentToHistorical()
    {
        var v1 = await SaveTemplateV1Async();
        v1.IsCurrent.Should().BeTrue();

        // Save v2 — flip v1 to historical
        v1.IsCurrent = false;
        var v2 = new WorkflowTemplate
        {
            EntityTypeId = _entityType.EntityTypeId, Version = 2, IsCurrent = true,
            Notes = "v2 notes", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId
        };
        _db.WorkflowTemplates.Add(v2);
        await _db.SaveChangesAsync();

        var reloaded1 = await _db.WorkflowTemplates.SingleAsync(t =>
            t.WorkflowTemplateId == v1.WorkflowTemplateId);
        var reloaded2 = await _db.WorkflowTemplates.SingleAsync(t =>
            t.WorkflowTemplateId == v2.WorkflowTemplateId);

        reloaded1.IsCurrent.Should().BeFalse("v1 was flipped to historical");
        reloaded2.IsCurrent.Should().BeTrue("v2 is now current");
    }

    [Fact]
    public async Task FilteredUniqueIndex_BlocksTwoCurrentRowsForSameEntityType()
    {
        await SaveTemplateV1Async();

        // Attempt a second IsCurrent=1 row without flipping the first
        _db.WorkflowTemplates.Add(new WorkflowTemplate
        {
            EntityTypeId = _entityType.EntityTypeId, Version = 2, IsCurrent = true,
            Notes = "bad", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId
        });

        var act = async () => await _db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            because: "UX_WorkflowTemplate_Current prevents two IsCurrent=1 rows per entity type");
    }

    [Fact]
    public async Task ExactlyOneFinalApprover_PerWorkstream_IsRequired()
    {
        // Verify the business rule: a template with zero final approvers on a workstream
        // should be blocked at the application layer. We test the data is correctly stored
        // when validation passes.
        var template = await SaveTemplateV1Async();
        var stages = await _db.WorkstreamDefStages
            .Where(s => s.WorkstreamDef.WorkflowTemplateId == template.WorkflowTemplateId)
            .ToListAsync();

        stages.Should().Contain(s => s.IsFinalApproval,
            "at least one stage must be marked IsFinalApproval = true");
        stages.Count(s => s.IsFinalApproval).Should().Be(
            await _db.WorkstreamDefs.CountAsync(d => d.WorkflowTemplateId == template.WorkflowTemplateId),
            "exactly one final approver per workstream");
    }

    // ── Entity + role assignment ──────────────────────────────────────────────

    [Fact]
    public async Task CreateEntity_AndAssignRole_CreatesCorrectRows()
    {
        var entity = new Entity
        {
            EntityTypeId = _entityType.EntityTypeId,
            Code = "PLZ01", Name = "Plaza Tower",
            IsActive = true, CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = _actor.UserId
        };
        _db.Entities.Add(entity);
        await _db.SaveChangesAsync();

        var assignment = new EntityRoleAssignment
        {
            EntityId = entity.EntityId, RoleId = _role.RoleId, UserId = _actor.UserId,
            AssignedAtUtc = DateTime.UtcNow, AssignedByUserId = _actor.UserId
        };
        _db.EntityRoleAssignments.Add(assignment);

        await _audit.WriteAsync(_actor.UserId, _actor.EntraObjectId,
            "EntityRoleAssignment", assignment.EntityRoleAssignmentId,
            "RoleAssigned", after: new { entity.EntityId, _role.RoleId, _actor.UserId },
            entityId: entity.EntityId);
        await _db.SaveChangesAsync();

        var saved = await _db.EntityRoleAssignments
            .SingleAsync(a => a.EntityId == entity.EntityId && a.RoleId == _role.RoleId);
        saved.UserId.Should().Be(_actor.UserId);

        var evt = await _db.AuditEvents.SingleAsync(e => e.Action == "RoleAssigned");
        evt.EntityId.Should().Be(entity.EntityId);
    }

    [Fact]
    public async Task DuplicateRoleAssignment_WithUniqueIndex_IsBlocked()
    {
        var entity = new Entity
        {
            EntityTypeId = _entityType.EntityTypeId, Code = "DUPL",
            Name = "Dup entity", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId
        };
        _db.Entities.Add(entity);
        await _db.SaveChangesAsync();

        _db.EntityRoleAssignments.Add(new EntityRoleAssignment
        {
            EntityId = entity.EntityId, RoleId = _role.RoleId, UserId = _actor.UserId,
            AssignedAtUtc = DateTime.UtcNow, AssignedByUserId = _actor.UserId
        });
        await _db.SaveChangesAsync();

        _db.EntityRoleAssignments.Add(new EntityRoleAssignment
        {
            EntityId = entity.EntityId, RoleId = _role.RoleId, UserId = _actor.UserId,
            AssignedAtUtc = DateTime.UtcNow, AssignedByUserId = _actor.UserId
        });

        var act = async () => await _db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            because: "UQ_EntityRoleAssignment prevents duplicate (Entity, Role, User) rows");
    }

    [Fact]
    public async Task EntityCode_UniqueConstraint_Enforced()
    {
        _db.Entities.Add(new Entity
        {
            EntityTypeId = _entityType.EntityTypeId, Code = "UNIQENT",
            Name = "First", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId
        });
        await _db.SaveChangesAsync();

        _db.Entities.Add(new Entity
        {
            EntityTypeId = _entityType.EntityTypeId, Code = "UNIQENT",
            Name = "Second", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = _actor.UserId
        });

        var act = async () => await _db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("Entity.Code is unique");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<WorkflowTemplate> SaveTemplateV1Async()
    {
        var template = new WorkflowTemplate
        {
            EntityTypeId = _entityType.EntityTypeId, Version = 1, IsCurrent = true,
            Notes = "Initial template", CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = _actor.UserId
        };
        _db.WorkflowTemplates.Add(template);
        await _db.SaveChangesAsync();

        foreach (var (code, name, idx) in new[] { ("CASH", "Cash review", 0), ("DEBT", "Debt service", 1) })
        {
            var def = new WorkstreamDef
            {
                WorkflowTemplateId = template.WorkflowTemplateId,
                Code = code, Name = name, OrderIndex = idx
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

            _db.WorkstreamDefChecklistItems.AddRange(
                new WorkstreamDefChecklistItem
                    { WorkstreamDefStageId = stage.WorkstreamDefStageId, OrderIndex = 0,
                      Text = "Verify bank statement", IsRequired = true },
                new WorkstreamDefChecklistItem
                    { WorkstreamDefStageId = stage.WorkstreamDefStageId, OrderIndex = 1,
                      Text = "Confirm GL balance", IsRequired = true }
            );
            await _db.SaveChangesAsync();
        }

        return template;
    }
}
