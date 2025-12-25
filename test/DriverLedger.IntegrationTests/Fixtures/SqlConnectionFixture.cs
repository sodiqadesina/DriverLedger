namespace DriverLedger.IntegrationTests.Fixtures;

/// <summary>
/// Provides SQL connection string for integration tests.
/// Connection string MUST be supplied via environment variable.
/// </summary>
public sealed class SqlConnectionFixture
{
    public string ConnectionString =>
        Environment.GetEnvironmentVariable("DRIVERLEDGER_SQL")
        ?? throw new InvalidOperationException(
            "Environment variable DRIVERLEDGER_SQL is not set. " +
            "Integration tests require an explicit database connection string.");
}
