namespace Mmorpg.GameService.Items.Contracts;

// The item commands of one area, registered in dependency injection as a singleton. A
// source builds its own handlers from the services it needs and never a handler of
// another area.
public interface IItemCommandSource
{
    IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
