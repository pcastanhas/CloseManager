namespace CloseManager.Web.Data.Entities;

/// <summary>
/// Entra (Azure AD) identity projection. Synced on login; no local credentials.
/// Matches the [User] table in schema.sql.
/// </summary>
public class User
{
    public long UserId { get; set; }
    public Guid EntraObjectId { get; set; }
    public string Upn { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LastSeenUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
