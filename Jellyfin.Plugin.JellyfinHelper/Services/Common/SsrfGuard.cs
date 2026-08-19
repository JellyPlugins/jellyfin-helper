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
    private static readonly IPAddress AwsImdsV4 = IPAddress.Parse("169.254.169.254");
    private static readonly IPAddress AlibabaImds = IPAddress.Parse("100.100.100.200");
    private static readonly IPAddress AwsImdsV6 = IPAddress.Parse("fd00:ec2::254");

    /// <summary>
    ///     Returns <see langword="true" /> if <paramref name="host" /> is a well-known cloud
    ///     instance-metadata endpoint (AWS/Azure IMDS, GCP, Alibaba).
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

        // Hostname-based checks (GCP uses a DNS name, not an IP).
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
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
            || ip.Equals(AwsImdsV6);
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
}
