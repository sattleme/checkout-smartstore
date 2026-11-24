using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Checkout.Shipping;
using Smartstore.Core.Data;
using Smartstore.Core.Security;
using Smartstore.Core.Stores;

namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>
/// A Smartstore shop as the module sees it: its database (Smartstore's SmartDbContext on SQLite, the
/// module's tables made by its migration), its orders and their notes, its settings (Smartstore's
/// own SettingService and SettingFactory), its encryption (Smartstore's Encryptor), its texts (the
/// module's own resource files, as Smartstore imports them), its log, its clock, and the SDK faked.
/// Each request is a <see cref="Scope"/> of its own, with its own SmartDbContext, as in Smartstore,
/// where the module's services live for a request. Store 1 has an integration key; its primary
/// currency is CHF.
/// </summary>
public sealed class Shop : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<Scope> _scopes = [];
    private int _markedPaid;

    /// <summary>A shop whose every store has a key, in CHF.</summary>
    public Shop()
    {
        Time = new ManualTime();
        Gateway = new FakeGateway(Time);
        Database = new TestDatabase();
        Texts = Texts.Of("en-us");
        using var scope = NewScope();
        scope.Keys.SaveAsync(SdkData.KEY, 0).GetAwaiter().GetResult();
    }

    /// <summary>The clock.</summary>
    public ManualTime Time { get; }

    /// <summary>The SDK.</summary>
    public FakeGateway Gateway { get; }

    /// <summary>The shop's database.</summary>
    public TestDatabase Database { get; }

    /// <summary>The store's primary currency (ICurrencyService.PrimaryCurrency).</summary>
    public string PrimaryCurrency { get; set; } = "CHF";

    /// <summary>The store's encryption key (SecuritySettings.EncryptionKey, 16 characters, as Smartstore makes it).</summary>
    public string EncryptionKey { get; set; } = "2718281828459045";

    /// <summary>The texts of the working (and the admin's) language.</summary>
    public Texts Texts { get; set; }

    /// <summary>What was logged: its level and message (formatted).</summary>
    public List<(LogLevel Level, string Message, Exception? Exception)> Log { get; } = [];

    /// <summary>How many times Smartstore's MarkOrderAsPaidAsync ran.</summary>
    public int MarkedPaid => Volatile.Read(ref _markedPaid);

    /// <summary>What a completion does after MarkOrderAsPaidAsync's checks: by default, what Smartstore does.</summary>
    public Func<Order, Task>? OnMarkPaid { get; set; }

    /// <summary>
    /// What Smartstore does once it saved the order paid (its completion's e-mails, OrderPaidEvent's
    /// consumers): nothing by default; a test makes it throw.
    /// </summary>
    public Func<Order, Task>? AfterMarkPaid { get; set; }

    /// <summary>The log's messages of <paramref name="level"/>.</summary>
    public IList<string> Logged(LogLevel level)
    {
        lock (_lock)
        {
            return [.. Log.Where(entry => entry.Level == level).Select(entry => entry.Message)];
        }
    }

    /// <summary>Everything logged, in one text (to look for what must never be there).</summary>
    public string AllLogged()
    {
        lock (_lock)
        {
            return string.Join("\n", Log.Select(entry => $"{entry.Level}: {entry.Message} {entry.Exception}"));
        }
    }

    internal void Write(LogLevel level, string message, Exception? exception)
    {
        lock (_lock)
        {
            Log.Add((level, message, exception));
        }
    }

    /// <summary>An order placed with the module, pending payment, stored as Smartstore stores it.</summary>
    public Order AddOrder(decimal total = 24.50m, Action<Order>? edit = null)
    {
        var order = new Order
        {
            OrderGuid = Guid.NewGuid(),
            StoreId = TestDatabase.StoreId,
            CustomerId = TestDatabase.CustomerId,
            BillingAddressId = 1,
            OrderTotal = total,
            PaymentMethodSystemName = SattlemeDefaults.SystemName,
            OrderStatus = OrderStatus.Pending,
            PaymentStatus = PaymentStatus.Pending,
            ShippingStatus = ShippingStatus.ShippingNotRequired,
            CustomerCurrencyCode = PrimaryCurrency,
            CurrencyRate = 1,
            CustomerLanguageId = 1,
            CreatedOnUtc = Time.UtcNow,
            UpdatedOnUtc = Time.UtcNow,
        };
        edit?.Invoke(order);
        using var db = Database.NewContext();
        db.Orders.Add(order);
        db.SaveChanges();
        if (string.IsNullOrEmpty(order.OrderNumber))
        {
            order.OrderNumber = order.Id.ToString(CultureInfo.InvariantCulture);
            db.SaveChanges();
        }
        return Order(order.Id);
    }

    /// <summary>The order <paramref name="id"/> as stored now (null when there is none).</summary>
    public Order Order(int id)
    {
        using var db = Database.NewContext();
        return db.Orders.IgnoreQueryFilters().AsNoTracking().Single(order => order.Id == id);
    }

    /// <summary>Changes the stored order <paramref name="id"/> (an admin's edit).</summary>
    public void Edit(int id, Action<Order> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        using var db = Database.NewContext();
        var order = db.Orders.IgnoreQueryFilters().Single(order => order.Id == id);
        edit(order);
        db.SaveChanges();
    }

    /// <summary>The notes of the order <paramref name="orderId"/>, oldest first.</summary>
    public IList<string> Notes(int orderId)
    {
        using var db = Database.NewContext();
        return [.. db.OrderNotes.AsNoTracking().Where(note => note.OrderId == orderId).OrderBy(note => note.Id).Select(note => note.Note)];
    }

    /// <summary>A request.</summary>
    public Scope NewScope()
    {
        var scope = new Scope(this);
        lock (_lock)
        {
            _scopes.Add(scope);
        }
        return scope;
    }

    /// <summary>Changes the module's settings of the scope <paramref name="storeId"/> (0: every store), through Smartstore's SettingFactory.</summary>
    public void Configure(Action<Configuration.SattlemeSettings> change, int storeId = 0)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var scope = new Scope(this);
        var settings = scope.SettingFactory.LoadSettingsAsync<Configuration.SattlemeSettings>(storeId).GetAwaiter().GetResult();
        change(settings);
        scope.SettingFactory.SaveSettingsAsync(settings, storeId).GetAwaiter().GetResult();
    }

    /// <summary>Changes the store <paramref name="storeId"/>.</summary>
    public void EditStore(Action<Store> edit, int storeId = TestDatabase.StoreId)
    {
        ArgumentNullException.ThrowIfNull(edit);
        using var db = Database.NewContext();
        edit(db.Stores.Single(store => store.Id == storeId));
        db.SaveChanges();
    }

    /// <summary>
    /// Smartstore's MarkOrderAsPaidAsync (OrderProcessingService.Payment.cs and CheckOrderStatusAsync
    /// in OrderProcessingService.cs, 6.4.0): refused unless Order.CanMarkOrderAsPaid; paid now, with
    /// its note; Pending becomes Processing, then Complete when nothing is to ship; saved.
    /// </summary>
    internal async Task MarkOrderAsPaidAsync(SmartDbContext db, Order order)
    {
        if (!order.CanMarkOrderAsPaid())
        {
            throw new PaymentException("You can't mark this order as paid");
        }
        if (OnMarkPaid is { } hook)
        {
            await hook(order);
        }
        order.PaymentStatus = PaymentStatus.Paid;
        order.PaidDateUtc = Time.UtcNow;
        db.OrderNotes.Add(new OrderNote { OrderId = order.Id, Note = "Order has been marked as paid", CreatedOnUtc = Time.UtcNow });
        if (order.OrderStatus == OrderStatus.Pending)
        {
            order.OrderStatus = OrderStatus.Processing;
        }
        if (order.ShippingStatus is ShippingStatus.ShippingNotRequired or ShippingStatus.Delivered)
        {
            order.OrderStatus = OrderStatus.Complete;
        }
        await db.SaveChangesAsync();
        Interlocked.Increment(ref _markedPaid);
        if (AfterMarkPaid is { } after)
        {
            await after(order);
        }
    }

    /// <summary>Smartstore's encryptor, with the store's encryption key.</summary>
    internal Encryptor Encryptor()
    {
        return new Encryptor(new SecuritySettings { EncryptionKey = EncryptionKey });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var scope in _scopes)
            {
                scope.Dispose();
            }
        }
        Database.Dispose();
    }
}
