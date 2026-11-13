using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mmorpg.Client.EditModeTests")]

// PlayMode tests point the running client's session at another GameService.
[assembly: InternalsVisibleTo("Mmorpg.Client.PlayModeTests")]
