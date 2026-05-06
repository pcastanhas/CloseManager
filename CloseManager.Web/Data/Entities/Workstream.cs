namespace CloseManager.Web.Data.Entities;

public class ClosePeriod
{
    public long ClosePeriodId { get; set; }
    public long EntityId { get; set; }
    public string Period { get; set; } = string.Empty;     // yyyyMM
    public DateTime OpenedAtUtc { get; set; }
    public long OpenedByUserId { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public long? ClosedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public Entity Entity { get; set; } = null!;
    public User OpenedBy { get; set; } = null!;
    public User? ClosedBy { get; set; }
    public ICollection<Workstream> Workstreams { get; set; } = [];
}

public class Workstream
{
    public long WorkstreamId { get; set; }
    public long ClosePeriodId { get; set; }
    public string Period { get; set; } = string.Empty;     // denormalized
    public long EntityId { get; set; }                     // denormalized
    public long WorkstreamDefId { get; set; }

    // Snapshot of def metadata at instantiation
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int OrderIndex { get; set; }

    public string Status { get; set; } = "NotStarted";     // NotStarted|InProgress|NeedsRevision|Approved|Rebuilt
    public int Round { get; set; } = 1;
    public int CurrentStageIndex { get; set; }

    // Lock
    public long? LockedByUserId { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public DateTime? LockExpiresAtUtc { get; set; }

    // Lifecycle
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public long? ApprovedByUserId { get; set; }
    public long? RebuiltFromWorkstreamId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public long CreatedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public ClosePeriod ClosePeriod { get; set; } = null!;
    public WorkstreamDef WorkstreamDef { get; set; } = null!;
    public User? LockedBy { get; set; }
    public User? ApprovedBy { get; set; }
    public User CreatedBy { get; set; } = null!;
    public Workstream? RebuiltFrom { get; set; }
    public ICollection<WorkstreamStage> Stages { get; set; } = [];
    public ICollection<WorkstreamFile> Files { get; set; } = [];
    public ICollection<ChecklistItem> ChecklistItems { get; set; } = [];
    public ICollection<Comment> Comments { get; set; } = [];
}

public class WorkstreamStage
{
    public long WorkstreamStageId { get; set; }
    public long WorkstreamId { get; set; }
    public long? SourceDefStageId { get; set; }
    public int OrderIndex { get; set; }
    public int RoleId { get; set; }                        // snapshot
    public string StageKind { get; set; } = string.Empty; // 'Prepare' | 'Review'
    public string? DisplayName { get; set; }
    public bool IsFinalApproval { get; set; }              // snapshot
    public int? StuckThresholdHours { get; set; }          // snapshot

    // Per-stage lifecycle (cleared on send-back, preserved for audit)
    public DateTime? EnteredAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public long? StartedByUserId { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long? CompletedByUserId { get; set; }
    public string? Outcome { get; set; }                   // 'Advanced' | 'SentBack'

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public Workstream Workstream { get; set; } = null!;
    public WorkstreamDefStage? SourceDefStage { get; set; }
    public User? StartedBy { get; set; }
    public User? CompletedBy { get; set; }
    public ICollection<ChecklistItem> ChecklistItems { get; set; } = [];
}
