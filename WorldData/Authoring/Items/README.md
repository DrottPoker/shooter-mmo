# Item Catalog Authoring

`core.item-catalog.json` is the neutral source of truth for the current shared
item catalog. `Tools/ItemCatalogCompiler` validates and compiles it into
`WorldData/Runtime/Items/core.item-catalog.json`.

## Authoring Contract

The catalog contains these separate concepts:

- Categories define one primary classification per item.
- Tags provide many-to-many Bag slot compatibility.
- Equipment slots have stable ids and are referenced explicitly by compatible
  item definitions.
- Location eligibility currently records explicit Secure Container permission.
  The hard weapon prohibition is also enforced by the pure domain rule.
- Default policies are server-owned policy ids and remain separate from category
  and tags.
- Bag layouts contain contiguous stable slot indices. General slots configure no
  tags. Specialized slots configure one or more accepted tag ids.
- Secure Container tiers have stable ids and integer slot capacities.
- Unit weight and Bag carry-capacity bonuses use unitless, non-negative integer
  values. Physical measurement units and decimal weight values are not part of
  the contract.

The baseline balance scale uses weight `1` for one ammunition unit, weight `10`
for a pistol, and base character carry capacity `200`. These are gameplay
values, not physical measurements. The current representative training rifle
uses weight `25`, and the Field Pack grants `50` additional capacity.

Catalog ids, definition ids, tag ids, category ids, equipment slot ids, policy
ids, and tier ids use canonical lowercase identifiers. They must not be reused
for a different meaning.

Every authoring field is validated. Unknown JSON properties, duplicate JSON
properties, missing required values, unknown references, duplicate ids,
duplicate Bag slot indices, non-contiguous Bag layouts, invalid weights, invalid
stack limits, and structurally impossible combinations fail compilation.

The format deliberately has no definition inheritance, default contents, or
definition-to-definition container references. Catalog reference cycles are
therefore not representable. Future item-instance placement still applies the
pure Bag containment-cycle rule.

## Revisions And Structural Changes

The compiler sorts all unordered content before hashing it. Authoring order does
not affect the result.

- The complete runtime catalog has one deterministic `revision`.
- Every item definition has a deterministic `structuralFingerprint` covering
  rule-affecting fields such as category, tags, weight, stack limit, equipment
  compatibility, location eligibility, default policies, and Bag layout.
- Every Secure Container tier has a structural fingerprint covering its stable
  id and slot capacity.
- Display-only changes alter the catalog revision but do not alter an item's
  structural fingerprint.

AuthService now mirrors these fingerprints into PostgreSQL. A structural change
is accepted automatically only when no live item instance or affected Secure
Container entitlement references it. Otherwise AuthService rejects startup
until an explicit data migration makes the persistent state compatible.
Regenerating runtime JSON is never sufficient authorization to invalidate live
state.

## Unity Editor Workflow

Open `Shooter MMO > Tools > Item Catalog` in the Unity project. The Editor-only
tool provides a searchable definition list with a category filter, create and
duplicate actions, every gameplay field in this contract, Bag slot editing, and
the separate client presentation mapping.

- Definition ids already present in runtime JSON are locked and cannot be
  deleted or reused. A new id remains editable until its first successful bake.
- `Validate` invokes the strict command-line compiler against temporary files
  and writes nothing.
- `Save` writes the authoring and client presentation files but leaves runtime
  gameplay JSON unchanged.
- `Save And Bake` validates and atomically writes authoring, deterministic
  runtime gameplay, and client presentation JSON.
- Display-only, structural, added, and removed definitions are reported against
  the previous runtime catalog. Structural changes require explicit
  confirmation and Phase 3 persistence compatibility review.

Client presentation JSON is bundled at
`shooter-mmorpg-unity-client/Assets/Resources/Items/Presentation/item-presentation-catalog.json`.
It contains icon Resources paths, localization keys, fallback text, and optional
prefab presentation keys. Icon files are client-owned assets below
`Assets/Resources` and use one Sprite per file. The catalog has a separate
deterministic presentation revision and records the exact source gameplay
revision. These client fields never alter gameplay structural fingerprints.

A failed validation does not write canonical files. A failed multi-file bake
restores previous content. Correct the reported field paths or select `Reload`,
then use the `--verify` command below to confirm that checked-in runtime content
still matches authoring.

## Compile And Verify

From the repository root, compile an intentional authoring change:

```powershell
dotnet run --project Tools/ItemCatalogCompiler -- `
  WorldData/Authoring/Items/core.item-catalog.json `
  WorldData/Runtime/Items/core.item-catalog.json
```

Verify that checked-in runtime content matches authoring:

```powershell
dotnet run --project Tools/ItemCatalogCompiler -- `
  WorldData/Authoring/Items/core.item-catalog.json `
  WorldData/Runtime/Items/core.item-catalog.json `
  --verify
```

The current development catalog reports nine definitions and one base Secure
Container tier. Validation failure or stale runtime content returns exit code 1.
