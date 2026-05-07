using CloseManager.Web.Data.Services;
using Hangfire;
using Hangfire.Server;
using Microsoft.AspNetCore.SignalR;

namespace CloseManager.Web.Jobs;

/// <summary>
/// Hangfire background job that opens a close period across a list of entities.
/// Called one job per period-open action. Each entity is processed independently
/// so partial failures don't abort the whole batch — failed entities can be retried.
///
/// Progress is broadcast via SignalR so the Period Management page can show live updates.
/// </summary>
public class OpenPeriodJob
{
    private readonly PeriodService _periods;
    private readonly IHubContext<PeriodProgressHub> _hub;
    private readonly ILogger<OpenPeriodJob> _logger;

    public OpenPeriodJob(
        PeriodService periods,
        IHubContext<PeriodProgressHub> hub,
        ILogger<OpenPeriodJob> logger)
    {
        _periods = periods;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Entry point called by Hangfire. Enqueue via:
    ///   BackgroundJob.Enqueue&lt;OpenPeriodJob&gt;(j => j.ExecuteAsync(period, entityIds, actorUserId, actorEntraOid, null));
    /// </summary>
    [JobDisplayName("Open period {0}")]
    public async Task ExecuteAsync(
        string period,
        long[] entityIds,
        long actorUserId,
        Guid actorEntraOid,
        PerformContext? context)  // injected by Hangfire
    {
        _logger.LogInformation("OpenPeriodJob starting: period={Period}, entities={Count}",
            period, entityIds.Length);

        var results = new List<OpenEntityResult>();
        var completed = 0;

        foreach (var entityId in entityIds)
        {
            var result = await _periods.OpenEntityAsync(entityId, period, actorUserId, actorEntraOid);
            results.Add(result);
            completed++;

            if (!result.Succeeded)
                _logger.LogWarning("Failed to open EntityId={EntityId} in period {Period}: {Error}",
                    entityId, period, result.ErrorMessage);

            // Broadcast progress to connected Blazor clients
            await _hub.Clients.Group($"period-{period}").SendAsync("Progress", new
            {
                Period = period,
                Completed = completed,
                Total = entityIds.Length,
                FailedEntityId = result.Succeeded ? (long?)null : entityId,
                ErrorMessage = result.ErrorMessage
            });
        }

        var succeeded = results.Count(r => r.Succeeded);
        var failed    = results.Count(r => !r.Succeeded);

        _logger.LogInformation(
            "OpenPeriodJob complete: period={Period}, succeeded={S}, failed={F}",
            period, succeeded, failed);

        // Final broadcast — signals the UI to switch from "opening" to "open" state
        await _hub.Clients.Group($"period-{period}").SendAsync("Completed", new
        {
            Period = period,
            Succeeded = succeeded,
            Failed = failed,
            FailedEntityIds = results.Where(r => !r.Succeeded).Select(r => r.EntityId).ToArray()
        });
    }
}

/// <summary>
/// SignalR hub for period-open progress updates.
/// Clients join group "period-{yyyyMM}" to receive progress for that period.
/// </summary>
public class PeriodProgressHub : Hub
{
    public async Task JoinPeriodGroup(string period)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"period-{period}");

    public async Task LeavePeriodGroup(string period)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"period-{period}");
}
