namespace Mmorpg.Client.Networking
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

        // The English diagnostic for logs; the player reads the text of the code.
        public string ToDiagnostic()
        {
            return Message + " [" + Code + "]";
        }
    }
}
