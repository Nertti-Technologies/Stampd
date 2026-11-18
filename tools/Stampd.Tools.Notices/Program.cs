// Generates THIRD-PARTY-NOTICES.txt from the restored dependency graph of every shipped
// project (src/**), and enforces the licence policy in notices/policy.json.
//
//   dotnet run --project tools/Stampd.Tools.Notices            regenerate the file
//   dotnet run --project tools/Stampd.Tools.Notices -- --check fail if stale or a policy violation
//
// Requires a prior `dotnet restore` (it reads obj/project.assets.json). Offline and
// deterministic: the same lock files always produce the same bytes.

using Stampd.Tools.Notices;

bool check = args.Contains("--check");
string root = RepoRoot.Find(Directory.GetCurrentDirectory());

var policy = LicencePolicy.Load(Path.Combine(root, "notices", "policy.json"));
var inputs = new NoticeInputs(Path.Combine(root, "notices", "inputs"));

var packages = DependencyGraph.CollectShippedPackages(root);
var components = packages.Select(p => Component.FromPackage(p, inputs)).ToList();
components.AddRange(inputs.NonNuGetComponents());

var violations = components.SelectMany(policy.Check).ToList();
foreach (string v in violations) Console.Error.WriteLine($"licence policy: {v}");

string text = NoticesWriter.Render(components);
string outputPath = Path.Combine(root, "THIRD-PARTY-NOTICES.txt");

if (check)
{
    string current = File.Exists(outputPath) ? File.ReadAllText(outputPath) : string.Empty;
    if (!string.Equals(current, text, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "THIRD-PARTY-NOTICES.txt is out of date. Run: dotnet run --project tools/Stampd.Tools.Notices");
        return 1;
    }
    if (violations.Count > 0) return 1;
    Console.WriteLine($"notices up to date: {components.Count} components, policy clean.");
    return 0;
}

File.WriteAllText(outputPath, text);
Console.WriteLine($"wrote {outputPath}: {components.Count} components.");
return violations.Count > 0 ? 1 : 0;
