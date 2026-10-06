using System.Net.Mail;
using Microsoft.Extensions.Options;
using TicketPortal.Api.Services;

namespace TicketPortal.Api.Startup
{
    // C7-5: ONE central place that checks every setting the API needs before it serves a request.
    //
    // It is wired through the options system (AddOptions<StartupSettings>().ValidateOnStart()), so a
    // bad configuration stops the host during startup with a single message that lists EVERY
    // problem at once - not one crash per fix. The rules are a pure function over IConfiguration
    // (Validate), so they are unit-tested without starting a web host.
    //
    // Scope on purpose: JWT, auth lifetime, bootstrap admin, private storage, CORS, SMTP/reset URL,
    // ERP integration (including the Chunk 6 outbound-call, availability and seat-hold limit settings,
    // which used to be checked by their own block in Program.cs and now live here) and the
    // demo-payment switch. There is deliberately NO payment-gateway secret
    // and NO tax-provider requirement here (decision D5): the personal demo runs on the mocked
    // gateway and local tax rules, and must keep starting without either.
    //
    // "Strict" environments are everything except Development and Testing - the same split Program.cs
    // and DbSeeder already use - so local demos keep working with the committed development defaults.
    public sealed class StartupSettings
    {
    }

    public sealed class StartupSettingsValidator(
        IConfiguration configuration,
        IWebHostEnvironment environment) : IValidateOptions<StartupSettings>
    {
        public ValidateOptionsResult Validate(string? name, StartupSettings options)
        {
            var problems = Validate(configuration, environment.EnvironmentName, environment.ContentRootPath, environment.WebRootPath);
            return problems.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(problems);
        }

        // The development bootstrap password (DbSeeder.BootstrapAdminPassword). Duplicated as a
        // literal so this file has no dependency on the seeder; a unit test pins the two together.
        public const string DevelopmentBootstrapPassword = "Admin@12345";

        public static bool IsStrictEnvironment(string environmentName) =>
            !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<string> Validate(
            IConfiguration configuration,
            string environmentName,
            string contentRootPath,
            string? webRootPath)
        {
            var problems = new List<string>();
            var strict = IsStrictEnvironment(environmentName);
            var production = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);

            ValidateJwt(configuration, strict, problems);
            ValidateAuth(configuration, problems);
            ValidatePayments(configuration, production, problems);
            ValidateBootstrapAdmin(configuration, strict, problems);
            ValidatePrivateStorage(configuration, strict, contentRootPath, webRootPath, problems);
            ValidateCors(configuration, production, problems);
            ValidatePasswordReset(configuration, production, problems);
            ValidateErp(configuration, production, problems);
            ValidateChunk6Settings(configuration, production, problems);

            return problems;
        }

        // ---- JWT -------------------------------------------------------------------------------
        private static void ValidateJwt(IConfiguration configuration, bool strict, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(configuration["JWT:Issuer"]))
                problems.Add("JWT:Issuer is required.");
            if (string.IsNullOrWhiteSpace(configuration["JWT:Audience"]))
                problems.Add("JWT:Audience is required.");

            if (!strict) return; // Development/Testing use the committed, clearly-labelled dev key.

            var signingKey = configuration["JWT:SigningKey"];
            if (IsPlaceholderOrWeakSigningKey(signingKey))
            {
                problems.Add(
                    "JWT:SigningKey is missing, a placeholder, or shorter than 32 characters. Set a real value via " +
                    "'dotnet user-secrets set \"JWT:SigningKey\" \"...\"' or the JWT__SigningKey environment variable " +
                    "before starting the API outside Development. See docs/01-Run-and-Manual-Test-Guide.md, " +
                    "\"First-time secrets setup\".");
            }
        }

        // Shared with Program.cs's earliest guard (the JWT bearer setup reads the key while services
        // are being registered, which is before ValidateOnStart can run).
        public static bool IsPlaceholderOrWeakSigningKey(string? signingKey) =>
            string.IsNullOrWhiteSpace(signingKey)
            || signingKey.Length < 32
            || signingKey.Contains("ReplaceThisWith", StringComparison.OrdinalIgnoreCase)
            || signingKey.Contains("Development-Only", StringComparison.OrdinalIgnoreCase);

        // ---- Token lifetime --------------------------------------------------------------------
        private static void ValidateAuth(IConfiguration configuration, List<string> problems)
        {
            var minutes = configuration.GetValue<int?>("Auth:AccessTokenMinutes") ?? 15;
            if (minutes is < 1 or > 60)
                problems.Add("Auth:AccessTokenMinutes must be between 1 and 60.");
        }

        // ---- Demo payments ---------------------------------------------------------------------
        private static void ValidatePayments(IConfiguration configuration, bool production, List<string> problems)
        {
            if (production
                && configuration.GetValue("Payments:DemoMode", false)
                && !configuration.GetValue("Payments:AllowDemoInProduction", false))
            {
                problems.Add("Payments:DemoMode cannot be enabled in Production unless Payments:AllowDemoInProduction is explicitly true.");
            }
        }

        // ---- Bootstrap admin (non-development only) --------------------------------------------
        private static void ValidateBootstrapAdmin(IConfiguration configuration, bool strict, List<string> problems)
        {
            var userName = configuration["Seeding:BootstrapAdmin:UserName"]?.Trim();
            var password = configuration["Seeding:BootstrapAdmin:Password"];
            var email = configuration["Seeding:BootstrapAdmin:Email"];

            var hasUser = !string.IsNullOrWhiteSpace(userName);
            var hasPassword = !string.IsNullOrWhiteSpace(password);

            // Nothing configured is allowed (the API then starts with no admin and logs that).
            if (!hasUser && !hasPassword)
            {
                if (!string.IsNullOrWhiteSpace(email))
                    problems.Add("Seeding:BootstrapAdmin:Email is set but Seeding:BootstrapAdmin:UserName/Password are not.");
                return;
            }

            if (!strict) return; // Development seeds its own documented admin instead.

            if (hasUser != hasPassword)
            {
                problems.Add("Both Seeding:BootstrapAdmin:UserName and Seeding:BootstrapAdmin:Password must be supplied together.");
                return;
            }

            if (password!.Length < 16 || password == DevelopmentBootstrapPassword)
                problems.Add("Seeding:BootstrapAdmin:Password must be at least 16 characters and must not be the development password.");
            if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
                problems.Add("Seeding:BootstrapAdmin:Email is not a valid e-mail address.");
        }

        // ---- Private file storage (national-ID photos) -----------------------------------------
        private static void ValidatePrivateStorage(
            IConfiguration configuration, bool strict, string contentRootPath, string? webRootPath, List<string> problems)
        {
            var configured = configuration["Storage:PrivateFilesRoot"];
            if (string.IsNullOrWhiteSpace(configured))
            {
                if (strict)
                    problems.Add("Storage:PrivateFilesRoot must point to persistent storage outside the public web root outside Development.");
                return;
            }

            if (configured.IndexOf('\0') >= 0 || configured.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                problems.Add("Storage:PrivateFilesRoot contains characters that are not valid in a path.");
                return;
            }

            // Same resolution rules as PassengerIdPhotoMigration.ResolvePrivateRoot.
            var path = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRootPath, configured));
            var webRoot = Path.GetFullPath(webRootPath ?? Path.Combine(contentRootPath, "wwwroot"));
            if (path.Equals(webRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("Storage:PrivateFilesRoot must not be inside the public web root (wwwroot); private ID photos would become downloadable.");
            }
        }

        // ---- CORS ------------------------------------------------------------------------------
        private static void ValidateCors(IConfiguration configuration, bool production, List<string> problems)
        {
            var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            for (var i = 0; i < origins.Length; i++)
            {
                var origin = origins[i];
                if (string.IsNullOrWhiteSpace(origin) || origin.Contains('*'))
                {
                    problems.Add($"Cors:AllowedOrigins[{i}] must be an exact origin such as https://app.example.com (no wildcards, not blank).");
                    continue;
                }

                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)
                    || origin.EndsWith('/'))
                {
                    problems.Add($"Cors:AllowedOrigins[{i}] ('{origin}') must be scheme://host[:port] only - no path, query, trailing slash or credentials.");
                    continue;
                }

                if (production && uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
                    problems.Add($"Cors:AllowedOrigins[{i}] ('{origin}') must use https in Production (plain http is only accepted for localhost).");
            }
        }

        // ---- Password reset links + SMTP -------------------------------------------------------
        private static void ValidatePasswordReset(IConfiguration configuration, bool production, List<string> problems)
        {
            var smtpHost = configuration["PasswordReset:Smtp:Host"];
            var smtpConfigured = !string.IsNullOrWhiteSpace(smtpHost);

            var publicAppUrl = configuration["PasswordReset:PublicAppUrl"];
            Uri? publicUri = null;
            if (!string.IsNullOrWhiteSpace(publicAppUrl))
            {
                if (!TryGetSafeHttpUrl(publicAppUrl, out publicUri, out var urlProblem))
                    problems.Add($"PasswordReset:PublicAppUrl {urlProblem}");
                else if (production && publicUri!.Scheme == Uri.UriSchemeHttp && !IsLoopback(publicUri))
                    problems.Add("PasswordReset:PublicAppUrl must use https in Production (plain http is only accepted for localhost).");
            }

            if (!smtpConfigured) return; // Reset e-mail is optional; the sender reports clearly if it is used unconfigured.

            // Cross-setting rule: real mail with reset links that point at this machine is always a mistake.
            if (production && (publicUri is null || IsLoopback(publicUri)))
                problems.Add("PasswordReset:PublicAppUrl must be the public address of the Angular app when PasswordReset:Smtp:Host is configured in Production (reset e-mails would otherwise link to localhost).");

            var port = configuration.GetValue<int?>("PasswordReset:Smtp:Port") ?? 587;
            if (port is < 1 or > 65535)
                problems.Add("PasswordReset:Smtp:Port must be between 1 and 65535.");

            var username = configuration["PasswordReset:Smtp:Username"];
            var password = configuration["PasswordReset:Smtp:Password"];
            if (string.IsNullOrWhiteSpace(username) != string.IsNullOrWhiteSpace(password))
                problems.Add("PasswordReset:Smtp:Username and PasswordReset:Smtp:Password must be supplied together (or both left empty).");

            var from = configuration["PasswordReset:Smtp:FromAddress"];
            if (!string.IsNullOrWhiteSpace(from) && !IsValidEmail(from))
                problems.Add("PasswordReset:Smtp:FromAddress is not a valid e-mail address.");
        }

        // ---- External ERP integration ----------------------------------------------------------
        private static void ValidateErp(IConfiguration configuration, bool production, List<string> problems)
        {
            var baseUrl = configuration["Integrations:HanifErpBaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                if (!TryGetSafeHttpUrl(baseUrl, out var uri, out var urlProblem))
                    problems.Add($"Integrations:HanifErpBaseUrl {urlProblem}");
                else if (production && uri!.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
                    problems.Add("Integrations:HanifErpBaseUrl must use https in Production (it carries an API key); plain http is only accepted for localhost.");
            }

            var attempts = configuration.GetValue<int?>("Integrations:MaxSyncAttempts");
            if (attempts is < 1 or > 20)
                problems.Add("Integrations:MaxSyncAttempts must be between 1 and 20.");
        }

        // ---- Chunk 6: seat-hold limits, ERP availability policy, outbound destinations ------------
        // The parsers below are the SAME code the request paths use (SeatHoldLimits,
        // ExternalAvailabilityPolicy), so what is accepted at startup is exactly what will be accepted
        // at runtime; a typo can never silently turn a protection off.
        private static void ValidateChunk6Settings(IConfiguration configuration, bool production, List<string> problems)
        {
            Collect(problems, () => SeatHoldLimits.FromConfiguration(configuration));
            Collect(problems, () => ExternalAvailabilityPolicy.GetFailureMode(configuration));
            Collect(problems, () => ExternalAvailabilityPolicy.GetCacheTtl(configuration));

            // Local/private destinations exist for a mock ERP on a developer machine. They must never
            // be switchable on in Production, where they would expose internal services to the
            // integration feature (SSRF).
            if (!production) return;
            try
            {
                if (configuration.GetValue<bool?>(IntegrationSecurityOptions.AllowLocalDestinationsKey) == true)
                    problems.Add($"{IntegrationSecurityOptions.AllowLocalDestinationsKey} must not be enabled in Production.");
            }
            catch (InvalidOperationException)
            {
                problems.Add($"{IntegrationSecurityOptions.AllowLocalDestinationsKey} must be true or false.");
            }
        }

        // Runs a parser that throws InvalidOperationException with a message naming the bad key.
        private static void Collect(List<string> problems, Action parse)
        {
            try { parse(); }
            catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        }

        private static void Collect<T>(List<string> problems, Func<T> parse) => Collect(problems, () => { _ = parse(); });

        // ---- helpers ---------------------------------------------------------------------------
        private static bool TryGetSafeHttpUrl(string value, out Uri? uri, out string problem)
        {
            problem = string.Empty;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                problem = $"('{value}') must be an absolute http(s) URL.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                problem = "must not contain credentials (user:password@); put secrets in user-secrets or environment variables.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                problem = "must not contain a query string or fragment.";
                return false;
            }

            return true;
        }

        private static bool IsLoopback(Uri uri) =>
            uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

        private static bool IsValidEmail(string value)
        {
            try
            {
                var address = new MailAddress(value);
                return string.Equals(address.Address, value.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    public static class StartupSettingsExtensions
    {
        public static IServiceCollection AddStartupSettingsValidation(this IServiceCollection services)
        {
            services.AddSingleton<IValidateOptions<StartupSettings>, StartupSettingsValidator>();
            services.AddOptions<StartupSettings>().ValidateOnStart();
            return services;
        }
    }
}
