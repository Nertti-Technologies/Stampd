using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Stampd.Tools.Notices;

/// <summary>
/// One third-party component as it appears in the notices: its licence identifier, the licence
/// text to reproduce, and any NOTICE / THIRD-PARTY-NOTICES files it ships.
/// </summary>
/// <param name="Name">Package id or component name.</param>
/// <param name="Version">Resolved version.</param>
/// <param name="License">SPDX expression, or "file:&lt;name&gt;" for packages with custom terms.</param>
/// <param name="LicenseText">Text to reproduce; null when none could be found (a policy violation).</param>
/// <param name="TextSource">Where the licence text came from, for provenance.</param>
/// <param name="Notices">NOTICE / THIRD-PARTY-NOTICES texts shipped by the component, carried forward verbatim.</param>
/// <param name="ProjectUrl">Project home page from the package metadata, when stated.</param>
internal sealed record Component(
    string Name,
    string Version,
    string License,
    string? LicenseText,
    string TextSource,
    IReadOnlyList<string> Notices,
    string? ProjectUrl)
{
    private static readonly string[] LicenseFilePrefixes = ["license", "licence", "copying"];
    private static readonly string[] NoticeFilePrefixes = ["notice", "third-party-notices", "thirdpartynotices"];

    public static Component FromPackage(PackageRef package, NoticeInputs inputs)
    {
        string? nuspec = Directory.Exists(package.Folder)
            ? Directory.EnumerateFiles(package.Folder, "*.nuspec").FirstOrDefault()
            : null;
        if (nuspec is null)
            return new Component(package.Id, package.Version, "UNKNOWN", null, "package not restored", [], null);

        var meta = XDocument.Load(nuspec).Descendants().First(e => e.Name.LocalName == "metadata");
        string? Element(string name) => meta.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var licenseEl = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        string? projectUrl = Element("projectUrl");
        string? repositoryUrl = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "repository")
            ?.Attribute("url")?.Value ?? projectUrl;
        var notices = new List<string>();

        string license;
        string? text = null;
        string source;

        if (licenseEl is not null && licenseEl.Attribute("type")?.Value == "file")
        {
            license = "file:" + licenseEl.Value.Trim();
            string path = Path.Combine(package.Folder, licenseEl.Value.Trim().Replace('\\', '/'));
            text = File.Exists(path) ? File.ReadAllText(path) : null;
            source = "package licence file";
        }
        else
        {
            // Expression (preferred) or a legacy licenseUrl, which resolves through an override input.
            license = licenseEl?.Value.Trim() ?? "URL:" + (Element("licenseUrl") ?? "none");
            // An override is an explicit, reviewed decision, so it wins over what the package ships.
            string? packaged = RootFile(package.Folder, LicenseFilePrefixes);
            if (inputs.Override(package.Id) is { } ov) { text = ov.Text; license = ov.License ?? license; source = ov.Source; }
            else if (packaged is not null) { text = File.ReadAllText(packaged); source = "package " + Path.GetFileName(packaged); }
            else if (inputs.Repository(repositoryUrl) is { } repo)
            {
                text = repo.Licence.Text;
                source = repo.Licence.Source;
                if (repo.Notice is not null) notices.Add(repo.Notice.Text);
            }
            else if (inputs.Spdx(license) is { } spdx) { text = spdx.Text; source = spdx.Source + " (copyright not stated in the package)"; }
            else source = "none found";
        }

        notices.AddRange(Directory.EnumerateFiles(package.Folder)
            .Where(f => HasPrefix(f, NoticeFilePrefixes) && !f.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText));

        return new Component(package.Id, package.Version, license, text, source, notices, projectUrl);
    }

    private static string? RootFile(string folder, string[] prefixes) =>
        Directory.EnumerateFiles(folder).Where(f => HasPrefix(f, prefixes)).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();

    private static bool HasPrefix(string path, string[] prefixes) =>
        prefixes.Any(p => Path.GetFileName(path).StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Hand-curated inputs under notices/inputs; each file records its source and retrieval date.</summary>
internal sealed partial class NoticeInputs(string folder)
{
    public sealed record Input(string Text, string Source, string? License);

    /// <summary>Licence text for a package that ships none, from notices/inputs/overrides/&lt;id&gt;.txt.</summary>
    public Input? Override(string packageId) => Read(Path.Combine(folder, "overrides", packageId + ".txt"));

    /// <summary>
    /// Licence (and NOTICE, if any) of the package's GitHub repository, from
    /// notices/inputs/repos/&lt;owner&gt;__&lt;repo&gt;[.NOTICE].txt, written by notices/fetch-repo-licence.sh.
    /// </summary>
    public (Input Licence, Input? Notice)? Repository(string? url)
    {
        if (url is null) return null;
        var m = GitHubRepo().Match(url);
        if (!m.Success) return null;
        string stem = Path.Combine(folder, "repos", $"{m.Groups[1].Value}__{m.Groups[2].Value}");
        // Repository names are case-insensitive on GitHub; match the fetched file regardless of case.
        string? licencePath = Directory.Exists(Path.GetDirectoryName(stem))
            ? Directory.EnumerateFiles(Path.GetDirectoryName(stem)!, "*.txt")
                .FirstOrDefault(f => string.Equals(f, stem + ".txt", StringComparison.OrdinalIgnoreCase))
            : null;
        if (licencePath is null || Read(licencePath) is not { } licence) return null;
        return (licence, Read(licencePath[..^".txt".Length] + ".NOTICE.txt"));
    }

    /// <summary>Standard text for a single SPDX identifier, from notices/inputs/spdx/&lt;id&gt;.txt.</summary>
    public Input? Spdx(string expression) =>
        SpdxId().IsMatch(expression) ? Read(Path.Combine(folder, "spdx", expression + ".txt")) : null;

    public IEnumerable<Component> NonNuGetComponents()
    {
        string path = Path.Combine(folder, "non-nuget.json");
        if (!File.Exists(path)) yield break;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            string license = c.GetProperty("license").GetString()!;
            var text = Read(Path.Combine(folder, c.GetProperty("textFile").GetString()!)) ?? Spdx(license);
            string usage = c.GetProperty("usage").GetString()!;
            yield return new Component(
                c.GetProperty("name").GetString()!, c.GetProperty("version").GetString()!, license,
                text?.Text, $"{text?.Source ?? "none found"}; {usage}", [], c.GetProperty("url").GetString());
        }
    }

    // Input files start with "Source: <url>", "Retrieved: <date>" and optionally "License: <spdx>"
    // header lines, then a blank line, then the verbatim text.
    private static Input? Read(string path)
    {
        if (!File.Exists(path)) return null;
        string raw = File.ReadAllText(path).ReplaceLineEndings("\n");
        int split = raw.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0) throw new InvalidOperationException($"{path}: missing header block.");
        var header = raw[..split].Split('\n')
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!header.TryGetValue("Source", out string? source) || !header.TryGetValue("Retrieved", out string? date))
            throw new InvalidOperationException($"{path}: header must state Source and Retrieved.");
        header.TryGetValue("License", out string? license);
        return new Input(raw[(split + 2)..], $"{source} (retrieved {date})", license);
    }

    [GeneratedRegex(@"^[A-Za-z0-9.\-+]+$")]
    private static partial Regex SpdxId();

    [GeneratedRegex(@"github\.com/([^/]+)/([^/.]+(?:\.[^/.]+)*?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRepo();
}
