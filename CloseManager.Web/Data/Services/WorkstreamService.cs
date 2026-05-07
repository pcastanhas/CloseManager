using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;

namespace CloseManager.Web.Data.Services;

/// <summary>
/// Handles workstream state transitions (lock, submit, checklist actions)
/// and the work-items / dashboard queries. All state mutations call SPs via Dapper.
/// </summary>
public class WorkstreamService
{
    private readonly AppDbContext _db;
    private readonly ILogger<WorkstreamService> _logger;

    public WorkstreamService(AppDbContext db, ILogger<WorkstreamService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private SqlConnection GetConnection()
        => new(_db.Database.GetConnectionString());

    // ── Lock ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if lock was acquired, false if blocked.
    /// </summary>
    public async Task<bool> AcquireLockAsync(
        long workstreamId, long userId, Guid actorEntraOid, int lockMinutes = 15)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        // sp_AcquireLock uses SET NOCOUNT OFF so @@ROWCOUNT flows to ExecuteAsync
        var rows = await conn.ExecuteAsync("sp_AcquireLock",
            new { WorkstreamId = workstreamId, UserId = userId,
                  LockMinutes = lockMinutes, ActorEntraOid = actorEntraOid },
            commandType: System.Data.CommandType.StoredProcedure);
        return rows == 1;
    }

    // ── Submit ────────────────────────────────────────────────────────────────

    public async Task SubmitWorkstreamAsync(
        long workstreamId, long userId, Guid actorEntraOid, string? note = null)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("sp_SubmitWorkstream",
            new { WorkstreamId = workstreamId, UserId = userId,
                  ActorEntraOid = actorEntraOid, Note = note },
            commandType: System.Data.CommandType.StoredProcedure,
            commandTimeout: 30);
    }

    // ── Checklist actions ─────────────────────────────────────────────────────

    public async Task ApproveChecklistItemAsync(
        long checklistItemId, long userId, Guid actorEntraOid)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("sp_ApproveChecklistItem",
            new { ChecklistItemId = checklistItemId, UserId = userId,
                  ActorEntraOid = actorEntraOid },
            commandType: System.Data.CommandType.StoredProcedure);
    }

    public async Task FlagChecklistItemAsync(
        long checklistItemId, long userId, Guid actorEntraOid, string comment)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("sp_FlagChecklistItemWithComment",
            new { ChecklistItemId = checklistItemId, UserId = userId,
                  ActorEntraOid = actorEntraOid, CommentBody = comment },
            commandType: System.Data.CommandType.StoredProcedure);
    }

    // ── Work-items query ──────────────────────────────────────────────────────

    public async Task<WorkItemsResult> GetWorkItemsAsync(long userId)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        const string sql = @"
        -- Preparer items: workstreams at stage 0 where user is Preparer on the entity
        SELECT
            w.WorkstreamId,
            w.Code,
            w.Name              AS WorkstreamName,
            e.Name              AS EntityName,
            e.Code              AS EntityCode,
            w.Period,
            w.Status,
            w.Round,
            w.CurrentStageIndex,
            w.LockedByUserId,
            lu.DisplayName      AS LockedByName,
            w.LockExpiresAtUtc,
            ws0.EnteredAtUtc    AS StageEnteredAt,
            'Preparer'          AS UserRoleName,
            0                   AS IsPreparer,
            (SELECT COUNT(*) FROM ChecklistItem ci
             WHERE ci.WorkstreamId = w.WorkstreamId AND ci.IsDeleted = 0) AS TotalItems,
            (SELECT COUNT(*) FROM ChecklistItem ci
             WHERE ci.WorkstreamId = w.WorkstreamId
               AND ci.PreparerStatus = 'Ready' AND ci.IsDeleted = 0)      AS ReadyItems,
            (SELECT TOP 1 1 FROM WorkstreamFile wf
             WHERE wf.WorkstreamId = w.WorkstreamId
               AND wf.FileRole = 'Primary' AND wf.IsDeleted = 0)          AS HasPrimaryFile,
            wsd.StuckThresholdHours
        FROM Workstream w
        INNER JOIN ClosePeriod cp ON cp.ClosePeriodId = w.ClosePeriodId AND cp.IsDeleted = 0
        INNER JOIN Entity e       ON e.EntityId = w.EntityId
        INNER JOIN EntityRoleAssignment era
            ON era.EntityId = w.EntityId AND era.UserId = @UserId AND era.IsDeleted = 0
        INNER JOIN Role r ON r.RoleId = era.RoleId AND r.Code = 'Preparer'
        LEFT JOIN WorkstreamStage ws0
            ON ws0.WorkstreamId = w.WorkstreamId AND ws0.OrderIndex = 0 AND ws0.IsDeleted = 0
        LEFT JOIN WorkstreamDefStage wsd
            ON wsd.WorkstreamDefStageId = ws0.SourceDefStageId
        LEFT JOIN [User] lu ON lu.UserId = w.LockedByUserId
        WHERE w.CurrentStageIndex = 0
          AND w.Status IN ('NotStarted', 'InProgress', 'NeedsRevision')
          AND w.IsDeleted = 0
          AND cp.ClosedAtUtc IS NULL

        UNION ALL

        -- Reviewer items: workstreams where current stage role matches user's assignment
        SELECT
            w.WorkstreamId,
            w.Code,
            w.Name,
            e.Name,
            e.Code,
            w.Period,
            w.Status,
            w.Round,
            w.CurrentStageIndex,
            w.LockedByUserId,
            lu.DisplayName,
            w.LockExpiresAtUtc,
            ws_cur.EnteredAtUtc,
            r.Name              AS UserRoleName,
            1                   AS IsPreparer,
            (SELECT COUNT(*) FROM ChecklistItem ci
             WHERE ci.WorkstreamId = w.WorkstreamId
               AND ci.WorkstreamStageId = ws_cur.WorkstreamStageId
               AND ci.IsDeleted = 0) AS TotalItems,
            (SELECT COUNT(*) FROM ChecklistItem ci
             WHERE ci.WorkstreamId = w.WorkstreamId
               AND ci.WorkstreamStageId = ws_cur.WorkstreamStageId
               AND ci.ReviewerStatus = 'Approved' AND ci.IsDeleted = 0) AS ReadyItems,
            NULL,
            ws_cur.StuckThresholdHours
        FROM Workstream w
        INNER JOIN ClosePeriod cp ON cp.ClosePeriodId = w.ClosePeriodId AND cp.IsDeleted = 0
        INNER JOIN Entity e       ON e.EntityId = w.EntityId
        INNER JOIN WorkstreamStage ws_cur
            ON ws_cur.WorkstreamId = w.WorkstreamId
           AND ws_cur.OrderIndex   = w.CurrentStageIndex
           AND ws_cur.IsDeleted    = 0
        INNER JOIN EntityRoleAssignment era
            ON era.EntityId = w.EntityId
           AND era.UserId   = @UserId
           AND era.RoleId   = ws_cur.RoleId
           AND era.IsDeleted = 0
        INNER JOIN Role r ON r.RoleId = era.RoleId
        LEFT JOIN [User] lu ON lu.UserId = w.LockedByUserId
        WHERE w.CurrentStageIndex > 0
          AND w.Status = 'InProgress'
          AND w.IsDeleted = 0
          AND cp.ClosedAtUtc IS NULL;";

        var rows = await conn.QueryAsync<WorkItemRow>(sql, new { UserId = userId });
        return WorkItemsResult.FromRows(rows.ToList(), userId);
    }

    // ── Dashboard query ───────────────────────────────────────────────────────

    public async Task<List<DashboardEntityRow>> GetDashboardAsync(long userId, string period)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        const string sql = @"
        SELECT
            e.EntityId,
            e.Name          AS EntityName,
            e.Code          AS EntityCode,
            et.Name         AS EntityTypeName,
            w.WorkstreamId,
            w.Code          AS WorkstreamCode,
            w.Name          AS WorkstreamName,
            w.OrderIndex,
            w.Status,
            w.Round,
            w.CurrentStageIndex,
            ws_cur.StuckThresholdHours,
            ws_cur.EnteredAtUtc AS CurrentStageEnteredAt,
            (SELECT DisplayName FROM [User] WHERE UserId = w.LockedByUserId) AS LockedByName,
            r_cur.Name      AS CurrentRoleName
        FROM Entity e
        INNER JOIN EntityType et ON et.EntityTypeId = e.EntityTypeId
        INNER JOIN EntityRoleAssignment era
            ON era.EntityId = e.EntityId
           AND era.UserId   = @UserId
           AND era.IsDeleted = 0
        INNER JOIN ClosePeriod cp
            ON cp.EntityId = e.EntityId
           AND cp.Period    = @Period
           AND cp.IsDeleted = 0
        INNER JOIN Workstream w
            ON w.ClosePeriodId = cp.ClosePeriodId
           AND w.IsDeleted     = 0
        LEFT JOIN WorkstreamStage ws_cur
            ON ws_cur.WorkstreamId = w.WorkstreamId
           AND ws_cur.OrderIndex   = w.CurrentStageIndex
           AND ws_cur.IsDeleted    = 0
        LEFT JOIN Role r_cur ON r_cur.RoleId = ws_cur.RoleId
        WHERE e.IsDeleted = 0
        ORDER BY et.Name, e.Name, w.OrderIndex;";

        var rows = await conn.QueryAsync<DashboardEntityRow>(sql,
            new { UserId = userId, Period = period });
        return rows.ToList();
    }

    public async Task<string?> GetCurrentPeriodAsync()
    {
        var month = DateTime.UtcNow.ToString("yyyyMM");
        return await _db.ClosePeriods
            .Where(cp => cp.Period == month && !cp.IsDeleted && cp.ClosedAtUtc == null)
            .Select(cp => cp.Period)
            .FirstOrDefaultAsync()
            ?? await _db.ClosePeriods
               .Where(cp => !cp.IsDeleted && cp.ClosedAtUtc == null)
               .OrderByDescending(cp => cp.Period)
               .Select(cp => cp.Period)
               .FirstOrDefaultAsync();
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class WorkItemRow
{
    public long WorkstreamId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string WorkstreamName { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string EntityCode { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Round { get; set; }
    public int CurrentStageIndex { get; set; }
    public long? LockedByUserId { get; set; }
    public string? LockedByName { get; set; }
    public DateTime? LockExpiresAtUtc { get; set; }
    public DateTime? StageEnteredAt { get; set; }
    public string UserRoleName { get; set; } = string.Empty;
    public bool IsPreparer { get; set; }
    public int TotalItems { get; set; }
    public int ReadyItems { get; set; }
    public bool? HasPrimaryFile { get; set; }
    public int? StuckThresholdHours { get; set; }

    public bool IsLocked => LockedByUserId.HasValue && LockExpiresAtUtc > DateTime.UtcNow;
    public bool IsStuck => StageEnteredAt.HasValue && StuckThresholdHours.HasValue
        && (DateTime.UtcNow - StageEnteredAt.Value).TotalHours > StuckThresholdHours.Value;

    public string PeriodLabel => DateTime.TryParseExact(Period, "yyyyMM",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d)
        ? d.ToString("MMM yyyy") : Period;

    public string WaitingLabel
    {
        get
        {
            if (Status == "NeedsRevision") return StageEnteredAt.HasValue
                ? $"Sent back {FormatAge(StageEnteredAt.Value)}" : "Sent back";
            if (StageEnteredAt.HasValue) return $"Landed {FormatAge(StageEnteredAt.Value)}";
            return Status == "NotStarted" ? "Not started" : "In progress";
        }
    }

    private static string FormatAge(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}

public class WorkItemsResult
{
    public List<WorkItemRow> NeedsAttention { get; set; } = [];
    public List<WorkItemRow> UpNextAndInProgress { get; set; } = [];
    public int TotalCount => NeedsAttention.Count + UpNextAndInProgress.Count;
    public int AttentionCount => NeedsAttention.Count;

    public static WorkItemsResult FromRows(List<WorkItemRow> rows, long userId)
    {
        var result = new WorkItemsResult();
        foreach (var row in rows)
        {
            var isAttention = row.Status == "NeedsRevision"
                           || row.IsStuck
                           || row.Round >= 4;
            if (isAttention) result.NeedsAttention.Add(row);
            else             result.UpNextAndInProgress.Add(row);
        }
        // Sort: longest waiting first within each section
        result.NeedsAttention.Sort((a, b) =>
            (a.StageEnteredAt ?? DateTime.UtcNow).CompareTo(b.StageEnteredAt ?? DateTime.UtcNow));
        result.UpNextAndInProgress.Sort((a, b) =>
            (a.StageEnteredAt ?? DateTime.UtcNow).CompareTo(b.StageEnteredAt ?? DateTime.UtcNow));
        return result;
    }
}

public class DashboardEntityRow
{
    public long EntityId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityCode { get; set; } = string.Empty;
    public string EntityTypeName { get; set; } = string.Empty;
    public long WorkstreamId { get; set; }
    public string WorkstreamCode { get; set; } = string.Empty;
    public string WorkstreamName { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Round { get; set; }
    public int CurrentStageIndex { get; set; }
    public int? StuckThresholdHours { get; set; }
    public DateTime? CurrentStageEnteredAt { get; set; }
    public string? LockedByName { get; set; }
    public string? CurrentRoleName { get; set; }

    public bool IsStuck => CurrentStageEnteredAt.HasValue && StuckThresholdHours.HasValue
        && (DateTime.UtcNow - CurrentStageEnteredAt.Value).TotalHours > StuckThresholdHours.Value;
}
