using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Options;

namespace Stampd.WebApi.Services;

/// <summary>Settings for webhook targets, bound from <c>Stampd:Webhooks</c>.</summary>
public sealed class WebhookTargetOptions
{
    /// <summary>
    /// Host names that may resolve to loopback, link-local or private addresses, for
    /// on-premises receivers. Matched exactly, ignoring case. Empty by default, so
    /// webhooks reach public addresses only.
    /// </summary>
    public IList<string> AllowedPrivateHosts { get; } = [];
}

/// <summary>
/// Decides whether a webhook may be sent to an address. Webhook URLs are supplied by
/// API callers, so without this a caller could make the server POST to loopback,
/// cloud metadata (169.254.169.254) or other internal services.
/// </summary>
/// <remarks>
/// Checked twice: when a webhook is registered (a friendly 400), and on every
/// connection the delivery worker opens (<see cref="ConnectAsync"/>), against the
/// address actually being dialled. The second check is the one that holds: DNS can
/// change after registration.
/// </remarks>
public sealed class WebhookTargetPolicy
{
    private readonly HashSet<string> _allowedPrivateHosts;

    public WebhookTargetPolicy(IOptions<WebhookTargetOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _allowedPrivateHosts = new HashSet<string>(
            options.Value.AllowedPrivateHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsHostAllowlisted(string host) => _allowedPrivateHosts.Contains(host);

    /// <summary>
    /// Registration-time check. Returns an error message, or null when the URL may be
    /// used. Host names are resolved, so a name pointing at an internal address is refused.
    /// </summary>
    public async Task<string?> ValidateAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (IsHostAllowlisted(url.IdnHost))
        {
            return null;
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(url.IdnHost, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(url.IdnHost, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return "Url host could not be resolved.";
            }
        }

        return addresses.Length == 0 || addresses.Any(IsBlocked)
            ? "Url must point at a public address. Add internal receivers to Stampd:Webhooks:AllowedPrivateHosts."
            : null;
    }

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> for the delivery client: resolves
    /// the host, refuses blocked addresses unless the host is allowlisted, then connects
    /// to an address that passed the check.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        var allowlisted = IsHostAllowlisted(host);
        var permitted = addresses.Where(a => allowlisted || !IsBlocked(a)).ToArray();
        if (permitted.Length == 0)
        {
            throw new HttpRequestException("Webhook target resolves to a blocked address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True for addresses a webhook must not reach: unspecified, loopback, link-local
    /// (including cloud metadata), private, carrier-grade NAT, unique-local and multicast.
    /// IPv4-mapped IPv6 addresses are checked as IPv4.
    /// </summary>
    internal static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8
                || b[0] == 10                                  // 10.0.0.0/8
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64.0.0/10
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                || b[0] >= 224;                                // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal                     // fe80::/10
                || address.IsIPv6SiteLocal                     // fec0::/10
                || address.IsIPv6Multicast                     // ff00::/8
                || (b[0] & 0xFE) == 0xFC;                      // fc00::/7
        }

        return true;
    }
}
