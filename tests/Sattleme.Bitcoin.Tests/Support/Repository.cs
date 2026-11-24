using System.Reflection;

namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>Where the repository, Smartstore's assemblies and the module's built archive are (written into this assembly by the build).</summary>
public static class Repository
{
    /// <summary>The repository's root.</summary>
    public static string Root => Metadata("RepositoryRoot");

    /// <summary>The module's sources.</summary>
    public static string Module => Path.Combine(Root, "src", "Sattleme.Bitcoin");

    /// <summary>Smartstore's assemblies the module is built against (scripts/smartstore.sh).</summary>
    public static string Smartstore => Metadata("SmartstoreDir");

    /// <summary>artifacts/&lt;configuration&gt;/: the archive, and what it holds (stage/).</summary>
    public static string Package => Metadata("ModulePackage");

    /// <summary>What the archive holds, as the build left it.</summary>
    public static string PackageStage => Path.Combine(Package, "stage");

    /// <summary>The module's folder in the archive, as the build left it.</summary>
    public static string PackageFolder => Path.Combine(PackageStage, "Modules", "Sattleme.Bitcoin");

    /// <summary>The module's version.</summary>
    public static string Version => Metadata("Version");

    /// <summary>The archive.</summary>
    public static string Archive => Path.Combine(Package, $"Smartstore.Module.Sattleme.Bitcoin.{Version}.zip");

    /// <summary>A file of the repository.</summary>
    public static string File(params string[] parts)
    {
        return Path.Combine([Root, .. parts]);
    }

    private static string Metadata(string key)
    {
        return typeof(Repository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value
            ?? throw new InvalidOperationException(key);
    }
}
