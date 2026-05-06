using CloseManager.Web.Auth;
using CloseManager.Web.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloseManager.Web.Data.Services;

/// <summary>
/// Resolves the current user's identity for use in services and pages.
/// Scoped — one instance per Blazor circuit.
/// </summary>
public class CurrentUserService
{
    private readonly AuthenticationStateProvider _auth;
    private readonly AppDbContext _db;
    private readonly AdminOptions _adminOptions;

    private long? _userId;
    private Guid? _entraObjectId;
    private bool? _isAdmin;

    public CurrentUserService(
        AuthenticationStateProvider auth,
        AppDbContext db,
        IOptions<AdminOptions> adminOptions)
    {
        _auth = auth;
        _db = db;
        _adminOptions = adminOptions.Value;
    }

    public async Task<long> GetUserIdAsync()
    {
        if (_userId.HasValue) return _userId.Value;
        await ResolveAsync();
        return _userId!.Value;
    }

    public async Task<Guid> GetEntraObjectIdAsync()
    {
        if (_entraObjectId.HasValue) return _entraObjectId.Value;
        await ResolveAsync();
        return _entraObjectId!.Value;
    }

    public async Task<bool> IsAdminAsync()
    {
        if (_isAdmin.HasValue) return _isAdmin.Value;
        await ResolveAsync();
        return _isAdmin!.Value;
    }

    private async Task ResolveAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var principal = state.User;

        _entraObjectId = UserSyncService.GetEntraObjectId(principal);

        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.EntraObjectId == _entraObjectId);

        _userId = user?.UserId
            ?? throw new InvalidOperationException("Current user not found in database — sign-out and sign in again.");

        // Admin check: groups claim contains the configured admin group ID
        _isAdmin = !string.IsNullOrEmpty(_adminOptions.AdminGroupId)
            && principal.Claims.Any(c =>
                c.Type is "groups" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"
                && c.Value == _adminOptions.AdminGroupId);
    }
}
