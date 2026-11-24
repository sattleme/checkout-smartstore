using System.Reflection;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Sattleme.Bitcoin.Configuration;
using Sattleme.Bitcoin.Providers;
using Sattleme.Bitcoin.Tasks;
using Sattleme.Bitcoin.Tests.Support;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Localization;
using Smartstore.Engine.Modularity;
using Smartstore.Scheduling;
using SmartstoreSattleme.Sattleme.Checkout;

namespace Sattleme.Bitcoin.Tests;

/// <summary>
/// The payment method as Smartstore's checkout uses it: offered or not, the order placed, the
/// customer sent to the payment page; and the module's installation.
/// </summary>
[TestFixture]
public sealed class PaymentMethodTests
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

    private static Task<bool> ExcludedAsync(Scope scope, int storeId = TestDatabase.StoreId)
    {
        var filter = new SattlemePaymentFilter(scope.Keys, scope.Maker);
        var provider = new Provider<IPaymentMethod>(new Lazy<IPaymentMethod, ProviderMetadata>(() => scope.Provider,
            new ProviderMetadata { SystemName = SattlemeDefaults.SystemName }));
        return filter.IsExcludedAsync(new PaymentFilterRequest { PaymentProvider = provider, StoreId = storeId });
    }

    [Test]
    public async Task Is_offered_with_a_key_and_a_currency_the_SDK_converts()
    {
        Assert.That(await ExcludedAsync(_shop.NewScope()), Is.False);
    }

    [Test]
    public async Task Is_hidden_without_a_key_that_can_be_read()
    {
        _shop.EncryptionKey = "1414213562373095";
        Assert.That(await ExcludedAsync(_shop.NewScope()), Is.True);
        _shop.EncryptionKey = "2718281828459045";
        await _shop.NewScope().Keys.RemoveAsync(0);
        Assert.That(await ExcludedAsync(_shop.NewScope()), Is.True);
    }

    [Test]
    public async Task Is_hidden_in_a_currency_the_SDK_does_not_convert()
    {
        _shop.PrimaryCurrency = "XAU";
        Assert.That(await ExcludedAsync(_shop.NewScope()), Is.True);
    }

    [Test]
    public async Task Leaves_other_payment_methods_alone()
    {
        await _shop.NewScope().Keys.RemoveAsync(0);
        var scope = _shop.NewScope();
        var filter = new SattlemePaymentFilter(scope.Keys, scope.Maker);
        var other = new Provider<IPaymentMethod>(new Lazy<IPaymentMethod, ProviderMetadata>(() => null!,
            new ProviderMetadata { SystemName = "Payments.CashOnDelivery" }));

        Assert.That(await filter.IsExcludedAsync(new PaymentFilterRequest { PaymentProvider = other, StoreId = 1 }), Is.False);
    }

    [Test]
    public async Task Uses_a_store_s_own_key()
    {
        await _shop.NewScope().Keys.RemoveAsync(0);
        await _shop.NewScope().Keys.SaveAsync(SdkData.KEY, TestDatabase.StoreId);

        Assert.That(await ExcludedAsync(_shop.NewScope()), Is.False);
        Assert.That(await ExcludedAsync(_shop.NewScope(), storeId: 2), Is.True, "another store has no key");
    }

    [Test]
    public async Task Charges_the_additional_fee_as_Smartstore_s_own_methods()
    {
        _shop.Configure(settings =>
        {
            settings.AdditionalFee = 2.5m;
            settings.AdditionalFeePercentage = true;
        });
        var (fee, percentage) = await _shop.NewScope().Provider.GetPaymentFeeInfoAsync(null!);

        Assert.That(fee, Is.EqualTo(2.5m));
        Assert.That(percentage, Is.True);
    }

    [Test]
    public async Task Places_the_order_pending_payment_without_asking_the_SDK()
    {
        // Smartstore calls ProcessPaymentAsync inside the placement's database transaction.
        var result = await _shop.NewScope().Provider.ProcessPaymentAsync(new ProcessPaymentRequest { StoreId = 1, OrderGuid = Guid.NewGuid(), OrderTotal = 24.50m });

        Assert.That(result.NewPaymentStatus, Is.EqualTo(Smartstore.Core.Checkout.Payment.PaymentStatus.Pending));
        Assert.That(_shop.Gateway.Creates + _shop.Gateway.Merchants + _shop.Gateway.Statuses, Is.Zero);
    }

    [Test]
    public async Task Refuses_the_order_with_a_message_when_the_store_cannot_take_Bitcoin_now()
    {
        await _shop.NewScope().Keys.RemoveAsync(0);
        var noKey = await Assert.ThrowsAsync<PaymentException>(() => _shop.NewScope().Provider.ProcessPaymentAsync(new ProcessPaymentRequest { StoreId = 1 }));
        Assert.That(noKey!.Message, Is.EqualTo(_shop.Texts.Format("Error.Unavailable")));

        await _shop.NewScope().Keys.SaveAsync(SdkData.KEY, 0);
        _shop.PrimaryCurrency = "XAU";
        await Assert.ThrowsAsync<PaymentException>(() => _shop.NewScope().Provider.ProcessPaymentAsync(new ProcessPaymentRequest { StoreId = 1 }));
    }

    [Test]
    public async Task Makes_the_invoice_once_the_order_is_placed_and_sends_the_customer_to_the_payment_page()
    {
        var order = _shop.AddOrder();
        var scope = _shop.NewScope();
        scope.HttpContextAccessor.HttpContext = new DefaultHttpContext();
        var request = new PostProcessPaymentRequest { Order = order };
        await scope.Provider.PostProcessPaymentAsync(request);

        var record = _shop.Database.Records().Single();
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Is.Not.Null);
        Assert.That(request.RedirectUrl, Is.EqualTo($"/sattleme/pay/{order.OrderGuid}?token={record.PageToken}"));
    }

    [Test]
    public async Task Sends_the_customer_to_the_payment_page_even_when_no_invoice_could_be_made()
    {
        _shop.Gateway.Create = (_, _) => throw new NetworkException("down");
        var order = _shop.AddOrder();
        var scope = _shop.NewScope();
        scope.HttpContextAccessor.HttpContext = new DefaultHttpContext();
        var request = new PostProcessPaymentRequest { Order = order };
        await scope.Provider.PostProcessPaymentAsync(request);

        Assert.That(request.RedirectUrl, Does.StartWith($"/sattleme/pay/{order.OrderGuid}?token="));
        var page = scope.Pages.PreparePaymentPage(_shop.Database.Records().Single(), _shop.Order(order.Id), _shop.Database.Invoices().Single());
        Assert.That(page.Phase, Is.EqualTo("none"));
        Assert.That(page.CanMakeNew, Is.True, "the page offers to try again");
        Assert.That(page.Problem, Is.EqualTo(_shop.Texts.Format("ErrorCode.network")));
    }

    [Test]
    public async Task Complete_payment_leads_to_the_payment_page_as_it_is()
    {
        var order = _shop.AddOrder();
        var scope = _shop.NewScope();
        scope.HttpContextAccessor.HttpContext = new DefaultHttpContext();
        await scope.Provider.PostProcessPaymentAsync(new PostProcessPaymentRequest { Order = order });
        var again = new PostProcessPaymentRequest { Order = _shop.Order(order.Id), IsRePostProcessPayment = true };
        await _shop.NewScope().Provider.PostProcessPaymentAsync(again);

        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        Assert.That(await scope.Provider.CanRePostProcessPaymentAsync(order), Is.True);
        var other = _shop.AddOrder(edit: stored => stored.PaymentMethodSystemName = "Payments.CashOnDelivery");
        Assert.That(await scope.Provider.CanRePostProcessPaymentAsync(other), Is.False);
    }

    [Test]
    public void Never_refunds_captures_or_voids_by_itself()
    {
        var provider = _shop.NewScope().Provider;
        Assert.That(provider.SupportCapture, Is.False);
        Assert.That(provider.SupportRefund, Is.False);
        Assert.That(provider.SupportPartiallyRefund, Is.False);
        Assert.That(provider.SupportVoid, Is.False);
        Assert.That(provider.RecurringPaymentType, Is.EqualTo(RecurringPaymentType.NotSupported));
        Assert.That(provider.RequiresInteraction, Is.False, "no payment form at checkout");
        Assert.That(typeof(SattlemeProvider).GetCustomAttribute<PaymentMethodAttribute>()!.Type, Is.EqualTo(PaymentMethodType.Redirection));
        Assert.That(typeof(SattlemeProvider).GetCustomAttribute<SystemNameAttribute>()!.Name, Is.EqualTo("Payments.Sattleme"));
    }

    #region The module's installation

    private sealed class ModuleSetup
    {
        public required IModule Module { get; init; }

        public required Mock<ITaskStore> Tasks { get; init; }

        public required Mock<IXmlResourceManager> Resources { get; init; }
    }

    /// <summary>The module (Module.cs) as Smartstore makes it: its services, its descriptor (module.json).</summary>
    private static ModuleSetup Module(Scope scope)
    {
        var tasks = new Mock<ITaskStore>();
        var moduleType = typeof(SattlemeProvider).Assembly.GetType("Sattleme.Bitcoin.Module", throwOnError: true)!;
        var module = (IModule)Activator.CreateInstance(moduleType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [scope.Store, tasks.Object], null)!;
        var descriptor = new Mock<IModuleDescriptor>();
        descriptor.SetupGet(d => d.SystemName).Returns(SattlemeDefaults.ModuleSystemName);
        descriptor.SetupGet(d => d.FriendlyName).Returns("Bitcoin (sattle.me)");
        descriptor.SetupGet(d => d.ResourceRootKey).Returns(Texts.RootKey);
        module.Descriptor = descriptor.Object;

        var resources = new Mock<IXmlResourceManager>();
        var container = new ContainerBuilder();
        container.RegisterInstance(resources.Object).As<IXmlResourceManager>();
        scope.Services.SetupGet(s => s.Container).Returns(container.Build());
        // Smartstore's LocalizationService.DeleteLocaleStringResourcesAsync, as 6.4.0 has it: every
        // resource whose name starts with the key and a dot (a root key), or with the key.
        scope.Localization.Setup(l => l.DeleteLocaleStringResourcesAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(async (string key, bool keyIsRootKey) =>
            {
                var pattern = (key.EndsWith('.') || !keyIsRootKey ? key : key + ".") + "%";
                return await scope.Db.LocaleStringResources.Where(resource => EF.Functions.Like(resource.ResourceName, pattern)).ExecuteDeleteAsync();
            });
        moduleType.GetProperty("Services", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.SetValue(module, scope.Services.Object);
        return new ModuleSetup { Module = module, Tasks = tasks, Resources = resources };
    }

    [Test]
    public async Task Installs_its_settings_texts_and_background_checks()
    {
        var scope = _shop.NewScope();
        var setup = Module(scope);
        TaskDescriptor? task = null;
        setup.Tasks.Setup(t => t.GetOrAddTaskAsync<SattlemeCheckTask>(It.IsAny<Action<TaskDescriptor>>()))
            .ReturnsAsync((Action<TaskDescriptor> create) =>
            {
                task = new TaskDescriptor();
                create(task);
                return task;
            });

        await setup.Module.InstallAsync(new ModuleInstallationContext { Logger = NullLogger.Instance, ModuleDescriptor = setup.Module.Descriptor });

        setup.Resources.Verify(r => r.ImportModuleResourcesFromXmlAsync(setup.Module.Descriptor, null, true, null), Times.Once);
        Assert.That(task!.CronExpression, Is.EqualTo("* * * * *"));
        Assert.That(task.Enabled, Is.True);
        Assert.That(task.StopOnError, Is.False, "a failed run never stops the checks");
        var settings = await scope.SettingFactory.LoadSettingsAsync<SattlemeSettings>();
        Assert.That(settings.MinutesToPay, Is.EqualTo(15));
        using var db = _shop.Database.NewContext();
        Assert.That(db.Settings.Any(setting => setting.Name == "SattlemeSettings.MinutesToPay"), Is.True);
    }

    [Test]
    public async Task Stays_installed_while_a_payment_may_still_come()
    {
        await _shop.NewScope().Maker.MakeAsync(_shop.AddOrder());
        var scope = _shop.NewScope();
        var setup = Module(scope);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(setup.Module.UninstallAsync);

        Assert.That(refused!.Message, Is.EqualTo(_shop.Texts.Format("Uninstall.PendingOne", 1)));
        Assert.That(refused.Message, Does.Contain("1 Bitcoin invoice may still be paid or is being confirmed"), "one in the singular");
        setup.Tasks.Verify(t => t.DeleteTaskAsync(It.IsAny<TaskDescriptor>()), Times.Never);
        Assert.That(await _shop.NewScope().Keys.KeyOfStoreAsync(1), Is.EqualTo(SdkData.KEY), "nothing removed");

        await _shop.NewScope().Maker.MakeAsync(_shop.AddOrder());
        refused = await Assert.ThrowsAsync<InvalidOperationException>(Module(_shop.NewScope()).Module.UninstallAsync);
        Assert.That(refused!.Message, Does.Contain("2 Bitcoin invoices may still be paid or are being confirmed"), "more in the plural");
    }

    [Test]
    public async Task Uninstalls_everything_it_installed_and_keeps_the_invoices()
    {
        // An installed module: its texts imported (every name of its resource files), its settings, its task.
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _shop.Configure(settings => settings.MinutesToPay = 20);
        using (var db = _shop.Database.NewContext())
        {
            foreach (var (name, value) in _shop.Texts.Values)
            {
                db.LocaleStringResources.Add(new LocaleStringResource { LanguageId = 1, ResourceName = name, ResourceValue = value });
            }
            db.LocaleStringResources.Add(new LocaleStringResource { LanguageId = 1, ResourceName = "Plugins.FriendlyName.Payments.Manual", ResourceValue = "Credit card" });
            db.SaveChanges();
        }
        var scope = _shop.NewScope();
        var setup = Module(scope);
        var task = new TaskDescriptor { Type = nameof(SattlemeCheckTask) };
        setup.Tasks.Setup(t => t.GetTaskByTypeAsync(nameof(SattlemeCheckTask))).ReturnsAsync(task);

        await setup.Module.UninstallAsync();

        setup.Tasks.Verify(t => t.DeleteTaskAsync(task), Times.Once);
        using var after = _shop.Database.NewContext();
        Assert.That(after.LocaleStringResources.Select(resource => resource.ResourceName).ToList(),
            Is.EqualTo(new[] { "Plugins.FriendlyName.Payments.Manual" }), "every text of the module, and only those");
        Assert.That(after.Settings.Where(setting => setting.Name.StartsWith("SattlemeSettings")).ToList(), Is.Empty, "its settings and the key");
        Assert.That(_shop.Database.Invoices(), Has.Count.EqualTo(1), "the invoices stay, as Smartstore's guidelines ask");
    }

    #endregion
}
