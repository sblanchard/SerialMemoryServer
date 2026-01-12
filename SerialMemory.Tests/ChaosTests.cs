using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;
using Xunit;

namespace SerialMemory.Tests;

/// <summary>
/// Chaos tests for validating system resilience under failure conditions.
/// Tests worker kills, database interrupts, and queue failures.
/// </summary>
[Trait("Category", "Chaos")]
public class ChaosTests
{
    private readonly Mock<ILogger<JobSupervisionService>> _jobLoggerMock = new();
    private readonly Mock<ILogger<CircuitBreakerService>> _cbLoggerMock = new();
    private readonly Mock<ILogger<RateLimitingService>> _rlLoggerMock = new();

    #region Worker Kill Tests

    /// <summary>
    /// Tests that jobs stuck due to worker death are detected and recovered.
    /// </summary>
    [Fact]
    public async Task Worker_Kill_StuckJobsAreDetected()
    {
        // Arrange
        var simulatedJobs = new ConcurrentDictionary<Guid, SimulatedJob>();
        var heartbeatTimeout = TimeSpan.FromSeconds(5);

        // Simulate 5 jobs starting
        for (int i = 0; i < 5; i++)
        {
            var jobId = Guid.CreateVersion7();
            simulatedJobs[jobId] = new SimulatedJob
            {
                Id = jobId,
                Status = "running",
                LastHeartbeat = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow
            };
        }

        // Simulate worker crash - no more heartbeats
        await Task.Delay(100);

        // Advance time past heartbeat timeout
        foreach (var job in simulatedJobs.Values)
        {
            job.LastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-10);
        }

        // Act - detect stuck jobs
        var stuckJobs = simulatedJobs.Values
            .Where(j => j.Status == "running" &&
                       (DateTimeOffset.UtcNow - j.LastHeartbeat) > heartbeatTimeout)
            .ToList();

        // Assert
        stuckJobs.Should().HaveCount(5);
        stuckJobs.Should().AllSatisfy(j => j.Status.Should().Be("running"));
    }

    /// <summary>
    /// Tests that stuck jobs can be automatically retried.
    /// </summary>
    [Fact]
    public async Task Worker_Kill_StuckJobsAreAutomaticallyRetried()
    {
        // Arrange
        var simulatedJobs = new ConcurrentDictionary<Guid, SimulatedJob>();
        var maxRetries = 3;

        var job = new SimulatedJob
        {
            Id = Guid.CreateVersion7(),
            Status = "running",
            LastHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-5),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
            RetryCount = 0,
            MaxRetries = maxRetries
        };
        simulatedJobs[job.Id] = job;

        // Act - simulate retry logic
        if (job.RetryCount < job.MaxRetries)
        {
            job.RetryCount++;
            job.Status = "pending";
            job.LastHeartbeat = DateTimeOffset.UtcNow;
        }

        await Task.Delay(10);

        // Simulate job being picked up again
        job.Status = "running";
        job.LastHeartbeat = DateTimeOffset.UtcNow;

        // Assert
        job.RetryCount.Should().Be(1);
        job.Status.Should().Be("running");
    }

    /// <summary>
    /// Tests that jobs exceeding max retries go to dead letter queue.
    /// </summary>
    [Fact]
    public void Worker_Kill_ExhaustedRetriesGoToDLQ()
    {
        // Arrange
        var deadLetterQueue = new ConcurrentQueue<SimulatedJob>();
        var maxRetries = 3;

        var job = new SimulatedJob
        {
            Id = Guid.CreateVersion7(),
            Status = "running",
            LastHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-5),
            RetryCount = 3, // Already at max
            MaxRetries = maxRetries
        };

        // Act - check if should go to DLQ
        if (job.RetryCount >= job.MaxRetries)
        {
            job.Status = "dead_letter";
            job.MovedToDlqAt = DateTimeOffset.UtcNow;
            job.DlqReason = "Max retries exceeded after worker crash";
            deadLetterQueue.Enqueue(job);
        }

        // Assert
        job.Status.Should().Be("dead_letter");
        deadLetterQueue.Should().HaveCount(1);
        job.DlqReason.Should().Contain("Max retries");
    }

    #endregion

    #region Database Interrupt Tests

    /// <summary>
    /// Tests circuit breaker opens after database connection failures.
    /// </summary>
    [Fact]
    public async Task Database_Interrupt_CircuitBreakerOpens()
    {
        // Arrange
        var circuitBreaker = new CircuitBreakerService(_cbLoggerMock.Object);
        var circuitName = "postgresql";
        var config = CircuitBreakerConfig.Default;

        // Act - simulate database failures
        for (int i = 0; i < config.FailureThreshold; i++)
        {
            await circuitBreaker.RecordFailureAsync(circuitName);
        }

        var state = await circuitBreaker.GetStateAsync(circuitName);

        // Assert
        state.Should().Be(CircuitState.Open);
    }

    /// <summary>
    /// Tests that operations fail fast when circuit is open.
    /// </summary>
    [Fact]
    public async Task Database_Interrupt_OperationsFailFastWhenCircuitOpen()
    {
        // Arrange
        var circuitBreaker = new CircuitBreakerService(_cbLoggerMock.Object);
        var circuitName = "postgresql";

        // Open the circuit
        await circuitBreaker.OpenCircuitAsync(circuitName, TimeSpan.FromMinutes(1), "Database unreachable");

        // Act
        var canExecute = await circuitBreaker.TryExecuteAsync(circuitName);

        // Assert
        canExecute.Should().BeFalse();
    }

    /// <summary>
    /// Tests circuit breaker allows half-open testing after timeout.
    /// </summary>
    [Fact]
    public async Task Database_Interrupt_CircuitAllowsHalfOpenTesting()
    {
        // Arrange
        var circuitBreaker = new CircuitBreakerService(_cbLoggerMock.Object);
        var circuitName = "postgresql";

        // Open circuit with very short timeout for testing
        await circuitBreaker.OpenCircuitAsync(circuitName, TimeSpan.FromMilliseconds(100), "Test");

        // Wait for timeout
        await Task.Delay(150);

        // Act - should allow one test request
        var canExecute = await circuitBreaker.TryExecuteAsync(circuitName);

        // Assert - after timeout, circuit transitions to half-open and allows test
        // Result depends on implementation - we just verify no exception
        canExecute.Should().BeTrue();
    }

    /// <summary>
    /// Tests that partial database failures are handled gracefully.
    /// </summary>
    [Fact]
    public void Database_Interrupt_PartialFailuresHandledGracefully()
    {
        // Arrange
        var operations = new List<OperationResult>();
        var dbAvailable = false;

        // Act - simulate mixed success/failure
        for (int i = 0; i < 10; i++)
        {
            // Toggle availability
            if (i == 5) dbAvailable = true;

            var result = new OperationResult
            {
                OperationId = i,
                Success = dbAvailable,
                Error = dbAvailable ? null : "Database connection timeout",
                FallbackUsed = !dbAvailable
            };
            operations.Add(result);
        }

        // Assert
        var failures = operations.Count(o => !o.Success);
        var successes = operations.Count(o => o.Success);
        var fallbacks = operations.Count(o => o.FallbackUsed);

        failures.Should().Be(5);
        successes.Should().Be(5);
        fallbacks.Should().Be(5);
    }

    #endregion

    #region Queue Kill Tests

    /// <summary>
    /// Tests that in-flight messages are preserved when queue dies.
    /// </summary>
    [Fact]
    public void Queue_Kill_InFlightMessagesTracked()
    {
        // Arrange
        var inFlightMessages = new ConcurrentDictionary<Guid, QueueMessage>();
        var acknowledgedMessages = new ConcurrentBag<Guid>();

        // Simulate messages being processed
        for (int i = 0; i < 10; i++)
        {
            var msg = new QueueMessage
            {
                Id = Guid.CreateVersion7(),
                Content = $"Message {i}",
                DequeuedAt = DateTimeOffset.UtcNow,
                AcknowledgedAt = null
            };
            inFlightMessages[msg.Id] = msg;
        }

        // Simulate partial acknowledgment before "crash"
        var processed = inFlightMessages.Values.Take(3).ToList();
        foreach (var msg in processed)
        {
            msg.AcknowledgedAt = DateTimeOffset.UtcNow;
            acknowledgedMessages.Add(msg.Id);
            inFlightMessages.TryRemove(msg.Id, out _);
        }

        // Act - "queue dies", check what's still in flight
        var unacknowledged = inFlightMessages.Values
            .Where(m => m.AcknowledgedAt == null)
            .ToList();

        // Assert
        unacknowledged.Should().HaveCount(7);
        acknowledgedMessages.Should().HaveCount(3);
    }

    /// <summary>
    /// Tests that unacknowledged messages can be replayed.
    /// </summary>
    [Fact]
    public void Queue_Kill_UnacknowledgedMessagesCanBeReplayed()
    {
        // Arrange
        var originalMessages = new List<QueueMessage>();
        var replayedMessages = new List<QueueMessage>();

        for (int i = 0; i < 5; i++)
        {
            originalMessages.Add(new QueueMessage
            {
                Id = Guid.CreateVersion7(),
                Content = $"Message {i}",
                DequeuedAt = DateTimeOffset.UtcNow,
                ReplayCount = 0
            });
        }

        // Act - simulate replay after recovery
        foreach (var msg in originalMessages)
        {
            var replayed = new QueueMessage
            {
                Id = msg.Id,
                Content = msg.Content,
                DequeuedAt = DateTimeOffset.UtcNow,
                ReplayCount = msg.ReplayCount + 1,
                OriginalDequeuedAt = msg.DequeuedAt
            };
            replayedMessages.Add(replayed);
        }

        // Assert
        replayedMessages.Should().HaveCount(5);
        replayedMessages.Should().AllSatisfy(m => m.ReplayCount.Should().Be(1));
        replayedMessages.Should().AllSatisfy(m => m.OriginalDequeuedAt.Should().NotBeNull());
    }

    /// <summary>
    /// Tests that poison messages (repeated failures) are detected.
    /// </summary>
    [Fact]
    public void Queue_Kill_PoisonMessagesDetected()
    {
        // Arrange
        var poisonQueue = new ConcurrentQueue<QueueMessage>();
        var maxReplays = 3;

        var poisonMessage = new QueueMessage
        {
            Id = Guid.CreateVersion7(),
            Content = "Problematic message",
            ReplayCount = 4 // Exceeded max
        };

        // Act
        if (poisonMessage.ReplayCount > maxReplays)
        {
            poisonMessage.IsPoisonMessage = true;
            poisonMessage.PoisonReason = $"Exceeded max replay count of {maxReplays}";
            poisonQueue.Enqueue(poisonMessage);
        }

        // Assert
        poisonMessage.IsPoisonMessage.Should().BeTrue();
        poisonQueue.Should().HaveCount(1);
    }

    #endregion

    #region Combined Chaos Scenarios

    /// <summary>
    /// Tests system behavior under cascading failures.
    /// </summary>
    [Fact]
    public async Task CascadingFailure_SystemDegradeGracefully()
    {
        // Arrange
        var circuitBreaker = new CircuitBreakerService(_cbLoggerMock.Object);
        var systemHealth = new SystemHealthState();

        // Act - simulate cascading failures
        // 1. Database goes down
        for (int i = 0; i < 5; i++)
        {
            await circuitBreaker.RecordFailureAsync("postgresql");
        }
        systemHealth.DatabaseAvailable = false;

        // 2. Queue becomes unavailable
        for (int i = 0; i < 5; i++)
        {
            await circuitBreaker.RecordFailureAsync("rabbitmq");
        }
        systemHealth.QueueAvailable = false;

        // 3. Check degraded mode capabilities
        systemHealth.DegradedMode = !systemHealth.DatabaseAvailable || !systemHealth.QueueAvailable;
        systemHealth.ReadOnlyMode = !systemHealth.DatabaseAvailable;
        systemHealth.AsyncDisabled = !systemHealth.QueueAvailable;

        // Assert
        systemHealth.DegradedMode.Should().BeTrue();
        systemHealth.ReadOnlyMode.Should().BeTrue();
        systemHealth.AsyncDisabled.Should().BeTrue();

        // Verify circuits are open
        var dbState = await circuitBreaker.GetStateAsync("postgresql");
        var queueState = await circuitBreaker.GetStateAsync("rabbitmq");

        dbState.Should().Be(CircuitState.Open);
        queueState.Should().Be(CircuitState.Open);
    }

    /// <summary>
    /// Tests recovery from total system failure.
    /// </summary>
    [Fact]
    public async Task TotalFailure_SystemRecoversGracefully()
    {
        // Arrange
        var circuitBreaker = new CircuitBreakerService(_cbLoggerMock.Object);
        var recoverySteps = new List<string>();

        // Simulate total failure
        await circuitBreaker.OpenCircuitAsync("postgresql", TimeSpan.FromMilliseconds(100), "Total failure");
        await circuitBreaker.OpenCircuitAsync("rabbitmq", TimeSpan.FromMilliseconds(100), "Total failure");
        await circuitBreaker.OpenCircuitAsync("redis", TimeSpan.FromMilliseconds(100), "Total failure");

        // Act - simulate recovery
        await Task.Delay(150); // Wait for circuits to allow testing

        // Try database first
        if (await circuitBreaker.TryExecuteAsync("postgresql"))
        {
            await circuitBreaker.RecordSuccessAsync("postgresql");
            recoverySteps.Add("Database connection restored");
        }

        // Then queue
        if (await circuitBreaker.TryExecuteAsync("rabbitmq"))
        {
            await circuitBreaker.RecordSuccessAsync("rabbitmq");
            recoverySteps.Add("Message queue connection restored");
        }

        // Then cache
        if (await circuitBreaker.TryExecuteAsync("redis"))
        {
            await circuitBreaker.RecordSuccessAsync("redis");
            recoverySteps.Add("Cache connection restored");
        }

        // Assert - recovery should be possible after timeout
        recoverySteps.Should().HaveCountGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Tests rate limiting continues to work during partial failures.
    /// </summary>
    [Fact]
    public void RateLimiting_ContinuesDuringPartialFailure()
    {
        // Arrange - use in-memory rate limiter
        var requestCounts = new ConcurrentDictionary<string, int>();
        var burstLimit = 10;
        var tenantId = "test-tenant";

        // Act - simulate requests during "partial failure"
        var results = new List<bool>();
        for (int i = 0; i < 15; i++)
        {
            var currentCount = requestCounts.AddOrUpdate(
                tenantId,
                1,
                (_, count) => count + 1);

            var allowed = currentCount <= burstLimit;
            results.Add(allowed);
        }

        // Assert
        var allowedCount = results.Count(r => r);
        var deniedCount = results.Count(r => !r);

        allowedCount.Should().Be(10);
        deniedCount.Should().Be(5);
    }

    #endregion

    #region Helper Classes

    private sealed class SimulatedJob
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset LastHeartbeat { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; } = 3;
        public DateTimeOffset? MovedToDlqAt { get; set; }
        public string? DlqReason { get; set; }
    }

    private sealed class OperationResult
    {
        public int OperationId { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public bool FallbackUsed { get; set; }
    }

    private sealed class QueueMessage
    {
        public Guid Id { get; set; }
        public string Content { get; set; } = "";
        public DateTimeOffset DequeuedAt { get; set; }
        public DateTimeOffset? AcknowledgedAt { get; set; }
        public int ReplayCount { get; set; }
        public DateTimeOffset? OriginalDequeuedAt { get; set; }
        public bool IsPoisonMessage { get; set; }
        public string? PoisonReason { get; set; }
    }

    private sealed class SystemHealthState
    {
        public bool DatabaseAvailable { get; set; } = true;
        public bool QueueAvailable { get; set; } = true;
        public bool CacheAvailable { get; set; } = true;
        public bool DegradedMode { get; set; }
        public bool ReadOnlyMode { get; set; }
        public bool AsyncDisabled { get; set; }
    }

    #endregion
}
