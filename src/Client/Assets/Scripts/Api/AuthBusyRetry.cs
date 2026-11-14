using System;

namespace Mmorpg.Client.Api
{
    // How a login or registration waits out GameService's 503 auth_busy: the password hashing
    // queue is full, so the client waits the answer's Retry-After, kept between one and 30
    // seconds and five seconds without one, and sends again by itself, at most MaximumRetries
    // times. The caller passes the time, so the rules run without real waiting.
    public sealed class AuthBusyRetry
    {
        public const string BusyCode = "auth_busy";
        public const int MaximumRetries = 5;
        public const int DefaultDelaySeconds = 5;
        public const int ShortestDelaySeconds = 1;
        public const int LongestDelaySeconds = 30;

        public int Retries { get; private set; }

        public bool IsWaiting { get; private set; }

        // When the next attempt is due, in the caller's seconds.
        public double DueAt { get; private set; }

        public static bool IsBusy(ApiError error)
        {
            return error != null
                && error.Kind == ApiErrorKind.Http
                && error.StatusCode == 503
                && string.Equals(error.Code, BusyCode, StringComparison.Ordinal);
        }

        public static int DelaySeconds(ApiError error)
        {
            var seconds = error != null ? error.RetryAfterSeconds : 0;
            return seconds <= 0
                ? DefaultDelaySeconds
                : Math.Min(LongestDelaySeconds, Math.Max(ShortestDelaySeconds, seconds));
        }

        // Plans the next attempt after a busy answer; false for any other answer and once the
        // retries are used up, when the caller shows the error.
        public bool TrySchedule(ApiError error, double now)
        {
            if (!IsBusy(error) || Retries >= MaximumRetries)
            {
                IsWaiting = false;
                return false;
            }

            Retries++;
            IsWaiting = true;
            DueAt = now + DelaySeconds(error);
            return true;
        }

        public bool IsDue(double now)
        {
            return IsWaiting && now >= DueAt;
        }

        // Whole seconds until the next attempt, for the busy line.
        public int SecondsLeft(double now)
        {
            return IsWaiting ? Math.Max(0, (int)Math.Ceiling(DueAt - now)) : 0;
        }

        // The planned attempt is being sent.
        public void BeginAttempt()
        {
            IsWaiting = false;
        }

        // The player cancelled, changed the form, or sent a new request: no attempt is planned,
        // and the next busy answer starts counting again.
        public void Reset()
        {
            Retries = 0;
            IsWaiting = false;
        }
    }
}
