using System.Globalization;
using System.Xml;

namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>
/// The module's texts of one language, read from its resource file as Smartstore imports it
/// (XmlResourceManager: the file flattened by Smartstore's own code, each name prefixed with the
/// module's root key unless AppendRootKey is false, each value trimmed).
/// </summary>
public sealed class Texts
{
    /// <summary>The module's root key (module.json, ResourceRootKey).</summary>
    public const string RootKey = "Plugins.Sattleme.Bitcoin";

    private readonly Dictionary<string, string> _values;

    private Texts(Dictionary<string, string> values)
    {
        _values = values;
    }

    /// <summary>The names and values.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The resource file of <paramref name="culture"/> (en-us, fr-fr, de-de, it-it) of the module's sources.</summary>
    public static Texts Of(string culture)
    {
        return Read(Path.Combine(Repository.Module, "Localization", $"resources.{culture}.xml"));
    }

    /// <summary>The resource file <paramref name="path"/>.</summary>
    public static Texts Read(string path)
    {
        var source = new XmlDocument();
        source.Load(path);
        var flat = ResourceFiles.Flatten(source);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlElement resource in flat.SelectNodes("//Language/LocaleResource")!)
        {
            var name = resource.GetAttribute("Name").Trim();
            if (!string.Equals(resource.GetAttribute("AppendRootKey"), "false", StringComparison.OrdinalIgnoreCase))
            {
                name = RootKey + "." + name;
            }
            values[name] = resource.SelectSingleNode("Value")?.InnerText ?? "";
        }
        return new Texts(values);
    }

    /// <summary>The value of <paramref name="name"/>, or null.</summary>
    public string? Get(string name)
    {
        return _values.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The module's resource <paramref name="name"/> (after its prefix), filled in.</summary>
    public string Format(string name, params object[] arguments)
    {
        return string.Format(CultureInfo.GetCultureInfo("en-US"), _values[SattlemeDefaults.ResourcePrefix + name], arguments);
    }
}
