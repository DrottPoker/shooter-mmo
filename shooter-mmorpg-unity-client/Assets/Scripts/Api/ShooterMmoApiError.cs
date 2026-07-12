using System;

namespace ShooterMmo.Api
{
    public enum ShooterMmoApiErrorKind
    {
        Http,
        Timeout,
        Network,
        InvalidResponse
    }

    public sealed class ShooterMmoApiError
    {
        public ShooterMmoApiError(
            ShooterMmoApiErrorKind kind,
            long statusCode,
            string code,
            string message,
            string correlationId)
        {
            Kind = kind;
            StatusCode = statusCode;
            Code = string.IsNullOrWhiteSpace(code) ? "unknown_error" : code;
            Message = string.IsNullOrWhiteSpace(message) ? "The request failed." : message;
            CorrelationId = correlationId ?? string.Empty;
        }

        public ShooterMmoApiErrorKind Kind { get; private set; }

        public long StatusCode { get; private set; }

        public string Code { get; private set; }

        public string Message { get; private set; }

        public string CorrelationId { get; private set; }

        public bool IsUnauthorized
        {
            get { return StatusCode == 401; }
        }

        public string ToDisplayMessage()
        {
            var status = StatusCode > 0 ? StatusCode + " " : string.Empty;
            var correlation = string.IsNullOrWhiteSpace(CorrelationId)
                ? string.Empty
                : " Correlation: " + CorrelationId + ".";
            return status + Code + ": " + Message + correlation;
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
