# Item Catalog Authoring

This directory is the neutral source of truth for the shared item catalog:

- `catalog.json` holds the catalog id, the character item settings, the economy
  settings, the progression limits, categories, tags, tool kinds, equipment
  slots, weapon and ammunition families, Secure Container tiers, the base tier
  id, and the retired ids.
- `definitions/<definition-id>.json` holds exactly one item definition. The file
  name must equal the `id` inside the file.
- `vendors/<vendor-id>.json` holds exactly one vendor profile. The file name
  must equal the `id` inside the file.

`tools/ItemCatalogCompiler` validates all of them and compiles them into
`src/Shared/WorldData/Generated/Items/core.item-catalog.json`, the runtime
catalog that GameService, SimulationWorker, and the Unity client read. The
general content rules are in [../README.md](../README.md), and the item rules
that this content feeds, such as Bags, policies, and vendor trades, are in
[Inventory And Death Loot Design](../../docs/INVENTORY_AND_DEATH_LOOT_DESIGN.md).

## Authoring Contract

`formatVersion` in `catalog.json` names the catalog format. It must equal
`ItemCatalogFormat.Version` in the WorldData runtime, and the compiler rejects
any other version, so a format change updates the compiler, the Item Catalog
window, this content, and the [test fixture catalog](#test-fixture-catalog)
together. The format contains these separate concepts:

- **Character settings** in `character` set the base carry capacity and the
  slot counts of the permanent inventory and the bank that every new character
  starts with. They are content, not code.
- **Economy settings** in `economy` name the character currency with
  `currencyDisplayName`, such as `Gold`, trimmed and at most 32 characters, and
  set the `startingBalance` every new character receives, from 0 to
  1,000,000,000,000. They are content, not code. The balance itself is
  character state that GameService owns.
- **Progression** in `progression` sets `maximumCharacterLevel`, the temporary
  level cap of 200. Every level requirement must fit it.
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
- **Location eligibility** records explicit Secure Container permission. A
  weapon can never be Secure Container eligible; the pure domain rule enforces
  that prohibition as well.
- **Player destroyability** in `playerDestroyable` is required on every
  definition and says whether the player may destroy the item. A protected
  quest item can never be player-destroyable.
- **Default policies** in `defaultPolicies` are server-owned policy ids,
  separate from category and tags: `protected_on_death` and `insured`, as
  [Item Policies](../../docs/INVENTORY_AND_DEATH_LOOT_DESIGN.md#item-policies)
  describes. An `insured` default needs non-stackable equipment.
- **Bag layouts** contain contiguous stable slot indices. General slots
  configure no tags. Specialized slots configure one or more accepted tag ids.
- **Weapon families** in `weaponFamilies` name a combat style (`melee`,
  `ranged`, or `magic`), the ammunition family a `ranged` family fires, and the
  `projectileSpeed` of its attacks in metres per second. A `ranged` family needs
  an ammunition family and a positive speed. A `melee` family has neither and
  hits at once. A `magic` family has no ammunition family, and its attacks cost
  mana instead; the `staff` family's bolts fly at 20 m/s. Every family also
  names its `windupSeconds`, from 0 to 5: an attack starts at once and releases
  its blow, arrow, or bolt that much later, when the body's attack clip strikes
  or looses, so a weapon's `attackIntervalSeconds` must be longer. The families
  are `sword` (0.35 s), `axe` (0.6 s), `bow` (0.8 s), and `staff` (0.15 s).
- **Ammunition families** in `ammunitionFamilies` group ammunition. Any
  ammunition of a weapon family's ammunition family works with every weapon of
  that family.
- **Weapon data** in a definition's `weapon` block names its weapon family and
  damage type (`normal`, `fire`, `cold`, `nature`, or `shadow`), the integer base
  damage roll from `minimumDamage` to `maximumDamage`, the
  `attackIntervalSeconds` between attacks, the attack `range` in metres, and the
  `manaCost` of every attack, from 1 to 1,000,000 for a weapon of a `magic`
  family and 0 for every other weapon. Every `weapon` definition has exactly
  this block, and no other definition has it. The shipped weapons and their
  numbers are listed in [Combat](../../docs/GAME_FEATURES.md#combat).
- **Ammunition data** in a definition's `ammunition` block names its ammunition
  family and its `damageBonusPercent` of the weapon roll. Every `ammunition`
  definition has exactly this block, and no other definition has it.
- **Level requirements** in `requiredCharacterLevel` are at least 1 on every
  definition. GameService refuses to equip an item into any equipment slot while
  the character's level is lower.
- **Item value** in a definition's `value` is its base price in whole currency
  units, from 0 to 1,000,000,000. Vendors sell and buy at a share of it, and `0`
  means vendors never trade the item.
- **Tool kinds** in `toolKinds` are identities, such as `pickaxe` and
  `woodcutting_axe`, that name what an item serves as a tool for. The list is
  required and may be empty. A resource profile in the actor catalog names the
  tool kind it requires, as
  [Resource Nodes And Item References](../README.md#resource-nodes-and-item-references)
  describes.
- **Definition tool kinds** in a definition's `toolKinds` list at most 8 tool
  kinds. Only a definition compatible with the `tool` equipment slot may have
  tool kinds, because gathering requires an item with the required tool kind
  equipped in that slot, such as the Starter Pickaxe for `pickaxe`.
- **Vendor profiles** in `vendors/` describe what an NPC vendor sells and buys.
  They are described in [Vendor Profiles](#vendor-profiles).
- **Secure Container tiers** have stable ids and integer slot capacities.
- **Retired ids** in `retired` list every category, tag, equipment slot,
  definition, and tier id that was released and later removed. A retired id can
  never return with any meaning.

Code requires only the ids that rules give a meaning to: the `weapon`,
`ammunition`, `bag`, and `quest_item` categories and the `bag` equipment slot.
The `tool` equipment slot is optional, but only a definition compatible with it
can carry tool kinds. The combat styles and damage types are a closed set in
code, because the combat triangle and the damage colors give each of them a
meaning. Every other category, tag, slot, family, tool kind, and vendor is
content.

Unit weight, carry capacity, and the Bag carry-capacity bonus use unitless,
non-negative integer values in `unitWeight`, `baseCarryCapacity`, and
`carryCapacityBonus`. Physical measurement units and decimal weight values are
not part of the contract; do not add such fields. The balance scale is one
ammunition unit `1`, a one-handed weapon about `100`, and base character carry
capacity `2000`, so 1000 spare arrows use half of the base capacity. A
stackable definition may only be compatible with equipment slots that hold more
than one unit, and one equipment slot holds at most the smaller of its own
maximum quantity and the definition's stack size.

Catalog ids, definition ids, tag ids, category ids, equipment slot ids, tool
kind ids, vendor ids, policy ids, and tier ids use canonical lowercase
identifiers. They must not be reused for a different meaning.

Every authoring field is validated. Unknown JSON properties, duplicate JSON
properties, missing required values, unknown references, duplicate ids, reused
retired ids, duplicate Bag slot indices, non-contiguous Bag layouts, invalid
weights, invalid stack limits, and structurally impossible combinations fail
compilation.

The format deliberately has no definition inheritance, default contents, or
definition-to-definition container references. Catalog reference cycles are
therefore not representable. Future item-instance placement still applies the
pure Bag containment-cycle rule.

### Vendor Profiles

A vendor profile describes what an NPC vendor sells and buys. A `vendor`
capability in actor content names it by `profileId`, so one profile can serve
several NPCs. Prices are named from the player's side and set in basis points
of the item value, where `10000` is the value itself:

- `id` is the vendor id, and `displayName` is shown in the client.
- `offers` is the ordered list of `{ "definitionId": ... }` entries the vendor
  sells, at most 32, each definition once. The client lists the offers in this
  order. An offered definition needs a positive value and no default policies,
  and it can never be a quest item. Offers have no stock limit.
- `purchasePriceBasisPoints` sets what players pay, from 1 to 1,000,000.
- `salePriceBasisPoints` sets what players receive when they sell, from 0 to
  1,000,000.
- `saleCategories` lists the item categories the vendor buys. It can never
  contain `quest_item`, and a vendor never buys an item whose value is 0.
- A vendor must offer at least one item or buy at least one category.

Every vendor's `salePriceBasisPoints` must stay below every vendor's
`purchasePriceBasisPoints` in the catalog, not only its own, so buying from one
vendor and selling to the same or another vendor can never create currency.

`VendorPricing` in the WorldData runtime is the one price rule. The Unity client
shows prices with it and GameService charges them with it, so a shown price and
a charged price cannot differ for the same catalog. A purchase costs
`ceil(value * quantity * purchasePriceBasisPoints / 10000)`, and a sale pays
`floor(value * quantity * salePriceBasisPoints / 10000)`. Both use exact decimal
arithmetic, and a total above the maximum balance of 1,000,000,000,000 is out of
range. A sale whose total rounds down to 0, such as two Bronze Arrows of value
1 at `4000`, is refused with `vendor_will_not_buy`, so the vendor never takes an
item for nothing.

The shipped `vendor.city_general_store`, "General Store", is the profile of the
Trade capability of Mira the Quartermaster (`npc.city_services`). Its file,
[`vendors/vendor.city_general_store.json`](vendors/vendor.city_general_store.json),
lists its offers in display order. It sells at `10000` and buys every category
except quest items at `4000`, so selling three Iron Ore of value 10 there pays
12, and selling one Wolf Pelt of value 15 pays 6.

## Revisions And Structural Changes

The compiler sorts all unordered content before hashing it. Authoring order does
not affect the result, except for equipment slots and vendor offers, whose order
is their display order.

- The complete runtime catalog has one deterministic `revision`. It covers the
  format version, the character and economy settings, the progression limits,
  every identity including the tool kinds, the slot quantities and order, the
  weapon and ammunition families, every definition fingerprint, every
  definition's value, tool kinds, combat data, and level requirement, every
  vendor profile including its offer order, and the retired ids.
- Every item definition has a deterministic `structuralFingerprint` covering
  rule-affecting fields: category, tags, weight, stack limit, equipment
  compatibility, blocked equipment slots, player destroyability, location
  eligibility, default policies, and Bag layout.
- Every Secure Container tier has a structural fingerprint covering its stable
  id and slot capacity.
- Display-only changes, combat data, level requirements, values, tool kinds,
  vendor profiles, economy settings, and equipment slot reordering alter the
  catalog revision but do not alter an item's structural fingerprint, because
  none of them changes custody.
- Structural fingerprints hash their own version,
  `ItemCatalogFormat.StructuralFingerprintVersion`, instead of the catalog format
  version. A new format that only adds non-structural fields, such as values,
  tool kinds, vendor profiles, the economy settings, mana costs, or family
  windups, therefore keeps every persisted definition compatible and needs no
  item migration or development database reset. Change that version only when
  the meaning of a structural field changes.

After a compile, inspect the `structuralFingerprint` of every changed definition
and Secure Container tier in the diff of the runtime catalog. A display-only
edit changes the catalog revision without changing a structural fingerprint.

GameService mirrors the catalog into PostgreSQL at startup and refuses to start
when the change would invalidate live state. The error names the affected
stable id:

- A structural definition change is accepted only when no item instance uses the
  definition. A tier change is accepted only when no account entitlement uses
  the tier.
- Lowering an equipment slot's maximum quantity is accepted only when no
  equipped stack is larger than the new maximum.
- Changing the character settings is accepted only while no character has item
  state, because every character copies them when its item state is created.
- The economy settings may change at any time. Only characters created later
  receive the new starting balance, and existing characters keep their balance.
- Values, tool kinds, and vendor profiles hold no item state, so they may change
  at any time. A tool kind or vendor profile that leaves the catalog turns
  inactive in the database, and an inactive vendor profile cannot trade.
- A category, tag, equipment slot, definition, or tier id that exists in the
  database must stay in the catalog or be listed as retired. GameService retires
  the row of a retired definition only when no item instance uses it, of a
  retired equipment slot only when nothing is equipped in it, and of a retired
  tier only when no entitlement uses it. A retired row stays in the database, so
  audit records and old corpses keep their references.

Regenerating runtime JSON is never sufficient authorization to invalidate live
state. A change that live state depends on needs an explicit data migration. On
a local development database the documented alternative is a database reset,
described in
[Local Development](../../docs/LOCAL_DEVELOPMENT.md#reset-the-local-development-database).
GameService and SimulationWorker read the catalog from their build output, so
rebuild and restart both after a catalog change.

## Unity Editor Workflow

Open `MMORPG > Tools > Item Catalog` in the Unity project. The window edits the
authoring files in `content/items` and is not a second catalog: its live checks
call the shared compiler in `Mmorpg.WorldData`, and `Validate`, `Save`, and
`Save And Bake` run `tools/ItemCatalogCompiler`, which stays the validation and
structural fingerprint authority for backend development and CI. The window and
the Unity client read catalog JSON through one JsonUtility shape,
`ItemCatalogJson` in the WorldData runtime, so a new catalog field is added
there once.

1. Find definitions in the list by id, name, category, or tag, and narrow it with
   the category, tag, and `Problems` filters. `New`, `Duplicate`, and
   `Remove Draft` create, copy, and remove draft definitions. Hold Ctrl or Shift
   to select several definitions.
2. Edit the gameplay fields of this contract and the separate client
   presentation fields in the same window. With several definitions selected,
   every field applies to all of them, and a field whose values differ shows a
   dash. Id, display name, Bag layout, combat data, and presentation are edited
   one definition at a time. A definition's `Value` field and its `Tool Kinds`
   flags, which list the catalog tool kinds, edit its economy fields.
3. Fix what the live check finds. A field with a problem turns red, the list
   marks its definition, and choosing a problem in the panel below selects the
   definition and its field.
4. Undo and redo with Ctrl+Z and Ctrl+Y or the Edit menu; every edit is one step
   in `Edit > Undo History`. Unsaved edits survive script reloads, and closing
   the window asks whether to save them.
5. Optionally choose an icon Sprite below `Assets/Content/Items/Icons`, one
   Sprite per file. The folder is an Addressables folder entry, so the window
   stores the icon's address, such as `Items/Icons/<file>.png`, not the image,
   in the presentation file. An item may stay without an icon until approved
   client art exists.
6. Select `Validate` to run the strict command-line compiler against temporary
   files. It writes nothing.
7. Select `Save` to write `catalog.json`, one file per definition, one file per
   vendor profile, and the client presentation file, without replacing the
   runtime gameplay catalog. The files of removed draft definitions and removed
   vendor profiles are deleted.
8. Select `Save And Bake` to validate and atomically write the authoring files,
   the deterministic runtime gameplay catalog, and the client presentation
   file. Unchanged files are not rewritten. Display-only, structural, added, and
   removed definitions are reported against the previous runtime catalog, and
   structural changes require explicit confirmation.

Expected result: before a write the window shows which gameplay and
presentation revisions the change produces, the status then reports `Item
catalog saved and baked successfully.` with the compiler output, and the
`--verify` command under [Compile And Verify](#compile-and-verify) passes.

The `Definitions | Vendors` toolbar toggle switches the window to the vendor
profiles. Vendors mode lists them with a problem filter and `New` and `Remove`
actions. A new profile offers nothing, sells at `10000`, buys at `4000`, and
buys no category until one is chosen. For one selected profile the window edits
`Vendor Id`, `Display Name`, `Selling Price (basis points)` for
`purchasePriceBasisPoints`, `Buying Price (basis points)` for
`salePriceBasisPoints`, the `Buys Categories` flags without the quest item
category, and the ordered offers. `Add Offer` lists only definitions that a
vendor may offer and that the profile does not offer yet, and each offer has
`Up`, `Down`, and `Remove`. Live validation marks findings on the vendor and
field they belong to. Renaming or removing a profile does not change actor
content, so a vendor capability that names the old id fails validation until it
is updated in Actor Studio.

- Definition ids already present in runtime JSON are locked and cannot be
  deleted or reused. Retired ids can never be taken by a new definition. A new
  id remains editable until its first successful bake.
- The character and economy settings, the progression limits, categories, tags,
  tool kind identities, equipment slots, weapon and ammunition families, tiers,
  and retired ids are edited by hand in `catalog.json`. The window keeps them
  unchanged when it saves. To retire a released definition, move its id to
  `retired.definitions` and delete its definition file.
- The window loads only authoring files that the compiler accepts, so fix a
  hand edit that breaks the catalog before you open it.

Client presentation JSON is bundled through Addressables at
`src/Client/Assets/Content/Items/Presentation/item-presentation-catalog.json`.
It maps every gameplay definition id to an optional icon address below
`Items/Icons/`, a localization key, fallback text, an optional prefab
presentation key, and the look of the item on a body:

- `garments` names the garment textures the item shows when worn, such as
  `WoolCloak`, at most four. `loweredGarments` names those it shows instead
  while its wearer has lowered it, such as `WoolHoodDown` for the hood. `dyes`
  holds the colors of their dye channels as `#RRGGBB` with upper-case digits,
  at most three, or none for their natural colors. Lowered garments and dyes
  need garments. The Item Catalog window edits them as comma-separated lists,
  and `ItemGarmentTests` requires that every item of the `head`, `body`, `legs`,
  `hands`, `feet`, or `back` slot shows garments that every body's appearance
  catalog has, in its own slot, with no more dye colors than they have dye
  channels; see [Equipment Visuals Design](../../docs/EQUIPMENT_VISUALS_DESIGN.md#items).
- `attachment` names the attachment an item holds on the socket of its
  equipment slot, such as `IronSword` for the Iron Sword in `main_hand`, or is
  empty. The window edits it as `Attachment`, and `ItemAttachmentTests` requires
  that every body's appearance catalog has it and that every equipment slot of
  the item is its socket; see
  [Attachments](../../docs/EQUIPMENT_VISUALS_DESIGN.md#attachments). A Tool slot
  item may name a tool attachment instead, such as `MiningPickaxe` for the
  Starter Pickaxe and `WoodcuttingAxe` for the Woodcutting Axe, which the body
  holds only while its character gathers; `ToolInUseTests` requires that only
  items of the Tool slot name tools; see
  [Tools In Use](../../docs/EQUIPMENT_VISUALS_DESIGN.md#tools-in-use).

Icon files are client-owned assets below `Assets/Content/Items/Icons` and use
one Sprite per file. The presentation file has its own deterministic
presentation revision and records the exact gameplay catalog revision it was
baked against; the client checks the pair once when it loads them and caches the
lookup. Presentation fields never alter gameplay structural fingerprints, and
inventory responses therefore carry definition ids and instance state, never
icons or complete definitions.

A failed validation writes nothing. A failed multi-file bake restores every
previous file. Correct the field paths that the error names, or select `Reload`
to return to the checked-in content, then run the `--verify` command below to
confirm that the checked-in runtime content still matches the authoring files.

## Test Fixture Catalog

`tests/Fixtures/ItemCatalog` holds a second, test-owned catalog in the same
format: `content/items` with neutral `fixture.*` definitions and
`content/development/item-packages.json`, plus its compiled
`generated/Items/core.item-catalog.json`. Rule tests in .NET and Unity use it,
so changing game content or balance never breaks them. Its economy differs from
the game content: the currency is named `Fixture Coins` and new characters start
with a balance of 0. Its only vendor profile, `vendor.fixture_store`, offers
`fixture.arrow`, `fixture.potion`, `fixture.pickaxe`, and `fixture.pack`, sells
at `10000`, and buys every fixture category except quest items at `4000`.
`fixture.pickaxe` provides the `pickaxe` tool kind. Recompile it after an
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

Verify that the checked-in runtime catalog matches the authoring files, as CI
does:

```powershell
dotnet run --project tools/ItemCatalogCompiler -- --verify
```

Expected result: both commands print the catalog id `core`, its SHA-256
revision, and the numbers of definitions and Secure Container tiers, and they
print the same revision. `--verify` writes nothing and returns exit code 1 for
malformed authoring, a definition file whose name does not match its id,
invalid rules, or stale runtime JSON. Neither command needs the Unity Editor.

Run the actor compiler afterwards when actor content refers to a changed item,
as [Compile And Verify](../README.md#compile-and-verify) describes.

To bake the gameplay and presentation catalogs without the window, such as after
editing the authoring files or the presentation file by hand, close the project
in every Unity Editor and run the batch bake; structural changes are baked
without asking:

```powershell
& "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" -batchmode -nographics `
  -projectPath src/Client -executeMethod Mmorpg.WorldData.Editor.Items.ItemCatalogCommandLine.SaveAndBake `
  -quit -logFile -
```

Expected result: the log reports `Baked the item catalog`, and Unity exits with
code 0. A validation failure logs `The item catalog was not baked` and exits
with code 1.
