namespace Mmorpg.GameService.Items.Transactions;

// Everything the kernel needs to know about one command kind. ItemCommandCatalog holds the
// registrations of every IItemCommandSource by command type, so the kernel never names a
// command or a handler.
internal abstract class ItemCommandRegistration
{
    private protected ItemCommandRegistration(Type commandType, string operationKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
        CommandType = commandType;
        OperationKind = operationKind;
    }

    public Type CommandType { get; }

    // The persisted operation kind, which the metrics and logs also carry.
    public string OperationKind { get; }

    // A system operation with a derived or server-held id that is retried until it commits:
    // its id need not be random, and its rejections release the claim instead of being
    // stored for replay.
    public bool RetriedSystemOperation { get; init; }

    // A commit is logged at Information instead of Debug.
    public bool OperationallySignificant { get; init; }

    // Every outcome counts in the policy actions metric, labeled with the operation kind.
    public bool PolicyAction { get; init; }
}

internal sealed class ItemCommandRegistration<TCommand> : ItemCommandRegistration
    where TCommand : notnull
{
    public ItemCommandRegistration(
        string operationKind,
        Func<TCommand, Guid?> actorCharacter,
        IItemCommandHandler<TCommand> handler)
        : base(typeof(TCommand), operationKind)
    {
        ArgumentNullException.ThrowIfNull(actorCharacter);
        ArgumentNullException.ThrowIfNull(handler);
        ActorCharacter = actorCharacter;
        Handler = handler;
    }

    // The character whose live session a simulation worker actor must hold and that the
    // operation claim records, or null for a command without one.
    public Func<TCommand, Guid?> ActorCharacter { get; }

    public IItemCommandHandler<TCommand> Handler { get; }

    // The command as the idempotency payload stores it; the command itself by default.
    public Func<TCommand, TCommand> Canonicalize { get; init; } = static command => command;
}
