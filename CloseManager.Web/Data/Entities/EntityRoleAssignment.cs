namespace CloseManager.Web.Data.Entities;

public class EntityRoleAssignment
{
    public long EntityRoleAssignmentId { get; set; }
    public long EntityId { get; set; }
    public int RoleId { get; set; }
    public long UserId { get; set; }
    public DateTime AssignedAtUtc { get; set; }
    public long AssignedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public Entity Entity { get; set; } = null!;
    public Role Role { get; set; } = null!;
    public User User { get; set; } = null!;
    public User AssignedBy { get; set; } = null!;
}
