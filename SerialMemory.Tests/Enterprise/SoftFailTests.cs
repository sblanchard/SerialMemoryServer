using SerialMemory.Core.Enterprise;
using Xunit;

namespace SerialMemory.Tests.Enterprise;

public class SoftFailTests
{
    [Fact]
    public async Task TryAsync_Success_ReturnsSuccessResult()
    {
        var result = await SoftFail.TryAsync(() => Task.FromResult(42));

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
        Assert.Equal(SoftFailSeverity.None, result.Severity);
    }

    [Fact]
    public async Task TryAsync_Failure_ReturnsFailResult()
    {
        var result = await SoftFail.TryAsync<int>(() =>
            throw new InvalidOperationException("test error"));

        Assert.False(result.IsSuccess);
        Assert.Equal("test error", result.Error);
        Assert.NotNull(result.Exception);
        Assert.Equal(SoftFailSeverity.Warning, result.Severity); // InvalidOperationException -> Warning
    }

    [Fact]
    public async Task TryWithFallbackAsync_Success_ReturnsValue()
    {
        var result = await SoftFail.TryWithFallbackAsync(
            () => Task.FromResult(42),
            fallbackValue: -1);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.FallbackUsed);
    }

    [Fact]
    public async Task TryWithFallbackAsync_Failure_ReturnsFallback()
    {
        var result = await SoftFail.TryWithFallbackAsync(
            () => throw new Exception("fail"),
            fallbackValue: -1,
            fallbackName: "default");

        Assert.True(result.IsSuccess); // Fallback counts as success
        Assert.Equal(-1, result.Value);
        Assert.Equal("default", result.FallbackUsed);
    }

    [Fact]
    public async Task TryWithCascadingFallbackAsync_Primary_Succeeds()
    {
        var result = await SoftFail.TryWithCascadingFallbackAsync(
            () => Task.FromResult(1),
            ("fallback1", () => Task.FromResult(2)),
            ("fallback2", () => Task.FromResult(3)));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Null(result.FallbackUsed);
    }

    [Fact]
    public async Task TryWithCascadingFallbackAsync_UsesFirstSuccessfulFallback()
    {
        var result = await SoftFail.TryWithCascadingFallbackAsync<int>(
            () => throw new Exception("primary fail"),
            ("fallback1", () => throw new Exception("fallback1 fail")),
            ("fallback2", () => Task.FromResult(3)));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value);
        Assert.Equal("fallback2", result.FallbackUsed);
    }

    [Fact]
    public async Task TryWithCascadingFallbackAsync_AllFail_ReturnsFailure()
    {
        var result = await SoftFail.TryWithCascadingFallbackAsync<int>(
            () => throw new Exception("primary fail"),
            ("fallback1", () => throw new Exception("fallback1 fail")));

        Assert.False(result.IsSuccess);
        Assert.Contains("All operations failed", result.Error);
    }

    [Fact]
    public async Task TryWithTimeoutAsync_CompletesInTime_ReturnsValue()
    {
        var result = await SoftFail.TryWithTimeoutAsync(
            () => Task.FromResult(42),
            timeout: TimeSpan.FromSeconds(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task TryWithTimeoutAsync_Timeout_ReturnsFallback()
    {
        var result = await SoftFail.TryWithTimeoutAsync(
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return 42;
            },
            timeout: TimeSpan.FromMilliseconds(10),
            timeoutFallback: -1);

        Assert.True(result.IsSuccess);
        Assert.Equal(-1, result.Value);
        Assert.Equal("timeout", result.FallbackUsed);
    }

    [Fact]
    public async Task TryWithRetryAsync_SucceedsFirstTry_NoRetry()
    {
        var attempts = 0;
        var result = await SoftFail.TryWithRetryAsync(
            () =>
            {
                attempts++;
                return Task.FromResult(42);
            },
            maxRetries: 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task TryWithRetryAsync_FailsThenSucceeds_Retries()
    {
        var attempts = 0;
        var result = await SoftFail.TryWithRetryAsync(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new Exception("fail");
                return Task.FromResult(42);
            },
            maxRetries: 3,
            delayBetweenRetries: TimeSpan.FromMilliseconds(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task TryWithRetryAsync_AllFail_ReturnsFailure()
    {
        var attempts = 0;
        var result = await SoftFail.TryWithRetryAsync<int>(
            () =>
            {
                attempts++;
                throw new Exception("always fail");
            },
            maxRetries: 2,
            delayBetweenRetries: TimeSpan.FromMilliseconds(1));

        Assert.False(result.IsSuccess);
        Assert.Equal(3, attempts); // 1 initial + 2 retries
    }

    [Fact]
    public void SoftResult_Map_Success_TransformsValue()
    {
        var result = SoftResult<int>.Success(42);
        var mapped = result.Map(x => x * 2);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(84, mapped.Value);
    }

    [Fact]
    public void SoftResult_Map_Failure_PropagatesError()
    {
        var result = SoftResult<int>.Fail("error");
        var mapped = result.Map(x => x * 2);

        Assert.False(mapped.IsSuccess);
        Assert.Equal("error", mapped.Error);
    }

    [Fact]
    public void SoftResult_GetValueOrDefault_Success_ReturnsValue()
    {
        var result = SoftResult<int>.Success(42);
        Assert.Equal(42, result.GetValueOrDefault(-1));
    }

    [Fact]
    public void SoftResult_GetValueOrDefault_Failure_ReturnsDefault()
    {
        var result = SoftResult<int>.Fail("error");
        Assert.Equal(-1, result.GetValueOrDefault(-1));
    }

    [Fact]
    public void Combine_BothSuccess_ReturnsCombined()
    {
        var result1 = SoftResult<int>.Success(1);
        var result2 = SoftResult<string>.Success("two");

        var combined = SoftFail.Combine(result1, result2);

        Assert.True(combined.IsSuccess);
        Assert.Equal((1, "two"), combined.Value);
    }

    [Fact]
    public void Combine_FirstFails_ReturnsFirstError()
    {
        var result1 = SoftResult<int>.Fail("first error");
        var result2 = SoftResult<string>.Success("two");

        var combined = SoftFail.Combine(result1, result2);

        Assert.False(combined.IsSuccess);
        Assert.Equal("first error", combined.Error);
    }

    [Fact]
    public void DegradationManager_DefaultLevel_IsFull()
    {
        var manager = new DegradationManager();
        Assert.Equal(DegradationLevel.Full, manager.CurrentLevel);
        Assert.Empty(manager.DisabledFeatures);
    }

    [Fact]
    public void DegradationManager_SetLevel_DisablesFeatures()
    {
        var manager = new DegradationManager();

        manager.SetLevel(DegradationLevel.ReadOnly, "test");

        Assert.Equal(DegradationLevel.ReadOnly, manager.CurrentLevel);
        Assert.Contains("writes", manager.DisabledFeatures);
        Assert.Contains("deletes", manager.DisabledFeatures);
    }

    [Fact]
    public void DegradationManager_IsFeatureEnabled_RespectsLevel()
    {
        var manager = new DegradationManager();
        manager.SetLevel(DegradationLevel.ReadOnly, "test");

        Assert.False(manager.IsFeatureEnabled("writes"));
        Assert.False(manager.IsFeatureEnabled("deletes"));
        // Features not in disabled list should still be enabled
    }

    [Fact]
    public void DegradationManager_Restore_ReturnsToFull()
    {
        var manager = new DegradationManager();
        manager.SetLevel(DegradationLevel.Offline, "test");

        manager.Restore();

        Assert.Equal(DegradationLevel.Full, manager.CurrentLevel);
        Assert.Empty(manager.DisabledFeatures);
    }
}
