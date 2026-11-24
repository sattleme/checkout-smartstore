using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;
using SmartstoreSattleme.Sattleme.Checkout;
using Order = Smartstore.Core.Checkout.Orders.Order;

namespace Sattleme.Bitcoin.Tests;

/// <summary>What the payment page, the order's page and the admin's block show, in each state.</summary>
[TestFixture]
public sealed class PageModelTests
{
    private Shop _shop = null!;
    private Order _order = null!;
    private SattlemeInvoice _invoice = null!;
    private SattlemeOrder _record = null!;

    [SetUp]
    public async Task SetUp()
    {
        _shop = new Shop();
        _order = _shop.AddOrder();
        _invoice = (await _shop.NewScope().Maker.MakeAsync(_order)).Invoice!;
        _record = _shop.Database.Records().Single();
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private async Task Reports(PaymentStatus status)
    {
        _shop.Gateway.Status = (_, _) => Task.FromResult(status);
        _shop.Time.Advance(SattlemeDefaults.PageInterval);
        await _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private Task<Models.PaymentPageModel> PageAsync()
    {
        var invoice = _shop.Database.Invoices().Last(stored => stored.OrderId == _order.Id);
        return Task.FromResult(_shop.NewScope().Pages.PreparePaymentPage(_record, _shop.Order(_order.Id), invoice));
    }

    [Test]
    public async Task Open_shows_one_QR_code_the_wallet_link_the_invoice_the_address_the_amounts_and_the_time_left()
    {
        _shop.Time.Advance(TimeSpan.FromSeconds(75));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("open"));
        Assert.That(page.Uri, Is.EqualTo(StoredInvoice.Of(_invoice)!.PaymentUri));
        Assert.That(page.QrSvg, Does.StartWith("<svg").And.EndWith("</svg>"));
        Assert.That(page.Lightning, Is.EqualTo(StoredInvoice.Of(_invoice)!.Bolt11));
        Assert.That(page.Address, Is.EqualTo(SdkData.ADDRESS));
        Assert.That(page.HasOnchain, Is.True);
        Assert.That(page.ToPaySats, Is.EqualTo(24_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        Assert.That(page.ToPayBtc, Is.EqualTo("0.000245"));
        Assert.That(page.Price, Is.EqualTo("CHF 24.50"));
        Assert.That(page.SecondsLeft, Is.EqualTo(825));
        Assert.That(page.TimeLeft, Is.EqualTo("13:45"));
        Assert.That(page.PollSeconds, Is.EqualTo(3));
        Assert.That(page.OrderNumber, Is.EqualTo(_order.OrderNumber));
    }

    [Test]
    public async Task A_Lightning_only_invoice_shows_no_address()
    {
        _shop.Gateway.Create = (order, _) => Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow, onchain: false));
        var order = _shop.AddOrder(0.50m);
        var invoice = (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
        var record = _shop.Database.Records().Single(stored => stored.OrderId == order.Id);
        var page = _shop.NewScope().Pages.PreparePaymentPage(record, order, invoice);

        Assert.That(page.Uri, Does.StartWith("lightning:"));
        Assert.That(page.Address, Is.Null);
        Assert.That(page.HasOnchain, Is.False);
    }

    [Test]
    public async Task An_underpayment_shows_the_rest_to_pay()
    {
        await Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 20_000, 4_500));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("underpaid"));
        Assert.That(page.Uri, Is.EqualTo("lightning:lnbc4500n1rest"));
        Assert.That(page.Lightning, Is.EqualTo("lnbc4500n1rest"));
        Assert.That(page.Address, Is.Null, "the rest's URI says how to pay it");
        Assert.That(page.ToPaySats, Is.EqualTo(4_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        Assert.That(page.ReceivedSats, Is.EqualTo(20_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        Assert.That(page.QrSvg, Is.Not.Null);
    }

    [Test]
    public async Task An_underpayment_whose_rest_is_not_ready_says_so()
    {
        await Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 20_000, 4_500, restReady: false));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("underpaid"));
        Assert.That(page.Uri, Is.Null);
        Assert.That(page.QrSvg, Is.Null);
    }

    [Test]
    public async Task Short_of_the_amount_and_accepted_by_the_merchant_the_page_says_being_confirmed()
    {
        // The merchant's acceptance (SPEC.md §31), or its tolerance: received below sats, no rest.
        await Reports(SdkData.Unconfirmed(15_000, 1));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("unconfirmed"));
        Assert.That(page.Uri, Is.Null, "no rest to pay");
        Assert.That(page.QrSvg, Is.Null);
        Assert.That(page.ReceivedSats, Is.EqualTo(15_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
    }

    [Test]
    public async Task Being_confirmed_shows_no_QR_code_and_asks_less_often()
    {
        await Reports(SdkData.Unconfirmed(24_500, 2));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("unconfirmed"));
        Assert.That(page.Confirmations, Is.EqualTo(2));
        Assert.That(page.Uri, Is.Null);
        Assert.That(page.QrSvg, Is.Null);
        Assert.That(page.PollSeconds, Is.EqualTo(30));
    }

    [Test]
    public async Task Past_its_end_the_page_waits_for_the_final_status()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(16));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("ended"));
        Assert.That(page.QrSvg, Is.Null);
        Assert.That(page.CanMakeNew, Is.False, "not before a check found it expired");
        Assert.That(page.PollSeconds, Is.EqualTo(5));
    }

    [Test]
    public async Task Expired_unpaid_offers_a_new_invoice_underpaid_does_not()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await Reports(SdkData.Expired());
        var page = await PageAsync();
        Assert.That(page.Phase, Is.EqualTo("expired"));
        Assert.That(page.CanMakeNew, Is.True);
        Assert.That(page.HasReceived, Is.False);
        Assert.That(page.PollSeconds, Is.Null);

        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired(5_000));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var record = _shop.Database.Records().Single(stored => stored.OrderId == order.Id);
        var underpaid = _shop.NewScope().Pages.PreparePaymentPage(record, _shop.Order(order.Id), _shop.Database.Invoices().Single(stored => stored.Id == invoice.Id));
        Assert.That(underpaid.Phase, Is.EqualTo("expired"));
        Assert.That(underpaid.HasReceived, Is.True);
        Assert.That(underpaid.CanMakeNew, Is.False);
    }

    [Test]
    public async Task Paid_says_so_and_stops_asking()
    {
        await Reports(SdkData.PaidLightning());
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("paid"));
        Assert.That(page.PollSeconds, Is.Null);
        Assert.That(page.Uri, Is.Null);
    }

    [Test]
    public async Task The_version_changes_with_what_the_page_shows()
    {
        var open = (await PageAsync()).Version;
        await Reports(SdkData.Open(_invoice.ExpiresAt));
        Assert.That((await PageAsync()).Version, Is.EqualTo(open), "nothing new: no reload");
        await Reports(SdkData.Unconfirmed(24_500, 0));
        var unconfirmed = (await PageAsync()).Version;
        Assert.That(unconfirmed, Is.Not.EqualTo(open));
        await Reports(SdkData.Unconfirmed(24_500, 1));
        Assert.That((await PageAsync()).Version, Is.Not.EqualTo(unconfirmed));
    }

    [TestCase("javascript:alert(1)")]
    [TestCase("https://evil.example/")]
    [TestCase("data:text/html,<script>alert(1)</script>")]
    public async Task Never_links_to_anything_but_a_payment_URI(string uri)
    {
        _shop.Gateway.Create = (order, _) =>
        {
            var json = SdkData.InvoiceJson(order.Ref, order.Fiat!.Amount, order.Fiat.Currency, _shop.Time.UnixNow, _shop.Time.UnixNow + 900, order.Note!);
            json["paymentUri"] = uri;
            return Task.FromResult(Invoice.FromJson(json.ToJsonString()));
        };
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
        var record = _shop.Database.Records().Single(stored => stored.OrderId == order.Id);
        var page = _shop.NewScope().Pages.PreparePaymentPage(record, order, invoice);

        Assert.That(page.Uri, Is.Null);
        Assert.That(page.QrSvg, Is.Null);
    }

    [Test]
    public void The_order_s_page_links_to_the_payment_page_with_its_secret()
    {
        var model = _shop.NewScope().Pages.PrepareOrderPayment(_record, _shop.Order(_order.Id), _invoice);

        Assert.That(model.Phase, Is.EqualTo("open"));
        Assert.That(model.PayUrl, Does.Contain(_record.PageToken).And.Contain(_order.OrderGuid.ToString()));
    }

    [Test]
    public async Task The_admin_sees_the_reference_amounts_rate_address_transactions_proof_and_checks()
    {
        await Reports(SdkData.PaidMixed(24_500));
        var model = _shop.NewScope().Pages.PrepareAdminOrderPayment(_shop.Database.Invoices());

        var invoice = model.Invoices.Single();
        Assert.That(invoice.Reference, Is.EqualTo(_invoice.Reference));
        Assert.That(invoice.State, Is.EqualTo(_shop.Texts.Format("State.paid")));
        Assert.That(invoice.Amount, Does.Contain("BTC").And.Contain("24.50 CHF").And.Contain("sats"));
        Assert.That(invoice.Rate, Does.Contain("100000").And.Contain("coingecko"));
        Assert.That(invoice.Address, Is.EqualTo(SdkData.ADDRESS));
        Assert.That(invoice.AddressLink, Is.True);
        Assert.That(invoice.Txids, Is.EqualTo(new[] { SdkData.TXID }));
        Assert.That(invoice.TransactionsAtAddress, Is.False);
        Assert.That(invoice.Proofs.Select(proof => proof.Preimage), Is.EqualTo(new[] { SdkData.PREIMAGE }));
        // The payment hash a preimage proves: its SHA-256 (BOLT 11).
        Assert.That(invoice.Proofs.Single().PaymentHash, Is.EqualTo(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(SdkData.PREIMAGE)))));
        Assert.That(invoice.Received, Is.EqualTo(24_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " sats"));
        Assert.That(invoice.LastCheck, Is.Not.Null);
        Assert.That(invoice.NextCheck, Is.Null);
        Assert.That(invoice.Error, Is.Null);
        Assert.That(model.Warning, Is.Null);
    }

    [Test]
    public async Task The_admin_reads_errors_in_plain_words_and_is_warned_while_a_payment_may_come()
    {
        _shop.Gateway.Status = (_, _) => throw new NetworkException("down");
        await _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var model = _shop.NewScope().Pages.PrepareAdminOrderPayment(_shop.Database.Invoices());

        Assert.That(model.Invoices.Single().Error, Is.EqualTo(_shop.Texts.Format("ErrorCode.network")));
        Assert.That(model.Warning, Does.StartWith(_shop.Texts.Format("Admin.Warning.Payable", "")[..30]));

        Assert.That(_shop.NewScope().Pages.ErrorText("something_new"), Is.EqualTo("something_new"));
    }

    [Test]
    public async Task The_admin_sees_what_came_of_a_Lightning_payment_never_0()
    {
        await Reports(SdkData.PaidLightning());
        var invoice = _shop.NewScope().Pages.PrepareAdminOrderPayment(_shop.Database.Invoices()).Invoices.Single();

        Assert.That(invoice.Received, Is.EqualTo(24_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " sats"));
        Assert.That(invoice.TransactionsAtAddress, Is.False);
        Assert.That(invoice.Proofs.Single().PaymentHash, Has.Length.EqualTo(64));
    }

    [Test]
    public async Task The_admin_reads_an_underpayment_as_ended_underpaid_with_its_address_s_transactions_and_a_warning()
    {
        _shop.Time.Advance(TimeSpan.FromSeconds(_invoice.ExpiresAt - _shop.Time.UnixNow + SattlemeDefaults.GraceSeconds + 11));
        await Reports(SdkData.Expired(5_000));
        var model = _shop.NewScope().Pages.PrepareAdminOrderPayment(_shop.Database.Invoices());

        var invoice = model.Invoices.Single();
        Assert.That(invoice.State, Is.EqualTo(_shop.Texts.Format("State.expiredUnderpaid")));
        Assert.That(invoice.Txids, Is.Empty);
        Assert.That(invoice.TransactionsAtAddress, Is.True, "led to the deposit address's page");
        Assert.That(model.Warning, Is.EqualTo(_shop.Texts.Format("Admin.Warning.Underpaid",
            5_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), 24_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), _invoice.Reference)));
    }

    [Test]
    public async Task The_admin_reads_an_invoice_that_ended_unpaid_as_such()
    {
        _shop.Time.Advance(TimeSpan.FromSeconds(_invoice.ExpiresAt - _shop.Time.UnixNow + SattlemeDefaults.GraceSeconds + 11));
        await Reports(SdkData.Expired());
        var model = _shop.NewScope().Pages.PrepareAdminOrderPayment(_shop.Database.Invoices());

        Assert.That(model.Invoices.Single().State, Is.EqualTo(_shop.Texts.Format("State.expired")));
        Assert.That(model.Invoices.Single().TransactionsAtAddress, Is.False);
        Assert.That(model.Warning, Is.Null);
    }

    [Test]
    public async Task The_admin_reads_a_failed_first_check_as_such()
    {
        var model = _shop.NewScope().Pages.PrepareAdminOrderPayment(_shop.Database.Invoices());
        Assert.That(model.Invoices.Single().State, Is.EqualTo(_shop.Texts.Format("State.unchecked")));

        _shop.Gateway.Status = (_, _) => throw new NetworkException("down");
        await _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        model = _shop.NewScope().Pages.PrepareAdminOrderPayment(_shop.Database.Invoices());

        Assert.That(model.Invoices.Single().State, Is.EqualTo(_shop.Texts.Format("State.uncheckedFailed")));
    }

    [Test]
    public void A_preimage_proves_the_payment_hash_of_its_SHA_256()
    {
        // 32 zero bytes: their SHA-256 (python3 -c "import hashlib; print(hashlib.sha256(bytes(32)).hexdigest())").
        var preimage = new string('0', 64);
        Assert.That(PaymentPages.PaymentHashOf(preimage), Is.EqualTo("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"));
        Assert.That(PaymentPages.PaymentHashOf("not hex"), Is.Null);
        Assert.That(PaymentPages.PaymentHashOf(new string('A', 64)), Is.Null, "lowercase hex only, as the SDK writes it");
    }

    private async Task<(bool Block, bool CompletePayment, Models.OrderPaymentModel Model)> OffersAsync()
    {
        var order = _shop.Order(_order.Id);
        var invoice = _shop.Database.Invoices().Last(stored => stored.OrderId == _order.Id);
        var scope = _shop.NewScope();
        var model = scope.Pages.PrepareOrderPayment(_record, order, invoice);
        return (model.OffersPayment, await scope.Provider.CanRePostProcessPaymentAsync(order), model);
    }

    private void PastTheEnd()
    {
        _shop.Time.Advance(TimeSpan.FromSeconds(_invoice.ExpiresAt - _shop.Time.UnixNow + SattlemeDefaults.GraceSeconds + 11));
    }

    [Test]
    public async Task The_order_s_pages_lead_to_pay_an_invoice_that_can_be_paid()
    {
        var (block, complete, _) = await OffersAsync();
        Assert.That(block && complete, Is.True);

        await Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 15_000, 9_500));
        (block, complete, _) = await OffersAsync();
        Assert.That(block && complete, Is.True, "the rest of an underpayment can be paid");
    }

    [Test]
    public async Task The_order_s_pages_offer_no_payment_while_one_is_being_confirmed_or_once_one_came()
    {
        await Reports(SdkData.Unconfirmed(24_500, 1));
        var (block, complete, model) = await OffersAsync();
        Assert.That(block || complete, Is.False, "being confirmed");
        Assert.That(model.Text, Is.EqualTo("Order.Phase.unconfirmed"));

        await Reports(SdkData.PaidOnchain(24_500));
        (block, complete, _) = await OffersAsync();
        Assert.That(block || complete, Is.False, "paid");
    }

    [Test]
    public async Task The_order_s_pages_offer_no_payment_while_the_check_after_the_end_is_awaited()
    {
        _shop.Time.Advance(TimeSpan.FromSeconds(_invoice.ExpiresAt - _shop.Time.UnixNow + 1));
        var (block, complete, model) = await OffersAsync();
        Assert.That(block || complete, Is.False);
        Assert.That(model.Text, Is.EqualTo("Order.Phase.ended"));
    }

    [Test]
    public async Task The_order_s_pages_offer_a_new_invoice_once_the_last_ended_with_nothing_received()
    {
        PastTheEnd();
        await Reports(SdkData.Expired());
        var (block, complete, model) = await OffersAsync();
        Assert.That(block && complete, Is.True);
        Assert.That(model.Text, Is.EqualTo("Order.Phase.expired"));
    }

    [Test]
    public async Task The_order_s_pages_say_what_came_of_an_underpayment_and_offer_no_payment()
    {
        PastTheEnd();
        await Reports(SdkData.Expired(5_000));
        var (block, complete, model) = await OffersAsync();

        Assert.That(block || complete, Is.False, "no new invoice after an underpayment");
        Assert.That(model.Text, Is.EqualTo("Order.Phase.expiredUnderpaid"));
        Assert.That(model.TextArguments, Is.EqualTo(new object[]
        {
            5_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            24_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        }));
    }

    [Test]
    public void Writes_bitcoins_exactly()
    {
        Assert.That(PaymentPages.Btc(49_000), Is.EqualTo("0.00049"));
        Assert.That(PaymentPages.Btc(100_000_000), Is.EqualTo("1"));
        Assert.That(PaymentPages.Btc(1), Is.EqualTo("0.00000001"));
    }
}
