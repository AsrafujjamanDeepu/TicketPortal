using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using TicketPortal.Api.Services;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // Chunk 6 / C6-3 — the pure parts of the outbound-ERP hardening: which addresses and URLs may
    // be called, which secret references are accepted and how they resolve, and what gets scrubbed
    // from stored text. No database and no network (DNS answers are supplied by the tests).
    public class IntegrationSecurityTests
    {
        private static ErpDestinationPolicy.HostResolver Resolves(params string[] addresses) =>
            (host, ct) => Task.FromResult(addresses.Select(a => IPAddress.Parse(a)).ToArray());

        // ---------------------------------------------------------------- addresses

        [Theory]
        [InlineData("8.8.8.8", false, true)]
        [InlineData("93.184.216.34", false, true)]
        [InlineData("172.32.0.1", false, true)]              // just outside 172.16.0.0/12
        [InlineData("2001:4860:4860::8888", false, true)]
        [InlineData("::ffff:8.8.8.8", false, true)]
        [InlineData("127.0.0.1", false, false)]
        [InlineData("127.0.0.1", true, true)]                // Development talking to the mock ERP
        [InlineData("10.1.2.3", false, false)]
        [InlineData("10.1.2.3", true, true)]
        [InlineData("172.16.0.1", false, false)]
        [InlineData("172.31.255.255", false, false)]
        [InlineData("192.168.1.1", false, false)]
        [InlineData("100.64.0.1", false, false)]             // carrier-grade NAT
        [InlineData("::1", false, false)]
        [InlineData("::1", true, true)]
        [InlineData("fc00::1", false, false)]
        [InlineData("fd12:3456::1", false, false)]
        [InlineData("fd12:3456::1", true, true)]
        [InlineData("::ffff:10.0.0.1", false, false)]        // IPv4-mapped private
        [InlineData("2002:0a00:0001::", false, false)]       // 6to4 wrapping 10.0.0.1
        // Never allowed, not even for local development:
        [InlineData("169.254.169.254", true, false)]         // cloud metadata
        [InlineData("169.254.1.1", true, false)]             // link-local
        [InlineData("fe80::1", true, false)]
        [InlineData("0.0.0.0", true, false)]
        [InlineData("224.0.0.1", true, false)]
        [InlineData("255.255.255.255", true, false)]
        [InlineData("::ffff:169.254.169.254", true, false)]  // metadata, IPv4-mapped
        public void IsAddressAllowed_FollowsThePolicy(string address, bool allowLocal, bool expected)
        {
            Assert.Equal(expected, ErpDestinationPolicy.IsAddressAllowed(IPAddress.Parse(address), allowLocal));
        }

        // ---------------------------------------------------------------- base URL

        [Fact]
        public async Task PublicHttpsLiteral_IsAllowed_WithoutAnyDnsLookup()
        {
            var result = await ErpDestinationPolicy.ValidateBaseUrlAsync(
                "https://8.8.8.8/api/v1", false, (h, ct) => throw new InvalidOperationException("DNS must not be used for a literal address."));

            Assert.True(result.IsAllowed, result.Reason);
        }

        [Fact]
        public async Task HostNameResolvingToAPublicAddress_IsAllowed()
        {
            var result = await ErpDestinationPolicy.ValidateBaseUrlAsync(
                "https://erp.partner.example/api", false, Resolves("93.184.216.34"));

            Assert.True(result.IsAllowed, result.Reason);
        }

        [Theory]
        [InlineData("10.0.0.5")]
        [InlineData("127.0.0.1")]
        [InlineData("169.254.169.254")]
        [InlineData("192.168.0.10")]
        [InlineData("::1")]
        public async Task HostNameResolvingToANonPublicAddress_IsRefused(string resolvedTo)
        {
            var result = await ErpDestinationPolicy.ValidateBaseUrlAsync(
                "https://innocent-looking.example/api", false, Resolves(resolvedTo));

            Assert.False(result.IsAllowed);
            Assert.Contains("non-public address", result.Reason);
        }

        [Fact]
        public async Task HostNameResolvingToOnePublicAndOnePrivateAddress_IsRefused()
        {
            // Any of the addresses could be the one connected to, so ALL must pass.
            var result = await ErpDestinationPolicy.ValidateBaseUrlAsync(
                "https://mixed.example/api", false, Resolves("93.184.216.34", "10.0.0.5"));

            Assert.False(result.IsAllowed);
        }

        [Fact]
        public async Task AHostThatCannotBeResolved_IsRefused()
        {
            ErpDestinationPolicy.HostResolver failing = (h, ct) => throw new SocketException();
            var result = await ErpDestinationPolicy.ValidateBaseUrlAsync("https://nowhere.example/api", false, failing);

            Assert.False(result.IsAllowed);
            Assert.Contains("could not be resolved", result.Reason);

            var empty = await ErpDestinationPolicy.ValidateBaseUrlAsync(
                "https://empty.example/api", false, (h, ct) => Task.FromResult(Array.Empty<IPAddress>()));
            Assert.False(empty.IsAllowed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a url")]
        [InlineData("/relative/path")]
        [InlineData("ftp://8.8.8.8/")]
        [InlineData("file:///etc/passwd")]
        [InlineData("javascript:alert(1)")]
        [InlineData("https://user:password@8.8.8.8/api")]
        [InlineData("https://8.8.8.8/api?token=1")]
        [InlineData("https://8.8.8.8/api#frag")]
        public async Task MalformedOrUnsafeUrls_AreRefused(string? url)
        {
            var result = await ErpDestinationPolicy.ValidateBaseUrlAsync(url, false, Resolves("8.8.8.8"));
            Assert.False(result.IsAllowed);
        }

        [Fact]
        public async Task PlainHttp_IsRefusedOutsideDevelopment_AndAllowedForLocalDevelopment()
        {
            var production = await ErpDestinationPolicy.ValidateBaseUrlAsync("http://8.8.8.8/api", false);
            Assert.False(production.IsAllowed);
            Assert.Contains("HTTPS", production.Reason);

            var development = await ErpDestinationPolicy.ValidateBaseUrlAsync("http://127.0.0.1:5099/api/v1", true);
            Assert.True(development.IsAllowed, development.Reason);
        }

        [Theory]
        [InlineData("http://localhost:5099/api/v1")]
        [InlineData("https://127.0.0.1/api")]
        [InlineData("https://[::1]/api")]
        [InlineData("https://10.0.0.1/api")]
        public async Task LocalAndPrivateDestinations_AreRefusedUnlessLocalDevelopmentIsOn(string url)
        {
            var refused = await ErpDestinationPolicy.ValidateBaseUrlAsync(url, false, Resolves("127.0.0.1", "::1"));
            Assert.False(refused.IsAllowed);
        }

        [Fact]
        public async Task TheMetadataAddress_IsRefusedEvenInDevelopment()
        {
            var result = await ErpDestinationPolicy.ValidateBaseUrlAsync("http://169.254.169.254/latest/meta-data", true);
            Assert.False(result.IsAllowed);
        }

        // ---------------------------------------------------------------- secret references

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("env:HANIF_ERP_API_KEY")]
        [InlineData("env:A")]
        [InlineData("ENV:lower_case_9")]
        public void AcceptableSecretReferences_Validate(string? reference)
        {
            Assert.Null(IntegrationSecretReference.Validate(reference));
        }

        [Theory]
        [InlineData("a-literal-api-key-value")]
        [InlineData("sk_live_abcdef123456")]
        [InlineData("env:JWT:SigningKey")]                          // a section path, not a name
        [InlineData("env:ConnectionStrings:DefaultConnection")]
        [InlineData("env:1STARTS_WITH_DIGIT")]
        [InlineData("env:has space")]
        [InlineData("env:with-dash")]
        [InlineData("env:../../etc/passwd")]
        [InlineData("Bearer abcdef")]
        public void LiteralsAndOtherFormats_AreRejected_WithoutEchoingTheValue(string reference)
        {
            var problem = IntegrationSecretReference.Validate(reference);

            Assert.NotNull(problem);
            Assert.DoesNotContain(reference, problem);
        }

        [Fact]
        public void ABareEnvPrefix_IsRejected()
        {
            // (Kept out of the theory above: the message itself legitimately contains "env:NAME".)
            Assert.NotNull(IntegrationSecretReference.Validate("env:"));
        }

        [Fact]
        public void TooLongAName_IsRejected()
        {
            Assert.NotNull(IntegrationSecretReference.Validate("env:" + new string('A', 65)));
            Assert.Null(IntegrationSecretReference.Validate("env:" + new string('A', 64)));
        }

        [Fact]
        public void Resolve_ReadsOnlyUnderTheDedicatedSection()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integrations:Secrets:MY_KEY"] = "the-secret",
                ["Integrations:Secrets:EMPTY_KEY"] = "",
                ["JWT:SigningKey"] = "jwt-signing-key-value",
                ["MY_ROOT_KEY"] = "root-level-value",
            }).Build();

            Assert.Equal("the-secret", IntegrationSecretReference.Resolve(config, "env:MY_KEY"));

            // Everything outside the section is unreachable through a reference.
            Assert.Null(IntegrationSecretReference.Resolve(config, "env:JWT:SigningKey"));
            Assert.Null(IntegrationSecretReference.Resolve(config, "env:MY_ROOT_KEY"));
            Assert.Null(IntegrationSecretReference.Resolve(config, "env:NOT_THERE"));
            Assert.Null(IntegrationSecretReference.Resolve(config, "env:EMPTY_KEY"));

            // A literal is never "resolved" to itself.
            Assert.Null(IntegrationSecretReference.Resolve(config, "the-secret"));
            Assert.Null(IntegrationSecretReference.Resolve(config, null));
        }

        [Fact]
        public void Resolve_DoesNotReadAnArbitraryProcessEnvironmentVariable()
        {
            const string name = "TP_CHUNK6_UNIT_TEST_ONLY_VARIABLE";
            Environment.SetEnvironmentVariable(name, "value-from-the-process-environment");
            try
            {
                var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

                // The old code fell back to Environment.GetEnvironmentVariable(NAME) for any NAME.
                Assert.Null(IntegrationSecretReference.Resolve(config, $"env:{name}"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [Fact]
        public void Resolve_FindsAValueSuppliedAsTheDeploymentEnvironmentVariable()
        {
            const string variable = "Integrations__Secrets__TP_CHUNK6_UNIT_TEST_KEY";
            Environment.SetEnvironmentVariable(variable, "deployed-secret");
            try
            {
                var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
                Assert.Equal("deployed-secret", IntegrationSecretReference.Resolve(config, "env:TP_CHUNK6_UNIT_TEST_KEY"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        [Fact]
        public void Redact_RemovesEveryOccurrence_AndIsSafeForNullsAndTinySecrets()
        {
            var secret = "abcd-1234-secret";
            var text = $"401 for key {secret}; retry with {secret}.";

            var redacted = IntegrationSecretReference.Redact(text, secret);

            Assert.DoesNotContain(secret, redacted);
            Assert.Equal("401 for key [redacted]; retry with [redacted].", redacted);

            Assert.Null(IntegrationSecretReference.Redact(null, secret));
            Assert.Equal("text", IntegrationSecretReference.Redact("text", null));
            Assert.Equal("text", IntegrationSecretReference.Redact("text", ""));

            // A 1-3 character "secret" would shred ordinary words, so it is left alone.
            Assert.Equal("a cat sat", IntegrationSecretReference.Redact("a cat sat", "at"));
        }

        // ---------------------------------------------------------------- other typed inputs

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("X-API-Key", true)]
        [InlineData("Authorization", true)]
        [InlineData("api-key", true)]
        [InlineData("Bad Header", false)]
        [InlineData("X:Y", false)]
        [InlineData("X\r\nInjected: 1", false)]
        [InlineData("Host", false)]
        [InlineData("content-length", false)]
        [InlineData("Transfer-Encoding", false)]
        [InlineData("Idempotency-Key", false)]
        public void ApiKeyHeaderName_IsValidated(string? header, bool acceptable)
        {
            Assert.Equal(acceptable, IntegrationInputRules.ValidateApiKeyHeaderName(header) is null);
        }

        [Theory]
        [InlineData("GET", "/trips/{tripId}/seats", true)]
        [InlineData("post", "/bookings/confirm", true)]
        [InlineData("DELETE", "/bookings/{bookingId}", true)]
        [InlineData("TRACE", "/x", false)]
        [InlineData("", "/x", false)]
        [InlineData("GET", "trips", false)]
        [InlineData("GET", "//evil.example/x", false)]
        [InlineData("GET", "/x://y", false)]
        [InlineData("GET", "/a/../b", false)]
        [InlineData("GET", "/a\\b", false)]
        [InlineData("GET", "", false)]
        [InlineData("GET", null, false)]
        public void EndpointMethodAndPath_AreValidated(string method, string? path, bool acceptable)
        {
            Assert.Equal(acceptable, IntegrationInputRules.ValidateEndpoint(method, path) is null);
        }

        // ---------------------------------------------------------------- HTTP handler

        [Fact]
        public void TheHandler_RefusesRedirects_Proxies_AndCookies()
        {
            var handler = Assert.IsType<SocketsHttpHandler>(
                ErpHttpHandlerFactory.Create(new IntegrationSecurityOptions(AllowLocalDestinations: false)));

            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseProxy);
            Assert.False(handler.UseCookies);
        }

        [Theory]
        [InlineData("http://127.0.0.1:9/", false)]
        [InlineData("http://[::1]:9/", false)]
        [InlineData("http://localhost:9/", false)]
        [InlineData("http://10.0.0.5:9/", false)]
        [InlineData("http://169.254.169.254/latest/meta-data", false)]
        [InlineData("http://169.254.169.254/latest/meta-data", true)]   // refused even in Development
        public async Task TheHandler_RefusesToConnect_ToADisallowedAddress(string url, bool allowLocal)
        {
            using var client = new HttpClient(ErpHttpHandlerFactory.Create(new IntegrationSecurityOptions(allowLocal)));

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

            // HttpClient wraps whatever the connect callback throws, so look through the chain.
            Assert.Contains("not a permitted network address", ex.ToString());
        }

        // ---------------------------------------------------------------- options

        [Fact]
        public void LocalDestinations_DefaultToTheEnvironment_AndCanBeOverriddenByConfiguration()
        {
            static IntegrationSecurityOptions For(string environment, string? setting)
            {
                var values = new Dictionary<string, string?>();
                if (setting is not null) values[IntegrationSecurityOptions.AllowLocalDestinationsKey] = setting;
                var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
                return IntegrationSecurityOptions.From(config, new FakeEnvironment(environment));
            }

            Assert.True(For("Development", null).AllowLocalDestinations);
            Assert.False(For("Production", null).AllowLocalDestinations);
            Assert.False(For("Testing", null).AllowLocalDestinations);
            Assert.False(For("Development", "false").AllowLocalDestinations);
            Assert.True(For("Production", "true").AllowLocalDestinations);   // Program.cs refuses to START like this
        }

        private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
        {
            public string EnvironmentName { get; set; } = name;
            public string ApplicationName { get; set; } = "Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
        }
    }
}
