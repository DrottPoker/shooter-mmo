using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Platform.Modules;

// The composition contract between a GameService module and the host.
public interface IGameServiceModule
{
    string Name { get; }

    // Migrations this module owns. The host merges every module's list by id.
    IReadOnlyList<DatabaseMigration> Migrations { get; }

    void AddServices(IServiceCollection services);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
