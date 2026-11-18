using System.Text.Json;

namespace Stampd.Tools.Notices;

/// <summary>
/// The licence-policy gate (notices/policy.json): which licences may ship, and which packages
/// with custom terms the owner has explicitly reviewed. Anything else fails the build.
/// </summary>
internal sealed class LicencePolicy
{
    private readonly HashSet<string> _allowed;
    private readonly Dictionary<string, string> _reviewed;

    private LicencePolicy(HashSet<string> allowed, Dictionary<string, string> reviewed)
    {
        _allowed = allowed;
        _reviewed = reviewed;
    }

    public static LicencePolicy Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var allowed = doc.RootElement.GetProperty("allowed").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewed = doc.RootElement.GetProperty("reviewed").EnumerateArray()
            .ToDictionary(e => e.GetProperty("package").GetString()!, e => e.GetProperty("reason").GetString()!,
                          StringComparer.OrdinalIgnoreCase);
        return new LicencePolicy(allowed, reviewed);
    }

    public IEnumerable<string> Check(Component c)
    {
        string who = $"{c.Name} {c.Version}";
        if (_reviewed.ContainsKey(c.Name))
        {
            if (c.LicenseText is null) yield return $"{who}: reviewed package has no licence text to reproduce.";
            yield break;
        }
        if (!IsAllowed(c.License))
            yield return $"{who}: licence '{c.License}' is not on the allow list; owner review required.";
        else if (c.LicenseText is null)
            yield return $"{who}: no licence text found; add notices/inputs/overrides/{c.Name}.txt.";
    }

    // "A OR B" ships if either is allowed; "A AND B" (or WITH) needs every part allowed.
    private bool IsAllowed(string expression)
    {
        string e = expression.Trim('(', ')', ' ');
        string[] any = e.Split(" OR ", StringSplitOptions.TrimEntries);
        return any.Any(alt => alt.Split([" AND ", " WITH "], StringSplitOptions.TrimEntries).All(_allowed.Contains));
    }
}
