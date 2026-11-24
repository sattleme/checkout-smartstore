using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Sattleme.Bitcoin.Controllers;
using Sattleme.Bitcoin.Providers;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tasks;
using Smartstore.Caching;
using Smartstore.Core;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Configuration;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Stores;
using Smartstore.Engine.Modularity;
using IActivityLogger = Smartstore.Core.Logging.IActivityLogger;
using Notifier = Smartstore.Core.Logging.Notifier;

namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>
/// One request: the module's services, as Smartstore's container gives them to it, around one
/// SmartDbContext of its own. Smartstore's settings, encryptor and order rules are its own; what
/// would need a running application is faked (the currency service, the store context, the
/// localization service reading the module's resource files, the links, the log).
/// </summary>
public sealed class Scope : IDisposable
{
    internal Scope(Shop shop)
    {
        Shop = shop;
        Db = shop.Database.NewContext();
        Time = shop.Time;

        CurrencyService = new Mock<ICurrencyService>();
        CurrencyService.SetupGet(c => c.PrimaryCurrency).Returns(() => new Currency { Id = 1, CurrencyCode = shop.PrimaryCurrency, Name = shop.PrimaryCurrency, DisplayLocale = "de-CH", Rate = 1 });
        CurrencyService.Setup(c => c.CreateMoney(It.IsAny<decimal>(), It.IsAny<object>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns((decimal amount, object code, bool _, bool _) => new Money(amount, new Currency { CurrencyCode = (string)code, Name = (string)code, DisplayLocale = "de-CH", Rate = 1 }));

        var stores = Db.Stores.AsNoTracking().ToList();
        var cache = (StoreEntityCache)Activator.CreateInstance(typeof(StoreEntityCache), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, [stores], null)!;
        StoreContext = new Mock<IStoreContext>();
        StoreContext.Setup(s => s.GetCachedStores()).Returns(cache);
        StoreContext.SetupProperty(s => s.CurrentStore, stores.First(store => store.Id == TestDatabase.StoreId));

        Localization = new Mock<ILocalizationService>();
        Localization.Setup(l => l.GetResource(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string name, int _, bool _, string defaultValue, bool empty) => shop.Texts.Get(name) ?? (empty ? "" : string.IsNullOrEmpty(defaultValue) ? name : defaultValue));
        Localizer = (name, arguments) => new LocalizedString(arguments.Length == 0
            ? shop.Texts.Get(name) ?? name
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, shop.Texts.Get(name) ?? name, arguments));

        LanguageService = new Mock<ILanguageService>();
        LanguageService.Setup(l => l.GetMasterLanguageIdAsync(It.IsAny<int>())).ReturnsAsync(1);

        DateTimeHelper = new Mock<IDateTimeHelper>();
        DateTimeHelper.Setup(d => d.ConvertToUserTime(It.IsAny<DateTime>(), It.IsAny<DateTimeKind>())).Returns((DateTime time, DateTimeKind _) => time);

        HttpContextAccessor = new HttpContextAccessor();
        Links = new FakeLinks();
        Notifier = new Notifier();

        OrderProcessing = new Mock<IOrderProcessingService>();
        OrderProcessing.Setup(o => o.MarkOrderAsPaidAsync(It.IsAny<Order>())).Returns((Order order) => shop.MarkOrderAsPaidAsync(Db, order));

        Settings = new SettingService(NullCache.Instance, Db, Mock.Of<IActivityLogger>());
        SettingFactory = new SettingFactory(HttpContextAccessor, NullCache.Instance, new ContextFactory(shop.Database));
        Store = new SattlemeStore(Db);
        Keys = new IntegrationKeys(Db, shop.Encryptor(), Settings);
        Notes = new OrderNotes(Db, LanguageService.Object, Localization.Object, new LocalizationSettings(), Time);
        Maker = new InvoiceMaker(CurrencyService.Object, Logger<InvoiceMaker>(), Keys, shop.Gateway, SettingFactory, StoreContext.Object, Notes, Store, Time);
        Shredder = new PersonalDataShredder(Store);
        Checker = new PaymentChecker(Db, Logger<PaymentChecker>(), Keys, Maker, OrderProcessing.Object, shop.Gateway, Notes, Shredder, Store, Time);
        Task = new SattlemeCheckTask(Db, Logger<SattlemeCheckTask>(), Notes, Checker, Shredder, Store, Time);
        Pages = new PaymentPages(CurrencyService.Object, DateTimeHelper.Object, HttpContextAccessor, Links, Localization.Object, Time);
        Messages = new CustomerMessages(DateTimeHelper.Object, Localization.Object);
        Provider = new SattlemeProvider(HttpContextAccessor, Keys, Maker, Links, Logger<SattlemeProvider>(), SettingFactory, StoreContext.Object, Store, Time)
        {
            T = Localizer,
        };

        Services = new Mock<ICommonServices>();
        Services.SetupGet(s => s.DbContext).Returns(Db);
        Services.SetupGet(s => s.Notifier).Returns(Notifier);
        Services.SetupGet(s => s.Localization).Returns(Localization.Object);
        Services.SetupGet(s => s.StoreContext).Returns(StoreContext.Object);
        Services.SetupGet(s => s.CurrencyService).Returns(CurrencyService.Object);
        Services.SetupGet(s => s.Settings).Returns(Settings);
        Services.SetupGet(s => s.SettingFactory).Returns(SettingFactory);
        Services.SetupGet(s => s.DateTimeHelper).Returns(DateTimeHelper.Object);
    }

    /// <summary>The shop.</summary>
    public Shop Shop { get; }

    /// <summary>The request's database context.</summary>
    public SmartDbContext Db { get; }

    /// <summary>The clock.</summary>
    public ManualTime Time { get; }

    /// <summary>Smartstore's services, faked.</summary>
    public Mock<ICurrencyService> CurrencyService { get; }

    /// <inheritdoc cref="CurrencyService"/>
    public Mock<IStoreContext> StoreContext { get; }

    /// <inheritdoc cref="CurrencyService"/>
    public Mock<ILocalizationService> Localization { get; }

    /// <inheritdoc cref="CurrencyService"/>
    public Localizer Localizer { get; }

    /// <inheritdoc cref="CurrencyService"/>
    public Mock<ILanguageService> LanguageService { get; }

    /// <inheritdoc cref="CurrencyService"/>
    public Mock<IDateTimeHelper> DateTimeHelper { get; }

    /// <inheritdoc cref="CurrencyService"/>
    public Mock<IOrderProcessingService> OrderProcessing { get; }

    /// <inheritdoc cref="CurrencyService"/>
    public Mock<ICommonServices> Services { get; }

    /// <summary>The request's HTTP context (none unless a test sets one).</summary>
    public HttpContextAccessor HttpContextAccessor { get; }

    /// <summary>The links (Smartstore's LinkGenerator, faked with the module's routes).</summary>
    public FakeLinks Links { get; }

    /// <summary>Smartstore's notifier (the messages shown on the next page).</summary>
    public Notifier Notifier { get; }

    /// <summary>Smartstore's own settings services.</summary>
    public SettingService Settings { get; }

    /// <inheritdoc cref="Settings"/>
    public SettingFactory SettingFactory { get; }

    /// <summary>The module's services.</summary>
    public SattlemeStore Store { get; }

    /// <inheritdoc cref="Store"/>
    public IntegrationKeys Keys { get; }

    /// <inheritdoc cref="Store"/>
    public OrderNotes Notes { get; }

    /// <inheritdoc cref="Store"/>
    public InvoiceMaker Maker { get; }

    /// <inheritdoc cref="Store"/>
    public PersonalDataShredder Shredder { get; }

    /// <inheritdoc cref="Store"/>
    public PaymentChecker Checker { get; }

    /// <inheritdoc cref="Store"/>
    public SattlemeCheckTask Task { get; }

    /// <inheritdoc cref="Store"/>
    public PaymentPages Pages { get; }

    /// <inheritdoc cref="Store"/>
    public CustomerMessages Messages { get; }

    /// <inheritdoc cref="Store"/>
    public SattlemeProvider Provider { get; }

    /// <summary>The payment page's controller, for this request.</summary>
    public SattlemePaymentController PaymentController(HttpContext? httpContext = null)
    {
        var controller = new SattlemePaymentController(Db, Messages, Maker, Checker, Pages, Store);
        Prepare(controller, httpContext);
        return controller;
    }

    /// <summary>The configuration page's controller, for this request, in the store scope <paramref name="storeScope"/>.</summary>
    public SattlemeAdminController AdminController(int storeScope = 0, HttpContext? httpContext = null, ISattlemeGateway? gateway = null)
    {
        var providers = new Mock<IProviderManager>();
        providers.Setup(p => p.GetProvider(SattlemeDefaults.SystemName, It.IsAny<int>()))
            .Returns(() => new Provider<IProvider>(new Lazy<IProvider, ProviderMetadata>(() => Provider, new ProviderMetadata { SystemName = SattlemeDefaults.SystemName, FriendlyName = "Bitcoin (sattle.me)" })));
        var controller = new ScopedAdminController(storeScope, Db, Keys, Maker, Logger<SattlemeAdminController>(), gateway ?? Shop.Gateway, providers.Object, Pages, Store);
        Prepare(controller, httpContext);
        return controller;
    }

    private void Prepare(Controller controller, HttpContext? httpContext)
    {
        httpContext ??= new DefaultHttpContext();
        HttpContextAccessor.HttpContext = httpContext;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        if (controller is global::Smartstore.Web.Controllers.SmartController smart)
        {
            smart.Services = Services.Object;
            smart.T = Localizer;
        }
    }

    /// <summary>A logger writing to the shop's log.</summary>
    public ILogger<T> Logger<T>()
    {
        return new ShopLogger<T>(Shop);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Db.Dispose();
    }

    /// <summary>The configuration controller in a store scope the test chooses (Smartstore reads it from the admin's attributes).</summary>
    private sealed class ScopedAdminController(int storeScope, SmartDbContext db, IntegrationKeys keys, InvoiceMaker maker,
        ILogger<SattlemeAdminController> logger, ISattlemeGateway gateway, IProviderManager providerManager, PaymentPages pages, SattlemeStore store)
        : SattlemeAdminController(db, keys, maker, logger, gateway, providerManager, pages, store)
    {
        protected override int GetActiveStoreScopeConfiguration()
        {
            return storeScope;
        }
    }

    /// <summary>New contexts of the shop's database (Smartstore's SettingFactory reads its settings in one).</summary>
    private sealed class ContextFactory(TestDatabase database) : IDbContextFactory<SmartDbContext>
    {
        public SmartDbContext CreateDbContext()
        {
            return database.NewContext();
        }
    }
}

/// <summary>A logger writing to the shop's log.</summary>
internal sealed class ShopLogger<T>(Shop shop) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        shop.Write(logLevel, formatter(state, exception), exception);
    }
}

/// <summary>Smartstore's resource files, read as Smartstore reads them.</summary>
internal static class ResourceFiles
{
    /// <summary>Smartstore's own flattening of a resource file (XmlResourceManager.FlattenResourceFile), which uses no service.</summary>
    public static System.Xml.XmlDocument Flatten(System.Xml.XmlDocument source)
    {
        var manager = (XmlResourceManager)RuntimeHelpers.GetUninitializedObject(typeof(XmlResourceManager));
        return manager.FlattenResourceFile(source);
    }
}
