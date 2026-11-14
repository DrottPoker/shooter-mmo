using System;
using System.Globalization;

namespace Mmorpg.Client.Api
{
    public enum ApiErrorKind
    {
        Http,
        Timeout,
        Network,
        InvalidResponse
    }

    public sealed class ApiError
    {
        public ApiError(
            ApiErrorKind kind,
            long statusCode,
            string code,
            string message,
            string correlationId)
            : this(kind, statusCode, code, message, correlationId, 0)
        {
        }

        public ApiError(
            ApiErrorKind kind,
            long statusCode,
            string code,
            string message,
            string correlationId,
            int retryAfterSeconds)
        {
            Kind = kind;
            StatusCode = statusCode;
            Code = string.IsNullOrWhiteSpace(code) ? "unknown_error" : code;
            Message = string.IsNullOrWhiteSpace(message) ? "The request failed." : message;
            CorrelationId = correlationId ?? string.Empty;
            RetryAfterSeconds = Math.Max(0, retryAfterSeconds);
        }

        public ApiErrorKind Kind { get; private set; }

        public long StatusCode { get; private set; }

        public string Code { get; private set; }

        public string Message { get; private set; }

        public string CorrelationId { get; private set; }

        // The Retry-After of the answer in seconds, or 0 when it named none.
        public int RetryAfterSeconds { get; private set; }

        public bool IsUnauthorized
        {
            get { return StatusCode == 401; }
        }

        // The English diagnostic for logs; the player reads the text of the code.
        public string ToDiagnostic()
        {
            var status = StatusCode > 0 ? StatusCode + " " : string.Empty;
            var correlation = string.IsNullOrWhiteSpace(CorrelationId)
                ? string.Empty
                : " Correlation: " + CorrelationId + ".";
            var retry = RetryAfterSeconds > 0
                ? " Retry after " + RetryAfterSeconds.ToString(CultureInfo.InvariantCulture) + " s."
                : string.Empty;
            return status + Code + ": " + Message + correlation + retry;
        }

        // A Retry-After header in delay-seconds; a date or anything else reads as none.
        public static int ParseRetryAfter(string header)
        {
            int seconds;
            return !string.IsNullOrWhiteSpace(header)
                && int.TryParse(header.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                    ? seconds
                    : 0;
        }
    }

    [Serializable]
    internal sealed class ProblemDetailsResponse
    {
        public string title;
        public int status;
        public string detail;
        public string code;
        public string correlationId;
    }
}
