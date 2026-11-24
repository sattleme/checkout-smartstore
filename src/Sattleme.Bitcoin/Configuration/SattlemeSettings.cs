using Smartstore.Core.Configuration;

namespace Sattleme.Bitcoin.Configuration;

/// <summary>
/// The module's settings, in Smartstore's settings store, per store where the admin overrides them
/// (as Smartstore's own payment modules). Whether the method is offered, its name and description at
/// checkout, the stores and the rules it is limited to are Smartstore's own (Configuration → Payment
/// methods). The integration key is not one of these: it is written alone, encrypted
/// (<see cref="Services.IntegrationKeys"/>), so that no settings form ever binds it.
/// </summary>
public class SattlemeSettings : ISettings
{
    /// <summary>How many minutes the customer has to pay an invoice: 1 to 60.</summary>
    public int MinutesToPay { get; set; } = SattlemeDefaults.DefaultMinutes;

    /// <summary>An additional fee, as Smartstore's own payment methods charge one.</summary>
    public decimal AdditionalFee { get; set; }

    /// <summary>Whether <see cref="AdditionalFee"/> is a percentage of the order's total.</summary>
    public bool AdditionalFeePercentage { get; set; }
}
