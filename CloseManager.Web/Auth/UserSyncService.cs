using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CloseManager.Web.Auth;

/// <summary>
/// Syncs the authenticated Entra user into the local [User] table on sign-in.
/// </summary>
public class UserSyncService
{
    private readonly AppDbContext _db;
    private readonly ILogger<UserSyncService> _logger;

    public UserSyncService(AppDbContext db, ILogger<UserSyncService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<long?> SyncAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;

        var entraObjectId = GetEntraObjectId(principal);
        if (entraObjectId == Guid.Empty)
        {
            _logger.LogWarning("Authenticated principal has no valid oid claim; skipping user sync");
            return null;
        }

        var upn = principal.FindFirstValue(ClaimTypes.Upn)
               ?? principal.FindFirstValue("preferred_username")
               ?? principal.FindFirstValue(ClaimTypes.Email)
               ?? string.Empty;

        var displayName = principal.FindFirstValue("name")
                       ?? principal.FindFirstValue(ClaimTypes.Name)
                       ?? upn;

        var existing = await _db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == entraObjectId);

        if (existing is null)
        {
            var newUser = new User
            {
                EntraObjectId = entraObjectId,
                Upn = upn,
                DisplayName = displayName,
                IsActive = true,
                LastSeenUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.Users.Add(newUser);
            await _db.SaveChangesAsync();
            _logger.LogInformation("New user created: {DisplayName} ({Upn})", displayName, upn);
            return newUser.UserId;
        }

        existing.DisplayName = displayName;
        existing.Upn = upn;
        existing.LastSeenUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (!existing.IsActive)
            _logger.LogWarning("Deactivated user signed in: {DisplayName} ({Upn})", displayName, upn);

        return existing.UserId;
    }

    public async Task<bool> IsDeactivatedAsync(Guid entraObjectId)
    {
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.EntraObjectId == entraObjectId);
        return user is { IsActive: false };
    }

    internal static Guid GetEntraObjectId(ClaimsPrincipal principal)
    {
        var oidString = principal.FindFirstValue("oid")
                     ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        return Guid.TryParse(oidString, out var guid) ? guid : Guid.Empty;
    }
}
