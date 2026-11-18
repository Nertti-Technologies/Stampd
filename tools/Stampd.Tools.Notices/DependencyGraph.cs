using System.Text.Json;

namespace Stampd.Tools.Notices;

/// <summary>A resolved NuGet package and the folder it was restored into.</summary>
internal sealed record PackageRef(string Id, string Version, string Folder);

/// <summary>
/// Walks obj/project.assets.json of every project under src/ and returns the packages that
/// reach a recipient. Direct references marked PrivateAssets="all" (SourceLink, EF Design,
/// analyzers) are excluded together with whatever only they pulled in.
/// </summary>
internal static class DependencyGraph
{
    public static IReadOnlyList<PackageRef> CollectShippedPackages(string root)
    {
        var found = new SortedDictionary<string, PackageRef>(StringComparer.OrdinalIgnoreCase);
        foreach (string project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            string assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            if (!File.Exists(assets))
                throw new InvalidOperationException($"{assets} not found; run `dotnet restore Stampd.slnx` first.");
            foreach (var p in ShippedPackages(assets)) found[$"{p.Id}/{p.Version}"] = p;
        }
        return found.Values.ToList();
    }

    private static IEnumerable<PackageRef> ShippedPackages(string assetsPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var rootEl = doc.RootElement;
        string packageFolder = rootEl.GetProperty("packageFolders").EnumerateObject().First().Name;

        foreach (var framework in rootEl.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            // Target entries are keyed "Name/Version" and only hold the resolved version of each name.
            var target = rootEl.GetProperty("targets").EnumerateObject()
                .First(t => string.Equals(t.Name, framework.Name, StringComparison.OrdinalIgnoreCase)).Value;
            var byName = target.EnumerateObject().ToDictionary(
                e => e.Name[..e.Name.IndexOf('/', StringComparison.Ordinal)], e => e, StringComparer.OrdinalIgnoreCase);

            var pending = new Stack<string>();
            if (framework.Value.TryGetProperty("dependencies", out var direct))
            {
                foreach (var dep in direct.EnumerateObject())
                {
                    bool privateAssets = dep.Value.TryGetProperty("suppressParent", out var sp)
                        && string.Equals(sp.GetString(), "All", StringComparison.OrdinalIgnoreCase);
                    if (!privateAssets) pending.Push(dep.Name);
                }
            }
            // Project references ship their own dependencies.
            foreach (var entry in byName.Values.Where(e => IsType(e.Value, "project"))) pending.Push(NameOf(entry));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                string name = pending.Pop();
                if (!seen.Add(name) || !byName.TryGetValue(name, out var entry)) continue;
                if (entry.Value.TryGetProperty("dependencies", out var deps))
                    foreach (var d in deps.EnumerateObject()) pending.Push(d.Name);
                if (!IsType(entry.Value, "package")) continue;

                string version = entry.Name[(entry.Name.IndexOf('/', StringComparison.Ordinal) + 1)..];
                string folder = Path.Combine(packageFolder, name.ToLowerInvariant(), version.ToLowerInvariant());
                yield return new PackageRef(name, version, folder);
            }
        }
    }

    private static bool IsType(JsonElement entry, string type) =>
        entry.TryGetProperty("type", out var t) && string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase);

    private static string NameOf(JsonProperty entry) => entry.Name[..entry.Name.IndexOf('/', StringComparison.Ordinal)];
}

/// <summary>Locates the repository root (the folder holding Stampd.slnx).</summary>
internal static class RepoRoot
{
    public static string Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Stampd.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Stampd.slnx not found above the current directory.");
    }
}
