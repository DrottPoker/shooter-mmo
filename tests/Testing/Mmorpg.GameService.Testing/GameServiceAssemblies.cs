using System.Reflection;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Content;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Quests;

namespace Mmorpg.GameService.Testing;

// The GameService host and module assemblies, in dependency order.
internal static class GameServiceAssemblies
{
    public static Assembly Platform { get; } = typeof(IGameServiceModule).Assembly;

    public static Assembly Accounts { get; } = typeof(AccountsModule).Assembly;

    public static Assembly Characters { get; } = typeof(CharactersModule).Assembly;

    public static Assembly Placement { get; } = typeof(PlacementModule).Assembly;

    public static Assembly Quests { get; } = typeof(QuestsModule).Assembly;

    public static Assembly Items { get; } = typeof(ItemsModule).Assembly;

    public static Assembly Content { get; } = typeof(ContentModule).Assembly;

    public static Assembly Host { get; } = typeof(Mmorpg.GameService.Database.GameServiceSchema).Assembly;

    public static IReadOnlyList<Assembly> All { get; } =
        [Platform, Accounts, Characters, Placement, Quests, Items, Content, Host];
}
