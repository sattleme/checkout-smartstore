using System.Text.Json.Nodes;
using Moq;
using Sattleme.Bitcoin.Hooks;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;
using Smartstore.Core.Identity;
using Smartstore.Data.Hooks;
using Order = Smartstore.Core.Checkout.Orders.Order;
using ShopPaymentStatus = Smartstore.Core.Checkout.Payment.PaymentStatus;

namespace Sattleme.Bitcoin.Tests;

/// <summary>
/// The customer's personal data: removed when Smartstore removes theirs (a customer deleted, which
/// Smartstore makes a soft deletion, or anonymized by its GDPR tool): what leads to their coins goes
/// from their invoices, once the checks no longer need it; and exported with theirs.
/// </summary>
[TestFixture]
public sealed class PrivacyTests
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

    private static Events Events(Scope scope)
    {
        return new Events(scope.Db, new PersonalDataExport(scope.Db, scope.Store), scope.Localization.Object, scope.Shredder);
    }

    private async Task<Order> PaidOnchainAsync()
    {
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidOnchain(24_500));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Does.Contain(SdkData.ADDRESS));
        return order;
    }

    private static Customer Customer(int id)
    {
        return new Customer { Id = id };
    }

    private static Task AnonymizeAsync(Scope scope, int customerId)
    {
        return Events(scope).HandleEventAsync(new CustomerAnonymizedEvent(Mock.Of<IGdprTool>(), Customer(customerId), new Smartstore.Core.Localization.Language(), false));
    }

    [Test]
    public async Task Removes_the_address_and_transactions_of_the_customer_s_final_invoices()
    {
        var order = await PaidOnchainAsync();

        var scope = _shop.NewScope();
        await AnonymizeAsync(scope, TestDatabase.CustomerId);

        var stored = _shop.Database.Invoices().Single();
        Assert.That(stored.Shredded, Is.True);
        Assert.That(stored.InvoiceJson, Does.Not.Contain(SdkData.ADDRESS));
        Assert.That(stored.StatusJson, Does.Not.Contain(SdkData.TXID));
        var json = JsonNode.Parse(stored.InvoiceJson!)!;
        Assert.That((string?)json["onchainAddress"], Is.EqualTo(PersonalDataShredder.Shredded));
        Assert.That((string?)json["paymentUri"], Is.EqualTo(PersonalDataShredder.Shredded));
        Assert.That((string?)json["ref"], Is.EqualTo(stored.Reference), "what is not personal stays");
        Assert.That((string?)json["sats"], Is.EqualTo("24500"));
        var admin = scope.Pages.PrepareAdminOrderPayment(_shop.Database.Invoices());
        Assert.That(admin.Invoices.Single().Shredded, Is.True);
        Assert.That(admin.Invoices.Single().Txids, Is.Empty);
        Assert.That(admin.Invoices.Single().AddressLink, Is.False);
        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
    }

    [Test]
    public async Task Removes_them_when_Smartstore_deletes_the_customer()
    {
        await PaidOnchainAsync();
        var scope = _shop.NewScope();
        var entry = new Mock<IHookedEntity>();
        entry.SetupGet(e => e.Entity).Returns(Customer(TestDatabase.CustomerId));
        entry.SetupGet(e => e.IsSoftDeleted).Returns(true);
        var unchanged = new Mock<IHookedEntity>();
        unchanged.SetupGet(e => e.Entity).Returns(Customer(TestDatabase.CustomerId));
        unchanged.SetupGet(e => e.IsSoftDeleted).Returns(false);

        var hook = new CustomerDeletedHook(scope.Shredder);
        await hook.OnAfterSaveCompletedAsync([unchanged.Object], CancellationToken.None);
        Assert.That(_shop.Database.Invoices().Single().Shredded, Is.False, "an update that deletes nothing");
        await hook.OnAfterSaveCompletedAsync([entry.Object], CancellationToken.None);

        Assert.That(_shop.Database.Invoices().Single().Shredded, Is.True);
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Does.Not.Contain(SdkData.ADDRESS));
    }

    [Test]
    public async Task Keeps_what_the_checks_need_until_the_invoice_is_final()
    {
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Unconfirmed(24_500, 1));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        await AnonymizeAsync(_shop.NewScope(), TestDatabase.CustomerId);
        var pending = _shop.Database.Invoices().Single();
        Assert.That(pending.Shredded, Is.True);
        Assert.That(pending.InvoiceJson, Does.Contain(SdkData.ADDRESS), "its check still needs it");

        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidOnchain(24_500));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var final = _shop.Database.Invoices().Single();
        Assert.That(final.Final, Is.True);
        Assert.That(final.InvoiceJson, Does.Not.Contain(SdkData.ADDRESS));
        Assert.That(final.StatusJson, Does.Not.Contain(SdkData.TXID));
        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(ShopPaymentStatus.Paid));
    }

    [Test]
    public async Task Leaves_other_customers_invoices_alone()
    {
        await PaidOnchainAsync();

        await AnonymizeAsync(_shop.NewScope(), TestDatabase.CustomerId + 1);

        Assert.That(_shop.Database.Invoices().Single().Shredded, Is.False);
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Does.Contain(SdkData.ADDRESS));
    }

    [Test]
    public void Removes_the_rest_s_QR_code_which_holds_the_address()
    {
        var status = SdkData.OpenWithRest(1, 20_000, 4_500).ToJson();
        var shredded = JsonNode.Parse(PersonalDataShredder.ShredStatus(status)!)!;
        Assert.That(shredded["rest"], Is.Null);
        Assert.That((string?)shredded["received"], Is.EqualTo("20000"));
        Assert.That(PersonalDataShredder.ShredStatus(null), Is.Null);
        var lightning = SdkData.InvoiceJson("ss-1", "1.00", "CHF", 1, 2, "n", onchain: false).ToJsonString();
        Assert.That(PersonalDataShredder.ShredInvoice(lightning), Does.Contain("lightning:"), "a Lightning invoice names the merchant's node only");
    }

    [Test]
    public async Task Exports_the_customer_s_Bitcoin_payments_with_their_data()
    {
        var order = await PaidOnchainAsync();
        var result = new Dictionary<string, object>();

        await Events(_shop.NewScope()).HandleEventAsync(new GdprCustomerDataExportedEvent(Customer(TestDatabase.CustomerId), result));

        var payments = (List<Dictionary<string, object?>>)result["SattlemeBitcoinPayments"];
        var payment = payments.Single();
        Assert.That(payment["OrderNumber"], Is.EqualTo(order.OrderNumber));
        Assert.That(payment["Reference"], Is.EqualTo(_shop.Database.Invoices().Single().Reference));
        Assert.That(payment["Price"], Is.EqualTo("24.50 CHF"));
        Assert.That(payment["State"], Is.EqualTo("paid"));
        Assert.That(payment["Sats"], Is.EqualTo(24_500));
        Assert.That(payment["OnchainAddress"], Is.EqualTo(SdkData.ADDRESS));
        Assert.That(payment["Transactions"], Is.EqualTo(new[] { SdkData.TXID }));

        var none = new Dictionary<string, object>();
        await Events(_shop.NewScope()).HandleEventAsync(new GdprCustomerDataExportedEvent(Customer(TestDatabase.CustomerId + 1), none));
        Assert.That(none, Is.Empty, "nothing added for a customer who never paid with Bitcoin");
    }
}
