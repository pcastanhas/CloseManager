namespace CloseManager.Web.Data.Entities;

public class WorkstreamFile
{
    public long WorkstreamFileId { get; set; }
    public long WorkstreamId { get; set; }
    public string FileRole { get; set; } = string.Empty;   // 'Primary' | 'Supporting' | 'Reference'

    // SharePoint identity
    public string SpDriveId { get; set; } = string.Empty;
    public string SpItemId { get; set; } = string.Empty;
    public string SpWebUrl { get; set; } = string.Empty;
    public string SpRelativePath { get; set; } = string.Empty;

    // Metadata mirror
    public string FileName { get; set; } = string.Empty;
    public string? FileExtension { get; set; }
    public long? SizeBytes { get; set; }
    public byte[]? ContentHash { get; set; }               // SHA-256, populated async

    public DateTime UploadedAtUtc { get; set; }
    public long UploadedByUserId { get; set; }

    public long? ReplacesFileId { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public Workstream Workstream { get; set; } = null!;
    public User UploadedBy { get; set; } = null!;
    public WorkstreamFile? ReplacesFile { get; set; }
}

public class ChecklistItem
{
    public long ChecklistItemId { get; set; }
    public long WorkstreamId { get; set; }
    public long WorkstreamStageId { get; set; }
    public long? SourceDefItemId { get; set; }             // null = ad-hoc
    public long AddedByUserId { get; set; }
    public DateTime AddedAtUtc { get; set; }
    public int OrderIndex { get; set; }
    public string Text { get; set; } = string.Empty;

    // Preparer prep state (meaningful only on stage-1 items)
    public string PreparerStatus { get; set; } = "NotReady";  // 'NotReady' | 'Ready'
    public DateTime? PreparerMarkedAtUtc { get; set; }
    public long? PreparerMarkedByUserId { get; set; }

    // Reviewer verification state (meaningful on all stages > 0)
    public string ReviewerStatus { get; set; } = "Pending";   // 'Pending' | 'Approved' | 'NeedsRevision'
    public DateTime? ReviewerMarkedAtUtc { get; set; }
    public long? ReviewerMarkedByUserId { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public Workstream Workstream { get; set; } = null!;
    public WorkstreamStage Stage { get; set; } = null!;
    public WorkstreamDefChecklistItem? SourceDefItem { get; set; }
    public User AddedBy { get; set; } = null!;
    public User? PreparerMarkedBy { get; set; }
    public User? ReviewerMarkedBy { get; set; }
    public ICollection<Comment> Comments { get; set; } = [];
}

public class Comment
{
    public long CommentId { get; set; }
    public long WorkstreamId { get; set; }
    public long? ChecklistItemId { get; set; }             // null = workstream-level comment
    public long AuthorUserId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public Workstream Workstream { get; set; } = null!;
    public ChecklistItem? ChecklistItem { get; set; }
    public User Author { get; set; } = null!;
    public ICollection<CommentAttachment> Attachments { get; set; } = [];
}

public class CommentAttachment
{
    public long CommentAttachmentId { get; set; }
    public long CommentId { get; set; }
    public string SpDriveId { get; set; } = string.Empty;
    public string SpItemId { get; set; } = string.Empty;
    public string SpWebUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public long UploadedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public Comment Comment { get; set; } = null!;
    public User UploadedBy { get; set; } = null!;
}
