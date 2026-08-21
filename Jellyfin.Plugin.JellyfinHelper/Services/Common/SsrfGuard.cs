using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Shared SSRF guard for outbound integration URLs (Arr / Seerr connection tests and API calls).
///     Blocks well-known cloud instance-metadata endpoints, which are never a legitimate integration
///     target and are the classic SSRF exfiltration sink.
///     <para>
///     RFC-1918 / loopback / link-local addresses are intentionally NOT blocked: Radarr/Sonarr/Seerr
///     commonly run on the same host or LAN as Jellyfin, so a private-range block would break the
///     plugin's primary legitimate configuration. This guard is enforced centrally so every code path
///     (controller endpoints AND the configuration-save path that calls the services directly) is
///     covered — a per-controller check alone could be bypassed via the service layer.
///     </para>
/// </summary>
internal static class SsrfGuard
{
    // Blocked metadata IP addresses (resolved once for efficient comparison).
    private static readonly IPAddress AwsImdsV4 = IPAddress.Parse("169.254.169.254");   // AWS & GCP share this IPv4 address
    private static readonly IPAddress AlibabaImds = IPAddress.Parse("100.100.100.200");
    private static readonly IPAddress AwsImdsV6 = IPAddress.Parse("fd00:ec2::254");
    private static readonly IPAddress GcpImdsV6 = IPAddress.Parse("fd20:ce::254");      // GCP metadata IPv6

    /// <summary>
    ///     Returns <see langword="true" /> if <paramref name="host" /> is a well-known cloud
    ///     instance-metadata endpoint (AWS/Azure IMDS, GCP IPv4/IPv6/DNS, Alibaba).
    ///     Blocked GCP endpoints: <c>metadata.google.internal</c>, <c>metadata.goog</c>,
    ///     <c>169.254.169.254</c> (shared with AWS), and <c>fd20:ce::254</c> (GCP IPv6).
    ///     Also catches IPv4-mapped IPv6 representations such as <c>::ffff:169.254.169.254</c>
    ///     or the bracketed form <c>[::ffff:169.254.169.254]</c>.
    /// </summary>
    /// <param name="host">The host component of the target URI (e.g. <c>Uri.Host</c>).</param>
    /// <returns><see langword="true" /> if the host must be blocked.</returns>
    public static bool IsCloudMetadataHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Hostname-based checks (GCP exposes metadata under multiple DNS names).
        // metadata.goog is the short alias documented by GCP alongside metadata.google.internal.
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.goog", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Strip brackets from IPv6 literals (e.g. "[::1]" → "::1").
        var raw = host.Length > 2 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;

        if (!IPAddress.TryParse(raw, out var ip))
        {
            return false;
        }

        // Normalise IPv4-mapped IPv6 (e.g. ::ffff:169.254.169.254) to plain IPv4.
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return ip.Equals(AwsImdsV4)
            || ip.Equals(AlibabaImds)
            || ip.Equals(AwsImdsV6)
            || ip.Equals(GcpImdsV6);
    }

    /// <summary>
    ///     Throws <see cref="ArgumentException" /> if <paramref name="host" /> is a blocked
    ///     cloud metadata endpoint. Intended for use inside service-layer URL validation so the
    ///     block cannot be bypassed by callers that skip the controller.
    /// </summary>
    /// <param name="host">The host component of the target URI.</param>
    /// <param name="paramName">The parameter name to attribute the exception to.</param>
    public static void ThrowIfCloudMetadataHost(string? host, string paramName)
    {
        if (IsCloudMetadataHost(host))
        {
            throw new ArgumentException("The target host is not a permitted destination.", paramName);
        }
    }

    /// <summary>
    ///     Produces a credential-free label for an endpoint URL, safe to echo back to clients and
    ///     write to logs. A valid HTTP(S) URL can embed user-info credentials
    ///     (e.g. <c>https://user:password@host</c>); reflecting or logging the raw string would
    ///     leak the password. This returns only the scheme, host, and port
    ///     (<see cref="UriComponents.SchemeAndServer" />), dropping any user-info, path, query,
    ///     and fragment. Only <c>http</c>/<c>https</c> endpoints are accepted; anything else —
    ///     non-absolute input, a bare path (which <see cref="Uri" /> resolves to <c>file://</c> on
    ///     Unix), or another scheme — falls back to a fixed placeholder so no raw credential-bearing
    ///     text is ever surfaced.
    /// </summary>
    /// <param name="url">The configured endpoint URL (may contain user-info credentials).</param>
    /// <returns>A scheme+host+port label, or <c>"(invalid URL)"</c> when parsing fails.</returns>
    public static string SafeEndpointLabel(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }

        return "(invalid URL)";
    }
}
