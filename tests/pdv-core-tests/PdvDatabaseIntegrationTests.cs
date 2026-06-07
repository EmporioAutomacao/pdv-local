using PdvLocal.Core;

namespace PdvLocal.Core.Tests;

public sealed class PdvDatabaseIntegrationTests
{
    [Fact]
    public async Task ReadAsync_returns_status_when_test_database_is_configured()
    {
        var connectionString = Environment.GetEnvironmentVariable("PDV_LOCAL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var reader = new PdvDatabaseStatusReader(connectionString);

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.True(status.IsAvailable, status.Error);
        Assert.True(status.PdvSchemaExists);
        Assert.True(status.PdvTableCount >= 8);
    }
}
