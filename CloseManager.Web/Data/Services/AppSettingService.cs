using CloseManager.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CloseManager.Web.Data.Services;

/// <summary>
/// Typed accessor for AppSetting rows. All app configuration (except the DB
/// connection string) comes through here. Values are cached per-request via
/// scoped DI lifetime; the cache is invalidated on each new request.
/// </summary>
public class AppSettingService
{
    private readonly AppDbContext _db;
    private Dictionary<string, string?>? _cache;

    public AppSettingService(AppDbContext db)
    {
        _db = db;
    }

    // ── Typed accessors ───────────────────────────────────────────────────────

    public async Task<int> GetStuckThresholdHoursAsync()
    {
        var raw = await GetAsync("StuckThreshold.Default");
        return int.TryParse(raw, out var v) ? v : 24;
    }

    public async Task<int> GetLockDurationMinutesAsync()
    {
        var raw = await GetAsync("Lock.DurationMinutes");
        return int.TryParse(raw, out var v) ? v : 15;
    }

    public async Task<string> GetCloseConfirmPhraseAsync()
        => (await GetAsync("Period.CloseConfirmPhrase")) ?? "close period";

    public async Task<string?> GetSharePointTenantIdAsync()   => await GetAsync("SharePoint.TenantId");
    public async Task<string?> GetSharePointClientIdAsync()   => await GetAsync("SharePoint.ClientId");
    public async Task<string?> GetSharePointClientSecretAsync()=> await GetAsync("SharePoint.ClientSecret");
    public async Task<string?> GetSharePointSiteIdAsync()     => await GetAsync("SharePoint.SiteId");
    public async Task<string?> GetSharePointDriveIdAsync()    => await GetAsync("SharePoint.DriveId");

    public async Task<bool> IsSharePointConfiguredAsync()
    {
        var tenantId = await GetSharePointTenantIdAsync();
        var clientId = await GetSharePointClientIdAsync();
        var secret   = await GetSharePointClientSecretAsync();
        var siteId   = await GetSharePointSiteIdAsync();
        var driveId  = await GetSharePointDriveIdAsync();
        return !string.IsNullOrWhiteSpace(tenantId)
            && !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(secret)
            && !string.IsNullOrWhiteSpace(siteId)
            && !string.IsNullOrWhiteSpace(driveId);
    }

    // ── Raw access ────────────────────────────────────────────────────────────

    public async Task<string?> GetAsync(string key)
    {
        var cache = await GetCacheAsync();
        return cache.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>Returns all settings, including secrets (for the admin Settings page).</summary>
    public async Task<List<Data.Entities.AppSetting>> GetAllAsync()
        => await _db.AppSettings.OrderBy(s => s.Key).ToListAsync();

    /// <summary>
    /// Saves a value. Caller must pass actorUserId for audit trail.
    /// Writes an AuditEvent in the same SaveChangesAsync call.
    /// </summary>
    public async Task SaveAsync(
        string key,
        string? value,
        long actorUserId,
        Guid actorEntraObjectId,
        AuditService audit)
    {
        var setting = await _db.AppSettings.SingleAsync(s => s.Key == key);

        var before = new { setting.Key, Value = setting.IsSecret ? "[secret]" : setting.Value };
        setting.Value = value;
        setting.UpdatedAtUtc = DateTime.UtcNow;
        setting.UpdatedByUserId = actorUserId;

        var after = new { setting.Key, Value = setting.IsSecret ? "[secret]" : value };

        await audit.WriteAsync(
            actorUserId, actorEntraObjectId,
            "AppSetting", setting.AppSettingId,
            "AppSettingUpdated",
            before: before,
            after: after);

        _cache = null; // invalidate cache
        await _db.SaveChangesAsync();
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, string?>> GetCacheAsync()
    {
        if (_cache is not null) return _cache;
        _cache = await _db.AppSettings
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        return _cache;
    }
}
