using UnityEngine;

namespace Mmorpg.Client.Editor
{
    public enum WorldBuilderIssueSeverity
    {
        Error,
        Warning
    }

    // A problem the World Builder found in a scene, about the object it names. An error stops the
    // collision bake; a warning only shows in the World Builder.
    public sealed class WorldBuilderIssue
    {
        public WorldBuilderIssue(Object target, string message, WorldBuilderIssueSeverity severity = WorldBuilderIssueSeverity.Error)
        {
            Target = target;
            Message = message;
            Severity = severity;
        }

        public Object Target { get; }

        public string Message { get; }

        public WorldBuilderIssueSeverity Severity { get; }

        public override string ToString()
        {
            return (Target != null ? Target.name + ": " : string.Empty) + Message;
        }
    }
}
