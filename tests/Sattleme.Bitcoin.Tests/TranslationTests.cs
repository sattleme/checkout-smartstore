using System.Text.RegularExpressions;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;

namespace Sattleme.Bitcoin.Tests;

/// <summary>
/// The module's texts: English, French, German and Italian, the same resources and placeholders in
/// each, every resource the code and the views use present and every one used, read as Smartstore
/// imports a module's resource files (Localization/resources.{culture}.xml).
/// </summary>
[TestFixture]
public sealed partial class TranslationTests
{
    private static readonly string[] _cultures = ["en-us", "fr-fr", "de-de", "it-it"];

    /// <summary>The names Smartstore itself reads: the module's and its payment method's localized names and description.</summary>
    private static readonly string[] _smartstoreNames =
    [
        "Plugins.FriendlyName.Sattleme.Bitcoin",
        "Plugins.Description.Sattleme.Bitcoin",
        "Plugins.FriendlyName.Payments.Sattleme",
    ];

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [Test]
    public void The_resource_files_are_those_Smartstore_looks_for()
    {
        var files = Directory.GetFiles(Path.Combine(Repository.Module, "Localization")).Select(Path.GetFileName).Order().ToList();
        Assert.That(files, Is.EqualTo(_cultures.Select(culture => $"resources.{culture}.xml").Order()));
    }

    [Test]
    public void Every_language_has_every_resource()
    {
        var english = Texts.Of("en-us").Values;
        Assert.That(english, Has.Count.GreaterThan(100));
        foreach (var culture in _cultures)
        {
            var texts = Texts.Of(culture).Values;
            Assert.That(texts.Keys, Is.EquivalentTo(english.Keys), culture);
            Assert.That(texts.Values, Has.All.Not.Empty, culture);
            Assert.That(texts.Keys.Except(_smartstoreNames), Has.All.StartWith(SattlemeDefaults.ResourcePrefix), culture);
        }
    }

    [Test]
    public void Every_translation_has_the_placeholders_of_the_English_text()
    {
        var english = Texts.Of("en-us").Values;
        foreach (var culture in _cultures.Skip(1))
        {
            var texts = Texts.Of(culture).Values;
            foreach (var (name, value) in english)
            {
                var expected = Placeholder().Matches(value).Select(match => match.Groups[1].Value).Order().ToList();
                var actual = Placeholder().Matches(texts[name]).Select(match => match.Groups[1].Value).Order().ToList();
                Assert.That(actual, Is.EqualTo(expected), $"{culture}: {name}");
                Assert.DoesNotThrow(() => string.Format(System.Globalization.CultureInfo.InvariantCulture, texts[name], [.. Enumerable.Range(0, 5).Select(i => (object)i)]), $"{culture}: {name}");
            }
        }
    }

    [Test]
    public void German_is_spelled_as_in_Switzerland()
    {
        Assert.That(Texts.Of("de-de").Values.Values, Has.None.Contains("ß"));
    }

    /// <summary>The module's resources its code and views name: literally, or built from a known list.</summary>
    private static IEnumerable<string> Used()
    {
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.GetFiles(Repository.Module, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Repository.Module, "Views"), "*.cshtml", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal));
        var literal = new Regex("\"Plugins\\.Sattleme\\.Bitcoin\\.([A-Za-z_.]*[A-Za-z_])\"");
        var relative = new Regex("(?:ResourcePrefix \\+ |(?<=[ (!])Resource\\(|Refused\\(|AddAsync\\([^,]+, |AddNoteAsync\\([^,]+, [^,]+, |NoteResource\\(|\\? |: |=> |\"\\*)\"?((?:[A-Z][A-Za-z]*\\.)*[A-Z][A-Za-z]*(?:\\.[A-Za-z_]+)?)\"");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in literal.Matches(text))
            {
                yield return match.Groups[1].Value;
            }
            foreach (Match match in relative.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (name.Contains('.', StringComparison.Ordinal) && !name.StartsWith("Plugins.", StringComparison.Ordinal) && !name.StartsWith("Admin.Common.", StringComparison.Ordinal))
                {
                    yield return name;
                }
            }
        }
        foreach (var phase in Enum.GetNames<PaymentPhase>())
        {
            yield return "Order.Phase." + phase.ToLowerInvariant();
        }
        yield return "Order.Phase.expiredUnderpaid";
        foreach (var state in new[] { "none", "unchecked", "uncheckedFailed", "open", "unconfirmed", "paid", "expired", "expiredUnderpaid", "abandoned" })
        {
            yield return "State." + state;
        }
        foreach (var status in Enum.GetNames<KeyStatus>())
        {
            yield return "Key." + status;
        }
        foreach (var code in ErrorCodes())
        {
            yield return "ErrorCode." + code;
        }
        // The configuration's fields (LocalizedDisplay("*…")) and the hints Smartstore's labels show.
        foreach (var field in new[] { "IntegrationKey", "RemoveKey", "MinutesToPay", "AdditionalFee", "AdditionalFeePercentage" })
        {
            yield return field;
            yield return field + ".Hint";
        }
    }

    /// <summary>Every error code a record can hold: the SDK's (its exceptions' codes) and the module's own.</summary>
    private static string[] ErrorCodes()
    {
        return ["network", "timeout", "invalid_key", "invalid_option", "invalid_order", "revoked", "channel_not_found", "invalid_invoice",
            "unsupported_currency", "rate", "checkout_unavailable", "onchain_unavailable", "mismatch", "apply", "error", "no_key", "busy", "currency"];
    }

    [TestCase("en-us")]
    [TestCase("fr-fr")]
    [TestCase("de-de")]
    [TestCase("it-it")]
    public void Says_how_a_payment_came_only_where_the_SDK_s_status_tells_it(string culture)
    {
        // unconfirmed, expired with received, open with received: the status gives no method.
        var texts = Texts.Of(culture).Values;
        foreach (var name in new[] { "Note.Unconfirmed", "Note.Underpaid", "Pay.Underpaid", "Pay.ExpiredUnderpaid", "Order.Phase.expiredUnderpaid", "Admin.Warning.Underpaid" })
        {
            var text = texts[SattlemeDefaults.ResourcePrefix + name];
            Assert.That(text, Does.Not.Contain("on-chain").IgnoreCase.And.Not.Contain("Lightning").IgnoreCase, $"{culture}: {name}");
        }
        // Never a hedge on how it came.
        foreach (var (name, text) in texts)
        {
            Assert.That(text, Does.Not.Contain("if one was made").And.Not.Contain("s'il y en a eu un").And.Not.Contain("falls eine erfolgte").And.Not.Contain("se ce n'è stato uno"), $"{culture}: {name}");
        }
    }

    [Test]
    public void Every_resource_the_module_uses_is_translated()
    {
        var english = Texts.Of("en-us");
        var used = Used().Distinct().ToList();
        Assert.That(used, Has.Count.GreaterThan(100));
        Assert.That(used.Where(name => english.Get(SattlemeDefaults.ResourcePrefix + name) is null), Is.Empty);
    }

    [Test]
    public void Every_resource_of_the_files_is_used()
    {
        var used = Used().Select(name => SattlemeDefaults.ResourcePrefix + name).Concat(_smartstoreNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That(Texts.Of("en-us").Values.Keys.Where(name => !used.Contains(name)), Is.Empty);
    }

    [Test]
    public void Every_error_code_of_the_SDK_has_a_text()
    {
        var english = Texts.Of("en-us");
        var sdk = typeof(SmartstoreSattleme.Sattleme.Checkout.SattlemeException).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(SmartstoreSattleme.Sattleme.Checkout.SattlemeException)) && type.IsPublic && !type.IsAbstract)
            .ToList();
        Assert.That(sdk, Has.Count.GreaterThanOrEqualTo(11));
        var codes = new SmartstoreSattleme.Sattleme.Checkout.SattlemeException[]
        {
            new SmartstoreSattleme.Sattleme.Checkout.NetworkException("x"), new SmartstoreSattleme.Sattleme.Checkout.RevokedException(),
            new SmartstoreSattleme.Sattleme.Checkout.ChannelNotFoundException(false), new SmartstoreSattleme.Sattleme.Checkout.InvalidKeyException(),
            new SmartstoreSattleme.Sattleme.Checkout.InvalidOptionException("p", "m"), new SmartstoreSattleme.Sattleme.Checkout.InvalidOrderException("p", "m"),
            new SmartstoreSattleme.Sattleme.Checkout.InvalidInvoiceException("m"), new SmartstoreSattleme.Sattleme.Checkout.UnsupportedCurrencyException("XXX", "s"),
            new SmartstoreSattleme.Sattleme.Checkout.RateException("m"), new SmartstoreSattleme.Sattleme.Checkout.CheckoutUnavailableException(),
            new SmartstoreSattleme.Sattleme.Checkout.OnchainUnavailableException(),
        }.Select(exception => exception.Code);
        foreach (var code in codes)
        {
            Assert.That(english.Get(SattlemeDefaults.ResourcePrefix + "ErrorCode." + code), Is.Not.Null, code);
        }
    }

    [Test]
    public void Names_the_module_and_its_method_through_Smartstore_s_localized_names()
    {
        // Smartstore's admin reads "Plugins.FriendlyName.{SystemName}" and "Plugins.Description.{SystemName}"
        // of a module and of a provider (ModuleDescriptorExtensions, ProviderManager).
        var english = Texts.Of("en-us");
        Assert.That(english.Get("Plugins.FriendlyName." + SattlemeDefaults.ModuleSystemName), Is.EqualTo("Bitcoin (sattle.me)"));
        Assert.That(english.Get("Plugins.FriendlyName." + SattlemeDefaults.SystemName), Is.EqualTo("Bitcoin"));
        Assert.That(english.Get("Plugins.Description." + SattlemeDefaults.ModuleSystemName), Is.Not.Empty);
    }
}
