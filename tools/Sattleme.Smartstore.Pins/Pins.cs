using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sattleme.Pins;

/// <summary>
/// A pinned copy: the files of one repository at one commit, unchanged, in a directory of this
/// repository, with a PIN.json naming the repository, the commit, the version and each file's
/// SHA-256 (as the SDKs pin the specification in their spec/).
/// </summary>
/// <param name="Name">What the commands call it: <c>sdk</c>, <c>secp256k1</c>, <c>qrcode</c>.</param>
/// <param name="Directory">Its directory in this repository.</param>
/// <param name="Repository">The repository it comes from.</param>
/// <param name="DefaultRef">What a clone is read at when no commit is given: its HEAD, or a tag.</param>
/// <param name="VersionFile">The file of the copy that says its version.</param>
/// <param name="VersionPattern">Where the version is in that file (its first group).</param>
/// <param name="Selects">Whether a file of the repository (its path, with /) belongs to the copy.</param>
public sealed record PinDefinition(
    string Name,
    string Directory,
    string Repository,
    string DefaultRef,
    string VersionFile,
    string VersionPattern,
    Func<string, bool> Selects);

/// <summary>The pinned copies of this repository, their check and their import.</summary>
public static class Pins
{
    #region Fields

    /// <summary>The file of a copy that pins it.</summary>
    public const string PinFile = "PIN.json";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    #endregion

    #region Properties

    /// <summary>
    /// The SDK (Sattleme.Checkout): the files its package builds from (its project and sources,
    /// the build's props, global.json and .editorconfig, and what the package carries: README,
    /// CHANGELOG, LICENSE), and the conformance vectors of its cryptography the module's tests run
    /// against the SDK as the module ships it: the specification's NIP-44 and integration key
    /// vectors (with the specification's PIN.json, which hashes them), and BIP-340's own vectors.
    /// </summary>
    public static PinDefinition Sdk { get; } = new(
        "sdk",
        "sdk",
        "https://github.com/sattleme/checkout-dotnet",
        "HEAD",
        "src/Sattleme.Checkout/Sattleme.Checkout.csproj",
        "<Version>([^<]+)</Version>",
        path => path.StartsWith("src/Sattleme.Checkout/", StringComparison.Ordinal)
            || path is ".editorconfig" or "Directory.Build.props" or "Directory.Packages.props" or "global.json"
                or "LICENSE" or "README.md" or "CHANGELOG.md"
                or "spec/PIN.json" or "spec/vectors/nip44/nip44.vectors.json" or "spec/vectors/integration-keys.json"
                or "tests/Sattleme.Checkout.Tests/Vectors/bip340/test-vectors.csv"
                or "tests/Sattleme.Checkout.Tests/Vectors/bip340/LICENSE");

    /// <summary>
    /// NBitcoin.Secp256k1, the SDK's secp256k1: the files its package (NBitcoin.Secp256k1.csproj)
    /// compiles, at the tag of the version the SDK pins, with the project and the license.
    /// </summary>
    public static PinDefinition Secp256k1 { get; } = new(
        "secp256k1",
        "vendor/NBitcoin.Secp256k1",
        "https://github.com/MetacoSA/NBitcoin",
        "refs/tags/NBitcoin.Secp256k1/v4.0.4",
        "NBitcoin.Secp256k1/NBitcoin.Secp256k1.csproj",
        "<Version[^>]*>([^<]+)</Version>",
        path => (path.StartsWith("NBitcoin/Secp256k1/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
            || path is "NBitcoin/NullableShims.cs" or "NBitcoin.Secp256k1/NBitcoin.Secp256k1.csproj" or "LICENSE");

    /// <summary>
    /// The QR code generator (Net.Codecrete.QrCodeGenerator): the sources of its library project,
    /// the project and the license (not its signing key, Key.snk: the copy is compiled unsigned,
    /// under a name of the module's).
    /// </summary>
    public static PinDefinition QrCode { get; } = new(
        "qrcode",
        "vendor/QrCodeGenerator",
        "https://github.com/manuelbl/QrCodeGenerator",
        "refs/tags/v3.2.1",
        "QrCodeGenerator/QrCodeGenerator.csproj",
        "<Version>([^<]+)</Version>",
        path => (path.StartsWith("QrCodeGenerator/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal)
                && path.Count(c => c == '/') <= 2 && !path.StartsWith("QrCodeGenerator/docfx/", StringComparison.Ordinal))
            || path is "QrCodeGenerator/QrCodeGenerator.csproj" or "LICENSE");

    /// <summary>Every pinned copy.</summary>
    public static IReadOnlyList<PinDefinition> All { get; } = [Sdk, Secp256k1, QrCode];

    #endregion

    #region Methods

    /// <summary>
    /// What differs between the copy of <paramref name="pin"/> under <paramref name="root"/> (this
    /// repository) and its PIN.json: empty when it is the pinned one (every file of PIN.json there
    /// with its SHA-256, no other file, and PIN.json naming the repository, a commit, the version).
    /// </summary>
    public static IReadOnlyList<string> Check(string root, PinDefinition pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var problems = new List<string>();
        var directory = Path.Combine(root, pin.Directory);
        var pinPath = Path.Combine(directory, PinFile);
        if (!File.Exists(pinPath))
        {
            problems.Add($"{pin.Directory}/{PinFile} is missing");
            return problems;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(pinPath));
        var json = document.RootElement;
        if (StringOf(json, "repository") != pin.Repository)
        {
            problems.Add($"{pin.Directory}/{PinFile}: repository is not {pin.Repository}");
        }

        if (!Regex.IsMatch(StringOf(json, "commit") ?? "", "^[0-9a-f]{40}$"))
        {
            problems.Add($"{pin.Directory}/{PinFile}: commit is not a commit");
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (json.TryGetProperty("files", out var listed) && listed.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in listed.EnumerateObject())
            {
                files[file.Name] = file.Value.ValueKind == JsonValueKind.String ? file.Value.GetString()! : "";
            }
        }
        if (files.Count == 0)
        {
            problems.Add($"{pin.Directory}/{PinFile}: no files");
        }

        foreach (var (file, hash) in files)
        {
            var path = Path.Combine(directory, file);
            if (!pin.Selects(file))
            {
                problems.Add($"{pin.Directory}/{file} is not a file this copy takes");
            }

            if (!File.Exists(path))
            {
                problems.Add($"{pin.Directory}/{file} is missing");
            }
            else if (Sha256(File.ReadAllBytes(path)) != hash)
            {
                problems.Add($"{pin.Directory}/{file} differs from {PinFile}");
            }
        }

        foreach (var file in FilesUnder(directory))
        {
            if (file != PinFile && !files.ContainsKey(file))
            {
                problems.Add($"{pin.Directory}/{file} is not in {PinFile}");
            }
        }

        var versionText = files.ContainsKey(pin.VersionFile) && File.Exists(Path.Combine(directory, pin.VersionFile))
            ? File.ReadAllText(Path.Combine(directory, pin.VersionFile))
            : "";
        var version = Regex.Match(versionText, pin.VersionPattern);
        if (!version.Success || StringOf(json, "version") != version.Groups[1].Value)
        {
            problems.Add($"{pin.Directory}/{PinFile}: version is not the one {pin.VersionFile} says");
        }

        return problems;
    }

    /// <summary>
    /// Replaces the copy of <paramref name="pin"/> under <paramref name="root"/> with the files of
    /// the clone <paramref name="clone"/> at <paramref name="reference"/> (a commit, a tag; the
    /// pin's default when null), read from git's objects (whatever the clone's working tree holds),
    /// and writes its PIN.json. Gives the commit taken.
    /// </summary>
    public static string Import(string root, PinDefinition pin, string clone, string? reference)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var commit = Git(clone, "rev-parse", "--verify", $"{reference ?? pin.DefaultRef}^{{commit}}").Trim();
        var paths = Git(clone, "ls-tree", "-r", "--name-only", "-z", commit)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(pin.Selects)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0 || !paths.Contains(pin.VersionFile))
        {
            throw new InvalidOperationException($"{clone} at {commit} is not a clone of {pin.Repository}: {pin.VersionFile} is missing");
        }

        var directory = Path.Combine(root, pin.Directory);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var bytes = GitBytes(clone, "cat-file", "blob", $"{commit}:{path}");
            var target = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            files[path] = Sha256(bytes);
        }

        var version = Regex.Match(_utf8.GetString(File.ReadAllBytes(Path.Combine(directory, pin.VersionFile))), pin.VersionPattern);
        if (!version.Success)
        {
            throw new InvalidOperationException($"{pin.VersionFile} says no version");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("repository", pin.Repository);
            writer.WriteString("commit", commit);
            if (pin.DefaultRef.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                writer.WriteString("tag", pin.DefaultRef["refs/tags/".Length..]);
            }

            writer.WriteString("version", version.Groups[1].Value);
            writer.WriteStartObject("files");
            foreach (var (path, hash) in files)
            {
                writer.WriteString(path, hash);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        File.WriteAllBytes(Path.Combine(directory, PinFile), [.. stream.ToArray(), (byte)'\n']);
        return commit;
    }

    /// <summary>The SHA-256 of <paramref name="bytes"/>, in lowercase hex.</summary>
    public static string Sha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    #endregion

    #region Utilities

    private static string? StringOf(JsonElement json, string name)
    {
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IEnumerable<string> FilesUnder(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'));
    }

    private static string Git(string clone, params string[] arguments)
    {
        return _utf8.GetString(GitBytes(clone, arguments));
    }

    private static byte[] GitBytes(string clone, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(clone);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        using var output = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Result.Trim()}");
        }

        return output.ToArray();
    }

    #endregion
}
