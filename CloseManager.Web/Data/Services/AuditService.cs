using CloseManager.Web.Data;
using CloseManager.Web.Data.Entities;
using System.Security.Claims;
using System.Text.Json;

namespace CloseManager.Web.Data.Services;

/// <summary>
/// Shared audit event writer. All state-changing actions call WriteAsync() in the
/// same transaction as the state change. Never call this outside a transaction.
///
/// The audit trail is append-only; no update or delete methods are exposed.
/// </summary>
public class AuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db)
    {
        _db = db;
    }

    public async Task WriteAsync(
        long actorUserId,
        Guid actorEntraObjectId,
        string targetTable,
        long targetId,
        string action,
        object? before = null,
        object? after = null,
        string? notes = null,
        long? workstreamId = null,
        string? period = null,
        long? entityId = null)
    {
        var evt = new AuditEvent
        {
            OccurredAtUtc = DateTime.UtcNow,
            ActorUserId = actorUserId,
            ActorEntraObjectId = actorEntraObjectId,
            TargetTable = targetTable,
            TargetId = targetId,
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            Notes = notes,
            WorkstreamId = workstreamId,
            Period = period,
            EntityId = entityId
        };

        _db.AuditEvents.Add(evt);
        // Caller is responsible for SaveChangesAsync — this must be in the same transaction
    }
}
