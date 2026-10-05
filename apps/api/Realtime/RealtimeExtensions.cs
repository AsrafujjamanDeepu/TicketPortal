using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketPortal.Api.Hubs;

namespace TicketPortal.Api.Realtime
{
    // Wiring for the SignalR endpoint, kept out of Program.cs so that file only gains four
    // one-line calls. Order matters in Program.cs — see the comments there.
    public static class RealtimeExtensions
    {
        public static IServiceCollection AddRealtime(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<RealtimeOptions>(configuration.GetSection(RealtimeOptions.SectionName));

            var options = configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>()
                          ?? new RealtimeOptions();

            // Registered even when the kill switch is off: it is cheap, and it keeps the
            // service graph identical either way (only the endpoint disappears).
            services.AddSignalR(hub =>
            {
                hub.MaximumReceiveMessageSize = options.MaxReceiveMessageSizeBytes;

                // Chunk 7: per-connection cap on hub calls per second.
                hub.AddFilter<RealtimeRateLimitFilter>();
            });

            // Chunk 2 — automatic change capture. Singletons: none of them holds per-request
            // state (the tracker keeps its per-DbContext buffers in a weak table), which is also
            // what EF Core expects of an interceptor. Registered even when the kill switch is
            // off, like SignalR itself, so the service graph is identical either way; whether the
            // interceptors are actually ATTACHED to AppDbContext is decided by
            // AddRealtimeInterceptors below.
            services.AddSingleton<RealtimeConnectionTracker>();
            services.AddSingleton<RealtimeChangeTracker>();
            services.AddSingleton<IRealtimeNotifier, RealtimeNotifier>();
            services.AddSingleton<RealtimeSaveChangesInterceptor>();
            services.AddSingleton<RealtimeTransactionInterceptor>();
            services.AddHostedService<RealtimeSessionRevocationService>();

            return services;
        }

        // Chunk 2 — attaches the two change-capture interceptors to a DbContext. Called from the
        // (serviceProvider, options) overload of AddDbContext in Program.cs. With
        // Realtime:Enabled=false nothing is attached at all (plan principle 7), so a switched-off
        // feature costs the data layer exactly nothing.
        public static DbContextOptionsBuilder AddRealtimeInterceptors(
            this DbContextOptionsBuilder builder, IServiceProvider services)
        {
            if (!services.GetRequiredService<IOptions<RealtimeOptions>>().Value.Enabled)
                return builder;

            return builder.AddInterceptors(
                services.GetRequiredService<RealtimeSaveChangesInterceptor>(),
                services.GetRequiredService<RealtimeTransactionInterceptor>());
        }

        // Browsers cannot send an Authorization header on a WebSocket, so SignalR clients put
        // the JWT in ?access_token=. The JwtBearer handler reads it (Program.cs,
        // OnMessageReceived) — but the hub has no [Authorize] (the seat map is public), and
        // without this gate a token that FAILS validation just turns the request into an
        // anonymous one. The client would then look "connected" but silently lose every
        // private group — e.g. as soon as its 3-hour token expired. So: credentials present
        // + not authenticated = 401, which the clients already turn into "log in again".
        //
        // Must be registered AFTER UseAuthentication.
        public static WebApplication UseRealtimeAuthGate(this WebApplication app)
        {
            if (!app.Services.GetRequiredService<IOptions<RealtimeOptions>>().Value.Enabled)
                return app;

            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(RealtimeGroups.HubPath))
                {
                    var sentCredentials =
                        !string.IsNullOrEmpty(context.Request.Query["access_token"])
                        || context.Request.Headers.ContainsKey("Authorization");

                    if (sentCredentials && context.User.Identity?.IsAuthenticated != true)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        return;
                    }
                }

                await next();
            });

            return app;
        }

        // Must be registered AFTER UseAuthorization and UseCors (the negotiate call is a
        // cross-origin POST from the Angular/React dev servers).
        public static WebApplication MapRealtimeHub(this WebApplication app)
        {
            var options = app.Services.GetRequiredService<IOptions<RealtimeOptions>>().Value;

            if (!options.Enabled)
            {
                app.Logger.LogInformation(
                    "Realtime (SignalR) is disabled by configuration (Realtime:Enabled = false); {HubPath} is not mapped.",
                    RealtimeGroups.HubPath);
                return app;
            }

            app.MapHub<RealtimeHub>(RealtimeGroups.HubPath, endpoint =>
            {
                // Close the socket when the JWT that opened it expires, so a connection can
                // never outlive its token. The client reconnects, the gate above answers 401,
                // and the app sends the user back to login.
                endpoint.CloseOnAuthenticationExpiration = true;
            });

            return app;
        }
    }
}
