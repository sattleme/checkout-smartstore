using Microsoft.Extensions.Logging;
using Sattleme.Bitcoin.Services;
using Smartstore.Core.Data;
using Smartstore.Scheduling;

namespace Sattleme.Bitcoin.Tasks;

/// <summary>
/// The background checks (README.md, "Background checks"): the SDK has no webhooks, so Smartstore's
/// scheduler runs this task every minute. Each run checks the invoices whose check is due when it
/// starts, the longest due first (PaymentRules.NextCheck says when, counted from the run's start:
/// every minute while an invoice can be paid, around the end of its grace period, then every five
/// minutes until its final status is known, less often after failures), at most 20 and for about
/// 45 seconds, then gives up on the invoices still unsettled 90 days after they were made, with a
/// note on their order.
/// </summary>
[TaskName("SattlemeCheckTask")]
public class SattlemeCheckTask : ITask
{
    private readonly SmartDbContext _db;
    private readonly ILogger<SattlemeCheckTask> _logger;
    private readonly OrderNotes _notes;
    private readonly PaymentChecker _checker;
    private readonly PersonalDataShredder _shredder;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;

    /// <summary>The task.</summary>
    public SattlemeCheckTask(SmartDbContext db,
        ILogger<SattlemeCheckTask> logger,
        OrderNotes notes,
        PaymentChecker checker,
        PersonalDataShredder shredder,
        SattlemeStore store,
        TimeProvider time)
    {
        _db = db;
        _logger = logger;
        _notes = notes;
        _checker = checker;
        _shredder = shredder;
        _store = store;
        _time = time;
    }

    /// <inheritdoc/>
    public async Task Run(TaskExecutionContext ctx, CancellationToken cancelToken = default)
    {
        var started = _time.GetTimestamp();
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var invoice in await _store.GetDueAsync(now, SattlemeDefaults.TaskBatch))
        {
            // A check that could end after the run's budget waits for the next run; so does one
            // after the application started stopping (or the admin cancelled the run).
            if (cancelToken.IsCancellationRequested || SattlemeDefaults.TaskBudget - _time.GetElapsedTime(started) < SattlemeDefaults.CheckTimeout)
            {
                break;
            }
            // Skipped when the payment page's check holds it or just ended: due again in a minute.
            if (!await _checker.CheckAsync(invoice.Id, SattlemeDefaults.PageInterval, SattlemeDefaults.CheckTimeout, now))
            {
                await _store.SetNextCheckAsync(invoice.Id, now - SattlemeDefaults.RunSlack + SattlemeDefaults.OpenInterval);
            }
        }

        foreach (var invoice in await _store.GetUnsettledMadeBeforeAsync(now - SattlemeDefaults.GiveUpAfter, SattlemeDefaults.TaskBatch))
        {
            if (!await _store.GiveUpAsync(invoice.Id))
            {
                continue;
            }
            await _shredder.ShredIfFinalAsync(invoice.Id);
            _logger.LogWarning("sattle.me: the Bitcoin invoice {Reference} had no final status after {Days} days: its checks stopped", invoice.Reference, SattlemeDefaults.GiveUpAfter.Days);
            var order = await _db.Orders.FindByIdAsync(invoice.OrderId, false, cancelToken);
            if (order is not null)
            {
                await _notes.AddAsync(order.Id, "Note.Abandoned", invoice.Reference, SattlemeDefaults.GiveUpAfter.Days);
            }
        }
    }
}
