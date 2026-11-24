using Microsoft.Extensions.Logging;
using Moq;
using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;
using Smartstore.Core.Checkout.Orders;
using SmartstoreSattleme.Sattleme.Checkout;
using Order = Smartstore.Core.Checkout.Orders.Order;
using SdkPaymentStatus = SmartstoreSattleme.Sattleme.Checkout.PaymentStatus;
using ShopPaymentStatus = Smartstore.Core.Checkout.Payment.PaymentStatus;

namespace Sattleme.Bitcoin.Tests;

/// <summary>What the checks find, and what becomes of the order: every state the SDK reports, and its errors.</summary>
[TestFixture]
public sealed class PaymentCheckerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);
    private Shop _shop = null!;
    private Order _order = null!;
    private SattlemeInvoice _invoice = null!;

    [SetUp]
    public async Task SetUp()
    {
        _shop = new Shop();
        _order = _shop.AddOrder();
        _invoice = (await _shop.NewScope().Maker.MakeAsync(_order)).Invoice!;
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private void Reports(SdkPaymentStatus status)
    {
        _shop.Gateway.Status = (_, _) => Task.FromResult(status);
    }

    private Task<bool> CheckAsync()
    {
        return _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout);
    }

    private SattlemeInvoice Stored => _shop.Database.Invoices().Single(invoice => invoice.Id == _invoice.Id);

    private IList<string> Notes => _shop.Notes(_order.Id);

    private static string Sats(long sats)
    {
        return sats.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    }

    [Test]
    public async Task Checks_the_invoice_the_order_stored()
    {
        IInvoiceToCheck? checkedInvoice = null;
        _shop.Gateway.Status = (invoice, _) =>
        {
            checkedInvoice = invoice;
            return Task.FromResult(SdkData.Open(_invoice.ExpiresAt));
        };
        Assert.That(await CheckAsync(), Is.True);

        Assert.That(checkedInvoice!.Ref, Is.EqualTo(_invoice.Reference));
        Assert.That(_shop.Gateway.Keys, Has.All.EqualTo(SdkData.KEY));
    }

    [Test]
    public async Task Open_waits_for_the_payment()
    {
        Reports(SdkData.Open(_invoice.ExpiresAt));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("open"));
        Assert.That(Stored.Final, Is.False);
        Assert.That(Stored.CheckedOnUtc, Is.EqualTo(_shop.Time.UtcNow).Within(TimeSpan.FromSeconds(1)));
        Assert.That(Stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval).Within(TimeSpan.FromSeconds(1)));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        Assert.That(_shop.MarkedPaid, Is.Zero);
    }

    [Test]
    public async Task Open_with_a_rest_keeps_the_rest_to_show()
    {
        Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 20_000, 4_500));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("open"));
        Assert.That(Stored.Received, Is.EqualTo(20_000));
        var shown = StoredInvoice.Of(Stored)!;
        Assert.That(shown.RestSats, Is.EqualTo(4_500));
        Assert.That(shown.RestPaymentUri, Does.StartWith("lightning:"));
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Underpaid));
        Assert.That(_shop.MarkedPaid, Is.Zero);
    }

    [Test]
    public async Task Unconfirmed_waits_with_one_note()
    {
        Reports(SdkData.Unconfirmed(24_500, 1));
        await CheckAsync();
        Reports(SdkData.Unconfirmed(24_500, 2));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("unconfirmed"));
        Assert.That(Stored.Final, Is.False);
        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(Notes.Count(note => note == _shop.Texts.Format("Note.Unconfirmed", _invoice.Reference, Sats(24_500), Sats(24_500), 1)), Is.EqualTo(1));
        Assert.That(Notes, Has.None.Contains(SdkData.TXID), "no transaction in a note");
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Unconfirmed));
    }

    private static IEnumerable<TestCaseData> Paid()
    {
        // What came, how, against what the invoice asked: a Lightning payment is the invoice's own
        // amount (its status has no received), never "0 sats".
        yield return new TestCaseData(SdkData.PaidLightning(), "Note.PaidLightning", new object[] { Sats(24_500) }, "paid: lightning");
        yield return new TestCaseData(SdkData.PaidOnchain(24_500), "Note.PaidOnchain", new object[] { Sats(24_500), Sats(24_500), 1 }, "paid: on-chain");
        yield return new TestCaseData(SdkData.PaidMixed(24_500), "Note.PaidMixed", new object[] { Sats(24_500), Sats(24_500), 1, 1 }, "paid: on-chain and lightning");
    }

    [TestCaseSource(nameof(Paid))]
    public async Task Paid_marks_the_order_paid_through_Smartstore_with_the_reference(SdkPaymentStatus status, string note, object[] arguments, string result)
    {
        Reports(status);
        Assert.That(await CheckAsync(), Is.True);

        var order = _shop.Order(_order.Id);
        Assert.That(order.PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
        Assert.That(order.OrderStatus, Is.EqualTo(OrderStatus.Complete), "Smartstore completes a paid order with nothing to ship");
        Assert.That(order.CaptureTransactionId, Is.EqualTo(_invoice.Reference));
        Assert.That(order.CaptureTransactionResult, Is.EqualTo(result));
        Assert.That(order.HasNewPaymentNotification, Is.False, "nothing to settle by hand");
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format(note, [_invoice.Reference, .. arguments])));
        Assert.That(Notes, Has.None.Contains(" 0 sats"));
        Assert.That(Notes, Has.None.Contains("Note.PaidShort").And.None.Contains("Note.PaidOver"));
        Assert.That(Stored.Final, Is.True);
        Assert.That(Stored.NextCheckOnUtc, Is.Null);
        Assert.That(PaymentRules.PhaseOf(order, Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Paid));
    }

    [Test]
    public async Task Completes_the_order_once_whatever_checks_it_again()
    {
        Reports(SdkData.PaidLightning());
        await CheckAsync();
        Assert.That(await CheckAsync(), Is.False, "a final invoice is never checked again");
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, SattlemeDefaults.PageInterval, _timeout), Is.False);
        _shop.Time.Advance(TimeSpan.FromHours(1));
        await _shop.NewScope().Task.Run(null!, CancellationToken.None);

        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(1));
    }

    [Test]
    public async Task Completes_the_order_once_when_the_page_and_the_background_check_at_once()
    {
        var release = new TaskCompletionSource();
        _shop.Gateway.Status = async (_, _) =>
        {
            await release.Task;
            return SdkData.PaidLightning();
        };
        var checks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout))).ToList();
        await InvoiceMakerTests.WaitUntil(() => _shop.Gateway.Statuses >= 1);
        await Task.Delay(300);
        release.SetResult();
        var ran = await Task.WhenAll(checks);

        Assert.That(ran.Count(done => done), Is.EqualTo(1), "one check holds the invoice at a time");
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(1));
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
    }

    [Test]
    public async Task A_check_that_outlived_its_lease_decides_nothing()
    {
        var first = _shop.NewScope();
        var paused = new TaskCompletionSource();
        var resume = new TaskCompletionSource();
        _shop.Gateway.Status = async (_, _) =>
        {
            paused.TrySetResult();
            await resume.Task;
            return SdkData.Expired();
        };
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        var late = first.Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout);
        await paused.Task;
        // Its lease ends; another check takes the invoice, and finds it paid.
        _shop.Time.Advance(_timeout + SattlemeDefaults.LeaseMargin + TimeSpan.FromSeconds(1));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        Assert.That(await CheckAsync(), Is.True);
        resume.SetResult();
        await late;

        Assert.That(Stored.State, Is.EqualTo("paid"));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
        Assert.That(Notes, Has.None.EqualTo(_shop.Texts.Format("Note.Expired", _invoice.Reference)));
    }

    [Test]
    public async Task Expired_ends_the_invoice_with_a_note()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        Reports(SdkData.Expired());
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("expired"));
        Assert.That(Stored.Final, Is.True);
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.Expired", _invoice.Reference)));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        Assert.That(_shop.Order(_order.Id).HasNewPaymentNotification, Is.False);
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Expired));
        Assert.That(PaymentRules.MayBePaid(Stored), Is.False);
    }

    [Test]
    public async Task Expired_with_something_received_notes_what_reached_the_wallet_and_tells_the_merchant()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        Reports(SdkData.Expired(5_000));
        await CheckAsync();

        Assert.That(Stored.Final, Is.True);
        Assert.That(Stored.Received, Is.EqualTo(5_000));
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.Underpaid", _invoice.Reference, Sats(5_000), Sats(24_500))));
        Assert.That(_shop.Logged(LogLevel.Warning), Has.Some.Contains("underpaid"));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        Assert.That(_shop.Order(_order.Id).HasNewPaymentNotification, Is.True, "the badge of Smartstore's order list");
    }

    [Test]
    public async Task Expired_reported_before_the_end_of_the_grace_period_is_not_final()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(60));
        Reports(SdkData.Expired());
        await CheckAsync();

        Assert.That(Stored.Final, Is.False, "a payment of its last seconds may still be found");
        Assert.That(PaymentRules.MayBePaid(Stored), Is.True);
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Ended));
        Assert.That(Notes, Has.None.EqualTo(_shop.Texts.Format("Note.Expired", _invoice.Reference)));

        _shop.Time.Advance(TimeSpan.FromSeconds(61));
        await CheckAsync();
        Assert.That(Stored.Final, Is.True);
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Expired));
    }

    [Test]
    public async Task Compares_the_invoice_with_what_the_order_still_owes()
    {
        // 30.00 CHF, 5.50 of it refunded: the order owes 24.50, the invoice's price.
        var order = _shop.AddOrder(30m, stored => stored.RefundedAmount = 5.50m);
        var made = await _shop.NewScope().Maker.MakeAsync(order);
        Assert.That(_shop.Gateway.Orders.Last().Fiat!.Amount, Is.EqualTo("24.50"));
        Reports(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(made.Invoice!.Id, TimeSpan.Zero, _timeout);

        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
    }

    [Test]
    public async Task Notes_a_payment_that_received_another_amount_than_the_invoice_s()
    {
        Reports(SdkData.PaidOnchain(24_400));
        await CheckAsync();

        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid), "the merchant's tolerance accepted it");
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidOnchain", _invoice.Reference, Sats(24_400), Sats(24_500), 1)));
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidShort", _invoice.Reference, Sats(100))));
    }

    [Test]
    public async Task Notes_an_overpayment()
    {
        Reports(SdkData.PaidOnchain(30_000));
        await CheckAsync();

        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidOnchain", _invoice.Reference, Sats(30_000), Sats(24_500), 1)));
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidOver", _invoice.Reference, Sats(5_500))));
    }

    [Test]
    public async Task Completes_an_order_the_merchant_accepted_at_what_it_received_as_the_tolerance_does()
    {
        // 15 000 of 24 500 sats came on-chain: more short than any tolerance. The merchant accepted
        // the order at that in its app (SPEC.md §31): being confirmed, then paid, received below sats.
        Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 15_000, 9_500));
        await CheckAsync();
        Reports(SdkData.Unconfirmed(15_000, 1));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("unconfirmed"));
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Unconfirmed));

        Reports(SdkData.PaidOnchain(15_000));
        Assert.That(await CheckAsync(), Is.True);

        var order = _shop.Order(_order.Id);
        Assert.That(order.PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid), "never left pending for the amount alone");
        Assert.That(order.CaptureTransactionId, Is.EqualTo(_invoice.Reference));
        Assert.That(order.HasNewPaymentNotification, Is.False, "nothing for the merchant to settle");
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        // Its notes: made, being confirmed, paid, and how much came (the tolerance's note); no
        // other total, no underpayment.
        Assert.That(Notes.Where(note => note.Contains(_invoice.Reference, StringComparison.Ordinal)).Skip(1), Is.EqualTo(new[]
        {
            _shop.Texts.Format("Note.Unconfirmed", _invoice.Reference, Sats(15_000), Sats(24_500), 1),
            _shop.Texts.Format("Note.PaidOnchain", _invoice.Reference, Sats(15_000), Sats(24_500), 1),
            _shop.Texts.Format("Note.PaidShort", _invoice.Reference, Sats(9_500)),
        }));
        Assert.That(Stored.Final, Is.True);
        Assert.That(PaymentRules.PhaseOf(order, Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Paid));
    }

    [Test]
    public async Task Notes_nothing_more_when_the_exact_amount_came()
    {
        Reports(SdkData.PaidOnchain(24_500));
        await CheckAsync();

        Assert.That(Notes.Count(note => note.Contains(_invoice.Reference, StringComparison.Ordinal)), Is.EqualTo(2), "made, then paid");
    }

    [Test]
    public async Task A_total_changed_since_the_invoice_keeps_the_order_pending_with_a_note_and_tells_the_merchant()
    {
        // The admin edited the order's totals (Admin → Orders → Edit), then the old amount came.
        _shop.Edit(_order.Id, order => order.OrderTotal = 30m);
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        var order = _shop.Order(_order.Id);
        Assert.That(order.PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        Assert.That(order.OrderStatus, Is.EqualTo(OrderStatus.Pending), "never completed");
        Assert.That(order.HasNewPaymentNotification, Is.True);
        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidOtherTotal", _invoice.Reference, Sats(24_500), "24.50 CHF", "30.00 CHF")));
        Assert.That(_shop.Logged(LogLevel.Warning), Has.Some.Contains(_invoice.Reference).And.Some.Contains("stays pending"));
        Assert.That(Stored.Final, Is.True, "never completed later on its own");
        Assert.That(PaymentRules.PhaseOf(order, Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Review));
    }

    [Test]
    public async Task A_primary_currency_changed_since_the_invoice_keeps_the_order_pending()
    {
        _shop.PrimaryCurrency = "EUR";
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidOtherTotal", _invoice.Reference, Sats(24_500), "24.50 CHF", "24.50 EUR")));
    }

    [Test]
    public async Task A_payment_of_a_cancelled_order_leaves_it_cancelled_with_a_note_and_tells_the_merchant()
    {
        // Smartstore has no "on hold", and nothing takes an order out of Cancelled.
        _shop.Edit(_order.Id, order => order.OrderStatus = OrderStatus.Cancelled);
        Reports(SdkData.PaidOnchain(24_500));
        await CheckAsync();

        var order = _shop.Order(_order.Id);
        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(order.OrderStatus, Is.EqualTo(OrderStatus.Cancelled));
        Assert.That(order.PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        Assert.That(order.HasNewPaymentNotification, Is.True);
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidCancelled", _invoice.Reference, Sats(24_500))));
        Assert.That(_shop.Logged(LogLevel.Warning), Has.Some.Contains(_invoice.Reference).And.Some.Contains("cancelled"));
        Assert.That(Stored.Final, Is.True);
        Assert.That(PaymentRules.PhaseOf(order, Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Review));
    }

    [Test]
    public async Task A_payment_of_an_order_deleted_meanwhile_is_noted_on_it()
    {
        // Smartstore's admin deletes an order softly (Order.Deleted): its notes stay.
        _shop.Edit(_order.Id, order => order.Deleted = true);
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(_shop.Order(_order.Id).HasNewPaymentNotification, Is.True);
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidCancelled", _invoice.Reference, Sats(24_500))));
    }

    [Test]
    public async Task A_payment_of_an_order_removed_from_the_database_is_logged()
    {
        using (var db = _shop.Database.NewContext())
        {
            db.OrderNotes.RemoveRange(db.OrderNotes.Where(note => note.OrderId == _order.Id));
            db.Orders.Remove(db.Orders.Single(order => order.Id == _order.Id));
            db.SaveChanges();
        }
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(Stored.Final, Is.True);
        Assert.That(_shop.Logged(LogLevel.Error), Has.Some.Contains(_invoice.Reference).And.Some.Contains("no longer exists"));
    }

    [Test]
    public async Task A_payment_of_an_order_paid_otherwise_is_noted()
    {
        _shop.Edit(_order.Id, order =>
        {
            order.PaymentStatus = ShopPaymentStatus.Paid;
            order.CaptureTransactionId = "another-gateway-123";
        });
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(Notes, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidTwice", _invoice.Reference, Sats(24_500))));
        Assert.That(_shop.Order(_order.Id).HasNewPaymentNotification, Is.True);
        Assert.That(Stored.Final, Is.True);
    }

    [Test]
    public async Task A_completion_that_failed_in_Smartstore_is_done_again_later()
    {
        var scope = _shop.NewScope();
        scope.OrderProcessing.Setup(s => s.MarkOrderAsPaidAsync(It.IsAny<Order>())).ThrowsAsync(new InvalidOperationException("database down"));
        Reports(SdkData.PaidLightning());
        Assert.That(await scope.Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout), Is.True);

        Assert.That(Stored.Final, Is.False);
        Assert.That(Stored.Error, Is.EqualTo("apply"));
        Assert.That(_shop.Logged(LogLevel.Error), Has.Some.Contains("apply"));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));

        // The completion it claimed may still be under way for a while; then the next check does it.
        await CheckAsync();
        Assert.That(_shop.MarkedPaid, Is.Zero);
        _shop.Time.Advance(SattlemeDefaults.CompletionStale + TimeSpan.FromSeconds(1));
        await CheckAsync();
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        Assert.That(Stored.Final, Is.True);
    }

    [Test]
    public async Task A_completion_cut_short_after_the_order_was_marked_paid_is_not_done_twice()
    {
        // Smartstore marked the order paid, then the process stopped before the invoice was final.
        _shop.Edit(_order.Id, order =>
        {
            order.PaymentStatus = ShopPaymentStatus.Paid;
            order.CaptureTransactionId = _invoice.Reference;
        });
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(Notes, Has.None.EqualTo(_shop.Texts.Format("Note.PaidTwice", _invoice.Reference, Sats(24_500))));
        Assert.That(Notes.Count(note => note == _shop.Texts.Format("Note.PaidLightning", _invoice.Reference, Sats(24_500))), Is.EqualTo(1), "the notes of its payment, written now");
        Assert.That(_shop.Order(_order.Id).HasNewPaymentNotification, Is.False);
        Assert.That(_shop.Logged(LogLevel.Warning), Has.Some.Contains("stopped before its end"));
        Assert.That(Stored.Final, Is.True);
    }

    [Test]
    public async Task A_completion_whose_order_was_saved_paid_before_Smartstore_failed_is_finished_by_a_later_check()
    {
        // Smartstore saves the order paid, then completes an order with nothing to ship and sends
        // its e-mails: those throw (a background check without what they need, for one).
        _shop.AfterMarkPaid = _ => throw new InvalidOperationException("the order completed e-mail failed");
        Reports(SdkData.PaidOnchain(24_400));
        await CheckAsync();

        Assert.That(Stored.Final, Is.False, "checked again");
        Assert.That(Stored.Error, Is.EqualTo("apply"));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
        Assert.That(Notes, Has.None.Contains("paid on-chain"), "its notes not written yet");

        _shop.AfterMarkPaid = null;
        await CheckAsync();
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.EqualTo(1), "marked paid once");
        Assert.That(Stored.Final, Is.True);
        Assert.That(Notes.Count(note => note == _shop.Texts.Format("Note.PaidOnchain", _invoice.Reference, Sats(24_400), Sats(24_500), 1)), Is.EqualTo(1));
        Assert.That(Notes.Count(note => note == _shop.Texts.Format("Note.PaidShort", _invoice.Reference, Sats(100))), Is.EqualTo(1));
        Assert.That(Notes, Has.None.EqualTo(_shop.Texts.Format("Note.PaidTwice", _invoice.Reference, Sats(24_400))), "never a false paid twice");
        Assert.That(_shop.Order(_order.Id).HasNewPaymentNotification, Is.False);
    }

    [Test]
    public async Task What_a_failed_completion_left_unsaved_is_never_saved_by_a_later_call()
    {
        // Smartstore's completion changed the order, then threw before saving it: the same context
        // (the scheduled task checks several invoices in one) saves something else afterwards.
        var scope = _shop.NewScope();
        _shop.OnMarkPaid = order =>
        {
            order.OrderStatus = OrderStatus.Processing;
            order.PaymentStatus = ShopPaymentStatus.Paid;
            throw new InvalidOperationException("reward points failed");
        };
        Reports(SdkData.PaidLightning());
        await scope.Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout);
        var other = _shop.AddOrder(10m);
        await scope.Notes.AddAsync(other.Id, "Note.Expired", "ss-other");

        var order = _shop.Order(_order.Id);
        Assert.That(order.PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending));
        Assert.That(order.OrderStatus, Is.EqualTo(OrderStatus.Pending));
        Assert.That(Stored.Final, Is.False);
    }

    [Test]
    public async Task Completes_an_order_the_request_s_context_already_tracks_as_the_database_holds_it()
    {
        // The status request loaded the order before the check (SattlemePaymentController.Status):
        // the check marks paid the order as stored now, not that copy.
        var scope = _shop.NewScope();
        var tracked = scope.Db.Orders.Single(order => order.Id == _order.Id);
        _shop.Edit(_order.Id, order => order.OrderTotal = 30m);
        Reports(SdkData.PaidLightning());
        await scope.Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout);

        Assert.That(tracked.OrderTotal, Is.EqualTo(30m));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Pending), "the total changed: settled by hand");
    }

    private static IEnumerable<TestCaseData> CheckErrors()
    {
        yield return new TestCaseData(new NetworkException("down"), "network", LogLevel.Warning);
        yield return new TestCaseData(new RateException("no rate"), "rate", LogLevel.Warning);
        yield return new TestCaseData(new InvalidInvoiceException("damaged"), "invalid_invoice", LogLevel.Warning);
        yield return new TestCaseData(new RevokedException(), "revoked", LogLevel.Warning);
        yield return new TestCaseData(new ChannelNotFoundException(false), "channel_not_found", LogLevel.Warning);
        yield return new TestCaseData(new OperationCanceledException(), "timeout", LogLevel.Warning);
        yield return new TestCaseData(new InvalidOperationException("a bug"), "error", LogLevel.Error);
    }

    [TestCaseSource(nameof(CheckErrors))]
    public async Task A_failed_check_is_counted_and_done_again_later(Exception error, string code, LogLevel level)
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(30));
        _shop.Gateway.Status = (_, _) => throw error;
        await CheckAsync();

        Assert.That(Stored.Error, Is.EqualTo(code));
        Assert.That(Stored.Errors, Is.EqualTo(1));
        Assert.That(Stored.Final, Is.False);
        Assert.That(Stored.LeaseId, Is.Null);
        Assert.That(Stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval).Within(TimeSpan.FromSeconds(1)));
        Assert.That(_shop.Logged(level), Has.Exactly(1).Contains(code));

        // Failing again: later each time, logged once.
        await CheckAsync();
        Assert.That(Stored.Errors, Is.EqualTo(2));
        Assert.That(Stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + (2 * SattlemeDefaults.OpenInterval)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(_shop.Logged(level), Has.Exactly(1).Contains(code));
        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(_shop.AllLogged(), Does.Not.Contain(SdkData.KEY));

        Reports(SdkData.Expired());
        await CheckAsync();
        Assert.That(Stored.Errors, Is.Zero);
        Assert.That(Stored.Error, Is.Empty);
        Assert.That(Stored.Final, Is.True);
    }

    [Test]
    public async Task Checks_at_most_every_few_seconds_for_the_payment_page()
    {
        var page = SattlemeDefaults.PageInterval;
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.True);
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.False);
        _shop.Time.Advance(page - TimeSpan.FromSeconds(1));
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.False);
        _shop.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.True);

        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(2));
    }

    [Test]
    public async Task Never_checks_without_a_key()
    {
        await _shop.NewScope().Keys.RemoveAsync(0);
        await CheckAsync();

        Assert.That(_shop.Gateway.Statuses, Is.Zero);
        Assert.That(Stored.Error, Is.EqualTo("no_key"));
    }

    [Test]
    public async Task Checks_with_the_key_of_the_order_s_store()
    {
        const string storeKey = "sattleme_ck_qEVejHuCZpF4XMmBSw5p7iZYxCtm20XxzT1fnJ9VB8o.uPTAz1d8bbQaw2Jx22Wwrga2vI2F8allY5NrPC3Aogk.cmVsYXkyLmV4YW1wbGU";
        await _shop.NewScope().Keys.SaveAsync(storeKey, TestDatabase.StoreId);
        await CheckAsync();

        Assert.That(_shop.Gateway.Keys.Last(), Is.EqualTo(storeKey));
    }
}
