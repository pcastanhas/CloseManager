using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;

namespace CloseManager.Web.Data.Services;

/// <summary>
/// Calls the period lifecycle stored procedures via Dapper.
/// All state mutations go through these SPs — no direct EF updates on period tables.
/// </summary>
public class PeriodService
{
    private readonly AppDbContext _db;
    private readonly ILogger<PeriodService> _logger;

    public PeriodService(AppDbContext db, ILogger<PeriodService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private SqlConnection GetConnection()
        => new(_db.Database.GetConnectionString());

    // ── sp_OpenPeriod (single entity, called per-entity by the Hangfire job) ──

    public async Task<OpenEntityResult> OpenEntityAsync(
        long entityId, string period, long actorUserId, Guid actorEntraOid)
    {
        try
        {
            using var conn = GetConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("sp_OpenPeriod",
                new { EntityId = entityId, Period = period,
                      ActorUserId = actorUserId, ActorEntraOid = actorEntraOid },
                commandType: System.Data.CommandType.StoredProcedure,
                commandTimeout: 60);
            return OpenEntityResult.Success(entityId);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "sp_OpenPeriod failed for EntityId={EntityId} Period={Period}",
                entityId, period);
            return OpenEntityResult.Failure(entityId, ex.Message);
        }
    }

    // ── sp_ClosePeriod ────────────────────────────────────────────────────────

    public async Task ClosePeriodAsync(
        string period, long actorUserId, Guid actorEntraOid, string reason)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("sp_ClosePeriod",
            new { Period = period, ActorUserId = actorUserId,
                  ActorEntraOid = actorEntraOid, Reason = reason },
            commandType: System.Data.CommandType.StoredProcedure,
            commandTimeout: 30);
    }

    // ── sp_ReopenPeriod ───────────────────────────────────────────────────────

    public async Task ReopenPeriodAsync(
        string period, long actorUserId, Guid actorEntraOid, string reason)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("sp_ReopenPeriod",
            new { Period = period, ActorUserId = actorUserId,
                  ActorEntraOid = actorEntraOid, Reason = reason },
            commandType: System.Data.CommandType.StoredProcedure,
            commandTimeout: 30);
    }

    // ── sp_CloseEntityInPeriod ────────────────────────────────────────────────

    public async Task CloseEntityInPeriodAsync(
        long closePeriodId, long actorUserId, Guid actorEntraOid, string reason)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("sp_CloseEntityInPeriod",
            new { ClosePeriodId = closePeriodId, ActorUserId = actorUserId,
                  ActorEntraOid = actorEntraOid, Reason = reason },
            commandType: System.Data.CommandType.StoredProcedure,
            commandTimeout: 30);
    }

    // ── Period summary queries (used by the Periods page) ────────────────────

    public async Task<List<PeriodSummary>> GetPeriodSummariesAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        const string sql = @"
            SELECT
                cp.Period,
                COUNT(DISTINCT cp.ClosePeriodId)                                    AS EntityCount,
                SUM(CASE WHEN cp.ClosedAtUtc IS NULL THEN 0 ELSE 1 END)            AS ClosedEntityCount,
                MIN(cp.OpenedAtUtc)                                                 AS OpenedAtUtc,
                MAX(cp.ClosedAtUtc)                                                 AS ClosedAtUtc,
                COUNT(DISTINCT w.WorkstreamId)                                      AS TotalWorkstreams,
                COUNT(DISTINCT CASE WHEN w.Status='Approved' THEN w.WorkstreamId END) AS ApprovedWorkstreams,
                COUNT(DISTINCT CASE WHEN w.Status='InProgress' THEN w.WorkstreamId END) AS InProgressWorkstreams,
                COUNT(DISTINCT CASE WHEN w.Status='NeedsRevision' THEN w.WorkstreamId END) AS NeedsRevisionWorkstreams,
                COUNT(DISTINCT CASE WHEN w.Status='NotStarted' THEN w.WorkstreamId END) AS NotStartedWorkstreams,
                CASE WHEN MAX(cp.ClosedAtUtc) IS NOT NULL THEN 1 ELSE 0 END        AS IsClosed
            FROM ClosePeriod cp
            LEFT JOIN Workstream w ON w.ClosePeriodId = cp.ClosePeriodId AND w.IsDeleted = 0
            WHERE cp.IsDeleted = 0
            GROUP BY cp.Period
            ORDER BY cp.Period DESC;";

        var rows = await conn.QueryAsync<PeriodSummary>(sql);
        return rows.ToList();
    }

    public async Task<List<PeriodEntityRow>> GetPeriodEntityRowsAsync(string period)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        const string sql = @"
            SELECT
                cp.ClosePeriodId,
                cp.EntityId,
                e.Name      AS EntityName,
                e.Code      AS EntityCode,
                et.Name     AS EntityTypeName,
                cp.OpenedAtUtc,
                cp.ClosedAtUtc,
                COUNT(w.WorkstreamId)                                               AS TotalWorkstreams,
                COUNT(CASE WHEN w.Status='Approved' THEN 1 END)                    AS ApprovedWorkstreams,
                COUNT(CASE WHEN w.Status='InProgress' THEN 1 END)                  AS InProgressWorkstreams,
                COUNT(CASE WHEN w.Status='NeedsRevision' THEN 1 END)               AS NeedsRevisionWorkstreams,
                COUNT(CASE WHEN w.Status='NotStarted' THEN 1 END)                  AS NotStartedWorkstreams
            FROM ClosePeriod cp
            INNER JOIN Entity e ON e.EntityId = cp.EntityId
            INNER JOIN EntityType et ON et.EntityTypeId = e.EntityTypeId
            LEFT JOIN Workstream w ON w.ClosePeriodId = cp.ClosePeriodId AND w.IsDeleted = 0
            WHERE cp.Period = @Period AND cp.IsDeleted = 0
            GROUP BY cp.ClosePeriodId, cp.EntityId, e.Name, e.Code, et.Name,
                     cp.OpenedAtUtc, cp.ClosedAtUtc
            ORDER BY e.Name;";

        var rows = await conn.QueryAsync<PeriodEntityRow>(sql, new { Period = period });
        return rows.ToList();
    }

    // ── sp_ExpireLocks (called by Hangfire sweep job) ─────────────────────────

    public async Task ExpireLocksAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("sp_ExpireLocks",
            commandType: System.Data.CommandType.StoredProcedure,
            commandTimeout: 30);
    }

    // ── Active entity list for period-open dialog ─────────────────────────────

    public async Task<List<EntityOption>> GetActiveEntitiesAsync()
    {
        return await _db.Entities
            .Where(e => !e.IsDeleted && e.IsActive)
            .Include(e => e.EntityType)
            .OrderBy(e => e.Name)
            .Select(e => new EntityOption(e.EntityId, e.Code, e.Name, e.EntityType.Name))
            .ToListAsync();
    }

    public async Task<string?> GetMostRecentPeriodAsync()
    {
        return await _db.ClosePeriods
            .Where(cp => !cp.IsDeleted)
            .GroupBy(cp => cp.Period)
            .OrderByDescending(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefaultAsync();
    }

    public async Task<List<long>> GetEntityIdsInPeriodAsync(string period)
    {
        return await _db.ClosePeriods
            .Where(cp => cp.Period == period && !cp.IsDeleted)
            .Select(cp => cp.EntityId)
            .ToListAsync();
    }
}

// ── Result / DTO types ────────────────────────────────────────────────────────

public record OpenEntityResult(long EntityId, bool Succeeded, string? ErrorMessage)
{
    public static OpenEntityResult Success(long id) => new(id, true, null);
    public static OpenEntityResult Failure(long id, string msg) => new(id, false, msg);
}

public class PeriodSummary
{
    public string Period { get; set; } = string.Empty;
    public int EntityCount { get; set; }
    public int ClosedEntityCount { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public int TotalWorkstreams { get; set; }
    public int ApprovedWorkstreams { get; set; }
    public int InProgressWorkstreams { get; set; }
    public int NeedsRevisionWorkstreams { get; set; }
    public int NotStartedWorkstreams { get; set; }
    public bool IsClosed { get; set; }

    public string PeriodLabel => DateTime.TryParseExact(Period, "yyyyMM",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d)
        ? d.ToString("MMMM yyyy") : Period;

    public double ApprovalPct => TotalWorkstreams > 0
        ? (double)ApprovedWorkstreams / TotalWorkstreams * 100 : 0;

    public bool IsClosingSoon => !IsClosed && ApprovalPct >= 90
        && (DateTime.UtcNow - OpenedAtUtc).TotalDays >= 21;
}

public class PeriodEntityRow
{
    public long ClosePeriodId { get; set; }
    public long EntityId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityCode { get; set; } = string.Empty;
    public string EntityTypeName { get; set; } = string.Empty;
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public int TotalWorkstreams { get; set; }
    public int ApprovedWorkstreams { get; set; }
    public int InProgressWorkstreams { get; set; }
    public int NeedsRevisionWorkstreams { get; set; }
    public int NotStartedWorkstreams { get; set; }
    public bool IsEntityClosed => ClosedAtUtc.HasValue;
    public bool AllApproved => TotalWorkstreams > 0 && ApprovedWorkstreams == TotalWorkstreams;
}

public record EntityOption(long EntityId, string Code, string Name, string EntityTypeName);
