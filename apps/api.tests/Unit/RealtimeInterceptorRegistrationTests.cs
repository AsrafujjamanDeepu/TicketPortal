using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketPortal.Api.Data;
using TicketPortal.Api.Realtime;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), principle 7 / Chunk 2 step 5: with Realtime:Enabled=false the
    // change-capture interceptors must not be attached to AppDbContext AT ALL, and with it on,
    // both must be. No database is opened — only the DbContext OPTIONS are built.
    public class RealtimeInterceptorRegistrationTests
    {
        private static IServiceProvider BuildServices(bool enabled)
        {
            var connections = new RealtimeConnectionTracker();
            var tracker = new RealtimeChangeTracker();
            // Chunk 3 added members to IRealtimeNotifier; the shared null object keeps this test
            // from needing an edit every time the interface grows.
            var notifier = NullRealtimeNotifier.Instance;

            var services = new ServiceCollection();
            services.AddSingleton<IOptions<RealtimeOptions>>(Options.Create(new RealtimeOptions { Enabled = enabled }));
            services.AddSingleton(new RealtimeSaveChangesInterceptor(
                tracker, notifier, connections, NullLogger<RealtimeSaveChangesInterceptor>.Instance));
            services.AddSingleton(new RealtimeTransactionInterceptor(
                tracker, notifier, NullLogger<RealtimeTransactionInterceptor>.Instance));

            return services.BuildServiceProvider();
        }

        private static List<IInterceptor> AttachedInterceptors(bool enabled)
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\unused;Database=unused;Trusted_Connection=True;");

            builder.AddRealtimeInterceptors(BuildServices(enabled));

            return builder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors?.ToList()
                ?? new List<IInterceptor>();
        }

        [Fact]
        public void WhenEnabled_BothInterceptorsAreAttached()
        {
            var attached = AttachedInterceptors(enabled: true);

            Assert.Contains(attached, i => i is RealtimeSaveChangesInterceptor);
            Assert.Contains(attached, i => i is RealtimeTransactionInterceptor);
        }

        [Fact]
        public void WhenDisabled_NothingIsAttached()
        {
            var attached = AttachedInterceptors(enabled: false);

            Assert.DoesNotContain(attached, i => i is RealtimeSaveChangesInterceptor);
            Assert.DoesNotContain(attached, i => i is RealtimeTransactionInterceptor);
        }
    }
}
