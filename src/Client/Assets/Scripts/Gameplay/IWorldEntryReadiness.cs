namespace Mmorpg.Client.Gameplay
{
    // Something the first view of a World shows, which the entry cover waits for: whether what it
    // shows at the entry is ready, such as the prefabs of the world actors in view loaded. The World
    // shows once every part is ready, or at WorldSceneContext.FirstViewCapSeconds, and the rest
    // comes into view within the presentation budget as anything that comes later does.
    public interface IWorldEntryReadiness
    {
        bool IsReadyForEntry { get; }
    }
}
