using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;
using Smartstore.Core.Checkout.Orders;
using SmartstoreSattleme.Sattleme.Checkout;
using Order = Smartstore.Core.Checkout.Orders.Order;
using ShopPaymentStatus = Smartstore.Core.Checkout.Payment.PaymentStatus;

namespace Sattleme.Bitcoin.Tests;

/// <summary>Making an order's invoice: the reference, the lock, one payable invoice per order, the SDK's answers.</summary>
[TestFixture]
public sealed partial class InvoiceMakerTests
{
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

    [GeneratedRegex("^ss-[0-9a-f]{32}$")]
    private static partial Regex ReferenceFormat();

    private Task<MakeResult> MakeAsync(Order order)
    {
        return _shop.NewScope().Maker.MakeAsync(order);
    }

    [Test]
    public async Task Makes_the_invoice_of_the_order_placed()
    {
        var order = _shop.AddOrder();
        var made = await MakeAsync(order);

        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(made.Paid, Is.False);
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        var asked = _shop.Gateway.Orders.Single();
        Assert.That(asked.Ref, Does.Match(ReferenceFormat()));
        Assert.That(asked.Fiat, Is.EqualTo(new Price("24.50", "CHF")));
        Assert.That(asked.DurationSeconds, Is.EqualTo(15 * 60));
        Assert.That(asked.Note, Is.EqualTo("Sunrise Bakery · " + order.OrderNumber));
        Assert.That(asked.Sats, Is.Null);
        Assert.That(asked.Checkout, Is.False, "never the merchant's checkout link: the shop's own page");

        var stored = _shop.Database.Invoices().Single();
        Assert.That(stored.Reference, Is.EqualTo(asked.Ref));
        Assert.That(stored.OrderId, Is.EqualTo(order.Id));
        Assert.That(stored.PriceAmount, Is.EqualTo("24.50"));
        Assert.That(stored.PriceCurrency, Is.EqualTo("CHF"));
        Assert.That(stored.InvoiceJson, Is.Not.Null);
        Assert.That(Invoice.FromJson(stored.InvoiceJson!).Ref, Is.EqualTo(asked.Ref), "the SDK's own JSON");
        Assert.That(stored.ExpiresAt, Is.EqualTo(_shop.Time.UnixNow + 900));
        Assert.That(stored.NextCheckOnUtc, Is.Not.Null);
        Assert.That(stored.Error, Is.Empty);
        var record = _shop.Database.Records().Single();
        Assert.That(record.OrderId, Is.EqualTo(order.Id));
        Assert.That(record.OrderGuid, Is.EqualTo(order.OrderGuid));
        Assert.That(record.PageToken, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(record.LockId, Is.Null, "the lock is released");
        Assert.That(_shop.Notes(order.Id), Has.Some.EqualTo(_shop.Texts.Format("Note.Created", stored.Reference, "24,500")));
    }

    [Test]
    public async Task Stores_the_reference_before_the_SDK_is_asked()
    {
        List<SattlemeInvoice>? before = null;
        _shop.Gateway.Create = (order, _) =>
        {
            before = _shop.Database.Invoices();
            return Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow));
        };
        await MakeAsync(_shop.AddOrder());

        Assert.That(before, Has.Count.EqualTo(1));
        Assert.That(before![0].Reference, Is.EqualTo(_shop.Gateway.Orders.Single().Ref));
        Assert.That(before[0].InvoiceJson, Is.Null);
    }

    [Test]
    public async Task Holds_the_order_locked_in_the_database_while_the_SDK_makes_the_invoice()
    {
        SattlemeOrder? during = null;
        _shop.Gateway.Create = (order, _) =>
        {
            during = _shop.Database.Records().Single();
            return Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow));
        };
        await MakeAsync(_shop.AddOrder());

        Assert.That(during!.LockId, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(during.LockedUntilUtc, Is.GreaterThan(_shop.Time.UtcNow));
        Assert.That(_shop.Database.Records().Single().LockId, Is.Null);
    }

    [Test]
    public async Task Makes_a_new_random_reference_for_each_order()
    {
        for (var i = 0; i < 5; i++)
        {
            await MakeAsync(_shop.AddOrder());
        }

        var references = _shop.Database.Invoices().Select(invoice => invoice.Reference).ToList();
        Assert.That(references, Is.Unique);
        Assert.That(references, Has.All.Match(ReferenceFormat()));
    }

    [Test]
    public async Task Two_requests_at_once_get_one_invoice()
    {
        var release = new TaskCompletionSource();
        _shop.Gateway.Create = async (order, _) =>
        {
            await release.Task;
            return SdkData.InvoiceFor(order, _shop.Time.UnixNow);
        };
        var order = _shop.AddOrder();
        var first = Task.Run(() => MakeAsync(order));
        var second = Task.Run(() => MakeAsync(order));
        // Both are under way: one holds the lock and waits for the SDK, the other waits for the lock.
        await WaitUntil(() => _shop.Gateway.Creates == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        Assert.That(results.Select(result => result.Invoice?.Id), Is.All.Not.Null);
        Assert.That(results[0].Invoice!.Id, Is.EqualTo(results[1].Invoice!.Id));
        Assert.That(_shop.Database.Invoices(), Has.Count.EqualTo(1));
        Assert.That(_shop.Database.Records(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Hands_out_the_order_s_payable_invoice_again()
    {
        var order = _shop.AddOrder();
        var first = await MakeAsync(order);
        var again = await MakeAsync(_shop.Order(order.Id));

        Assert.That(again.Invoice!.Id, Is.EqualTo(first.Invoice!.Id));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
    }

    [Test]
    public async Task Makes_no_second_invoice_while_one_may_be_paid()
    {
        var order = _shop.AddOrder();
        var first = await MakeAsync(order);
        _shop.Edit(order.Id, stored => stored.OrderTotal = 30m);
        var other = await MakeAsync(_shop.Order(order.Id));

        Assert.That(other.Invoice, Is.Null);
        Assert.That(other.Refusal, Is.EqualTo("Error.Pending"));
        Assert.That(other.RefusalArguments.Single(), Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(first.Invoice!.ExpiresAt + SattlemeDefaults.GraceSeconds).UtcDateTime));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));

        // Its grace period over, its status unknown yet: still one that may be paid if something came.
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Database.Change(first.Invoice.Id, invoice =>
        {
            invoice.State = "unconfirmed";
            invoice.Received = 24_500;
        });
        var confirming = await MakeAsync(_shop.Order(order.Id));
        Assert.That(confirming.Refusal, Is.EqualTo("Error.Confirming"));
    }

    [Test]
    public async Task Makes_a_new_invoice_once_the_last_one_ended_unpaid()
    {
        var order = _shop.AddOrder();
        var first = await MakeAsync(order);
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.That(await _shop.NewScope().Checker.CheckAsync(first.Invoice!.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5)), Is.True);

        var second = await MakeAsync(_shop.Order(order.Id));

        Assert.That(second.Invoice, Is.Not.Null);
        Assert.That(second.Invoice!.Id, Is.Not.EqualTo(first.Invoice.Id));
        Assert.That(second.Invoice.Reference, Is.Not.EqualTo(first.Invoice.Reference));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(2));
        Assert.That(_shop.Notes(order.Id), Has.Some.EqualTo(_shop.Texts.Format("Note.Created", second.Invoice.Reference, "24,500")));
    }

    [Test]
    public async Task Makes_no_new_invoice_when_the_time_is_over_until_a_check_finds_it_expired()
    {
        var order = _shop.AddOrder();
        var first = await MakeAsync(order);
        _shop.Time.Advance(TimeSpan.FromMinutes(20));

        var same = await MakeAsync(_shop.Order(order.Id));
        Assert.That(same.Invoice!.Id, Is.EqualTo(first.Invoice!.Id), "the same price: that invoice, still being checked");
        _shop.Edit(order.Id, stored => stored.OrderTotal = 30m);
        var other = await MakeAsync(_shop.Order(order.Id));
        Assert.That(other.Refusal, Is.EqualTo("Error.Checking"));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));

        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        await _shop.NewScope().Checker.CheckAsync(first.Invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var again = await MakeAsync(_shop.Order(order.Id));
        Assert.That(again.Invoice, Is.Not.Null);
        Assert.That(_shop.Gateway.Orders.Last().Fiat!.Amount, Is.EqualTo("30.00"));
    }

    [Test]
    public async Task Makes_no_new_invoice_after_an_underpayment()
    {
        var order = _shop.AddOrder();
        var first = await MakeAsync(order);
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired(5_000));
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await _shop.NewScope().Checker.CheckAsync(first.Invoice!.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var second = await MakeAsync(_shop.Order(order.Id));

        Assert.That(second.Refusal, Is.EqualTo("Error.Underpaid"));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
    }

    [Test]
    public async Task Asks_for_the_same_reference_again_after_a_failed_try()
    {
        var calls = 0;
        _shop.Gateway.Create = (order, _) => ++calls == 1
            ? throw new NetworkException("relays unreachable")
            : Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow));
        var order = _shop.AddOrder();
        var failed = await MakeAsync(order);
        Assert.That(failed.Refusal, Is.EqualTo("Error.TryAgain"));
        Assert.That(failed.ErrorCode, Is.EqualTo("network"));
        Assert.That(_shop.Database.Invoices().Single().Error, Is.EqualTo("network"));

        var made = await MakeAsync(_shop.Order(order.Id));

        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(_shop.Gateway.Orders.Select(asked => asked.Ref).Distinct().Count(), Is.EqualTo(1));
        Assert.That(_shop.Database.Invoices(), Has.Count.EqualTo(1));
        Assert.That(_shop.Database.Invoices().Single().Error, Is.Empty);
    }

    [Test]
    public async Task Takes_the_invoice_the_SDK_made_for_an_earlier_try_InvoicePending()
    {
        Invoice? earlier = null;
        _shop.Gateway.Create = (order, _) =>
        {
            earlier = SdkData.InvoiceFor(order, _shop.Time.UnixNow - 60);
            throw new InvoicePendingException(SdkData.Declared(earlier), null);
        };
        var made = await MakeAsync(_shop.AddOrder());

        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(made.Paid, Is.False);
        var json = JsonNode.Parse(made.Invoice!.InvoiceJson!)!;
        Assert.That((string?)json["ref"], Is.EqualTo(earlier!.Ref));
        Assert.That(json["declaredAt"], Is.Not.Null, "the declared invoice, as the SDK gave it");
        Assert.That(made.Invoice.ExpiresAt, Is.EqualTo(earlier.ExpiresAt));
    }

    [Test]
    public async Task Stores_an_invoice_paid_already_and_completes_it_with_the_next_check_OrderPaid()
    {
        var order = _shop.AddOrder();
        _shop.Gateway.Create = (asked, _) =>
            throw new OrderPaidException(SdkData.Declared(SdkData.InvoiceFor(asked, _shop.Time.UnixNow)), (PaymentStatus.Paid)SdkData.PaidLightning());
        var made = await MakeAsync(order);
        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(made.Paid, Is.True);

        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(made.Invoice!.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
        Assert.That(_shop.Order(order.Id).CaptureTransactionId, Is.EqualTo(made.Invoice.Reference));
    }

    private static IEnumerable<TestCaseData> SdkErrors()
    {
        yield return new TestCaseData(new NetworkException("down"), "network", "Error.TryAgain", LogLevel.Warning);
        yield return new TestCaseData(new RateException("no rate"), "rate", "Error.TryAgain", LogLevel.Warning);
        yield return new TestCaseData(new OnchainUnavailableException(), "onchain_unavailable", "Error.TryAgain", LogLevel.Warning);
        yield return new TestCaseData(new ChannelNotFoundException(false), "channel_not_found", "Error.TryAgain", LogLevel.Warning);
        yield return new TestCaseData(new OperationCanceledException(), "timeout", "Error.TryAgain", LogLevel.Warning);
        yield return new TestCaseData(new InvalidOrderException("fiat", "above the merchant's maximum"), "invalid_order", "Error.NotPayable", LogLevel.Warning);
        yield return new TestCaseData(new UnsupportedCurrencyException("CHF", "coingecko"), "unsupported_currency", "Error.NotPayable", LogLevel.Warning);
        yield return new TestCaseData(new RevokedException(), "revoked", "Error.Unavailable", LogLevel.Error);
        yield return new TestCaseData(new ChannelNotFoundException(true), "channel_not_found", "Error.Unavailable", LogLevel.Error);
        yield return new TestCaseData(new InvalidOptionException("relayTimeoutMs", "out of range"), "invalid_option", "Error.Unavailable", LogLevel.Error);
        yield return new TestCaseData(new CheckoutUnavailableException(), "checkout_unavailable", "Error.Unavailable", LogLevel.Error);
        yield return new TestCaseData(new InvalidOperationException("a bug"), "error", "Error.Unavailable", LogLevel.Error);
    }

    [TestCaseSource(nameof(SdkErrors))]
    public async Task Refuses_with_a_message_safe_to_show_when_the_SDK_fails(Exception error, string code, string refusal, LogLevel level)
    {
        _shop.Gateway.Create = (_, _) => throw error;
        var order = _shop.AddOrder();
        var made = await MakeAsync(order);

        Assert.That(made.Invoice, Is.Null);
        Assert.That(made.ErrorCode, Is.EqualTo(code));
        Assert.That(made.Refusal, Is.EqualTo(refusal));
        Assert.That(_shop.Texts.Get(SattlemeDefaults.ResourcePrefix + refusal), Is.Not.Null.And.Not.Contains(error.Message));
        Assert.That(_shop.Logged(level), Has.Some.Contains(code));
        Assert.That(_shop.AllLogged(), Does.Not.Contain(SdkData.KEY), "never the key");
        Assert.That(_shop.Database.Invoices().Single().Error, Is.EqualTo(code));
        var reference = _shop.Database.Invoices().Single().Reference;
        Assert.That(_shop.Notes(order.Id), Has.Some.EqualTo(_shop.Texts.Format("Note.Failed", reference, _shop.Texts.Get(SattlemeDefaults.ResourcePrefix + "ErrorCode." + code)!)));
    }

    [Test]
    public async Task Never_shows_an_invoice_of_another_price()
    {
        _shop.Gateway.Create = (order, _) => Task.FromResult(Invoice.FromJson(SdkData.InvoiceJson(order.Ref, "2.45", "CHF", _shop.Time.UnixNow, _shop.Time.UnixNow + 900, order.Ref).ToJsonString()));
        var made = await MakeAsync(_shop.AddOrder());

        Assert.That(made.Invoice, Is.Null);
        Assert.That(made.ErrorCode, Is.EqualTo("mismatch"));
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Is.Null);
    }

    [Test]
    public async Task Refuses_without_a_key_that_can_be_read()
    {
        _shop.EncryptionKey = "1414213562373095";
        var made = await MakeAsync(_shop.AddOrder());

        Assert.That(made.Refusal, Is.EqualTo("Error.Unavailable"));
        Assert.That(made.ErrorCode, Is.EqualTo("no_key"));
        Assert.That(_shop.Gateway.Creates, Is.Zero);
    }

    [TestCase(24.50, "CHF", "24.50")]
    [TestCase(24.501, "CHF", "24.51")]
    [TestCase(0.001, "EUR", "0.01")]
    [TestCase(1500.2, "JPY", "1501")]
    [TestCase(10.0005, "BHD", "10.001")]
    public void Asks_for_the_total_in_the_primary_currency_rounded_up(decimal total, string currency, string amount)
    {
        _shop.PrimaryCurrency = currency;
        var price = _shop.NewScope().Maker.PriceOf(total);

        Assert.That(price, Is.EqualTo(new PriceToPay(amount, currency)));
    }

    [TestCase("XAU")]
    [TestCase("ABC")]
    public async Task Offers_no_price_in_a_currency_the_SDK_does_not_convert(string currency)
    {
        _shop.PrimaryCurrency = currency;
        var scope = _shop.NewScope();

        Assert.That(scope.Maker.PriceOf(10m), Is.Null);
        var made = await scope.Maker.MakeAsync(_shop.AddOrder());
        Assert.That(made.Refusal, Is.EqualTo("Error.NotPayable"));
        Assert.That(_shop.Gateway.Creates, Is.Zero);
    }

    [Test]
    public void Offers_no_price_for_a_total_of_zero()
    {
        Assert.That(_shop.NewScope().Maker.PriceOf(0m), Is.Null);
    }

    [Test]
    public async Task Asks_for_what_the_order_still_owes_once_gift_cards_reward_points_and_credit_paid_their_part()
    {
        // Smartstore's OrderTotal is what remains once gift cards, reward points and the credit
        // balance paid (OrderCalculationService); the refunded amount is what came back.
        var order = _shop.AddOrder(24.50m, stored => stored.RefundedAmount = 4.50m);
        await MakeAsync(order);

        Assert.That(_shop.Gateway.Orders.Single().Fiat, Is.EqualTo(new Price("20.00", "CHF")));
    }

    [TestCase(OrderStatus.Cancelled, ShopPaymentStatus.Pending)]
    [TestCase(OrderStatus.Pending, ShopPaymentStatus.Paid)]
    [TestCase(OrderStatus.Complete, ShopPaymentStatus.Paid)]
    [TestCase(OrderStatus.Pending, ShopPaymentStatus.Voided)]
    public async Task Refuses_an_invoice_for_an_order_not_pending_payment(OrderStatus orderStatus, ShopPaymentStatus paymentStatus)
    {
        var order = _shop.AddOrder(edit: stored =>
        {
            stored.OrderStatus = orderStatus;
            stored.PaymentStatus = paymentStatus;
        });

        var made = await MakeAsync(order);

        Assert.That(made.Refusal, Is.EqualTo("Error.NotPending"));
        Assert.That(_shop.Gateway.Creates, Is.Zero);
    }

    [Test]
    public async Task Refuses_an_invoice_for_an_order_of_another_payment_method_or_deleted()
    {
        var other = _shop.AddOrder(edit: stored => stored.PaymentMethodSystemName = "Payments.CashOnDelivery");
        var deleted = _shop.AddOrder(edit: stored => stored.Deleted = true);

        Assert.That((await MakeAsync(other)).Refusal, Is.EqualTo("Error.NotPending"));
        Assert.That((await MakeAsync(deleted)).Refusal, Is.EqualTo("Error.NotPending"));
        Assert.That(_shop.Gateway.Creates, Is.Zero);
    }

    [Test]
    public async Task Writes_the_time_to_pay_the_admin_set()
    {
        _shop.Configure(settings => settings.MinutesToPay = 42);
        await MakeAsync(_shop.AddOrder());

        Assert.That(_shop.Gateway.Orders.Single().DurationSeconds, Is.EqualTo(42 * 60));
    }

    [Test]
    public async Task Writes_the_time_to_pay_of_the_order_s_store()
    {
        _shop.Configure(settings => settings.MinutesToPay = 42);
        _shop.Configure(settings => settings.MinutesToPay = 5, TestDatabase.StoreId);
        await MakeAsync(_shop.AddOrder());

        Assert.That(_shop.Gateway.Orders.Single().DurationSeconds, Is.EqualTo(5 * 60));
    }

    [Test]
    public async Task Keeps_the_note_within_the_SDK_s_100_characters()
    {
        _shop.EditStore(store => store.Name = new string('é', 150) + "\u0007\n");
        var order = _shop.AddOrder();
        await MakeAsync(order);

        var note = _shop.Gateway.Orders.Single().Note!;
        Assert.That(note.EnumerateRunes().Count(), Is.LessThanOrEqualTo(100));
        Assert.That(note.EnumerateRunes().Any(System.Text.Rune.IsControl), Is.False);
        Assert.That(note, Does.EndWith("… · " + order.OrderNumber));
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held");
            }
            await Task.Delay(20);
        }
    }
}
