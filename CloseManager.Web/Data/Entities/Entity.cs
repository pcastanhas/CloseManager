namespace CloseManager.Web.Data.Entities;

public class Entity
{
    public long EntityId { get; set; }
    public int EntityTypeId { get; set; }
    public string Code { get; set; } = string.Empty;       // short code for SharePoint paths
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public long CreatedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public EntityType EntityType { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
    public ICollection<EntityRoleAssignment> RoleAssignments { get; set; } = [];
    public ICollection<ClosePeriod> ClosePeriods { get; set; } = [];
}
