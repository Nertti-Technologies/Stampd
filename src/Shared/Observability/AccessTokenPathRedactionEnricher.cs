using Serilog.Core;
using Serilog.Events;

namespace Stampd.Observability;

/// <summary>
/// Replaces signing-link access tokens in the <c>RequestPath</c> log property with a
/// placeholder, so a token never reaches a log sink. The token is a bearer credential:
/// anyone who reads it from a log can open and sign the document.
/// </summary>
/// <remarks>
/// <para>
/// <c>RequestPath</c> appears on the request-completion event and, through the ASP.NET
/// Core request scope, on every other event logged during the request, so the enricher
/// rewrites the property wherever it is found rather than formatting one event.
/// </para>
/// <para>
/// Compiled into both Stampd.WebApi (<c>/api/sign/{accessToken}</c>) and Stampd.UI
/// (<c>/sign/{AccessToken}</c>) as a linked file. Redaction is fail-safe: every segment
/// after a token prefix is treated as a token, except the literal routes listed in
/// <see cref="NonTokenSegments"/>.
/// </para>
/// </remarks>
internal sealed class AccessTokenPathRedactionEnricher : ILogEventEnricher
{
    internal const string RequestPathProperty = "RequestPath";
    internal const string Placeholder = "{accessToken}";

    private static readonly string[] TokenPrefixes = ["/api/sign/", "/sign/"];

    // Literal routes that share a token prefix. Coupled to BinarySignEndpoint's
    // "/api/sign/binary" route: a new literal route under /api/sign/ must be added here,
    // or its name is logged as {accessToken} (safe, but misleading).
    private static readonly string[] NonTokenSegments = ["binary"];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Properties.TryGetValue(RequestPathProperty, out var value)
            && value is ScalarValue { Value: string path }
            && Redact(path) is var redacted
            && !ReferenceEquals(redacted, path))
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(RequestPathProperty, new ScalarValue(redacted)));
        }
    }

    /// <summary>
    /// Returns <paramref name="path"/> with the segment after a token prefix replaced by
    /// <see cref="Placeholder"/>, or the same instance when there is nothing to redact.
    /// </summary>
    internal static string Redact(string path)
    {
        foreach (var prefix in TokenPrefixes)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = path.AsSpan(prefix.Length);
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            if (segment.IsEmpty || IsNonTokenSegment(segment))
            {
                return path;
            }

            return string.Concat(path.AsSpan(0, prefix.Length), Placeholder, slash < 0 ? [] : rest[slash..]);
        }

        return path;
    }

    private static bool IsNonTokenSegment(ReadOnlySpan<char> segment)
    {
        foreach (var literal in NonTokenSegments)
        {
            if (segment.Equals(literal, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
