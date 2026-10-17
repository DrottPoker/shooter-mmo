# Item Catalog Authoring

This directory is the neutral source of truth for the shared item catalog:

- `catalog.json` holds the catalog id, the character item settings, categories,
  tags, equipment slots, Secure Container tiers, the base tier id, and the
  retired ids.
- `definitions/<definition-id>.json` holds exactly one item definition. The file
  name must equal the `id` inside the file.

`tools/ItemCatalogCompiler` validates both and compiles them into
`src/Shared/WorldData/Generated/Items/core.item-catalog.json`. The general
content rules are in [../README.md](../README.md).

## Authoring Contract

Catalog format version 3 contains these separate concepts:

- **Character settings** in `character` set the base carry capacity and the
  slot counts of the permanent inventory and the bank that every new character
  starts with. They are content, not code.
- **Categories** define one primary classification per item.
- **Tags** provide many-to-many Bag slot compatibility.
- **Equipment slots** have stable ids and are listed in display order. Each slot
  has a `maximumQuantity`: `1` for a slot that holds one item, more for a slot
  that holds a stack, such as an ammunition slot. The Bag slot must hold one
  item. Item definitions reference compatible slots explicitly.
- **Blocked equipment slots** on a definition stay empty while the item is
  equipped, so a two-handed weapon in the main hand keeps the off hand free and
  nothing can be equipped into a blocked slot. A blocked slot cannot also be a
  compatible slot.
- **Location eligibility** records explicit Secure Container permission. The
  hard weapon prohibition is also enforced by the pure domain rule.
- **Default policies** are server-owned policy ids and remain separate from
  category and tags.
- **Bag layouts** contain contiguous stable slot indices. General slots
  configure no tags. Specialized slots configure one or more accepted tag ids.
- **Secure Container tiers** have stable ids and integer slot capacities.
- **Retired ids** in `retired` list every category, tag, equipment slot,
  definition, and tier id that was released and later removed. A retired id can
  never return with any meaning.

Code requires only the ids that rules give a meaning to: the `weapon`, `bag`,
and `quest_item` categories and the `bag` equipment slot. Every other category,
tag, and slot is content.

Unit weight, carry capacity, and the Bag carry-capacity bonus use unitless,
non-negative integer values. Physical measurement units and decimal weight
values are not part of the contract. The balance scale is one ammunition unit
`1`, a one-handed weapon about `100`, and base character carry capacity `2000`,
so 1000 spare arrows use half of the base capacity. A stackable definition may only be
compatible with equipment slots that hold more than one unit, and one equipment
slot holds at most the smaller of its own maximum quantity and the definition's
stack size.

Catalog ids, definition ids, tag ids, category ids, equipment slot ids, policy
ids, and tier ids use canonical lowercase identifiers. They must not be reused
for a different meaning.

Every authoring field is validated. Unknown JSON properties, duplicate JSON
properties, missing required values, unknown references, duplicate ids, reused
retired ids, duplicate Bag slot indices, non-contiguous Bag layouts, invalid
weights, invalid stack limits, and structurally impossible combinations fail
compilation.

The format deliberately has no definition inheritance, default contents, or
definition-to-definition container references. Catalog reference cycles are
therefore not representable. Future item-instance placement still applies the
pure Bag containment-cycle rule.

## Revisions And Structural Changes

The compiler sorts all unordered content before hashing it. Authoring order does
not affect the result, except for equipment slots, whose order is their display
order.

- The complete runtime catalog has one deterministic `revision`. It covers the
  character settings, every identity, the slot quantities and order, every
  definition fingerprint, and the retired ids.
- Every item definition has a deterministic `structuralFingerprint` covering
  rule-affecting fields such as category, tags, weight, stack limit, equipment
  compatibility, blocked equipment slots, location eligibility, default
  policies, and Bag layout.
- Every Secure Container tier has a structural fingerprint covering its stable
  id and slot capacity.
- Display-only changes and equipment slot reordering alter the catalog revision
  but do not alter an item's structural fingerprint.

GameService mirrors the catalog into PostgreSQL at startup and refuses to start
when the change would invalidate live state:

- A structural definition change is accepted only when no item instance uses the
  definition. A tier change is accepted only when no account entitlement uses
  the tier.
- Lowering an equipment slot's maximum quantity is accepted only when no
  equipped stack is larger than the new maximum.
- Changing the character settings is accepted only while no character has item
  state, because every character copies them when its item state is created.
- An id that exists in the database must stay in the catalog or be listed as
  retired. GameService retires the row of a retired definition only when no item
  instance uses it, of a retired equipment slot only when nothing is equipped in
  it, and of a retired tier only when no entitlement uses it. A retired row stays
  in the database, so audit records and old corpses keep their references.

Regenerating runtime JSON is never sufficient authorization to invalidate live
state. A change that live state depends on needs an explicit data migration. On
a local development database the documented alternative is a database reset,
described in
[Local Development](../../docs/LOCAL_DEVELOPMENT.md#reset-the-local-development-database).

## Unity Editor Workflow

Open `MMORPG > Tools > Item Catalog` in the Unity project. The editor and the
Unity client read catalog JSON through one JsonUtility shape, `ItemCatalogJson`
in the WorldData runtime, so a new catalog field is added there once. The
Editor-only tool provides a searchable definition list with category, tag, and problem
filters and multi-selection, create, duplicate, and remove-draft actions, every
gameplay field in this contract, Bag slot editing, and the separate client
presentation mapping. Selected definitions are edited together, live validation
marks the fields that break this contract, and every edit can be undone with
Unity's Undo.

- Definition ids already present in runtime JSON are locked and cannot be
  deleted or reused. Retired ids can never be taken by a new definition. A new
  id remains editable until its first successful bake.
- The character settings, categories, tags, equipment slots, tiers, and retired
  ids are edited in `catalog.json`. The tool keeps them unchanged when it saves.
  To retire a released definition, move its id to `retired.definitions` and
  delete its definition file.
- `Validate` invokes the strict command-line compiler against temporary files
  and writes nothing.
- `Save` writes `catalog.json`, one file per definition, and the client
  presentation file, but leaves runtime gameplay JSON unchanged. Definition files
  of removed draft definitions are deleted.
- `Save And Bake` validates and atomically writes the authoring files,
  deterministic runtime gameplay, and client presentation JSON. Unchanged files
  are not rewritten.
- Display-only, structural, added, and removed definitions are reported against
  the previous runtime catalog. Structural changes require explicit
  confirmation.

Client presentation JSON is bundled through Addressables at
`src/Client/Assets/Content/Items/Presentation/item-presentation-catalog.json`.
It contains icon Addressables addresses below `Items/Icons/`, localization keys,
fallback text, and optional prefab presentation keys. Icon files are
client-owned assets below `Assets/Content/Items/Icons` and use one Sprite per
file. The catalog has a separate
deterministic presentation revision and records the exact source gameplay
revision. These client fields never alter gameplay structural fingerprints.

A failed validation does not write canonical files. A failed multi-file bake
restores previous content. Correct the reported field paths or select `Reload`,
then use the `--verify` command below to confirm that checked-in runtime content
still matches authoring.

## Test Fixture Catalog

`tests/Fixtures/ItemCatalog` holds a second, test-owned catalog in the same
format: `content/items` with neutral `fixture.*` definitions and
`content/development/item-packages.json`, plus its compiled
`generated/Items/core.item-catalog.json`. Rule tests in .NET and Unity use it,
so changing game content or balance never breaks them. Recompile it after an
intentional change:

```powershell
dotnet run --project tools/ItemCatalogCompiler -- `
  --content-root tests/Fixtures/ItemCatalog/content `
  --generated-root tests/Fixtures/ItemCatalog/generated
```

`CompiledFixtureCatalogMatchesItsAuthoring` fails when the compiled fixture is
stale.

## Compile And Verify

From the repository root, compile an intentional authoring change:

```powershell
dotnet run --project tools/ItemCatalogCompiler
```

Verify that checked-in runtime content matches authoring:

```powershell
dotnet run --project tools/ItemCatalogCompiler -- --verify
```

Validation failure or stale runtime content returns exit code 1.
