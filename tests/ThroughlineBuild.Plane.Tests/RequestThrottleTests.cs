using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Plane.Tests;

public class RequestThrottleTests
{
    // A virtual clock + delay so the rolling-window admission logic is exercised
    // without real time passing. Delay advances the clock; nothing actually sleeps.
    private sealed class VirtualClock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int DelayCount { get; private set; }
        public TimeSpan TotalDelayed { get; private set; }

        public DateTimeOffset GetNow() => Now;

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            DelayCount++;
            TotalDelayed += d;
            Now += d;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task UnderBudget_AdmitsImmediately_NoWait()
    {
        var clock = new VirtualClock();
        var throttle = new RequestThrottle(60, TimeSpan.FromMinutes(1), clock.GetNow, clock.Delay);

        for (var i = 0; i < 60; i++)
            await throttle.AcquireAsync();

        Assert.Equal(0, clock.DelayCount);
        Assert.Equal(clock.Now, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task OverBudget_HardWaitsUntilWindowRolls()
    {
        var clock = new VirtualClock();
        var throttle = new RequestThrottle(2, TimeSpan.FromMinutes(1), clock.GetNow, clock.Delay);

        // First two admitted with no wait.
        await throttle.AcquireAsync();
        await throttle.AcquireAsync();
        Assert.Equal(0, clock.DelayCount);

        // Third must wait the full window (oldest admitted at t0, window = 60s).
        await throttle.AcquireAsync();

        Assert.Equal(1, clock.DelayCount);
        Assert.Equal(TimeSpan.FromMinutes(1), clock.TotalDelayed);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero), clock.Now);
    }

    [Fact]
    public async Task SteadyState_PacesAtWindowRate()
    {
        var clock = new VirtualClock();
        var throttle = new RequestThrottle(1, TimeSpan.FromMinutes(1), clock.GetNow, clock.Delay);

        // One per window: each call after the first waits a full window.
        await throttle.AcquireAsync(); // t0,    no wait
        await throttle.AcquireAsync(); // +60s
        await throttle.AcquireAsync(); // +60s

        Assert.Equal(2, clock.DelayCount);
        Assert.Equal(TimeSpan.FromMinutes(2), clock.TotalDelayed);
    }
}
