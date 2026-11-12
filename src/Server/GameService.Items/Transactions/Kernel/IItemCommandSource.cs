namespace Mmorpg.GameService.Items.Transactions;

// The item commands of one area, registered in dependency injection as a singleton. A
// source builds its own handlers from the services it needs and never a handler of
// another area.
internal interface IItemCommandSource
{
    IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
