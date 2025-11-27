using SerialMemory.Core.Enterprise;
using Xunit;

namespace SerialMemory.Tests.Enterprise;

public class CircuitBreakerTests
{
    [Fact]
    public void NewBreaker_StartsInClosedState()
    {
        var breaker = new CircuitBreaker("test");

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.AllowRequest());
    }

    [Fact]
    public void RecordFailure_BelowThreshold_StaysClosed()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 5);

        for (int i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.AllowRequest());
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCircuit()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 5);

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.AllowRequest());
    }

    [Fact]
    public void OpenCircuit_RejectsRequests()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 1);
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.AllowRequest());
    }

    [Fact]
    public void OpenCircuit_TransitionsToHalfOpen_AfterDuration()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(10));
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);

        Thread.Sleep(20);

        Assert.True(breaker.AllowRequest()); // Transitions to HalfOpen
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
    }

    [Fact]
    public void HalfOpen_ClosesOnSuccess()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(1));
        breaker.RecordFailure();

        Thread.Sleep(5);
        breaker.AllowRequest(); // Transition to HalfOpen

        breaker.RecordSuccess();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.FailureCount);
    }

    [Fact]
    public void HalfOpen_ReopensOnFailure()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(1));
        breaker.RecordFailure();

        Thread.Sleep(5);
        breaker.AllowRequest(); // Transition to HalfOpen

        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void Reset_ClearsStateAndCounters()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 1);
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);

        breaker.Reset();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.FailureCount);
        Assert.True(breaker.AllowRequest());
    }

    [Fact]
    public void ForceState_ChangesState()
    {
        var breaker = new CircuitBreaker("test");

        breaker.ForceState(CircuitState.Open);
        Assert.Equal(CircuitState.Open, breaker.State);

        breaker.ForceState(CircuitState.HalfOpen);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);

        breaker.ForceState(CircuitState.Closed);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void GetSnapshot_ReturnsCurrentState()
    {
        var breaker = new CircuitBreaker("test-breaker", failureThreshold: 3);
        breaker.RecordFailure();
        breaker.RecordSuccess();

        var snapshot = breaker.GetSnapshot();

        Assert.Equal("test-breaker", snapshot.Name);
        Assert.Equal(CircuitState.Closed, snapshot.State);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(1, snapshot.SuccessCount);
        Assert.Equal(3, snapshot.FailureThreshold);
    }

    [Fact]
    public async Task ExecuteAsync_Success_RecordsSuccess()
    {
        var breaker = new CircuitBreaker("test");

        var result = await CircuitBreakerExecutor.ExecuteAsync(
            breaker,
            () => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Equal(1, breaker.SuccessCount);
    }

    [Fact]
    public async Task ExecuteAsync_Failure_RecordsFailure()
    {
        var breaker = new CircuitBreaker("test");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CircuitBreakerExecutor.ExecuteAsync<int>(
                breaker,
                () => throw new InvalidOperationException("test")));

        Assert.Equal(1, breaker.FailureCount);
    }

    [Fact]
    public async Task ExecuteAsync_OpenCircuit_ThrowsException()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 1);
        breaker.RecordFailure();

        var ex = await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await CircuitBreakerExecutor.ExecuteAsync(
                breaker,
                () => Task.FromResult(42)));

        Assert.Equal("test", ex.BreakerName);
    }

    [Fact]
    public async Task ExecuteAsync_OpenCircuit_WithFallback_ReturnsFallback()
    {
        var breaker = new CircuitBreaker("test", failureThreshold: 1);
        breaker.RecordFailure();

        var result = await CircuitBreakerExecutor.ExecuteAsync(
            breaker,
            () => Task.FromResult(42),
            fallback: () => -1);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void Registry_GetOrCreate_ReturnsSameBreaker()
    {
        var registry = new CircuitBreakerRegistry();

        var breaker1 = registry.GetOrCreate("test");
        var breaker2 = registry.GetOrCreate("test");

        Assert.Same(breaker1, breaker2);
    }

    [Fact]
    public void Registry_GetAllSnapshots_ReturnsAllBreakers()
    {
        var registry = new CircuitBreakerRegistry();
        registry.GetOrCreate("breaker1");
        registry.GetOrCreate("breaker2");

        var snapshots = registry.GetAllSnapshots();

        Assert.Equal(2, snapshots.Count);
        Assert.Contains("breaker1", snapshots.Keys);
        Assert.Contains("breaker2", snapshots.Keys);
    }
}
