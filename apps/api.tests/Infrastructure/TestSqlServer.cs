using Microsoft.Data.SqlClient;

namespace TicketPortal.Api.Tests.Infrastructure
{
    // C7-4: ONE place that decides which SQL Server the integration tests talk to.
    //
    //  * Default (a developer's Windows machine): SQL Server LocalDB, exactly as before.
    //  * CI / Linux / Docker: set TICKETPORTAL_TEST_SQLSERVER to a server-level connection string
    //    WITHOUT a database, e.g.
    //      Server=localhost,1433;User Id=sa;Password=<pwd>;TrustServerCertificate=True
    //    Every test database name is appended to it, so each run still gets its own throwaway DB.
    public static class TestSqlServer
    {
        public const string EnvironmentVariable = "TICKETPORTAL_TEST_SQLSERVER";

        private const string LocalDbServer =
            @"Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True";

        private static string ServerConnectionString =>
            Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured
                ? configured
                : LocalDbServer;

        public static string ConnectionStringFor(string databaseName) =>
            new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = databaseName }.ConnectionString;

        public static string MasterConnectionString => ConnectionStringFor("master");

        // Best-effort: drops a per-run test database so repeated runs never accumulate files.
        public static async Task DropDatabaseQuietlyAsync(string databaseName)
        {
            try
            {
                await using var connection = new SqlConnection(MasterConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN " +
                    $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE [{databaseName}]; END";
                await command.ExecuteNonQueryAsync();
            }
            catch
            {
                // Cleanup must never fail a test run.
            }
        }
    }
}
