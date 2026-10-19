using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Items
{
    public enum CorpseViewClosureKind
    {
        // The view ended as the game intends, so the client shows at most a neutral notice.
        Expected,

        // The view ended because something went wrong, so the client shows an error.
        Failure
    }

    // How the client presents one corpse view closure that the worker reported.
    public sealed class CorpseViewClosure
    {
        public CorpseViewClosure(CorpseViewClosureKind kind, string notice, CorpseClientError error)
        {
            Kind = kind;
            Notice = notice ?? string.Empty;
            Error = error;
        }

        public CorpseViewClosureKind Kind { get; }

        // The neutral text of an expected closure, empty when nothing needs saying.
        public string Notice { get; }

        // The error of a failed closure, null for an expected one.
        public CorpseClientError Error { get; }
    }

    // Maps the stable closure code to its presentation. Only the codes the worker sends for
    // an intended end are expected; any other code, including an unknown one, stays an error
    // so a real invalidation is never hidden.
    public static class CorpseViewClosureRule
    {
        public static CorpseViewClosure Classify(string code, string message)
        {
            switch (code)
            {
                case RealtimeCorpseViewClosureCodes.ViewClosed:
                    return Expected(string.Empty);
                case RealtimeCorpseViewClosureCodes.Emptied:
                    return Expected(NoticeOrDefault(message, "The corpse is empty."));
                case RealtimeCorpseViewClosureCodes.Expired:
                    return Expected(NoticeOrDefault(message, "The corpse expired."));
                case RealtimeCombatErrorCodes.CharacterDead:
                    return Expected(NoticeOrDefault(message, "The character died."));
                default:
                    return new CorpseViewClosure(
                        CorpseViewClosureKind.Failure,
                        string.Empty,
                        new CorpseClientError(code, message));
            }
        }

        private static CorpseViewClosure Expected(string notice)
        {
            return new CorpseViewClosure(CorpseViewClosureKind.Expected, notice, null);
        }

        private static string NoticeOrDefault(string message, string fallback)
        {
            return string.IsNullOrWhiteSpace(message) ? fallback : message;
        }
    }
}
