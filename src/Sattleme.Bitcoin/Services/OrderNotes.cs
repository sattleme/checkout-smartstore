using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// The notes the module writes in an order's history (the admin's order page, Notes), in the admin
/// area's default language, never shown to the customer. They never hold an address or a transaction
/// (those lead to the customer's coins: the admin's tab shows them from the invoice, which the
/// removal of the customer's personal data empties).
/// </summary>
public class OrderNotes
{
    private readonly SmartDbContext _db;
    private readonly ILanguageService _languageService;
    private readonly ILocalizationService _localizationService;
    private readonly LocalizationSettings _localizationSettings;
    private readonly TimeProvider _time;

    /// <summary>The notes, written to <paramref name="db"/>.</summary>
    public OrderNotes(SmartDbContext db,
        ILanguageService languageService,
        ILocalizationService localizationService,
        LocalizationSettings localizationSettings,
        TimeProvider time)
    {
        _db = db;
        _languageService = languageService;
        _localizationService = localizationService;
        _localizationSettings = localizationSettings;
        _time = time;
    }

    /// <summary>
    /// Adds the note <paramref name="resource"/> (a resource of the module, a composite format) to
    /// the order <paramref name="orderId"/>, with <paramref name="arguments"/> (numbers of sats
    /// written as the admin language writes numbers, a <see cref="NoteResource"/> as its text in
    /// that language).
    /// </summary>
    public virtual async Task AddAsync(int orderId, string resource, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var text = await TextOfAsync(resource, arguments);
        _db.OrderNotes.Add(new OrderNote
        {
            OrderId = orderId,
            Note = text,
            DisplayToCustomer = false,
            CreatedOnUtc = _time.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds the note <paramref name="resource"/> as <see cref="AddAsync"/> does, unless the order has
    /// a note of the same text already (a check that wrote it, then stopped before its end).
    /// </summary>
    public virtual async Task AddOnceAsync(int orderId, string resource, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var text = await TextOfAsync(resource, arguments);
        if (await _db.OrderNotes.AnyAsync(note => note.OrderId == orderId && note.Note == text))
        {
            return;
        }
        await AddAsync(orderId, resource, arguments);
    }

    /// <summary>The text of the note <paramref name="resource"/> with <paramref name="arguments"/>, in the admin area's language.</summary>
    public virtual async Task<string> TextOfAsync(string resource, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var languageId = _localizationSettings.DefaultAdminLanguageId;
        if (languageId == 0)
        {
            languageId = await _languageService.GetMasterLanguageIdAsync();
        }
        var language = await _db.Languages.FindByIdAsync(languageId, false);
        var culture = CultureOf(language?.LanguageCulture);
        var format = TextOf(resource, languageId);
        var values = new object[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i] = arguments[i] switch
            {
                long sats => sats.ToString("N0", culture),
                NoteResource argument => TextOf(argument.Name, languageId),
                var other => other,
            };
        }
        try
        {
            return string.Format(culture, format, values);
        }
        catch (FormatException)
        {
            return format + " " + string.Join(" ", values);
        }
    }

    /// <summary>The module's resource <paramref name="name"/> in the language <paramref name="languageId"/>; the name itself when it has none.</summary>
    private string TextOf(string name, int languageId)
    {
        var text = _localizationService.GetResource(SattlemeDefaults.ResourcePrefix + name, languageId,
            logIfNotFound: false, defaultValue: "", returnEmptyIfNotFound: true);
        return string.IsNullOrEmpty(text) ? name : text;
    }

    private static CultureInfo CultureOf(string? name)
    {
        try
        {
            return string.IsNullOrEmpty(name) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}

/// <summary>An argument of a note: the module's resource <paramref name="Name"/>, in the note's language.</summary>
/// <param name="Name">The resource's name, after the module's prefix.</param>
public sealed record NoteResource(string Name);
