using Mmorpg.Client.Api;
using Mmorpg.Client.Ui;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // A login or registration that GameService refuses with auth_busy waits the answer's
    // Retry-After and goes again by itself, a bounded number of times. The tests pass the time,
    // so nothing really waits.
    public sealed class AuthBusyRetryTests
    {
        [Test]
        public void ABusyAnswerIsWaitedOutForItsRetryAfterAndRetriedABoundedNumberOfTimes()
        {
            var retry = new AuthBusyRetry();
            var now = 100d;

            for (var attempt = 1; attempt <= AuthBusyRetry.MaximumRetries; attempt++)
            {
                Assert.That(retry.TrySchedule(Busy(3), now), Is.True);
                Assert.That(retry.Retries, Is.EqualTo(attempt));
                Assert.That(retry.IsWaiting, Is.True);
                Assert.That(retry.SecondsLeft(now + 0.2), Is.EqualTo(3));
                Assert.That(retry.IsDue(now + 2.9), Is.False);
                Assert.That(retry.IsDue(now + 3), Is.True);
                retry.BeginAttempt();
                Assert.That(retry.IsWaiting, Is.False);
                now += 3;
            }

            Assert.That(retry.TrySchedule(Busy(3), now), Is.False, "The retries are used up, so the error shows.");
            Assert.That(retry.IsWaiting, Is.False);
        }

        [Test]
        public void TheWaitStaysBetweenOneAndThirtySecondsAndIsFiveWithoutAUsableHeader()
        {
            Assert.That(AuthBusyRetry.DelaySeconds(Busy(0)), Is.EqualTo(AuthBusyRetry.DefaultDelaySeconds));
            Assert.That(AuthBusyRetry.DelaySeconds(Busy(1)), Is.EqualTo(1));
            Assert.That(AuthBusyRetry.DelaySeconds(Busy(12)), Is.EqualTo(12));
            Assert.That(AuthBusyRetry.DelaySeconds(Busy(600)), Is.EqualTo(AuthBusyRetry.LongestDelaySeconds));
            Assert.That(ApiError.ParseRetryAfter("7"), Is.EqualTo(7));
            Assert.That(ApiError.ParseRetryAfter(" 12 "), Is.EqualTo(12));
            Assert.That(ApiError.ParseRetryAfter("Sat, 14 Nov 2026 12:00:00 GMT"), Is.EqualTo(0));
            Assert.That(ApiError.ParseRetryAfter("-3"), Is.EqualTo(0));
            Assert.That(ApiError.ParseRetryAfter(null), Is.EqualTo(0));
        }

        [Test]
        public void OnlyAnAuthBusyAnswerIsRetried()
        {
            var retry = new AuthBusyRetry();

            Assert.That(retry.TrySchedule(new ApiError(ApiErrorKind.Http, 503, "session_cache_invalidation_unavailable", "Redis.", "c", 5), 0), Is.False);
            Assert.That(retry.TrySchedule(new ApiError(ApiErrorKind.Http, 429, "rate_limit_exceeded", "Limit.", "c", 30), 0), Is.False);
            Assert.That(retry.TrySchedule(new ApiError(ApiErrorKind.Network, 0, "network_error", "Down.", null), 0), Is.False);
            Assert.That(retry.TrySchedule(null, 0), Is.False);
            Assert.That(retry.Retries, Is.EqualTo(0));
        }

        // The player cancels, changes the form, or sends again: nothing is planned, and the next
        // busy answer counts from one.
        [Test]
        public void AResetStopsTheWaitAndCountsAgain()
        {
            var retry = new AuthBusyRetry();
            Assert.That(retry.TrySchedule(Busy(5), 10), Is.True);
            Assert.That(retry.TrySchedule(Busy(5), 15), Is.True);

            retry.Reset();

            Assert.That(retry.IsWaiting, Is.False);
            Assert.That(retry.IsDue(1000), Is.False);
            Assert.That(retry.SecondsLeft(10), Is.EqualTo(0));
            Assert.That(retry.TrySchedule(Busy(5), 20), Is.True);
            Assert.That(retry.Retries, Is.EqualTo(1));
        }

        // The answer carries its Retry-After, the busy line counts it down, and once the retries
        // are used up the code shows its own text.
        [Test]
        public void TheBusyLineCountsDownAndTheCodeHasItsText()
        {
            var error = ApiClient.ParseHttpError(
                503,
                "{\"status\":503,\"detail\":\"Busy.\",\"code\":\"auth_busy\",\"correlationId\":\"c-9\"}",
                "fallback",
                "4");
            Assert.That(AuthBusyRetry.IsBusy(error), Is.True);
            Assert.That(error.RetryAfterSeconds, Is.EqualTo(4));
            Assert.That(error.ToDiagnostic(), Does.EndWith("Retry after 4 s."));

            var texts = FixtureTexts.Ui();
            var retry = new AuthBusyRetry();
            Assert.That(AccountEntryPresentation.BusyRetryText(retry, 0, texts), Is.Empty);
            retry.TrySchedule(error, 50);
            Assert.That(
                AccountEntryPresentation.BusyRetryText(retry, 51.5, texts),
                Is.EqualTo(texts.Format(
                    "ui/login/busy-retry",
                    MessageArgument.Text("seconds", "3"),
                    MessageArgument.Text("retry", "1"),
                    MessageArgument.Text("retries", AuthBusyRetry.MaximumRetries.ToString()))));
            Assert.That(texts.Get("ui/login/busy-retry"), Does.Not.StartWith("["));

            var messages = FixtureTexts.Messages(("auth_busy", "The game service is busy. Try again in a moment."));
            Assert.That(
                AccountEntryPresentation.ErrorText(error, "http://localhost:5000", messages),
                Is.EqualTo("The game service is busy. Try again in a moment."));
        }

        private static ApiError Busy(int retryAfterSeconds)
        {
            return new ApiError(ApiErrorKind.Http, 503, AuthBusyRetry.BusyCode, "Busy.", "c-1", retryAfterSeconds);
        }
    }
}
