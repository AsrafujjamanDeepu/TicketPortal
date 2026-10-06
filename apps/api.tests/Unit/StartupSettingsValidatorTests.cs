using Microsoft.Extensions.Configuration;
using TicketPortal.Api.Data;
using TicketPortal.Api.Services;
using TicketPortal.Api.Startup;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // C7-5: the central startup rules, exercised as a pure function (no web host, no database).
    public class StartupSettingsValidatorTests
    {
        private const string GoodKey = "a-real-signing-key-that-is-long-enough-0123456789";
        private static readonly string Content = Path.Combine(Path.GetTempPath(), "tp-content");

        private static IReadOnlyList<string> Check(string environment, params (string Key, string? Value)[] settings)
        {
            var values = new Dictionary<string, string?>
            {
                ["JWT:Issuer"] = "TicketPortalAPI",
                ["JWT:Audience"] = "TicketPortalAPI",
            };
            foreach (var (key, value) in settings) values[key] = value;
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            return StartupSettingsValidator.Validate(config, environment, Content, Path.Combine(Content, "wwwroot"));
        }

        private static (string, string?) S(string key, string? value) => (key, value);

        [Fact]
        public void Development_NeedsNoSecrets_SoTheDemoStartsWithCommittedDefaults()
        {
            Assert.Empty(Check("Development"));
            Assert.Empty(Check("Testing"));
        }

        [Fact]
        public void Production_WithJustTheDocumentedRequirements_IsValid_AndNeedsNoGatewayOrTaxSetting()
        {
            // Mirrors AccountSecurityTests.ProductionBootstrapTests' real configuration.
            var problems = Check("Production",
                S("JWT:SigningKey", GoodKey),
                S("Storage:PrivateFilesRoot", Path.Combine(Path.GetTempPath(), "tp-private")),
                S("Payments:DemoMode", "false"),
                S("Cors:AllowedOrigins:0", "http://localhost:4200"));
            Assert.Empty(problems);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("too-short")]
        [InlineData("ReplaceThisWith-a-long-random-value-0123456789")]
        [InlineData("Development-Only-Not-A-Secret-Signing-Key-32Chars+")]
        public void NonDevelopment_RejectsMissingShortOrPlaceholderSigningKeys(string? key)
        {
            var problems = Check("Production", S("JWT:SigningKey", key), S("Storage:PrivateFilesRoot", Path.GetTempPath() + "p"));
            Assert.Contains(problems, p => p.StartsWith("JWT:SigningKey"));
            Assert.True(StartupSettingsValidator.IsPlaceholderOrWeakSigningKey(key));
        }

        [Fact]
        public void EveryProblemIsReportedAtOnce_NotJustTheFirst()
        {
            var problems = Check("Production",
                S("JWT:SigningKey", ""),
                S("Auth:AccessTokenMinutes", "500"),
                S("Payments:DemoMode", "true"),
                S("Seeding:BootstrapAdmin:UserName", "boss"));
            Assert.True(problems.Count >= 5, string.Join(Environment.NewLine, problems));
            Assert.Contains(problems, p => p.StartsWith("JWT:SigningKey"));
            Assert.Contains(problems, p => p.StartsWith("Auth:AccessTokenMinutes"));
            Assert.Contains(problems, p => p.StartsWith("Payments:DemoMode"));
            Assert.Contains(problems, p => p.Contains("Seeding:BootstrapAdmin"));
            Assert.Contains(problems, p => p.StartsWith("Storage:PrivateFilesRoot"));
        }

        [Theory]
        [InlineData("0", true)]
        [InlineData("61", true)]
        [InlineData("1", false)]
        [InlineData("60", false)]
        public void AccessTokenMinutes_MustBeBetween1And60_InEveryEnvironment(string minutes, bool invalid)
        {
            var problems = Check("Development", S("Auth:AccessTokenMinutes", minutes));
            Assert.Equal(invalid, problems.Any(p => p.StartsWith("Auth:AccessTokenMinutes")));
        }

        [Fact]
        public void DemoPayments_InProduction_NeedAnExplicitOverride()
        {
            Assert.Contains(Check("Production", S("JWT:SigningKey", GoodKey), S("Storage:PrivateFilesRoot", Path.GetTempPath() + "p"), S("Payments:DemoMode", "true")),
                p => p.StartsWith("Payments:DemoMode"));
            Assert.DoesNotContain(Check("Production", S("JWT:SigningKey", GoodKey), S("Storage:PrivateFilesRoot", Path.GetTempPath() + "p"), S("Payments:DemoMode", "true"), S("Payments:AllowDemoInProduction", "true")),
                p => p.StartsWith("Payments:DemoMode"));
            Assert.DoesNotContain(Check("Development", S("Payments:DemoMode", "true")), p => p.StartsWith("Payments:DemoMode"));
        }

        [Fact]
        public void BootstrapAdmin_NeedsBothParts_AndAStrongNonDevelopmentPassword()
        {
            Assert.Contains(Check("Staging", S("Seeding:BootstrapAdmin:UserName", "boss")), p => p.Contains("must be supplied together"));
            Assert.Contains(Check("Staging", S("Seeding:BootstrapAdmin:UserName", "boss"), S("Seeding:BootstrapAdmin:Password", "short")), p => p.Contains("at least 16"));
            Assert.Contains(Check("Staging", S("Seeding:BootstrapAdmin:UserName", "boss"), S("Seeding:BootstrapAdmin:Password", StartupSettingsValidator.DevelopmentBootstrapPassword)), p => p.Contains("at least 16"));
            // A real deployment may legitimately name its bootstrap account "admin" when the password is strong.
            Assert.DoesNotContain(Check("Staging", S("Seeding:BootstrapAdmin:UserName", "admin"), S("Seeding:BootstrapAdmin:Password", "a-long-unique-passphrase-1")),
                p => p.Contains("Seeding:BootstrapAdmin"));
            Assert.DoesNotContain(Check("Staging", S("Seeding:BootstrapAdmin:UserName", "boss"), S("Seeding:BootstrapAdmin:Password", "a-long-unique-passphrase-1"), S("Seeding:BootstrapAdmin:Email", "boss@example.com")),
                p => p.Contains("Seeding:BootstrapAdmin"));
        }

        [Fact]
        public void TheDevelopmentBootstrapConstants_MatchTheSeeder()
        {
            Assert.Equal(DbSeeder.BootstrapAdminPassword, StartupSettingsValidator.DevelopmentBootstrapPassword);
        }

        [Fact]
        public void PrivateStorage_CannotLiveInsideWwwroot()
        {
            var inside = Check("Development", S("Storage:PrivateFilesRoot", Path.Combine(Content, "wwwroot", "ids")));
            Assert.Contains(inside, p => p.Contains("web root"));
            var relativeInside = Check("Development", S("Storage:PrivateFilesRoot", Path.Combine("wwwroot", "ids")));
            Assert.Contains(relativeInside, p => p.Contains("web root"));
            Assert.Empty(Check("Development", S("Storage:PrivateFilesRoot", Path.Combine(Content, "App_Data", "ids"))));
        }

        [Theory]
        [InlineData("*", true)]
        [InlineData("https://app.example.com/", true)]
        [InlineData("https://app.example.com/path", true)]
        [InlineData("ftp://app.example.com", true)]
        [InlineData("not a url", true)]
        [InlineData("http://app.example.com", true)]   // plain http outside localhost, Production
        [InlineData("https://app.example.com", false)]
        [InlineData("https://app.example.com:8443", false)]
        [InlineData("http://localhost:4200", false)]
        public void CorsOrigins_MustBeExactSafeOrigins_InProduction(string origin, bool invalid)
        {
            var problems = Check("Production", S("JWT:SigningKey", GoodKey), S("Storage:PrivateFilesRoot", Path.GetTempPath() + "p"), S("Cors:AllowedOrigins:0", origin));
            Assert.Equal(invalid, problems.Any(p => p.StartsWith("Cors:AllowedOrigins")));
        }

        [Fact]
        public void Smtp_RulesApplyOnlyWhenAHostIsConfigured_AndResetLinksMustNotPointAtLocalhostInProduction()
        {
            // No SMTP host: valid (reset e-mail is optional; the sender explains if it is used unconfigured).
            Assert.Empty(Check("Development", S("PasswordReset:Smtp:Port", "99999")));

            var production = new[]
            {
                S("JWT:SigningKey", GoodKey), S("Storage:PrivateFilesRoot", Path.GetTempPath() + "p"),
                S("PasswordReset:Smtp:Host", "smtp.example.com"),
            };
            Assert.Contains(Check("Production", production.Concat([S("PasswordReset:PublicAppUrl", "http://localhost:4200")]).ToArray()),
                p => p.Contains("link to localhost"));
            Assert.Contains(Check("Development", S("PasswordReset:Smtp:Host", "smtp.example.com"), S("PasswordReset:Smtp:Port", "0")),
                p => p.StartsWith("PasswordReset:Smtp:Port"));
            Assert.Contains(Check("Development", S("PasswordReset:Smtp:Host", "smtp.example.com"), S("PasswordReset:Smtp:Username", "u")),
                p => p.Contains("supplied together"));
            Assert.Contains(Check("Development", S("PasswordReset:Smtp:Host", "smtp.example.com"), S("PasswordReset:Smtp:FromAddress", "not-an-email")),
                p => p.Contains("FromAddress"));
            Assert.Empty(Check("Production", production.Concat([S("PasswordReset:PublicAppUrl", "https://app.example.com"), S("PasswordReset:Smtp:FromAddress", "no-reply@example.com")]).ToArray()));
        }

        [Theory]
        [InlineData("http://localhost:5099/api/v1", "Development", false)]
        [InlineData("https://erp.example.com/api/v1", "Production", false)]
        [InlineData("http://erp.example.com/api/v1", "Production", true)]
        [InlineData("https://user:secret@erp.example.com", "Development", true)]
        [InlineData("https://erp.example.com/api?key=abc", "Development", true)]
        [InlineData("erp.example.com", "Development", true)]
        public void ErpBaseUrl_MustBeASafeAbsoluteUrl(string url, string environment, bool invalid)
        {
            var settings = new List<(string, string?)> { S("Integrations:HanifErpBaseUrl", url) };
            if (environment == "Production")
            {
                settings.Add(S("JWT:SigningKey", GoodKey));
                settings.Add(S("Storage:PrivateFilesRoot", Path.GetTempPath() + "p"));
            }
            Assert.Equal(invalid, Check(environment, settings.ToArray()).Any(p => p.StartsWith("Integrations:HanifErpBaseUrl")));
        }

        // ---- Chunk 6 settings, now part of the central check (same parsers the request paths use) ----

        [Theory]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "0", true)]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "61", true)]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "six", true)]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "6", false)]
        [InlineData(SeatHoldLimits.MaxActiveHoldsPerUserKey, "0", true)]
        [InlineData(SeatHoldLimits.MaxActiveHoldsPerUserKey, "1001", true)]
        [InlineData(SeatHoldLimits.MaxActiveHoldsPerUserKey, "3", false)]
        [InlineData(ExternalAvailabilityPolicy.FailureModeKey, "Maybe", true)]
        [InlineData(ExternalAvailabilityPolicy.FailureModeKey, "Open", false)]
        [InlineData(ExternalAvailabilityPolicy.FailureModeKey, "closed", false)]
        [InlineData(ExternalAvailabilityPolicy.CacheSecondsKey, "-1", true)]
        [InlineData(ExternalAvailabilityPolicy.CacheSecondsKey, "abc", true)]
        [InlineData(ExternalAvailabilityPolicy.CacheSecondsKey, "0", false)]
        [InlineData(ExternalAvailabilityPolicy.CacheSecondsKey, "30", false)]
        public void Chunk6Settings_AreValidatedAtStartup_ByTheSameParsersTheRuntimeUses(string key, string value, bool invalid)
        {
            var problems = Check("Development", S(key, value));
            Assert.Equal(invalid, problems.Any(p => p.Contains(key)));
        }

        [Fact]
        public void LocalErpDestinations_AreAllowedInDevelopment_ButNeverInProduction()
        {
            Assert.Empty(Check("Development", S(IntegrationSecurityOptions.AllowLocalDestinationsKey, "true")));

            var production = new[] { S("JWT:SigningKey", GoodKey), S("Storage:PrivateFilesRoot", Path.GetTempPath() + "p") };
            Assert.Contains(
                Check("Production", production.Concat([S(IntegrationSecurityOptions.AllowLocalDestinationsKey, "true")]).ToArray()),
                p => p.Contains("must not be enabled in Production"));
            Assert.DoesNotContain(
                Check("Production", production.Concat([S(IntegrationSecurityOptions.AllowLocalDestinationsKey, "false")]).ToArray()),
                p => p.Contains(IntegrationSecurityOptions.AllowLocalDestinationsKey));
            Assert.Contains(
                Check("Production", production.Concat([S(IntegrationSecurityOptions.AllowLocalDestinationsKey, "perhaps")]).ToArray()),
                p => p.Contains("must be true or false"));
        }

        [Theory]
        [InlineData("0", true)]
        [InlineData("21", true)]
        [InlineData("5", false)]
        public void ErpMaxSyncAttempts_HasASaneRange(string attempts, bool invalid) =>
            Assert.Equal(invalid, Check("Development", S("Integrations:MaxSyncAttempts", attempts)).Any(p => p.StartsWith("Integrations:MaxSyncAttempts")));
    }
}
