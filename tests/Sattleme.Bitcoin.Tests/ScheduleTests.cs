using Microsoft.Extensions.Logging;
using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tasks;
using Sattleme.Bitcoin.Tests.Support;
using Smartstore.Scheduling;
using SmartstoreSattleme.Sattleme.Checkout;
using ShopPaymentStatus = Smartstore.Core.Checkout.Payment.PaymentStatus;

namespace Sattleme.Bitcoin.Tests;

/// <summary>The background checks: when each invoice is checked, how many a run takes, and the 90 days.</summary>
[TestFixture]
public sealed class ScheduleTests
{
    private static readonly DateTime _now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long _nowUnix = new DateTimeOffset(_now).ToUnixTimeSeconds();
    private Shop _shop = null!;

    [SetUp]
    public void SetUp()
    {
        _shop = new Shop();
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    [Test]
    public void Checks_every_minute_while_the_invoice_can_be_paid()
    {
        var expiresAt = _nowUnix + 600;
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now), Is.EqualTo(_now.AddMinutes(1)));
    }

    [Test]
    public void Checks_just_before_and_just_after_the_end_of_the_grace_period()
    {
        // The grace period ends 120 s after the invoice: checked 10 s before, then 10 s after.
        var expiresAt = _nowUnix - 100;
        var end = _now.AddSeconds(20);
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now), Is.EqualTo(end - SattlemeDefaults.EndMargin));
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, end - SattlemeDefaults.EndMargin), Is.EqualTo(end + SattlemeDefaults.EndMargin));
    }

    [Test]
    public void Waits_at_least_a_few_seconds_between_two_checks()
    {
        var expiresAt = _nowUnix - 115;
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now) - _now, Is.GreaterThanOrEqualTo(SattlemeDefaults.PageInterval));
    }

    [Test]
    public void Counts_a_run_s_waits_from_its_start_less_a_few_seconds()
    {
        // A check of a run that started 12 s before it ended: due again 55 s after that start, so
        // that the run a minute later takes it.
        var run = _now.AddSeconds(-12);
        Assert.That(PaymentRules.NextCheck(_nowUnix + 600, 0, _now, run), Is.EqualTo(run + SattlemeDefaults.OpenInterval - SattlemeDefaults.RunSlack));
        Assert.That(PaymentRules.NextCheck(_nowUnix - 3600, 0, _now, run), Is.EqualTo(run + SattlemeDefaults.SettlingInterval - SattlemeDefaults.RunSlack));
        Assert.That(PaymentRules.NextCheck(_nowUnix - 3600, 2, _now, run), Is.EqualTo(run + TimeSpan.FromMinutes(2) - SattlemeDefaults.RunSlack));
    }

    [Test]
    public async Task An_invoice_is_checked_by_every_run_of_the_minute_while_it_can_be_paid()
    {
        // Smartstore's scheduler runs the task every minute, a run's checks ending seconds after it
        // started: each run takes the invoice again (every minute, never every two).
        var invoice = await MakeAsync();
        _shop.Time.Advance(TimeSpan.FromMinutes(1));
        _shop.Gateway.Status = (stored, _) =>
        {
            _shop.Time.Advance(TimeSpan.FromSeconds(8));
            return Task.FromResult(SdkData.Open(stored.ExpiresAt));
        };
        for (var run = 1; run <= 4; run++)
        {
            var started = _shop.Time.UtcNow;
            await RunAsync();
            Assert.That(_shop.Gateway.Statuses, Is.EqualTo(run), $"run {run}");
            _shop.Time.Advance(started + TimeSpan.FromMinutes(1) - _shop.Time.UtcNow);
        }
        Assert.That(_shop.Database.Invoices().Single(stored => stored.Id == invoice.Id).Final, Is.False);
    }

    [Test]
    public void Checks_every_five_minutes_until_the_status_is_final()
    {
        var expiresAt = _nowUnix - 3600;
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now), Is.EqualTo(_now.AddMinutes(5)));
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(3, 4)]
    [TestCase(5, 16)]
    [TestCase(6, 30)]
    [TestCase(40, 30)]
    public void Checks_less_often_after_failures(int errors, int minutes)
    {
        var expiresAt = _nowUnix - 3600;
        Assert.That(PaymentRules.NextCheck(expiresAt, errors, _now), Is.EqualTo(_now.AddMinutes(minutes)));
    }

    private async Task<SattlemeInvoice> MakeAsync()
    {
        var order = _shop.AddOrder();
        return (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
    }

    [Test]
    public async Task A_run_checks_the_invoices_due_the_longest_due_first()
    {
        var first = await MakeAsync();
        var second = await MakeAsync();
        var later = await MakeAsync();
        _shop.Database.Change(first.Id, invoice => invoice.NextCheckOnUtc = _shop.Time.UtcNow.AddSeconds(-30));
        _shop.Database.Change(second.Id, invoice => invoice.NextCheckOnUtc = _shop.Time.UtcNow.AddSeconds(-90));
        _shop.Database.Change(later.Id, invoice => invoice.NextCheckOnUtc = _shop.Time.UtcNow.AddSeconds(30));
        var checkedReferences = new List<string>();
        _shop.Gateway.Status = (invoice, _) =>
        {
            checkedReferences.Add(invoice.Ref!);
            return Task.FromResult(SdkData.Open(invoice.ExpiresAt));
        };

        await RunAsync();

        Assert.That(checkedReferences, Is.EqualTo(new[] { second.Reference, first.Reference }));
        var stored = _shop.Database.Invoices().Single(invoice => invoice.Id == first.Id);
        Assert.That(stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval - SattlemeDefaults.RunSlack).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task A_run_checks_at_most_twenty_invoices()
    {
        for (var i = 0; i < SattlemeDefaults.TaskBatch + 3; i++)
        {
            await MakeAsync();
        }
        _shop.Time.Advance(TimeSpan.FromMinutes(1));

        await RunAsync();

        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(SattlemeDefaults.TaskBatch));
        await RunAsync();
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(SattlemeDefaults.TaskBatch + 3));
    }

    [Test]
    public async Task A_run_stops_checking_before_its_time_is_over()
    {
        for (var i = 0; i < 4; i++)
        {
            await MakeAsync();
        }
        _shop.Time.Advance(TimeSpan.FromMinutes(1));
        // Each check takes 9 s: the run stops once another one could end after its budget (45 s,
        // a check 20 s at most): after the third.
        _shop.Gateway.Status = (invoice, _) =>
        {
            _shop.Time.Advance(TimeSpan.FromSeconds(9));
            return Task.FromResult(SdkData.Open(invoice.ExpiresAt));
        };

        await RunAsync();

        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(3));
    }

    [Test]
    public async Task A_check_the_page_holds_is_left_for_a_minute()
    {
        var invoice = await MakeAsync();
        _shop.Time.Advance(TimeSpan.FromMinutes(1));
        _shop.Database.Change(invoice.Id, stored =>
        {
            stored.LeaseId = "the page's";
            stored.LeaseUntilUtc = _shop.Time.UtcNow.AddSeconds(30);
        });

        await RunAsync();

        Assert.That(_shop.Gateway.Statuses, Is.Zero);
        Assert.That(_shop.Database.Invoices().Single().NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval - SattlemeDefaults.RunSlack).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Gives_up_after_90_days_with_a_note()
    {
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Unconfirmed(24_500, 0));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _shop.Time.Advance(TimeSpan.FromDays(89));
        await RunAsync();
        Assert.That(_shop.Database.Invoices().Single().Final, Is.False);

        _shop.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        _shop.Gateway.Status = (_, _) => throw new NetworkException("down");
        await RunAsync();

        var stored = _shop.Database.Invoices().Single();
        Assert.That(stored.Final, Is.True);
        Assert.That(stored.Error, Is.EqualTo("abandoned"));
        Assert.That(stored.NextCheckOnUtc, Is.Null);
        Assert.That(_shop.Notes(order.Id), Has.Some.EqualTo(_shop.Texts.Format("Note.Abandoned", invoice.Reference, 90)));
        Assert.That(_shop.Logged(LogLevel.Warning), Has.Some.Contains(invoice.Reference).And.Some.Contains("90 days"));
        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        var statuses = _shop.Gateway.Statuses;
        await RunAsync();
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(statuses), "never checked again");
    }

    [Test]
    public async Task A_run_cancelled_checks_nothing_more()
    {
        await MakeAsync();
        _shop.Time.Advance(TimeSpan.FromMinutes(1));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await _shop.NewScope().Task.Run(null!, cancelled.Token);

        Assert.That(_shop.Gateway.Statuses, Is.Zero);
    }

    [Test]
    public void The_task_is_installed_to_run_every_minute()
    {
        // Smartstore's scheduler polls every minute (TaskScheduler), and its executor never starts a
        // task still running: a run's checks end within the minute.
        Assert.That(SattlemeDefaults.CheckTaskCron, Is.EqualTo("* * * * *"));
        var name = typeof(SattlemeCheckTask).GetCustomAttributes(typeof(TaskNameAttribute), false).Cast<TaskNameAttribute>().Single();
        Assert.That(name.Name, Is.EqualTo(nameof(SattlemeCheckTask)));
        Assert.That(SattlemeDefaults.TaskBudget, Is.LessThan(TimeSpan.FromSeconds(59)));
        Assert.That(SattlemeDefaults.CheckTimeout + SattlemeDefaults.LeaseMargin, Is.LessThan(TimeSpan.FromSeconds(60)));
    }

    private Task RunAsync()
    {
        return _shop.NewScope().Task.Run(null!, CancellationToken.None);
    }
}
