using System;

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
    /// <summary>
    ///     Returns <see langword="true" /> if <paramref name="host" /> is a well-known cloud
    ///     instance-metadata endpoint (AWS/Azure IMDS, GCP, Alibaba).
    /// </summary>
    /// <param name="host">The host component of the target URI (e.g. <c>Uri.Host</c>).</param>
    /// <returns><see langword="true" /> if the host must be blocked.</returns>
    public static bool IsCloudMetadataHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.Equals("169.254.169.254", StringComparison.OrdinalIgnoreCase) // AWS / Azure IMDS (link-local)
            || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) // GCP metadata
            || host.Equals("100.100.100.200", StringComparison.OrdinalIgnoreCase) // Alibaba Cloud IMDS
            || host.Equals("fd00:ec2::254", StringComparison.OrdinalIgnoreCase) // AWS IPv6 IMDS (bare)
            || host.Equals("[fd00:ec2::254]", StringComparison.OrdinalIgnoreCase); // AWS IPv6 IMDS (bracketed)
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
