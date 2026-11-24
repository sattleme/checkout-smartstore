using System.ComponentModel.DataAnnotations;
using Smartstore.Web.Modelling;

namespace Sattleme.Bitcoin.Models;

/// <summary>
/// The configuration page. The integration key goes one way only: entered, verified, saved
/// encrypted, never shown again (<see cref="IntegrationKey"/> is always empty on the page).
/// </summary>
[LocalizedDisplay("Plugins.Sattleme.Bitcoin.")]
public class ConfigurationModel : ModelBase
{
    /// <summary>A new integration key (sattleme_ck_…), write-only.</summary>
    [LocalizedDisplay("*IntegrationKey")]
    [DataType(DataType.Password)]
    public string? IntegrationKey { get; set; }

    /// <summary>Remove the key of the scope shown.</summary>
    [LocalizedDisplay("*RemoveKey")]
    public bool RemoveKey { get; set; }

    /// <summary>What the page says of the key (none, saved, for every store, cannot be read).</summary>
    public string KeyStatus { get; set; } = "";

    /// <summary>Whether a key that can be read is saved for the scope shown.</summary>
    public bool HasKey { get; set; }

    /// <summary>How many minutes the customer has to pay an invoice: 1 to 60.</summary>
    [LocalizedDisplay("*MinutesToPay")]
    public int MinutesToPay { get; set; }

    /// <summary>An additional fee.</summary>
    [LocalizedDisplay("*AdditionalFee")]
    public decimal AdditionalFee { get; set; }

    /// <summary>Whether the fee is a percentage of the order's total.</summary>
    [LocalizedDisplay("*AdditionalFeePercentage")]
    public bool AdditionalFeePercentage { get; set; }

    /// <summary>A warning when the store's primary currency is not one the SDK converts; null otherwise.</summary>
    public string? CurrencyWarning { get; set; }

    /// <summary>The store's primary currency (the fee's).</summary>
    public string PrimaryCurrencyCode { get; set; } = "";
}
