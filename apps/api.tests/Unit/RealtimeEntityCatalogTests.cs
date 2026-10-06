using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;
using TicketPortal.Api.Realtime;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 2 / principle 8 (fail closed). The entity -> audience map
    // in RealtimeEntityCatalog only stays trustworthy if it cannot silently fall out of step with
    // the model. These tests read the real AppDbContext via reflection (no database, no host):
    //   * a DbSet added without a decision in the catalog fails here, with the table named;
    //   * a catalog entry for a table that no longer exists fails here;
    //   * every table the catalog routes to an operator / customer has some way of working out
    //     WHO that is, so "Operator" can never mean "silently goes nowhere".
    public class RealtimeEntityCatalogTests
    {
        // Entity property names the scope resolver can turn into an operator / customer. Kept in
        // step by hand with RealtimeChangeCapture.RefProperties and RealtimeScopeResolver — if a
        // new parent kind is added there, add it here.
        private static readonly string[] OperatorSuppliers =
        {
            "BusOperatorId", "BookingId", "PaymentId", "BusId", "OperatorRouteId", "StaffProfileId",
            "OperatorInvoiceId", "OperatorSettlementId", "SalesCounterId", "CancellationPolicyId", "TripId"
        };

        private static readonly string[] CustomerSuppliers = { "CustomerProfileId", "BookingId" };

        // DbSet property name (== table name; AppDbContext has no ToTable overrides) -> entity type.
        private static Dictionary<string, Type> DbSets() =>
            typeof(AppDbContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.PropertyType.IsGenericType
                    && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .ToDictionary(p => p.Name, p => p.PropertyType.GetGenericArguments()[0], StringComparer.Ordinal);

        [Fact]
        public void EveryDbSet_HasAnExplicitDecisionInTheCatalog()
        {
            var listed = RealtimeEntityCatalog.ListedTables.ToHashSet(StringComparer.Ordinal);
            var missing = DbSets().Keys.Where(name => !listed.Contains(name)).OrderBy(n => n).ToList();

            Assert.True(
                missing.Count == 0,
                "These tables have no entry in RealtimeEntityCatalog. Decide who may hear about them " +
                "(PlatformOnly is a fine answer): " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryCatalogEntry_IsARealTable()
        {
            var real = DbSets().Keys.Concat(RealtimeEntityCatalog.IdentityTables).ToHashSet(StringComparer.Ordinal);
            var unknown = RealtimeEntityCatalog.ListedTables.Where(name => !real.Contains(name)).OrderBy(n => n).ToList();

            Assert.True(
                unknown.Count == 0,
                "RealtimeEntityCatalog lists tables that do not exist (renamed or removed?): " + string.Join(", ", unknown));
        }

        [Fact]
        public void IgnoredTables_AreNeverAlsoListed()
        {
            var both = RealtimeEntityCatalog.ListedTables.Where(RealtimeEntityCatalog.IgnoredTables.Contains).ToList();

            Assert.Empty(both);
        }

        [Fact]
        public void TableNamesEqualDbSetNames()
        {
            // The catalog (and the Chunk 2 capture code) rely on EF's table name being the DbSet
            // name. If anyone adds ToTable()/[Table] this guard tells them what to revisit.
            var source = File.ReadAllText(FindApiFile("Data", "AppDbContext.cs"));

            Assert.DoesNotContain(".ToTable(", source);
        }

        [Fact]
        public void EveryOperatorRoutedTable_CanBeResolvedToAnOperator()
        {
            var unresolvable = DbSets()
                .Where(kv => RealtimeEntityCatalog.AllowsOperator(kv.Key))
                .Where(kv => kv.Value.Name != "BusOperator"
                    && !OperatorSuppliers.Any(prop => kv.Value.GetProperty(prop) is not null))
                .Select(kv => kv.Key)
                .OrderBy(n => n)
                .ToList();

            Assert.True(
                unresolvable.Count == 0,
                "Marked Operator but nothing on the entity says which operator owns a row: " + string.Join(", ", unresolvable));
        }

        [Fact]
        public void EveryCustomerRoutedTable_CanBeResolvedToACustomer()
        {
            var unresolvable = DbSets()
                .Where(kv => RealtimeEntityCatalog.AllowsCustomer(kv.Key))
                .Where(kv => kv.Value.Name != "CustomerProfile"
                    && !CustomerSuppliers.Any(prop => kv.Value.GetProperty(prop) is not null))
                .Select(kv => kv.Key)
                .OrderBy(n => n)
                .ToList();

            Assert.True(
                unresolvable.Count == 0,
                "Marked Customer but nothing on the entity says which customer owns a row: " + string.Join(", ", unresolvable));
        }

        [Fact]
        public void UnknownTable_IsPlatformOnly()
        {
            Assert.Equal(RealtimeAudience.PlatformOnly, RealtimeEntityCatalog.AudienceOf("NoSuchTable"));
            Assert.False(RealtimeEntityCatalog.AllowsOperator("NoSuchTable"));
            Assert.False(RealtimeEntityCatalog.AllowsCustomer("NoSuchTable"));
            Assert.False(RealtimeEntityCatalog.AllowsShared("NoSuchTable"));
        }

        [Theory]
        [InlineData("PlatformLedgers")]
        [InlineData("ActivityLogs")]
        [InlineData("AuditLogs")]
        [InlineData("LoginHistories")]
        [InlineData("SystemSettings")]
        [InlineData("AspNetUsers")]
        [InlineData("PaymentWebhookEvents")]
        [InlineData("IntegrationSyncLogs")]
        public void SensitiveOrInternalTables_AreStrictlyPlatformOnly(string table)
        {
            Assert.Equal(RealtimeAudience.PlatformOnly, RealtimeEntityCatalog.AudienceOf(table));
        }

        [Theory]
        [InlineData("OperatorInvoices")]
        [InlineData("OperatorPayouts")]
        [InlineData("OperatorSettlements")]
        [InlineData("OperatorWallets")]
        public void OperatorFinanceTables_NeverReachCustomers(string table)
        {
            Assert.True(RealtimeEntityCatalog.AllowsOperator(table));
            Assert.False(RealtimeEntityCatalog.AllowsCustomer(table));
        }

        [Theory]
        [InlineData("CustomerWalletTransactions")]
        [InlineData("CustomerProfiles")]
        [InlineData("CustomerAddresses")]
        public void CustomerPrivateTables_NeverReachOperators(string table)
        {
            Assert.True(RealtimeEntityCatalog.AllowsCustomer(table));
            Assert.False(RealtimeEntityCatalog.AllowsOperator(table));
        }

        // apps/api.tests/Unit/<this file>  ->  apps/api/<folder>/<file>
        private static string FindApiFile(string folder, string file, [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            var unitDir = Path.GetDirectoryName(thisFile)!;
            var apps = Directory.GetParent(unitDir)!.Parent!.FullName;
            return Path.Combine(apps, "api", folder, file);
        }
    }
}
