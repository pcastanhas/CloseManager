namespace CloseManager.Web.Data.Entities;

public class Role
{
    public int RoleId { get; set; }
    public string Code { get; set; } = string.Empty;   // e.g. TreasuryRE
    public string Name { get; set; } = string.Empty;   // e.g. Treasury — Real estate
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public ICollection<EntityRoleAssignment> EntityRoleAssignments { get; set; } = [];
    public ICollection<WorkstreamDefStage> WorkstreamDefStages { get; set; } = [];
}
