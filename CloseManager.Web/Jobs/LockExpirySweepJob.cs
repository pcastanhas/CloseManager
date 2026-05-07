using CloseManager.Web.Data.Services;
using Hangfire;

namespace CloseManager.Web.Jobs;

/// <summary>
/// Recurring Hangfire job: clears workstream locks whose LockExpiresAtUtc has passed.
/// Runs every 2 minutes. Registered in Program.cs via RecurringJob.AddOrUpdate.
/// </summary>
public class LockExpirySweepJob
{
    private readonly PeriodService _periods;
    private readonly ILogger<LockExpirySweepJob> _logger;

    public LockExpirySweepJob(PeriodService periods, ILogger<LockExpirySweepJob> logger)
    {
        _periods = periods;
        _logger = logger;
    }

    [JobDisplayName("Lock expiry sweep")]
    public async Task ExecuteAsync()
    {
        _logger.LogDebug("LockExpirySweepJob running");
        await _periods.ExpireLocksAsync();
    }
}
