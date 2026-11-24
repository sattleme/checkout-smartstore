using Sattleme.Pins;

// The pinned copies (README.md, "Development"):
//   check                          every copy is the one its PIN.json pins, file by file
//   import <copy> <clone> [<ref>]  takes <copy> (sdk, secp256k1, qrcode) from a clone of its
//                                  repository at <ref> (a commit or a tag; by default the clone's
//                                  HEAD for the SDK, the pinned tag for the others)
var root = FindRoot();
switch (args)
{
    case ["check"]:
        var problems = Pins.All.SelectMany(pin => Pins.Check(root, pin)).ToList();
        foreach (var problem in problems)
        {
            Console.Error.WriteLine(problem);
        }

        if (problems.Count > 0)
        {
            return 1;
        }

        foreach (var pin in Pins.All)
        {
            Console.WriteLine($"{pin.Directory}: the pinned copy");
        }

        return 0;

    case ["import", var name, var clone, .. var rest] when rest.Length <= 1:
        var definition = Pins.All.FirstOrDefault(pin => pin.Name == name);
        if (definition is null)
        {
            Console.Error.WriteLine($"No copy {name}: {string.Join(", ", Pins.All.Select(pin => pin.Name))}");
            return 2;
        }
        var commit = Pins.Import(root, definition, Path.GetFullPath(clone), rest.Length == 1 ? rest[0] : null);
        Console.WriteLine($"{definition.Directory}: {definition.Repository} at {commit}");
        return 0;

    default:
        Console.Error.WriteLine("Usage: check | import <sdk|secp256k1|qrcode> <clone> [<commit or tag>]");
        return 2;
}

// This repository: the directory that holds global.json, from the working directory up.
static string FindRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "tools")))
        {
            return directory.FullName;
        }
    }
    throw new InvalidOperationException("Run it from within the repository");
}
