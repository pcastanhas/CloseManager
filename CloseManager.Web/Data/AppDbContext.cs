using CloseManager.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloseManager.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Entities.User> Users => Set<Entities.User>();
    public DbSet<Entities.EntityType> EntityTypes => Set<Entities.EntityType>();
    public DbSet<Entities.Entity> Entities => Set<Entities.Entity>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<EntityRoleAssignment> EntityRoleAssignments => Set<EntityRoleAssignment>();
    public DbSet<WorkflowTemplate> WorkflowTemplates => Set<WorkflowTemplate>();
    public DbSet<WorkstreamDef> WorkstreamDefs => Set<WorkstreamDef>();
    public DbSet<WorkstreamDefStage> WorkstreamDefStages => Set<WorkstreamDefStage>();
    public DbSet<WorkstreamDefChecklistItem> WorkstreamDefChecklistItems => Set<WorkstreamDefChecklistItem>();
    public DbSet<ClosePeriod> ClosePeriods => Set<ClosePeriod>();
    public DbSet<Workstream> Workstreams => Set<Workstream>();
    public DbSet<WorkstreamStage> WorkstreamStages => Set<WorkstreamStage>();
    public DbSet<WorkstreamFile> WorkstreamFiles => Set<WorkstreamFile>();
    public DbSet<ChecklistItem> ChecklistItems => Set<ChecklistItem>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CommentAttachment> CommentAttachments => Set<CommentAttachment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);

        m.Entity<Entities.User>(e =>
        {
            e.ToTable("User");
            e.HasKey(u => u.UserId);
            e.Property(u => u.UserId).UseIdentityColumn();
            e.Property(u => u.EntraObjectId).IsRequired();
            e.HasIndex(u => u.EntraObjectId).IsUnique();
            e.Property(u => u.Upn).HasMaxLength(256).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(u => u.RowVersion).IsRowVersion();
        });

        m.Entity<Entities.EntityType>(e =>
        {
            e.ToTable("EntityType");
            e.HasKey(x => x.EntityTypeId);
            e.Property(x => x.EntityTypeId).UseIdentityColumn();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        m.Entity<Entities.Entity>(e =>
        {
            e.ToTable("Entity");
            e.HasKey(x => x.EntityId);
            e.Property(x => x.EntityId).UseIdentityColumn();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.EntityType).WithMany(t => t.Entities).HasForeignKey(x => x.EntityTypeId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Role>(e =>
        {
            e.ToTable("Role");
            e.HasKey(x => x.RoleId);
            e.Property(x => x.RoleId).UseIdentityColumn();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        m.Entity<EntityRoleAssignment>(e =>
        {
            e.ToTable("EntityRoleAssignment");
            e.HasKey(x => x.EntityRoleAssignmentId);
            e.Property(x => x.EntityRoleAssignmentId).UseIdentityColumn();
            e.HasIndex(x => new { x.EntityId, x.RoleId, x.UserId }).IsUnique().HasFilter("[IsDeleted] = 0");
            e.HasIndex(x => new { x.UserId, x.IsDeleted });
            e.HasIndex(x => new { x.EntityId, x.RoleId, x.IsDeleted });
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Entity).WithMany(n => n.RoleAssignments).HasForeignKey(x => x.EntityId);
            e.HasOne(x => x.Role).WithMany(r => r.EntityRoleAssignments).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AssignedBy).WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<WorkflowTemplate>(e =>
        {
            e.ToTable("WorkflowTemplate");
            e.HasKey(x => x.WorkflowTemplateId);
            e.Property(x => x.WorkflowTemplateId).UseIdentityColumn();
            e.HasIndex(x => new { x.EntityTypeId, x.Version }).IsUnique();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.EntityType).WithMany(t => t.WorkflowTemplates).HasForeignKey(x => x.EntityTypeId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<WorkstreamDef>(e =>
        {
            e.ToTable("WorkstreamDef");
            e.HasKey(x => x.WorkstreamDefId);
            e.Property(x => x.WorkstreamDefId).UseIdentityColumn();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => new { x.WorkflowTemplateId, x.Code }).IsUnique();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.WorkflowTemplate).WithMany(t => t.WorkstreamDefs).HasForeignKey(x => x.WorkflowTemplateId);
        });

        m.Entity<WorkstreamDefStage>(e =>
        {
            e.ToTable("WorkstreamDefStage");
            e.HasKey(x => x.WorkstreamDefStageId);
            e.Property(x => x.WorkstreamDefStageId).UseIdentityColumn();
            e.Property(x => x.StageKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.HasIndex(x => new { x.WorkstreamDefId, x.OrderIndex }).IsUnique();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.WorkstreamDef).WithMany(d => d.Stages).HasForeignKey(x => x.WorkstreamDefId);
            e.HasOne(x => x.Role).WithMany(r => r.WorkstreamDefStages).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<WorkstreamDefChecklistItem>(e =>
        {
            e.ToTable("WorkstreamDefChecklistItem");
            e.HasKey(x => x.WorkstreamDefChecklistItemId);
            e.Property(x => x.WorkstreamDefChecklistItemId).UseIdentityColumn();
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();
            e.Property(x => x.Guidance).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Stage).WithMany(s => s.ChecklistItems).HasForeignKey(x => x.WorkstreamDefStageId);
        });

        m.Entity<ClosePeriod>(e =>
        {
            e.ToTable("ClosePeriod");
            e.HasKey(x => x.ClosePeriodId);
            e.Property(x => x.ClosePeriodId).UseIdentityColumn();
            e.Property(x => x.Period).HasMaxLength(6).IsFixedLength().IsRequired();
            e.HasIndex(x => new { x.EntityId, x.Period }).IsUnique();
            e.HasIndex(x => new { x.Period, x.IsDeleted });
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Entity).WithMany(n => n.ClosePeriods).HasForeignKey(x => x.EntityId);
            e.HasOne(x => x.OpenedBy).WithMany().HasForeignKey(x => x.OpenedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClosedBy).WithMany().HasForeignKey(x => x.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Workstream>(e =>
        {
            e.ToTable("Workstream");
            e.HasKey(x => x.WorkstreamId);
            e.Property(x => x.WorkstreamId).UseIdentityColumn();
            e.Property(x => x.Period).HasMaxLength(6).IsFixedLength().IsRequired();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.HasIndex(x => new { x.ClosePeriodId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.Status, x.Period, x.IsDeleted });
            e.HasIndex(x => x.LockExpiresAtUtc).HasFilter("[LockedByUserId] IS NOT NULL");
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.ClosePeriod).WithMany(p => p.Workstreams).HasForeignKey(x => x.ClosePeriodId);
            e.HasOne(x => x.WorkstreamDef).WithMany(d => d.Workstreams).HasForeignKey(x => x.WorkstreamDefId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LockedBy).WithMany().HasForeignKey(x => x.LockedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RebuiltFrom).WithMany().HasForeignKey(x => x.RebuiltFromWorkstreamId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<WorkstreamStage>(e =>
        {
            e.ToTable("WorkstreamStage");
            e.HasKey(x => x.WorkstreamStageId);
            e.Property(x => x.WorkstreamStageId).UseIdentityColumn();
            e.Property(x => x.StageKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.HasIndex(x => new { x.WorkstreamId, x.OrderIndex }).IsUnique();
            e.HasIndex(x => new { x.RoleId, x.IsDeleted }).IncludeProperties(x => new { x.WorkstreamId, x.OrderIndex });
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Workstream).WithMany(w => w.Stages).HasForeignKey(x => x.WorkstreamId);
            e.HasOne(x => x.SourceDefStage).WithMany().HasForeignKey(x => x.SourceDefStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StartedBy).WithMany().HasForeignKey(x => x.StartedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CompletedBy).WithMany().HasForeignKey(x => x.CompletedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<WorkstreamFile>(e =>
        {
            e.ToTable("WorkstreamFile");
            e.HasKey(x => x.WorkstreamFileId);
            e.Property(x => x.WorkstreamFileId).UseIdentityColumn();
            e.Property(x => x.FileRole).HasMaxLength(20).IsRequired();
            e.Property(x => x.SpDriveId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SpItemId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SpWebUrl).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SpRelativePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.FileExtension).HasMaxLength(20);
            e.Property(x => x.ContentHash).HasMaxLength(32);
            e.HasIndex(x => new { x.WorkstreamId, x.IsDeleted, x.FileRole });
            e.HasIndex(x => new { x.SpDriveId, x.SpItemId }).IsUnique().HasFilter("[IsDeleted] = 0");
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Workstream).WithMany(w => w.Files).HasForeignKey(x => x.WorkstreamId);
            e.HasOne(x => x.UploadedBy).WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReplacesFile).WithMany().HasForeignKey(x => x.ReplacesFileId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<ChecklistItem>(e =>
        {
            e.ToTable("ChecklistItem");
            e.HasKey(x => x.ChecklistItemId);
            e.Property(x => x.ChecklistItemId).UseIdentityColumn();
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();
            e.Property(x => x.PreparerStatus).HasMaxLength(20).IsRequired();
            e.Property(x => x.ReviewerStatus).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.WorkstreamId, x.IsDeleted });
            e.HasIndex(x => new { x.WorkstreamStageId, x.IsDeleted, x.OrderIndex });
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Workstream).WithMany(w => w.ChecklistItems).HasForeignKey(x => x.WorkstreamId);
            e.HasOne(x => x.Stage).WithMany(s => s.ChecklistItems).HasForeignKey(x => x.WorkstreamStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SourceDefItem).WithMany().HasForeignKey(x => x.SourceDefItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AddedBy).WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PreparerMarkedBy).WithMany().HasForeignKey(x => x.PreparerMarkedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReviewerMarkedBy).WithMany().HasForeignKey(x => x.ReviewerMarkedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Comment>(e =>
        {
            e.ToTable("Comment");
            e.HasKey(x => x.CommentId);
            e.Property(x => x.CommentId).UseIdentityColumn();
            e.Property(x => x.Body).IsRequired();
            e.HasIndex(x => new { x.ChecklistItemId, x.PostedAtUtc }).HasFilter("[ChecklistItemId] IS NOT NULL");
            e.HasIndex(x => new { x.WorkstreamId, x.PostedAtUtc });
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Workstream).WithMany(w => w.Comments).HasForeignKey(x => x.WorkstreamId);
            e.HasOne(x => x.ChecklistItem).WithMany(c => c.Comments).HasForeignKey(x => x.ChecklistItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<CommentAttachment>(e =>
        {
            e.ToTable("CommentAttachment");
            e.HasKey(x => x.CommentAttachmentId);
            e.Property(x => x.CommentAttachmentId).UseIdentityColumn();
            e.Property(x => x.SpDriveId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SpItemId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SpWebUrl).HasMaxLength(1000).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Comment).WithMany(c => c.Attachments).HasForeignKey(x => x.CommentId);
            e.HasOne(x => x.UploadedBy).WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvent");
            e.HasKey(x => x.AuditEventId);
            e.Property(x => x.AuditEventId).UseIdentityColumn();
            e.Property(x => x.TargetTable).HasMaxLength(40).IsRequired();
            e.Property(x => x.Period).HasMaxLength(6).IsFixedLength();
            e.Property(x => x.Action).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.WorkstreamId, x.OccurredAtUtc });
            e.HasIndex(x => new { x.TargetTable, x.TargetId, x.OccurredAtUtc });
            e.HasIndex(x => new { x.Period, x.OccurredAtUtc });
            e.HasIndex(x => new { x.ActorUserId, x.OccurredAtUtc });
            e.HasIndex(x => new { x.EntityId, x.OccurredAtUtc });
            e.HasIndex(x => new { x.Action, x.OccurredAtUtc });
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<AppSetting>(e =>
        {
            e.ToTable("AppSetting");
            e.HasKey(x => x.AppSettingId);
            e.Property(x => x.AppSettingId).UseIdentityColumn();
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Value).HasMaxLength(1000);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.UpdatedBy).WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
