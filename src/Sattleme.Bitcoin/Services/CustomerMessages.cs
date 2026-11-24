using System.Globalization;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Localization;

namespace Sattleme.Bitcoin.Services;

/// <summary>What the customer is told, in their language and time zone.</summary>
public class CustomerMessages
{
    private readonly IDateTimeHelper _dateTimeHelper;
    private readonly ILocalizationService _localizationService;

    /// <summary>The messages.</summary>
    public CustomerMessages(IDateTimeHelper dateTimeHelper, ILocalizationService localizationService)
    {
        _dateTimeHelper = dateTimeHelper;
        _localizationService = localizationService;
    }

    /// <summary>Why no invoice is made (<paramref name="made"/>'s refusal), in the working language.</summary>
    public virtual string Refusal(MakeResult made)
    {
        ArgumentNullException.ThrowIfNull(made);
        var text = _localizationService.GetResource(SattlemeDefaults.ResourcePrefix + made.Refusal);
        if (made.RefusalArguments.Length == 0)
        {
            return text;
        }
        var values = made.RefusalArguments.Select(argument => argument is DateTime utc ? Time(utc) : argument).ToArray();
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, values);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary><paramref name="utc"/> as the customer reads a time: in their time zone, short.</summary>
    public virtual string Time(DateTime utc)
    {
        var local = _dateTimeHelper.ConvertToUserTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc), DateTimeKind.Utc);
        return local.ToString("t", CultureInfo.CurrentCulture);
    }
}
