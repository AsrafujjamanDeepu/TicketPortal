using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TicketPortal.Api.Services
{
    // Chunk 6 / C6-3 — hardening for the API's OUTBOUND calls to an operator's own ERP.
    //
    // Two things an administrator types into an OperatorIntegration record are dangerous if the
    // server trusts them blindly, because the server then makes a network request on the
    // administrator's behalf, from inside our network, carrying a credential:
    //
    //   1. SecretReference  — which secret to send. Before this chunk, "env:NAME" could name ANY
    //                         configuration key or environment variable (for example JWT:SigningKey
    //                         or a connection string), and a literal value was accepted too. Now it
    //                         is a name inside ONE dedicated configuration section only.
    //   2. BaseUrl          — where to send it. Before this chunk it could point at loopback,
    //                         private networks or a cloud metadata address (SSRF), over plain HTTP,
    //                         and a redirect could carry the request somewhere else entirely.
    //
    // Everything here is deliberately small, static and side-effect free so it can be unit-tested
    // without a database.

    // Thrown when an integration cannot be called safely (bad destination, unusable secret). The
    // message is written to IntegrationSyncLog.ErrorMessage and shown to administrators, so it
    // must never contain a secret value — and none of the code that creates it puts one there.
    public class IntegrationConfigurationException : Exception
    {
        public IntegrationConfigurationException(string message) : base(message) { }
    }

    // ---------------------------------------------------------------------------------------
    // Secret references
    // ---------------------------------------------------------------------------------------
    public static class IntegrationSecretReference
    {
        // The only accepted shape is "env:NAME". The prefix is kept (rather than renamed) so rows
        // seeded before this chunk — "env:HANIF_ERP_API_KEY" — stay valid with no data migration.
        public const string Prefix = "env:";

        // NAME is looked up ONLY under this configuration section. In a deployment that means the
        // environment variable Integrations__Secrets__NAME, user-secrets, or a secrets provider —
        // never an arbitrary key like JWT:SigningKey, and never an unrelated process variable.
        public const string ConfigurationSection = "Integrations:Secrets";

        private static readonly Regex NamePattern =
            new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Returns null when the reference is acceptable (a missing/blank reference means "this
        // integration has no secret", which is valid for AuthType None), otherwise a message that
        // is safe to show the administrator. The offending text itself is never echoed back,
        // because someone pasting a real key into this field is exactly the mistake to contain.
        public static string? Validate(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            return TryGetName(reference, out _)
                ? null
                : $"SecretReference must look like '{Prefix}NAME' (letters, digits and underscores, starting with a letter), " +
                  $"naming a value under configuration section '{ConfigurationSection}'. " +
                  "Literal secrets and other formats are not accepted.";
        }

        public static bool TryGetName(string? reference, out string name)
        {
            name = string.Empty;
            if (string.IsNullOrWhiteSpace(reference)
                || !reference.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = reference[Prefix.Length..];
            if (!NamePattern.IsMatch(candidate))
            {
                return false;
            }

            name = candidate;
            return true;
        }

        // Returns the secret value, or null when the reference is blank, malformed, or its value
        // is not configured. Callers decide whether a missing secret is fatal for their AuthType.
        public static string? Resolve(IConfiguration configuration, string? reference)
        {
            if (!TryGetName(reference, out var name))
            {
                return null;
            }

            var value = configuration[$"{ConfigurationSection}:{name}"];
            return string.IsNullOrEmpty(value) ? null : value;
        }

        // Removes every occurrence of the secret from text that is about to be logged or stored
        // (error messages, operator response bodies). A response that echoes the API key back is
        // rare but real, and sync-log rows are readable by administrators.
        public static string? Redact(string? text, string? secret)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret) || secret.Length < 4)
            {
                return text;
            }

            return text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Other administrator-typed values that end up in an outbound request
    // ---------------------------------------------------------------------------------------
    public static class IntegrationInputRules
    {
        private static readonly Regex HeaderNamePattern =
            new("^[A-Za-z0-9-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Headers the HTTP stack or the destination's routing depend on — an "API key header"
        // must never be able to override them.
        private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Cookie", "Idempotency-Key",
        };

        private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "POST", "PUT", "PATCH", "DELETE",
        };

        public static string? ValidateApiKeyHeaderName(string? headerName)
        {
            if (string.IsNullOrWhiteSpace(headerName))
            {
                return null;
            }

            if (!HeaderNamePattern.IsMatch(headerName.Trim()))
            {
                return "ApiKeyHeaderName may contain only letters, digits and hyphens.";
            }

            return ReservedHeaders.Contains(headerName.Trim())
                ? $"ApiKeyHeaderName cannot be '{headerName.Trim()}'."
                : null;
        }

        public static string? ValidateEndpoint(string? httpMethod, string? pathTemplate)
        {
            if (string.IsNullOrWhiteSpace(httpMethod) || !AllowedMethods.Contains(httpMethod.Trim()))
            {
                return "HttpMethod must be one of GET, POST, PUT, PATCH or DELETE.";
            }

            if (string.IsNullOrWhiteSpace(pathTemplate)
                || !pathTemplate.StartsWith('/')
                || pathTemplate.StartsWith("//", StringComparison.Ordinal)
                || pathTemplate.Contains("://", StringComparison.Ordinal)
                || pathTemplate.Contains('\\')
                || pathTemplate.Contains("..", StringComparison.Ordinal))
            {
                return "PathTemplate must be a path starting with a single '/' (no scheme, host, backslashes or '..').";
            }

            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------------------------
    public sealed record IntegrationSecurityOptions(bool AllowLocalDestinations)
    {
        // Development (the committed appsettings.Development.json also sets this) may call a
        // mock ERP on localhost over plain HTTP. Everywhere else the default is "no": HTTPS and a
        // public address are required. Program.cs refuses to start in Production with it on.
        public const string AllowLocalDestinationsKey = "Integrations:AllowLocalDestinations";

        public static IntegrationSecurityOptions From(IConfiguration configuration, IHostEnvironment environment)
        {
            var configured = configuration.GetValue<bool?>(AllowLocalDestinationsKey);
            return new IntegrationSecurityOptions(configured ?? environment.IsDevelopment());
        }
    }

    // ---------------------------------------------------------------------------------------
    // Destination policy (SSRF guard)
    // ---------------------------------------------------------------------------------------
    public sealed record DestinationCheckResult(bool IsAllowed, string? Reason)
    {
        public static DestinationCheckResult Allowed { get; } = new(true, null);
        public static DestinationCheckResult Refused(string reason) => new(false, reason);
    }

    public static class ErpDestinationPolicy
    {
        // Used when this policy needs DNS. Tests substitute their own so they can prove that a
        // harmless-looking host name that RESOLVES to a private address is refused.
        public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

        public static readonly HostResolver SystemResolver =
            (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

        // True when it is acceptable to open a connection to this address.
        //
        //   Never allowed:  unspecified/"this network" (0.0.0.0/8, ::), link-local
        //                   (169.254.0.0/16 — includes the cloud metadata address — and fe80::/10),
        //                   site-local, multicast, reserved/broadcast (224.0.0.0 and up).
        //   Local ranges:   loopback, RFC 1918 private, carrier-grade NAT, unique-local IPv6,
        //                   documentation/benchmark/protocol-assignment blocks — allowed ONLY when
        //                   allowLocalDestinations is true (Development talking to a mock ERP).
        //   Everything else is treated as public and allowed.
        public static bool IsAddressAllowed(IPAddress address, bool allowLocalDestinations)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();

                if (b[0] == 0) return false;                       // 0.0.0.0/8
                if (b[0] == 169 && b[1] == 254) return false;      // link-local + cloud metadata
                if (b[0] >= 224) return false;                     // multicast, reserved, broadcast

                var isLocal =
                    b[0] == 127                                             // loopback
                    || b[0] == 10                                           // 10.0.0.0/8
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)            // 172.16.0.0/12
                    || (b[0] == 192 && b[1] == 168)                         // 192.168.0.0/16
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)           // 100.64.0.0/10 (CGNAT)
                    || (b[0] == 192 && b[1] == 0 && b[2] == 0)              // 192.0.0.0/24
                    || (b[0] == 192 && b[1] == 0 && b[2] == 2)              // 192.0.2.0/24 (docs)
                    || (b[0] == 198 && (b[1] == 18 || b[1] == 19))          // 198.18.0.0/15
                    || (b[0] == 198 && b[1] == 51 && b[2] == 100)           // 198.51.100.0/24 (docs)
                    || (b[0] == 203 && b[1] == 0 && b[2] == 113);           // 203.0.113.0/24 (docs)

                return allowLocalDestinations || !isLocal;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.Equals(IPAddress.IPv6Any)) return false;
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;

                var b = address.GetAddressBytes();

                // IPv4-compatible (::a.b.c.d) and 6to4 (2002:a.b.c.d::/16) embed an IPv4 address
                // that must pass the same checks as if it had been written in IPv4 form.
                var firstTwelveZero = true;
                for (var i = 0; i < 12; i++)
                {
                    if (b[i] != 0) { firstTwelveZero = false; break; }
                }

                if (firstTwelveZero && !address.Equals(IPAddress.IPv6Loopback))
                {
                    return IsAddressAllowed(new IPAddress(new[] { b[12], b[13], b[14], b[15] }), allowLocalDestinations);
                }

                if (b[0] == 0x20 && b[1] == 0x02)
                {
                    return IsAddressAllowed(new IPAddress(new[] { b[2], b[3], b[4], b[5] }), allowLocalDestinations);
                }

                var isLocal =
                    address.Equals(IPAddress.IPv6Loopback)                  // ::1
                    || (b[0] & 0xFE) == 0xFC                                // fc00::/7 unique-local
                    || address.IsIPv6Teredo                                 // 2001::/32
                    || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) // 64:ff9b::/96 NAT64
                    || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8); // 2001:db8::/32 docs

                return allowLocalDestinations || !isLocal;
            }

            return false; // Unknown address family — never connect to it.
        }

        // Full check of an OperatorIntegration.BaseUrl, used both when it is saved (so the
        // administrator gets a clear 400) and again right before every outbound call (so a record
        // that was valid when saved but whose DNS changed since is still refused).
        public static async Task<DestinationCheckResult> ValidateBaseUrlAsync(
            string? baseUrl,
            bool allowLocalDestinations,
            HostResolver? resolver = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return DestinationCheckResult.Refused("BaseUrl is required.");
            }

            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            {
                return DestinationCheckResult.Refused("BaseUrl must be an absolute URL such as https://erp.example.com/api.");
            }

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            {
                return DestinationCheckResult.Refused("BaseUrl must use http or https.");
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !allowLocalDestinations)
            {
                return DestinationCheckResult.Refused("BaseUrl must use HTTPS outside Development.");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return DestinationCheckResult.Refused("BaseUrl must not contain a user name or password.");
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                return DestinationCheckResult.Refused("BaseUrl must not contain a query string or fragment.");
            }

            IPAddress[] addresses;
            var host = uri.DnsSafeHost;

            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                try
                {
                    addresses = await (resolver ?? SystemResolver)(host, cancellationToken);
                }
                catch (SocketException)
                {
                    return DestinationCheckResult.Refused($"The host '{host}' could not be resolved.");
                }
            }

            if (addresses.Length == 0)
            {
                return DestinationCheckResult.Refused($"The host '{host}' could not be resolved.");
            }

            // EVERY address must be acceptable — a name that resolves to one public and one
            // private address could otherwise be connected to on the private one.
            foreach (var address in addresses)
            {
                if (!IsAddressAllowed(address, allowLocalDestinations))
                {
                    return DestinationCheckResult.Refused(
                        $"The host '{host}' resolves to a non-public address ({address}). " +
                        "Integrations may only call public internet addresses" +
                        (allowLocalDestinations ? " (local development addresses are allowed here, but not link-local/metadata ranges)." : "."));
                }
            }

            return DestinationCheckResult.Allowed;
        }
    }

    // ---------------------------------------------------------------------------------------
    // HTTP handler
    // ---------------------------------------------------------------------------------------
    public static class ErpHttpHandlerFactory
    {
        // The primary handler for the ExternalBookingSyncService typed client.
        //
        //  - AllowAutoRedirect = false: a 3xx from the operator is returned as-is and treated as a
        //    failed call, instead of silently re-sending the request (and the API-key header) to
        //    whatever address the response named.
        //  - UseProxy = false: validation happens against the real destination; a system proxy
        //    would turn that into "whatever the proxy can reach".
        //  - ConnectCallback: resolves the host itself and refuses to connect to a disallowed
        //    address. BaseUrl was already validated, but DNS can change between that check and
        //    this connection ("DNS rebinding"); checking the address actually being connected to
        //    closes that gap. TLS is negotiated on top of the stream this returns, so HTTPS
        //    certificate checks and SNI are unaffected.
        public static HttpMessageHandler Create(IntegrationSecurityOptions options)
        {
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var endpoint = context.DnsEndPoint;

                    var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
                        ? new[] { literal }
                        : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);

                    if (addresses.Length == 0
                        || addresses.Any(a => !ErpDestinationPolicy.IsAddressAllowed(a, options.AllowLocalDestinations)))
                    {
                        throw new HttpRequestException(
                            "The operator integration destination is not a permitted network address.");
                    }

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
        }
    }
}
