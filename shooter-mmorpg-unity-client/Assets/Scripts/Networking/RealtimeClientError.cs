namespace ShooterMmo.Networking
{
    public enum RealtimeClientErrorKind
    {
        Validation,
        Busy,
        Network,
        Timeout,
        Protocol,
        Server
    }

    public sealed class RealtimeClientError
    {
        public RealtimeClientError(RealtimeClientErrorKind kind, string code, string message)
        {
            Kind = kind;
            Code = code;
            Message = message;
        }

        public RealtimeClientErrorKind Kind { get; private set; }

        public string Code { get; private set; }

        public string Message { get; private set; }

        public string ToDisplayMessage()
        {
            return Message + " [" + Code + "]";
        }
    }
}
