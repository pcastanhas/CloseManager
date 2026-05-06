namespace CloseManager.Web.Data.Entities;

public class EntityType
{
    public int EntityTypeId { get; set; }
    public string Code { get; set; } = string.Empty;       // e.g. RealEstateAsset
    public string Name { get; set; } = string.Empty;       // e.g. Real estate asset
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // Nav
    public ICollection<Entity> Entities { get; set; } = [];
    public ICollection<WorkflowTemplate> WorkflowTemplates { get; set; } = [];
}
