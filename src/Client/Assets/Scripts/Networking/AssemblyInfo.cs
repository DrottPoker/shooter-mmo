using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mmorpg.Client.EditModeTests")]

// The live stack test composes the hub with the real transport it wraps.
[assembly: InternalsVisibleTo("Mmorpg.Client.PlayModeTests")]
