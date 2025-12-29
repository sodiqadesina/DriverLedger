namespace DriverLedger.IntegrationTests.Fixtures;

/// <summary>
/// Provides SQL connection string for integration tests.
/// Connection string MUST be supplied via environment variable.
/// </summary>
public sealed class SqlConnectionFixture
{
    public string ConnectionString { get; }

    public SqlConnectionFixture()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("DRIVERLEDGER_SQL")
            ?? throw new InvalidOperationException(
                "Environment variable DRIVERLEDGER_SQL is not set. " +
                "Example: Server=(localdb)\\MSSQLLocalDB;Database=DriverLedgerDb;Trusted_Connection=True;...");

        // Make a unique DB name per test run
        var dbName = $"DriverLedgerDb_test_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

        ConnectionString = WithDatabase(baseConn, dbName);
    }

    private static string WithDatabase(string conn, string dbName)
    {
        // safest: strip any existing Database/Initial Catalog and append ours
        var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p =>
                            !p.TrimStart().StartsWith("Database=", StringComparison.OrdinalIgnoreCase) &&
                            !p.TrimStart().StartsWith("Initial Catalog=", StringComparison.OrdinalIgnoreCase));

        return string.Join(';', parts) + $";Database={dbName};";
    }
}
