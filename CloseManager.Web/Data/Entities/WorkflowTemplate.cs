namespace CloseManager.Web.Data.Entities;

public class WorkflowTemplate
{
    public long WorkflowTemplateId { get; set; }
    public int EntityTypeId { get; set; }
    public int Version { get; set; }
    public bool IsCurrent { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public long CreatedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public EntityType EntityType { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
    public ICollection<WorkstreamDef> WorkstreamDefs { get; set; } = [];
}

public class WorkstreamDef
{
    public long WorkstreamDefId { get; set; }
    public long WorkflowTemplateId { get; set; }
    public string Code { get; set; } = string.Empty;       // e.g. CASH, DEBT_SVC
    public string Name { get; set; } = string.Empty;       // e.g. Debt service review
    public int OrderIndex { get; set; }
    public string? Description { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public WorkflowTemplate WorkflowTemplate { get; set; } = null!;
    public ICollection<WorkstreamDefStage> Stages { get; set; } = [];
    public ICollection<Workstream> Workstreams { get; set; } = [];
}

public class WorkstreamDefStage
{
    public long WorkstreamDefStageId { get; set; }
    public long WorkstreamDefId { get; set; }
    public int OrderIndex { get; set; }             // 0 = preparer, 1+ = reviewers
    public int RoleId { get; set; }
    public string StageKind { get; set; } = string.Empty;  // 'Prepare' | 'Review'
    public string? DisplayName { get; set; }
    public bool IsFinalApproval { get; set; }
    public int? StuckThresholdHours { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public WorkstreamDef WorkstreamDef { get; set; } = null!;
    public Role Role { get; set; } = null!;
    public ICollection<WorkstreamDefChecklistItem> ChecklistItems { get; set; } = [];
}

public class WorkstreamDefChecklistItem
{
    public long WorkstreamDefChecklistItemId { get; set; }
    public long WorkstreamDefStageId { get; set; }
    public int OrderIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Guidance { get; set; }
    public bool IsRequired { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public WorkstreamDefStage Stage { get; set; } = null!;
}
