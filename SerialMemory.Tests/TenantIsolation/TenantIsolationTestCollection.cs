using Xunit;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// Test collection for tenant isolation tests.
/// All tests in this collection share the same PostgreSQL container
/// and can run in parallel within the collection.
/// </summary>
[CollectionDefinition("TenantIsolation")]
public sealed class TenantIsolationTestCollection : ICollectionFixture<TenantIsolationTestFixture>
{
    // This class has no code, and is never created.
    // Its purpose is to be the place to apply [CollectionDefinition]
    // and all the ICollectionFixture<> interfaces.
}

/// <summary>
/// Shared fixture for tenant isolation tests.
/// </summary>
public sealed class TenantIsolationTestFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        // Collection-level initialization if needed
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}
