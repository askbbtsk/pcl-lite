using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar.Social;

namespace PCL.Core.Test.App.Lunar;

[TestClass]
public sealed class LunarSocialServiceLoginRetryTests
{
    [TestMethod]
    public async Task LoginRetriesWithOfficialBackoffAndStopsAfterSuccess()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        await LunarSocialServiceLoginRetry.RunAsync(
            "friend",
            _ =>
            {
                attempts++;
                return attempts < 3 ? Task.FromException(new InvalidOperationException("temporary")) : Task.CompletedTask;
            },
            () => true,
            CancellationToken.None,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            delays);
    }

    [TestMethod]
    public async Task LoginStopsAfterFiveAttemptsAndDoesNotDisconnectOtherServices()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        await LunarSocialServiceLoginRetry.RunAsync(
            "conversation",
            _ =>
            {
                attempts++;
                return Task.FromException(new InvalidOperationException("unavailable"));
            },
            () => true,
            CancellationToken.None,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.AreEqual(5, attempts);
        CollectionAssert.AreEqual(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8)
            },
            delays);
    }

    [TestMethod]
    public async Task LoginStopsRetryingWhenSocketCloses()
    {
        var attempts = 0;
        var socketOpen = true;
        var delays = new List<TimeSpan>();

        await LunarSocialServiceLoginRetry.RunAsync(
            "friend",
            _ =>
            {
                attempts++;
                socketOpen = false;
                return Task.FromException(new InvalidOperationException("socket closed"));
            },
            () => socketOpen,
            CancellationToken.None,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.AreEqual(1, attempts);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, delays);
    }

    [TestMethod]
    public async Task LoginPropagatesCallerCancellationDuringBackoff()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            LunarSocialServiceLoginRetry.RunAsync(
                "friend",
                _ =>
                {
                    attempts++;
                    return Task.FromException(new InvalidOperationException("temporary"));
                },
                () => true,
                cancellation.Token,
                (_, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                }));

        Assert.AreEqual(1, attempts);
    }
}
