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
`src/Shared/WorldData/Generated/Items/core.item-catalog.json`. The general
content rules are in [../README.md](../README.md).

## Authoring Contract

Catalog format version 5 contains these separate concepts:

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
- **Location eligibility** records explicit Secure Container permission. The
  hard weapon prohibition is also enforced by the pure domain rule.
- **Default policies** are server-owned policy ids and remain separate from
  category and tags.
- **Bag layouts** contain contiguous stable slot indices. General slots
  configure no tags. Specialized slots configure one or more accepted tag ids.
- **Weapon families** in `weaponFamilies` name a combat style (`melee`,
  `ranged`, or `magic`), the ammunition family a `ranged` family fires, and the
  `projectileSpeed` of its attacks in metres per second. A `ranged` family needs
  an ammunition family and a positive speed. A `melee` family has neither and
  hits at once.
- **Ammunition families** in `ammunitionFamilies` group ammunition. Any
  ammunition of a weapon family's ammunition family works with every weapon of
  that family.
- **Weapon data** in a definition's `weapon` block names its weapon family and
  damage type (`normal`, `fire`, `cold`, `nature`, or `shadow`), the integer base
  damage roll from `minimumDamage` to `maximumDamage`, the
  `attackIntervalSeconds` between attacks, and the attack `range` in metres.
  Every `weapon` definition has exactly this block, and no other definition has
  it.
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
  tool kind it requires.
- **Definition tool kinds** in a definition's `toolKinds` list at most 8 tool
  kinds. Only a definition compatible with the `tool` equipment slot may have
  tool kinds, because gathering requires an item with the required tool kind
  equipped in that slot. The Starter Pickaxe provides `pickaxe`, and the
  Woodcutting Axe (`tool.woodcutting_axe`, value 50) provides `woodcutting_axe`.
  The Oak Log (`material.oak_log`, value 10) is what an Oak Tree yields.
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
non-negative integer values. Physical measurement units and decimal weight
values are not part of the contract. The balance scale is one ammunition unit
`1`, a one-handed weapon about `100`, and base character carry capacity `2000`,
so 1000 spare arrows use half of the base capacity. A stackable definition may only be
compatible with equipment slots that hold more than one unit, and one equipment
slot holds at most the smaller of its own maximum quantity and the definition's
stack size.

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
range. A sale whose total rounds down to 0, such as two Bronze Arrows, is refused
with `vendor_will_not_buy`, so the vendor never takes an item for nothing.

The shipped `vendor.city_general_store`, "General Store", is the profile of the
Trade capability of Mira the Quartermaster (`npc.city_services`). It offers
Bronze Arrow (value 1), Minor Healing Potion (25), Cooked Trout (10), Starter
Pickaxe (50), Woodcutting Axe (50), and every item that shows a garment except the Leather Jerkin: the
Linen Shirt (12), Long-Sleeved Linen Shirt (16), Net Shirt (8), Wool Trousers
(15), Leather Boots (30), Leather Gloves (18), the Wool Robe (40), Wool Hood (12),
and Wool Cloak (35) with their blue and red ones (60, 18, and 50), the Nasal
Helm (60), the Iron Sword (120), and the Heater Shield (80). It sells at `10000`
and buys every category except quest items at
`4000`. Selling three Iron Ore of value 10 there pays 12, and selling one Wolf
Pelt of value 15 pays 6.

## Revisions And Structural Changes

The compiler sorts all unordered content before hashing it. Authoring order does
not affect the result, except for equipment slots and vendor offers, whose order
is their display order.

- The complete runtime catalog has one deterministic `revision`. It covers the
  character and economy settings, the progression limits, every identity
  including the tool kinds, the slot quantities and order, the weapon and
  ammunition families, every definition fingerprint, every definition's value,
  tool kinds, combat data, and level requirement, every vendor profile including
  its offer order, and the retired ids.
- Every item definition has a deterministic `structuralFingerprint` covering
  rule-affecting fields such as category, tags, weight, stack limit, equipment
  compatibility, blocked equipment slots, location eligibility, default
  policies, and Bag layout.
- Every Secure Container tier has a structural fingerprint covering its stable
  id and slot capacity.
- Display-only changes, combat data, level requirements, values, tool kinds,
  vendor profiles, economy settings, and equipment slot reordering alter the
  catalog revision but do not alter an item's structural fingerprint, because
  none of them changes custody.
- Structural fingerprints hash their own version,
  `ItemCatalogFormat.StructuralFingerprintVersion`, instead of the catalog format
  version. A new format that only adds non-structural fields therefore keeps
  every persisted definition compatible. Change that version only when the
  meaning of a structural field changes. Format 5 added values, tool kinds,
  vendor profiles, and the economy settings with `StructuralFingerprintVersion`
  still at 3, so it needed no item migration and no development database reset.

GameService mirrors the catalog into PostgreSQL at startup and refuses to start
when the change would invalidate live state:

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

## Unity Editor Workflow

Open `MMORPG > Tools > Item Catalog` in the Unity project. The editor and the
Unity client read catalog JSON through one JsonUtility shape, `ItemCatalogJson`
in the WorldData runtime, so a new catalog field is added there once. The
Editor-only tool provides a searchable definition list with category, tag, and problem
filters and multi-selection, create, duplicate, and remove-draft actions, every
gameplay field in this contract, Bag slot editing, and the separate client
presentation mapping. Selected definitions are edited together, live validation
marks the fields that break this contract, and every edit can be undone with
Unity's Undo. A definition's `Value` field and its `Tool Kinds` flags, which
list the catalog tool kinds, edit the economy fields of the definition.

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
- The character and economy settings, categories, tags, tool kind identities,
  equipment slots, tiers, and retired ids are edited in `catalog.json`. The tool
  keeps them unchanged when it saves. To retire a released definition, move its
  id to `retired.definitions` and delete its definition file.
- `Validate` invokes the strict command-line compiler against temporary files
  and writes nothing.
- `Save` writes `catalog.json`, one file per definition, one file per vendor
  profile, and the client presentation file, but leaves runtime gameplay JSON
  unchanged. Definition files of removed draft definitions and files of removed
  vendor profiles are deleted.
- `Save And Bake` validates and atomically writes the authoring files,
  deterministic runtime gameplay, and client presentation JSON. Unchanged files
  are not rewritten.
- Display-only, structural, added, and removed definitions are reported against
  the previous runtime catalog. Structural changes require explicit
  confirmation.

Client presentation JSON is bundled through Addressables at
`src/Client/Assets/Content/Items/Presentation/item-presentation-catalog.json`.
It contains icon Addressables addresses below `Items/Icons/`, localization keys,
fallback text, optional prefab presentation keys, and, since format 3, the
garments an item shows on a body: `garments`, the names of their garment
textures, such as `WoolCloak`, at most four; `loweredGarments`, those it shows
instead while its wearer has lowered it, such as `WoolHoodDown` for the hood; and
`dyes`, the colors of their dye channels as `#RRGGBB` with upper-case digits, at
most three, or none for their natural colors. Lowered garments and dyes need
garments. The Item Catalog window edits them as comma-separated lists, and
`ItemGarmentTests` requires that every item of the `head`, `body`, `legs`,
`hands`, `feet`, or `back` slot shows garments that every body's appearance
catalog has, in its own slot, with no more dye colors than they have dye
channels; see [Equipment Visuals Design](../../docs/EQUIPMENT_VISUALS_DESIGN.md#items).
Since format 4, `attachment` names the attachment an item holds on the socket of
its equipment slot, such as `IronSword` for the Iron Sword in `main_hand`, or is
empty. The Item Catalog window edits it as `Attachment`, and
`ItemAttachmentTests` requires that every body's appearance catalog has it and
that every equipment slot of the item is its socket; see
[Attachments](../../docs/EQUIPMENT_VISUALS_DESIGN.md#attachments).
Icon files are
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

Bake the gameplay and presentation catalogs without the window, such as after
editing the authoring files or the presentation file by hand, with the Unity
project closed in every Editor; structural changes are baked without asking:

```powershell
& "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" -batchmode -nographics `
  -projectPath src/Client -executeMethod Mmorpg.WorldData.Editor.Items.ItemCatalogCommandLine.SaveAndBake `
  -quit -logFile -
```

Verify that checked-in runtime content matches authoring:

```powershell
dotnet run --project tools/ItemCatalogCompiler -- --verify
```

Validation failure or stale runtime content returns exit code 1.
