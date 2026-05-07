namespace CloseManager.Web.Auth;

/// <summary>
/// Configuration options for admin access control.
/// AdminGroupId is the Entra group object ID whose members are treated as admins.
/// Bound from configuration key "AdminGroupId" in Program.cs.
/// </summary>
public class AdminOptions
{
    public string AdminGroupId { get; set; } = string.Empty;
}
