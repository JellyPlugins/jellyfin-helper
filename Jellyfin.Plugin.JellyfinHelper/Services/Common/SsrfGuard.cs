using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Shared SSRF guard for outbound integration URLs (Arr / Seerr connection tests and API calls).
/// </summary>
internal static class SsrfGuard
{
    // Well-known cloud instance-metadata IP literals. Named constants so the raw addresses
    // live in one place (the whole point of this guard is to block precisely these endpoints).
    private const string AwsImdsV4Address = "169.254.169.254";   // AWS & GCP share this IPv4 address
    private const string AlibabaImdsAddress = "100.100.100.200";
    private const string AwsImdsV6Address = "fd00:ec2::254";
    private const string GcpImdsV6Address = "fd20:ce::254";       // GCP metadata IPv6

    // Blocked metadata IP addresses (resolved once for efficient comparison).
    private static readonly IPAddress AwsImdsV4 = IPAddress.Parse(AwsImdsV4Address);
    private static readonly IPAddress AlibabaImds = IPAddress.Parse(AlibabaImdsAddress);
    private static readonly IPAddress AwsImdsV6 = IPAddress.Parse(AwsImdsV6Address);
    private static readonly IPAddress GcpImdsV6 = IPAddress.Parse(GcpImdsV6Address);

    /// <summary>
    ///     Returns true if host is a well-known cloud instance-metadata endpoint (AWS/Azure IMDS, GCP IPv4/IPv6/DNS, Alibaba).
    /// </summary>
    /// <param name="host">The host component of the target URI (e.g. <c>Uri.Host</c>).</param>
    /// <returns><see langword="true" /> if the host must be blocked.</returns>
    public static bool IsCloudMetadataHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Hostname-based checks (GCP exposes metadata under multiple DNS names). metadata.goog is the short alias documented by GCP alongside metadata.google.internal.
        var hostname = host.EndsWith('.') ? host[..^1] : host;
        if (hostname.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || hostname.Equals("metadata.goog", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Strip brackets from IPv6 literals (e.g. "[::1]" -> "::1").
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
    ///     Throws ArgumentException if is a blocked cloud metadata endpoint. Intended for use inside service-layer URL validation so the block cannot be bypassed by callers that skip the controller.
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
    ///     Produces a credential-free label for an endpoint URL, safe to echo back to clients and write to logs.
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
