using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mmorpg.Client.EditModeTests")]

// A PlayMode test enters a World scene's context that it composed with its own hub.
[assembly: InternalsVisibleTo("Mmorpg.Client.PlayModeTests")]

// The Editor's capture of the game views dresses bodies with a World's shared meshes and materials
// and keeps their lids open.
[assembly: InternalsVisibleTo("Mmorpg.Client.Editor")]
