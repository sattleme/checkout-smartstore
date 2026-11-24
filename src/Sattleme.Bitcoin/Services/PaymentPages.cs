using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Models;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Localization;
using ShopPaymentStatus = Smartstore.Core.Checkout.Payment.PaymentStatus;

namespace Sattleme.Bitcoin.Services;

/// <summary>The models of the payment page, of the customer's order page and of the admin's order page.</summary>
public partial class PaymentPages
{
    private static readonly Regex _txid = TxidPattern();
    private static readonly Regex _address = AddressPattern();
    private static readonly Regex _preimage = TxidPattern();

    private readonly ICurrencyService _currencyService;
    private readonly IDateTimeHelper _dateTimeHelper;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly LinkGenerator _linkGenerator;
    private readonly ILocalizationService _localizationService;
    private readonly TimeProvider _time;

    /// <summary>The factory.</summary>
    public PaymentPages(ICurrencyService currencyService,
        IDateTimeHelper dateTimeHelper,
        IHttpContextAccessor httpContextAccessor,
        LinkGenerator linkGenerator,
        ILocalizationService localizationService,
        TimeProvider time)
    {
        _currencyService = currencyService;
        _dateTimeHelper = dateTimeHelper;
        _httpContextAccessor = httpContextAccessor;
        _linkGenerator = linkGenerator;
        _localizationService = localizationService;
        _time = time;
    }

    /// <summary>
    /// The payment page of <paramref name="order"/>, whose last invoice is <paramref name="invoice"/>
    /// (null: none made).
    /// </summary>
    public virtual PaymentPageModel PreparePaymentPage(SattlemeOrder record, Order order, SattlemeInvoice? invoice)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(order);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var phase = PaymentRules.PhaseOf(order, invoice, now);
        var stored = invoice is null ? null : StoredInvoice.Of(invoice);
        var pending = order.PaymentStatus == ShopPaymentStatus.Pending && order.OrderStatus != OrderStatus.Cancelled && !order.Deleted;
        var model = new PaymentPageModel
        {
            Phase = PhaseName(phase),
            OrderNumber = order.GetOrderNumber(),
            OrderUrl = Path("Details", "Order", new { area = "", id = order.Id }),
            StatusUrl = PagePath("Status", record),
            CheckUrl = PagePath("Pay", record, check: true),
            NewInvoiceUrl = PagePath("NewInvoice", record),
            CanMakeNew = pending && (phase == PaymentPhase.None || (phase == PaymentPhase.Expired && invoice!.Received == 0)),
            Problem = phase == PaymentPhase.None && pending && !string.IsNullOrEmpty(invoice?.Error) ? ErrorText(invoice.Error) : null,
            Version = Version(phase, invoice, stored),
            PollSeconds = phase switch
            {
                PaymentPhase.Open or PaymentPhase.Underpaid or PaymentPhase.Arrived => 3,
                PaymentPhase.Ended => 5,
                PaymentPhase.Unconfirmed => 30,
                _ => null,
            },
        };
        if (stored is null)
        {
            return model;
        }

        var underpaid = phase == PaymentPhase.Underpaid;
        var toPay = underpaid ? stored.RestSats ?? 0 : stored.Sats;
        var uri = underpaid ? stored.RestPaymentUri : stored.PaymentUri;
        var show = phase is PaymentPhase.Open or PaymentPhase.Underpaid;
        uri = uri is not null && (uri.StartsWith("bitcoin:", StringComparison.Ordinal) || uri.StartsWith("lightning:", StringComparison.Ordinal)) ? uri : null;
        model.HasOnchain = stored.OnchainAddress is not null;
        model.Sats = Sats(stored.Sats);
        model.ToPaySats = Sats(toPay);
        model.ToPayBtc = Btc(toPay);
        model.ReceivedSats = Sats(stored.Received);
        model.HasReceived = stored.Received > 0;
        model.Confirmations = stored.Confirmations ?? 0;
        model.Price = PriceOf(stored);
        model.SecondsLeft = Math.Max(0, stored.ExpiresAt - now);
        model.TimeLeft = $"{model.SecondsLeft / 60}:{model.SecondsLeft % 60:00}";
        model.Until = Time(stored.ExpiresAt, "t");
        if (show && uri is not null)
        {
            model.Uri = uri;
            model.QrSvg = QrSvg.Of(uri, Resource("Pay.QrLabel"));
            model.Lightning = underpaid ? stored.RestBolt11 : stored.Bolt11;
            // On-chain to the invoice's address when its URI says so (a rest below 1 000 sats is Lightning only).
            model.Address = uri.StartsWith("bitcoin:", StringComparison.Ordinal) ? stored.OnchainAddress : null;
        }
        return model;
    }

    /// <summary>What the payment page's script gets: where the payment is, and the page's version.</summary>
    public virtual (string Phase, string Version) PrepareStatus(Order order, SattlemeInvoice? invoice)
    {
        ArgumentNullException.ThrowIfNull(order);
        var phase = PaymentRules.PhaseOf(order, invoice, _time.GetUtcNow().ToUnixTimeSeconds());
        return (PhaseName(phase), Version(phase, invoice, invoice is null ? null : StoredInvoice.Of(invoice)));
    }

    /// <summary>What the customer's order page says of its Bitcoin payment.</summary>
    public virtual OrderPaymentModel PrepareOrderPayment(SattlemeOrder record, Order order, SattlemeInvoice? invoice)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(order);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var phase = PaymentRules.PhaseOf(order, invoice, now);
        var until = invoice?.InvoiceJson is null ? null : Time(invoice.ExpiresAt, "t");
        var model = new OrderPaymentModel
        {
            Phase = PhaseName(phase),
            Text = "Order.Phase." + PhaseName(phase),
            TextArguments = [until ?? ""],
            OffersPayment = PaymentRules.OffersPayment(order, invoice, now),
            PayUrl = PagePath("Pay", record),
            Until = until,
        };
        // An underpayment that ended: what came, against the amount, as the payment page says it.
        if (phase == PaymentPhase.Expired && invoice!.Received > 0)
        {
            model.Text = "Order.Phase.expiredUnderpaid";
            model.TextArguments = [Sats(invoice.Received), Sats(StoredInvoice.Of(invoice)?.Sats ?? 0)];
        }
        return model;
    }

    /// <summary>What the admin's order page shows of the order's invoices, the latest first.</summary>
    public virtual AdminOrderPaymentModel PrepareAdminOrderPayment(IList<SattlemeInvoice> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var model = new AdminOrderPaymentModel();
        foreach (var invoice in invoices.OrderByDescending(invoice => invoice.Id))
        {
            model.Invoices.Add(PrepareAdminInvoice(invoice));
        }

        if (invoices.FirstOrDefault(PaymentRules.MayBePaid) is { } payable)
        {
            var end = payable.ExpiresAt + SattlemeDefaults.GraceSeconds;
            model.Warning = now < end && payable.Received == 0 && payable.State != "unconfirmed"
                ? string.Format(CultureInfo.CurrentCulture, Resource("Admin.Warning.Payable"), Time(end, "G"))
                : Resource("Admin.Warning.Confirming");
        }
        else if (invoices.OrderBy(invoice => invoice.Id).LastOrDefault() is { Final: true, State: "expired", Received: > 0 } underpaid)
        {
            // Something came, less than the amount: it is in the merchant's wallet, the order is not
            // paid. The merchant settles it before cancelling the order.
            model.Warning = string.Format(CultureInfo.CurrentCulture, Resource("Admin.Warning.Underpaid"),
                Sats(underpaid.Received), Sats(StoredInvoice.Of(underpaid)?.Sats ?? 0), underpaid.Reference);
        }
        return model;
    }

    /// <summary>An error's code in plain words (the code itself when it is not known).</summary>
    public virtual string ErrorText(string code)
    {
        var text = _localizationService.GetResource(SattlemeDefaults.ResourcePrefix + "ErrorCode." + code,
            logIfNotFound: false, defaultValue: "", returnEmptyIfNotFound: true);
        return string.IsNullOrEmpty(text) ? code : text;
    }

    /// <summary>The payment page's address of <paramref name="record"/>'s order (or its status, or its new invoice).</summary>
    public virtual string PagePath(string action, SattlemeOrder record, bool check = false)
    {
        ArgumentNullException.ThrowIfNull(record);
        object values = check
            ? new { area = "", orderGuid = record.OrderGuid, token = record.PageToken, check = 1 }
            : new { area = "", orderGuid = record.OrderGuid, token = record.PageToken };
        return Path(action, "SattlemePayment", values);
    }

    private AdminInvoiceModel PrepareAdminInvoice(SattlemeInvoice invoice)
    {
        var stored = StoredInvoice.Of(invoice);
        var model = new AdminInvoiceModel
        {
            Reference = invoice.Reference,
            State = Resource("State." + StateName(invoice)),
            Created = Time(invoice.CreatedOnUtc),
            LastCheck = invoice.CheckedOnUtc is { } checkedOn ? Time(checkedOn) : null,
            NextCheck = !invoice.Final && invoice.NextCheckOnUtc is { } next ? Time(next) : null,
            Error = string.IsNullOrEmpty(invoice.Error) || invoice.Error == "abandoned" ? null : ErrorText(invoice.Error),
            Shredded = invoice.Shredded,
            Amount = $"{invoice.PriceAmount} {invoice.PriceCurrency}",
        };
        if (stored is null)
        {
            return model;
        }

        model.Note = stored.Note;
        model.Amount = $"{Sats(stored.Sats)} sats ({Btc(stored.Sats)} BTC), {stored.FiatAmount ?? invoice.PriceAmount} {stored.FiatCurrency ?? invoice.PriceCurrency}";
        if (stored.RatePrice is not null)
        {
            model.Rate = string.Format(CultureInfo.CurrentCulture, Resource("Admin.RateValue"),
                stored.RatePrice, stored.FiatCurrency, stored.RateSource,
                stored.RateFetchedAt is { } fetched ? Time(fetched, "G") : "");
        }
        model.Expires = Time(stored.ExpiresAt, "G");
        // Paid in Lightning, what came is the invoice's own amount: its status has no received.
        var received = stored is { State: "paid", Method: "lightning" } ? stored.Sats : stored.Received;
        model.Received = stored.State is null ? null : $"{Sats(received)} sats";
        model.Confirmations = stored.Confirmations;
        model.Address = stored.OnchainAddress;
        model.AddressLink = stored.OnchainAddress is { } address && _address.IsMatch(address);
        model.Txids = [.. stored.Txids.Where(txid => _txid.IsMatch(txid))];
        model.TransactionsAtAddress = model.Txids.Count == 0 && model.AddressLink && stored.Received > 0 && stored.Method != "lightning";
        model.Proofs = [.. stored.Preimages.Select(preimage => new AdminProofModel { Preimage = preimage, PaymentHash = PaymentHashOf(preimage) })];
        return model;
    }

    /// <summary>
    /// The state of an invoice, as the admin reads it: ended underpaid (not unpaid) when something
    /// came; a first check that failed, as such (not "waiting for its first check").
    /// </summary>
    private static string StateName(SattlemeInvoice invoice)
    {
        if (invoice.Error == "abandoned")
        {
            return "abandoned";
        }
        if (invoice.InvoiceJson is null)
        {
            return "none";
        }
        return invoice.State switch
        {
            null when !string.IsNullOrEmpty(invoice.Error) => "uncheckedFailed",
            null => "unchecked",
            "expired" when invoice.Received > 0 => "expiredUnderpaid",
            var state => state,
        };
    }

    /// <summary>The payment hash a preimage proves: its SHA-256, in hex; null when it is not 32 bytes of hex.</summary>
    public static string? PaymentHashOf(string preimage)
    {
        ArgumentNullException.ThrowIfNull(preimage);
        return _preimage.IsMatch(preimage)
            ? Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(preimage)))
            : null;
    }

    private string Path(string action, string controller, object values)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var path = httpContext is null
            ? _linkGenerator.GetPathByAction(action, controller, values)
            : _linkGenerator.GetPathByAction(httpContext, action, controller, values);
        return path ?? "";
    }

    private string Resource(string name)
    {
        return _localizationService.GetResource(SattlemeDefaults.ResourcePrefix + name);
    }

    private string? PriceOf(StoredInvoice stored)
    {
        if (stored.FiatAmount is null || stored.FiatCurrency is null
            || !decimal.TryParse(stored.FiatAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }
        try
        {
            return _currencyService.CreateMoney(amount, stored.FiatCurrency, true, false).ToString();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return $"{stored.FiatAmount} {stored.FiatCurrency}";
        }
    }

    private string Time(long unixSeconds, string format)
    {
        return _dateTimeHelper.ConvertToUserTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime, DateTimeKind.Utc)
            .ToString(format, CultureInfo.CurrentCulture);
    }

    private string Time(DateTime utc)
    {
        return _dateTimeHelper.ConvertToUserTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc), DateTimeKind.Utc)
            .ToString("G", CultureInfo.CurrentCulture);
    }

    private static string PhaseName(PaymentPhase phase)
    {
        return phase.ToString().ToLowerInvariant();
    }

    /// <summary>An amount in sats, as the customer's language writes numbers.</summary>
    private static string Sats(long sats)
    {
        return sats.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>An amount in sats, in bitcoins, exactly: 49 000 is 0.00049.</summary>
    public static string Btc(long sats)
    {
        return (sats / 100_000_000m).ToString("0.########", CultureInfo.InvariantCulture);
    }

    /// <summary>What the page shows, in a few characters: its script reloads it when this changes.</summary>
    private static string Version(PaymentPhase phase, SattlemeInvoice? invoice, StoredInvoice? stored)
    {
        var shown = string.Join('|', phase, invoice?.Id, invoice?.Error, stored?.Received, stored?.RestBolt11, stored?.RestPaymentUri, stored?.Confirmations);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shown)))[..16];
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TxidPattern();

    [GeneratedRegex("^[a-z0-9]{14,90}$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}
