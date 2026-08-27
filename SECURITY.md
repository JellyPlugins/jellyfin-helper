## Supported Versions

| Version | Jellyfin Compatibility | Support Status |
| :--- | :--- | :--- |
| **3.0.x.x** | Jellyfin 12.0+ | :white_check_mark: Supported (Active) |
| **<= 2.1.0.6** | Jellyfin <= 10.11.x | :x: End of Life (No Fixes / Unsupported) |

> **Breaking Change & EOL Policy:** With the release of version 3.0.0.0, the entire 2.x branch (including version 2.1.0.6) has reached End of Life (EOL). No further updates, security patches, or backports will be provided for 2.x. Jellyfin Helper 3.0.0+ exclusively supports Jellyfin v12 and newer.

---

## Defense-in-Depth Security Controls

Jellyfin Helper processes media libraries, external API keys, and filesystem operations. To ensure data safety and system integrity, the plugin implements strict backend safety mechanisms:

* **Filesystem Isolation & Path Traversal Protection (`PathValidator`, `ReparsePointGuard`)**
  * Cleanup, deletion, link repair, and trash relocation operations are strictly sandboxed within configured media library directories.
  * Explicit rejection of sensitive system paths (e.g., `/config`, `/etc`, `C:\Windows`).
  * Fail-closed checks for symlinks, junctions, and reparse points (`ReparsePointGuard`) prevent infinite recursion and path-traversal escapes outside media roots.

* **API Key & Credential Masking (`ApiKeyMaskResolver`)**
  * API keys for integrated services (Overseerr, Jellyseerr, Radarr, Sonarr) are masked in responses (`ConfigurationResponse`) and never sent in plain text to the web frontend.
  * Backup exports redact credentials by default and set audit flags (`ContainsSecrets`, `CredentialsChanged`) when secrets are modified or restored.

* **Server-Side Request Forgery (SSRF) Protection (`SsrfGuard`)**
  * Outbound integration endpoints (Radarr, Sonarr, Seerr connection tests) validate URLs against internal IP ranges and explicitly block cloud metadata endpoints (e.g., AWS IMDS `169.254.169.254`, Azure metadata).

* **Denial-of-Service (DoS) & Memory Safety (`HttpResponseReader`, `LimitedStream`)**
  * All external HTTP responses are bounded via `LimitedStream` to prevent Out-Of-Memory (OOM) attacks from oversized external payloads.
  * Timeline aggregation and discovery endpoints enforce rate-limiting. Data file writes are atomic (`AtomicFile`) to prevent disk corruption on abrupt shutdowns.

* **Access Control & Privilege Elevation**
  * Administrative endpoints enforce Jellyfin's `RequiresElevation` authorization policy.
  * User-facing features (such as Seerr Discovery) enforce bitwise permission checks (`SeerrPermissions`) and respect global access toggles (`DiscoveryUserAccessEnabled`).

* **Input Validation & Output Sanitization**
  * Prevention of Stored XSS via strict UI text escaping across all dashboard tabs.
  * Input fields sanitize null bytes (`\0`), control characters, and header injection patterns before storage.

---

## Reporting a Vulnerability

If you discover a potential security vulnerability within Jellyfin Helper, please report it responsibly. **Do not open a public GitHub issue for undisclosed vulnerabilities.**

### How to Submit
* **Preferred Method:** Submit a private report via **[GitHub Private Vulnerability Reporting](https://github.com/JellyPlugins/jellyfin-helper/security/advisories/new)**.
* **Alternative:** If private reporting is unavailable, contact the project maintainers directly via the official JellyPlugins repository channels.

### Please Include:
* A detailed description of the issue and potential impact.
* Step-by-step instructions or a Minimal Working Example (MWE) to reproduce the vulnerability.
* Affected plugin versions and host environment details (e.g., OS, Jellyfin version, Docker vs. Bare-Metal).

---

## Response & Disclosure Process

1. **Acknowledgment:** We aim to acknowledge receipt of security reports within **48 hours**.
2. **Assessment:** The report will be triaged and validated within **5 business days**.
3. **Patch & Release:** Critical vulnerabilities receive high-priority fixes targeted for release within **14 days**.
4. **Coordinated Disclosure:** Security advisories (CVEs) will be published via GitHub Security Advisories upon the release of a patched version.
