namespace CloseManager.Web.Data.Entities;

/// <summary>
/// Append-only audit trail. Never updated, never soft-deleted.
/// Every state-changing action writes one row in the same transaction as the state change.
/// </summary>
public class AuditEvent
{
    public long AuditEventId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public long ActorUserId { get; set; }
    public Guid ActorEntraObjectId { get; set; }           // denormalized for forensic durability

    // Polymorphic target
    public string TargetTable { get; set; } = string.Empty;
    public long TargetId { get; set; }

    // Workstream context for fast scoped queries
    public long? WorkstreamId { get; set; }
    public string? Period { get; set; }
    public long? EntityId { get; set; }

    public string Action { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? Notes { get; set; }

    // Nav — no nav back to Workstream to keep the table purely append-only
    public User Actor { get; set; } = null!;
}

/// <summary>
/// Admin-managed key/value settings store.
/// All non-connection-string configuration lives here, not in appsettings.json.
/// </summary>
public class AppSetting
{
    public int AppSettingId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Description { get; set; }
    public bool IsSecret { get; set; }                     // hint to UI: mask value
    public DateTime UpdatedAtUtc { get; set; }
    public long? UpdatedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public User? UpdatedBy { get; set; }
}
