using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Sattleme.Bitcoin.Providers;
using Sattleme.Bitcoin.Tests.Support;
using Smartstore.Core.Packaging;
using Smartstore.Engine.Modularity;

namespace Sattleme.Bitcoin.Tests;

/// <summary>
/// The module as it ships: its archive is a package Smartstore installs (read here by Smartstore's
/// own code), holding exactly the module's files and none of Smartstore's; its assemblies are of its
/// own names with every embedded type under the prefix (never Sattleme.Checkout.dll nor
/// NBitcoin.Secp256k1.dll: another module may load other versions of those into the same host);
/// and the pinned copies it is built from are unchanged.
/// </summary>
[TestFixture]
public sealed class PackageTests
{
    private const string Folder = "Modules/Sattleme.Bitcoin/";

    /// <summary>The assemblies of the archive: the module's, and the SDK, NBitcoin.Secp256k1 and the QR code generator under its names.</summary>
    private static readonly string[] _assemblies =
    [
        "Sattleme.Bitcoin.dll",
        "Sattleme.Bitcoin.Sdk.dll",
        "Sattleme.Bitcoin.Secp256k1.dll",
        "Sattleme.Bitcoin.QrCode.dll",
    ];

    private static readonly string[] _embedded = ["Sattleme.Bitcoin.Sdk", "Sattleme.Bitcoin.Secp256k1", "Sattleme.Bitcoin.QrCode"];

    /// <summary>Names no assembly of the module may have, nor reference: the original packages', and QRCoder (others may ship it).</summary>
    private static readonly string[] _foreign = ["Sattleme.Checkout", "NBitcoin.Secp256k1", "NBitcoin", "Net.Codecrete.QrCodeGenerator", "QRCoder"];

    [Test]
    public void The_pinned_copies_are_unchanged()
    {
        foreach (var pin in Pins.Pins.All)
        {
            Assert.That(Pins.Pins.Check(Repository.Root, pin), Is.Empty, pin.Name);
        }
    }

    [Test]
    public void The_SDK_is_pinned_from_its_repository_at_a_commit()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Repository.File("sdk", "PIN.json")));
        var pin = document.RootElement;
        Assert.That(pin.GetProperty("repository").GetString(), Is.EqualTo("https://github.com/sattleme/checkout-dotnet"));
        Assert.That(pin.GetProperty("commit").GetString(), Does.Match("^[0-9a-f]{40}$"));
        Assert.That(pin.GetProperty("version").GetString(), Is.EqualTo("1.0.0"));
        Assert.That(pin.GetProperty("files").EnumerateObject().Count(), Is.GreaterThan(40));
    }

    private static List<string> Under(string directory, string pattern)
    {
        return [.. Directory.GetFiles(Path.Combine(Repository.Module, directory), pattern, SearchOption.AllDirectories)
            .Select(file => Folder + Path.GetRelativePath(Repository.Module, file).Replace('\\', '/'))];
    }

    [Test]
    public void The_archive_holds_the_manifest_and_the_module_s_folder_with_its_files_only()
    {
        Assert.That(File.Exists(Repository.Archive), Is.True, Repository.Archive);
        using var archive = ZipFile.OpenRead(Repository.Archive);
        var entries = archive.Entries.Where(entry => entry.Name.Length > 0).Select(entry => entry.FullName.Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

        var expected = _assemblies.Select(name => Folder + name)
            .Concat(["manifest.json", Folder + "module.json", Folder + "Sattleme.Bitcoin.deps.json"])
            .Concat(Under("Views", "*.cshtml"))
            .Concat(Under("Localization", "*.xml"))
            .Concat(Under("wwwroot", "*"))
            .Concat(new[] { "LICENSE", "Sattleme.Checkout.LICENSE", "NBitcoin.Secp256k1.LICENSE", "QrCodeGenerator.LICENSE" }.Select(name => Folder + "licenses/" + name))
            .Order(StringComparer.Ordinal).ToList();
        Assert.That(entries, Is.EqualTo(expected));
        Assert.That(entries, Has.None.Contains("Smartstore").And.None.EqualTo(Folder + "Sattleme.Checkout.dll").And.None.EqualTo(Folder + "NBitcoin.Secp256k1.dll"));
    }

    [Test]
    public void Smartstore_reads_the_archive_as_the_package_of_module_Sattleme_Bitcoin_0_1_0()
    {
        // Smartstore's own reading of an uploaded package (ExtensionPackage: manifest.json, PackageInstaller).
        using var package = new ExtensionPackage(File.OpenRead(Repository.Archive), leaveOpen: false);

        Assert.That(package.Descriptor.ExtensionType, Is.EqualTo(ExtensionType.Module));
        Assert.That(package.Descriptor.Name, Is.EqualTo(SattlemeDefaults.ModuleSystemName));
        Assert.That(package.Descriptor.Version, Is.EqualTo(new Version(1, 0, 0)));
        Assert.That(package.Descriptor.MinAppVersion, Is.EqualTo(new Version(6, 4, 0)));
        Assert.That(package.Descriptor.Group, Is.EqualTo("Payment"));
        Assert.That(package.FileName, Is.EqualTo(Path.GetFileName(Repository.Archive)), "the name Smartstore's packager gives it");
        Assert.That(Repository.Version, Is.EqualTo("1.0.0"));
    }

    [Test]
    public void Smartstore_reads_the_module_s_manifest_as_compatible_with_itself()
    {
        using var archive = ZipFile.OpenRead(Repository.Archive);
        using var reader = new StreamReader(archive.GetEntry(Folder + "module.json")!.Open());
        var descriptor = ModuleDescriptor.ParseManifest(reader.ReadToEnd());

        Assert.That(descriptor.SystemName, Is.EqualTo(SattlemeDefaults.ModuleSystemName));
        Assert.That(descriptor.FriendlyName, Is.EqualTo("Bitcoin (sattle.me)"));
        Assert.That(descriptor.Group, Is.EqualTo("Payment"));
        Assert.That(descriptor.Version, Is.EqualTo(new Version(1, 0, 0)));
        Assert.That(descriptor.MinAppVersion, Is.EqualTo(new Version(6, 4, 0)));
        Assert.That(descriptor.ResourceRootKey, Is.EqualTo(Texts.RootKey));
        Assert.That(SmartstoreVersion.Version, Is.EqualTo(new Version(6, 4, 0, 0)).Or.EqualTo(new Version(6, 4, 0)), "the assemblies the tests run");
        Assert.That(SmartstoreVersion.IsAssumedCompatible(descriptor.MinAppVersion), Is.True);
        var authors = System.Xml.Linq.XDocument.Load(Repository.File("Directory.Build.props")).Descendants("Authors").Single().Value;
        Assert.That(descriptor.Author, Is.EqualTo(authors), "the author metadata: the company's name alone");
    }

    [Test]
    public void The_archive_s_files_are_those_the_tests_run()
    {
        using var archive = ZipFile.OpenRead(Repository.Archive);
        foreach (var name in _assemblies)
        {
            using var stream = archive.GetEntry(Folder + name)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var loaded = Loaded().Single(assembly => assembly.GetName().Name + ".dll" == name);
            Assert.That(Convert.ToHexString(SHA256.HashData(buffer.ToArray())), Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaded.Location)))), name);
        }
    }

    private static IEnumerable<System.Reflection.Assembly> Loaded()
    {
        yield return typeof(SattlemeProvider).Assembly;
        yield return typeof(SmartstoreSattleme.Sattleme.Checkout.SattlemeClient).Assembly;
        yield return typeof(SmartstoreSattleme.NBitcoin.Secp256k1.ECPrivKey).Assembly;
        yield return typeof(SmartstoreSattleme.Net.Codecrete.QrCodeGenerator.QrCode).Assembly;
    }

    private static IEnumerable<(string Name, MetadataReader Reader, PEReader Pe)> Assemblies()
    {
        foreach (var name in _assemblies)
        {
            var pe = new PEReader(File.OpenRead(Path.Combine(Repository.PackageFolder, name)));
            yield return (name, pe.GetMetadataReader(), pe);
        }
    }

    [Test]
    public void No_assembly_has_or_references_the_names_of_the_original_packages()
    {
        foreach (var (name, reader, pe) in Assemblies())
        {
            using (pe)
            {
                var own = reader.GetString(reader.GetAssemblyDefinition().Name);
                Assert.That(own + ".dll", Is.EqualTo(name));
                var references = reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name)).ToList();
                Assert.That(references.Intersect(_foreign), Is.Empty, name);
                Assert.That(references.Where(reference => reference.StartsWith("Sattleme.", StringComparison.Ordinal)), Is.SubsetOf(_embedded), name);
            }
        }
    }

    [Test]
    public void Every_type_of_the_embedded_assemblies_is_under_the_prefix()
    {
        foreach (var (name, reader, pe) in Assemblies())
        {
            using (pe)
            {
                if (name == "Sattleme.Bitcoin.dll")
                {
                    continue;
                }
                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle);
                    var space = reader.GetString(type.Namespace);
                    var typeName = reader.GetString(type.Name);
                    if (!type.GetDeclaringType().IsNil || typeName == "<Module>" || typeName.StartsWith('<'))
                    {
                        continue;
                    }
                    var isPublic = (type.Attributes & System.Reflection.TypeAttributes.VisibilityMask) == System.Reflection.TypeAttributes.Public;
                    // The compiler's own attributes (Nullable…, Embedded): internal, one per assembly.
                    if (!isPublic && space is "Microsoft.CodeAnalysis" or "System.Runtime.CompilerServices" or "System.Diagnostics.CodeAnalysis")
                    {
                        continue;
                    }
                    Assert.That(space, Does.StartWith("SmartstoreSattleme."), $"{name}: {space}.{typeName}");
                }
            }
        }
    }

    [Test]
    public void The_module_reaches_the_SDK_through_the_prefixed_names_only()
    {
        var module = typeof(SattlemeProvider).Assembly;
        var referenced = module.GetReferencedAssemblies().Select(reference => reference.Name).ToList();
        Assert.That(referenced, Does.Contain("Sattleme.Bitcoin.Sdk").And.Contain("Sattleme.Bitcoin.QrCode"));
        Assert.That(referenced.Intersect(_foreign), Is.Empty);
        Assert.That(typeof(SmartstoreSattleme.Sattleme.Checkout.SattlemeClient).Assembly.GetName().Name, Is.EqualTo("Sattleme.Bitcoin.Sdk"));
    }

    [Test]
    public void The_module_references_no_assembly_Smartstore_does_not_ship()
    {
        // Whatever the module references, the host loads: Smartstore's release has it, or the module ships it.
        var shipped = Directory.GetFiles(Repository.Smartstore, "*.dll").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var framework = new[] { "System", "Microsoft.AspNetCore", "Microsoft.Extensions", "Microsoft.Net", "Microsoft.CSharp", "netstandard", "mscorlib", "Microsoft.Win32" };
        foreach (var (name, reader, pe) in Assemblies())
        {
            using (pe)
            {
                foreach (var handle in reader.AssemblyReferences)
                {
                    var reference = reader.GetString(reader.GetAssemblyReference(handle).Name);
                    var known = shipped.Contains(reference) || _embedded.Contains(reference)
                        || framework.Any(prefix => reference == prefix || reference.StartsWith(prefix + ".", StringComparison.Ordinal));
                    Assert.That(known, Is.True, $"{name} references {reference}");
                }
            }
        }
    }
}
