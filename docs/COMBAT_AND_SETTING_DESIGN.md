# Combat And Setting Design

Last updated: 2026-10-13

Status: Agreed design direction. Nothing in this document is implemented yet.
It replaces the earlier modern third-person shooter combat direction.

## Purpose

This document is the product source of truth for the game setting, character
progression, combat model, movement and camera direction, damage types, weapons,
ammunition, magic, threat, healing, PvP combat, and the related consumable
rules.

Each section separates three kinds of content:

- **Decided:** agreed product decisions.
- **Proposed:** recommended baselines that fit the decisions but have not been
  explicitly confirmed. They should be validated in prototypes and playtests
  before they are treated as locked.
- **Open:** questions that still need a decision.

Numbers in this document are starting values for tuning, not final balance.

## Summary Of Decisions

| Area | Decision |
| --- | --- |
| Setting | World of Warcraft style high fantasy with magic. Technology exists as a cultural trait of specific peoples, not as a modern era. |
| Weapons | Fantasy weapons only. Firearms are fantasy firearms, such as an improvised goblin pistol, never real-world models. |
| Playable races | Humans only for now. More races may be added later. |
| Progression | Classless. Gear, perks, and talents form a build, and the build decides whether a character plays as tank, damage, or healer. |
| Levels | One character level earned through combat experience from Mobs, quests, bosses, and dungeons. PvP kills give no experience, and death never costs experience. The level gives no stats, only unlocks and one talent point per level. Talent points go into a large open talent library of stats, perks, and other options. Talent ranks cost 1, 2, 3, and so on up to rank 10, and can be respecced for a fee. No intended long-term level cap, with a temporary cap of 200 at first. Power comes from gear and the build. Every profession has its own separate experience and level. |
| Healing | Every character can use every ability, including healing. Healing multipliers on gear and talents make healing effective, so a healer is a build commitment. |
| Threat | Mobs attack the character with the highest threat in a per-Mob threat table. Damage and healing generate threat. Tank builds get a threat multiplier and a taunt. |
| Combat feel | A blend of Old School RuneScape and RuneScape 3, without tiles and without clunky tick-based feel. |
| Movement | Click to move with smooth, continuous movement. |
| Camera | Freely rotatable camera with zoom, as in RuneScape. |
| Attacking | A character must stop moving before attacking. |
| Abilities while moving | Auto attacks and most abilities require standing still. Movement cancels channeled and cast-time abilities. A small number of abilities are marked as usable while moving. |
| Ability count | Deliberately few active abilities. Combat must not become a RuneScape 3 style rotation across many ability bars. The exact abilities and their number are decided later. |
| Ability source | One shared ability pool for every character. Players choose freely which abilities go on the bar. Some abilities require a matching weapon, such as a staff or a melee weapon. |
| Ability cost | Each ability has its own cooldown. There is no shared combat resource such as adrenaline. Mana and ammunition are the only combat resources. |
| Combat styles | Combat triangle: Melee beats Ranged, Ranged beats Magic, Magic beats Melee. |
| Defense | No active defensive mechanic for now, such as protection prayers or auras. Defense comes from gear stats such as armor. |
| Damage types | Normal, Fire, Cold, Nature, and Shadow. Normal is the default. Elements are situational. |
| Element source | The weapon decides the damage type, not the ammunition. |
| Elemental weapons | Most weapons are Normal, especially early. Elemental weapons are special found, looted, or crafted items with a fixed built-in damage type that can never be changed or added. |
| Resistances | Potions and gear can grant small elemental resistances. They make content easier but are never required. |
| Weaknesses | Mobs can be weak to an element. |
| Ammunition | Consumed and never recovered, tiered, and adds a small damage bonus. Any ammunition of the correct family works with any weapon of that family. |
| Ammunition slot | A dedicated equipment slot holds a weightless stack of at most 1000 units, tunable as content data. Ammunition carried in the inventory has weight. |
| Magic cost | Magic auto attacks cost mana. Mana regenerates slowly and is restored with mana potions and drinking. |
| PvP time to kill | Medium, about 10-20 seconds 1v1 with equal gear, extended by potions and healing. Skill decides both how fast a player kills and how long a player survives. |
| Equipment changes | Individual items can be changed freely during combat. Each change blocks attacks, abilities, and spells for about 1 second, so switching is possible but high-APM switching gives no advantage. |
| PvP balance | PvP and PvE use the same numbers. PvE is balanced through Mob health and defense. |
| PvP movement control | Movement impairments such as slows and roots exist, and so do counters such as a potion that removes them. Both escaping and catching must feel possible. |

## Background

The original direction was a Fortnite-like third-person shooter. The concern
that drove this design was PvE: shooting a stationary boss through magazine
after magazine, and shooting ordinary Mobs with no decisions involved, risks
feeling boring and awkward. The goal is a RuneScape, World of Warcraft, and
Final Fantasy feeling where the player is active, manages a rotation, and makes
decisions, and where dungeons are deliberate encounters instead of a stream of
enemies.

A hybrid shooter with an ability bar was considered. The chosen direction is
RuneScape-like combat and movement instead, with a smooth modern presentation.

## Setting

### Decided

- The world is World of Warcraft style high fantasy with magic everywhere.
- The base of the world is medieval fantasy: castles, forests, taverns, and
  magic.
- Technology is cultural. Specific peoples, such as goblins, dwarves, and
  gnomes, build firearms and machines. The rest of the world does not. The
  "modern" feeling exists in pockets tied to those cultures.
- Magic and technology exist side by side without being treated as opposites.
- Weapons are fantasy weapons. Real-world firearm models such as an M4A1 or an
  AK-47 do not exist in this world.
- Only humans are playable for now.

### Proposed

- The tone mixes epic threats with humor, for example goblin inventions that
  occasionally misfire.
- Each culture can own a city hub with its own style, professions, and weapon
  craft. This matches the safe city hubs in the original brief.
- Art direction is stylized low to mid poly with exaggerated proportions,
  strong silhouettes, and clear colors, so characters stay readable from a
  zoomed-out rotating camera.

## Progression And Roles

### Decided

- There are no classes.
- Everyone has access to the same kinds of actions. The build, made of gear,
  perks, and talents, decides the play style.
- Tank, damage, and healer roles come from the build, not from a class.
- Combat style (Melee, Ranged, or Magic) and role (tank, damage, or healer) are
  separate choices. A tank can be a melee or a ranged build.
- Roles work through the threat system described in [Threat](#threat).
- Each character has one character level. Combat earns the experience that
  raises it.
- Combat experience comes from Mobs, quests, and completed bosses and dungeons.
- PvP kills give no experience. The reward for PvP is loot. This prevents kill
  trading between friends or between a player's own characters.
- Dying never costs experience. Full loot and the corpse are already the death
  penalty.
- Every profession has its own experience and level, independent of the
  character level and of other professions.
- The character level gives no stats by itself. It only unlocks things.
- Power progression comes from gear and the build.
- Each character level grants one talent point.
- Talents form a large open talent library. Talent points can be spent on
  stats, perks, or other options, with no fixed path.
- Builds can differ completely at similar levels. One character may put its
  points into raw stats while another takes only perks and no stats from
  talents.
- Talents have ranks with escalating cost. The first rank costs 1 point, the
  second 2 points, the third 3 points, and so on up to rank 10. A talent at
  rank 10 costs 55 points in total, so every further investment in the same
  talent gives less value per point.
- Talents can be respecced for a fee at a city NPC. The fee grows with the
  number of spent talent points, which makes respecs a currency sink while
  still allowing mistakes to be corrected.
- There is no intended long-term level cap. Players can keep leveling to unlock
  more. Each level requires more experience than the previous one.
- The first release uses a temporary level cap of 200, defined as content data,
  to observe how progression plays out before the cap is raised or removed.
- A higher level gives a slight advantage through more talent points, which is
  intended as a reward for progress. Because a level gives no direct strength
  and talent ranks get more expensive, a well-played lower-level character, for
  example level 60, can still beat a level 90 character.

### Proposed

- Progression should mainly add options, such as new abilities, perks, and
  tools, rather than only larger numbers. Scaling Mob health purely with level
  creates long fights against damage sponges.
- Character levels unlock gear requirements and abilities in the shared pool.
  Gear level requirements stop a new character from equipping top-tier gear
  immediately.
- Stat talents and perk talents are balanced against each other so neither is
  the obvious choice. A point in stats gives a modest bonus, which also keeps
  level differences from becoming large raw power gaps in PvP.
- The escalating rank cost applies to every ranked talent, stats and perks
  alike. Some perks may have only one rank.

## Movement And Camera

### Decided

- Movement is click to move.
- Movement is smooth and continuous. There are no visible tiles.
- The camera rotates freely around the character and supports zoom.
- A character must stop moving before attacking.
- Auto attacks and most abilities require standing still.
- Starting to move cancels channeled and cast-time abilities.
- A small number of abilities are explicitly marked as usable while moving,
  for example a dash or blink, defensive abilities, and selected short instant
  abilities. This creates a constant choice between dealing damage and staying
  safe, gives telegraphed boss attacks real counterplay, and limits kiting so
  the combat triangle holds.

### Proposed

- Left click performs the default action for what is under the cursor: walk on
  the ground, attack a Mob, talk to an NPC, or loot a corpse.
- Right click opens a context menu with every available action, as in
  RuneScape.
- The context menu includes **Examine**, which shows a short description and,
  for Mobs, any elemental weakness.
- Pathfinding may use a navigation mesh or a fine internal grid, but the player
  never sees or feels tiles.
- Running uses run energy that drains faster with higher carried weight, which
  connects directly to the existing carry-weight and encumbrance rules.
- Buildings hide roofs or occluding walls when the camera would otherwise lose
  the character.

## Combat Model

### Combat Styles

#### Decided

There are three combat styles that form a triangle:

- **Melee beats Ranged.**
- **Ranged beats Magic.**
- **Magic beats Melee.**

The triangle works through armor types, as in RuneScape:

| Style | Armor | Strong against | Weak against |
| --- | --- | --- | --- |
| Melee | Heavy armor | Ranged | Magic |
| Ranged | Light armor | Magic | Melee |
| Magic | Cloth | Melee | Ranged |

Every character can train and use every style. Players choose the style that
fits the situation.

### Attacks And Abilities

#### Decided

- The number of active abilities is deliberately small. Combat must not become
  a RuneScape 3 style rotation across many ability bars. Decisions should come
  from timing, positioning, and reacting to the encounter, not from managing a
  large number of buttons.
- The exact abilities and how many a character can use are decided later.
- Abilities come from one shared pool that every character can access. Players
  choose freely which abilities go on their ability bar.
- Some abilities require a matching equipped weapon, for example a staff or a
  melee weapon.
- Each ability has its own cooldown. There is no shared combat resource such as
  RuneScape 3 adrenaline, because a build-and-spend resource drives the
  rotation feel this design avoids. Mana and ammunition are the only combat
  resources.
- A weapon special attack with its own energy, as in Old School RuneScape, may
  be added later if weapons need more identity.

#### Proposed

- Selecting a Mob starts an auto attack with the equipped weapon. The character
  walks into range, stops, and keeps attacking until the target dies, the player
  moves, or the player selects another action.
- Active abilities share one small ability bar.
- Attack abilities generally require a weapon of their combat style: Melee
  abilities need a melee weapon, Ranged abilities a ranged weapon, and Magic
  abilities a staff or other magic weapon. Healing, defensive, movement, and
  utility abilities usually have no weapon requirement. The requirement is set
  per ability in content data.
- Attack ability damage scales from the equipped weapon, so better weapons
  improve abilities as well as auto attacks.
- A global cooldown of about 1-1.5 seconds gives combat a deliberate rhythm
  without tiles or ticks and tolerates network latency.
- Ground-targeted abilities are aimed with the cursor, for example area damage,
  a thrown bomb, or a directional shot. The cursor is free because movement is
  click based.
- Gear and talents can grant a short dash or blink for avoiding mechanics.
- Two saved equipment loadouts can be swapped with one key as a convenience. A
  swap includes the matching ammunition. The existing primary and secondary
  weapon slots fit this model. A loadout swap is one equipment change and has
  the same short attack lockout as changing a single item.

### Defense

#### Decided

- There is no active defensive mechanic for now. No protection prayers and no
  auras. This can be revisited later.
- Defense comes from gear stats, such as armor.

#### Proposed

- Telegraphed attacks are countered through movement, positioning, and the few
  abilities that are usable while moving, not through a defensive toggle.
- Both elements and combat styles are handled through preparation before a
  fight: gear, potions, and the choice of armor type.

### Threat

#### Decided

- Mobs choose targets through a threat table, as in World of Warcraft. Each Mob
  keeps a threat value for every character it is fighting.
- Damage generates threat against the damaged Mob.
- Healing generates threat, split across the Mobs that are fighting the healed
  character.
- A Mob attacks the character with the highest threat.
- Tank builds get a threat multiplier from gear or talents and a taunt ability.
  Damage builds manage their threat so they do not pull Mobs off the tank.
- Threat only controls Mob targeting. Players always choose their own targets,
  so threat has no role in PvP.

#### Proposed

- A Mob switches target only when another character reaches about 120% of the
  current target's threat. The margin prevents targets from flickering between
  characters with similar threat.
- Taunt immediately gives the tank the highest threat on the taunted Mob. It is
  one of the few active abilities and does not get a separate ability bar.
- A threat table only contains characters that are in combat with that Mob, so
  it stays small and fits the existing central Mob scheduling.

### Healing

#### Decided

- Every character can use every ability, including healing abilities.
- Healing effectiveness comes from healing multipliers on gear and talents. A
  character without them can heal, but too weakly to fill the healer role.
- A healer is therefore a build commitment, not an unlocked ability.

#### Proposed

- Healing multipliers use the same gear and talent budget as damage and
  defense, so a dedicated healer gives up some damage or defense.
- Swapping into healing gear during a fight works like any other equipment
  change, at the cost of the attack lockout and the carried weight of the extra
  gear.
- Healing abilities cost mana, which makes mana management the core skill of
  healer builds.
- Allies are targeted by clicking them in the world or in party frames.

### Damage Types

#### Decided

| Damage type | Role |
| --- | --- |
| Normal | Default damage type for most weapons |
| Fire | Situational element |
| Cold | Situational element |
| Nature | Situational element |
| Shadow | Situational element |

- Normal damage is the standard. Elements are situational choices.
- All three combat styles can deal elemental damage, because the element comes
  from the weapon. A staff can be Fire or Cold. A sword, bow, or firearm can also
  carry an element.

#### Proposed

Each element should have a recognizable effect and a consistent color used in
damage numbers, visual effects, ground telegraphs, and UI:

| Damage type | Color | Possible effect |
| --- | --- | --- |
| Normal | White | No extra effect |
| Fire | Orange | Burning damage over time |
| Cold | Light blue | Slow |
| Nature | Green | Poison, possibly reduced healing |
| Shadow | Purple | Life steal |

### Resistances And Weaknesses

#### Decided

- Potions and gear can grant elemental resistance.
- Resistance is a small advantage that makes content easier when the player
  knows which element they will face. It is never required to complete content.
- Mobs can be weak to an element.

#### Proposed

| Modifier | Starting value |
| --- | --- |
| Resistance potion | 10-15% less damage from one element |
| Resistance from gear | Small bonus, at most about 20-25% total |
| Mob weak to an element | 15-25% more damage from that element |
| Mob with its own element | Takes reduced damage from that element |

- Player resistance is capped well below immunity.
- Elemental resistance on gear is a secondary bonus, not a reason to carry
  complete extra gear sets.
- Players learn weaknesses through visible design (a fire creature looks fire
  based), the Examine action, and possibly a bestiary that fills in as Mobs are
  defeated.

### Damage Calculation Order

#### Proposed

1. The weapon provides base damage and the damage type.
2. Ammunition adds its bonus for Ranged weapons.
3. The combat triangle and target armor affect all damage, regardless of
   element.
4. A small elemental modifier is applied last: weakness increases damage,
   resistance reduces it, and otherwise nothing changes.

This keeps Normal damage a safe default and prevents elemental weapons from
bypassing armor and becoming strictly better against armored targets.

## Weapons And Ammunition

### Decided

- The weapon decides the damage type. Ammunition never changes the element.
- Changing element means changing weapon.
- Most weapons deal Normal damage, especially early in progression.
- Elemental weapons are special items with a built-in damage type that players
  find, loot, or craft. Crafting creates a new weapon whose item definition
  carries the element. It never modifies an existing weapon.
- A weapon's damage type is fixed by its item definition. It can never be
  changed, added, or removed. There is no enchanting or other element
  modification.
- Ranged includes bows, crossbows, and fantasy firearms.
- Ammunition is consumed and comes in tiers, like RuneScape arrows.
- Used ammunition is never recovered. Every shot consumes one unit, and no
  ammunition is left on the ground. This makes ammunition a steady economy sink
  and keeps demand for crafted ammunition.
- Each Ranged weapon family uses its own ammunition family, such as arrows for
  bows.
- There is no tier restriction between weapon and ammunition. Any ammunition of
  the correct family works with any weapon of that family.
- The weapon provides the main damage. Ammunition gives a small bonus that is
  large enough to make better ammunition worth using.
- A dedicated equipment slot holds ammunition as a large stack with no weight,
  like every other equipped item.
- The ammunition slot holds at most 1000 units, the same cap for every
  ammunition family and tier. The cap prevents the weightless slot from being
  used to haul tradeable ammunition past the carry-weight system. It is content
  data, not hard-coded, so it can be tuned after playtests.
- Ammunition carried in the inventory has normal weight.

### Proposed

Weapon families and their feel:

| Family | Ammunition | Feel |
| --- | --- | --- |
| Bow | Arrows | Fast, quiet, lower damage per shot |
| Crossbow | Bolts | Slower, stronger, piercing |
| Fantasy firearm | Shot and powder | Slow reload, high damage, loud |

- The ammunition bonus is a percentage of weapon damage, for example 5-15%
  depending on tier, instead of a flat value. A flat bonus would make expensive
  ammunition a huge upgrade on a weak weapon. A percentage keeps the bonus
  proportional, so top-tier ammunition only pays off with a good weapon.
- Ammunition in the slot follows the same death rules as other equipment in
  [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).
- The lowest ammunition tier is very cheap. Ranged is the only style that
  costs money per attack, and new players must not be priced out of it.
- Gear or perks may later grant a chance to save ammunition on a shot. A saved
  unit is simply not consumed. It never becomes a ground item.
- Weapon tiers can be expressed through the culture and craftsmanship behind
  them, for example:
  1. Goblin scrap pistol: cheap and unreliable.
  2. Human flintlock: stable and dependable.
  3. Dwarven rifled gun: precise and heavy.
  4. Rune-etched rifle: magic and powder combined at the highest tier.

## Magic And Mana

### Decided

- Magic auto attacks cost mana instead of materials.
- Mana regenerates slowly.
- Mana potions restore mana.
- The cost of magic is time and risk: a caster has to stop and drink, and deals
  no damage while doing so.

### Proposed

| | During combat | Between fights |
| --- | --- | --- |
| Source | Mana potion | Drink while resting |
| Effect | Fast, partial restore | Slow, large restore |
| Limit | Shared potion cooldown | Interrupted by attacking or taking damage |
| Cost | More expensive, crafted by alchemy | Cheap |

- The shared potion cooldown prevents chaining potions to ignore mana.
- Mana management becomes a skill for healer builds, as in classic World of
  Warcraft.

## Combat Cost By Style

### Proposed

| Style | Auto attack cost | Economic link |
| --- | --- | --- |
| Ranged | Ammunition | Arrows, bolts, shot |
| Magic | Mana | Mana potions |
| Melee | Nothing | Takes more damage and uses more food and healing |

Eating food during combat delays the next attack, as in RuneScape. Together
with drinking for mana, no style gets free recovery during a fight.

## Encounter And Dungeon Principles

### Proposed

- Ordinary Mobs appear in small groups of about 3-5 that are a real threat,
  instead of large crowds that die instantly.
- Mobs have jobs: casters that must be interrupted, healers that must be
  controlled or killed first, patrols that must be timed, and armored Mobs that
  reward the right combat style.
- Dungeons vary their pace with rests between pulls, exploration, and possibly
  puzzles. Mana and food recovery naturally create the pull, fight, recover
  rhythm of classic World of Warcraft dungeons.
- Elite Mobs and bosses carry the long fights and the mechanics. Bosses have
  phases and telegraphed attacks that players avoid by moving and positioning.
- Attacks the player is expected to avoid have a wind-up of at least about
  300-500 ms, so players can react through network latency on a
  server-authoritative simulation.

## PvP Combat

The social PvP rules, such as rule areas, the hostile flag, and criminal
reputation, are defined in [MMO Codex Project Brief](MMO_Codex_Project_Brief.md).
This section covers how combat itself works between players.

### Decided

- Time to kill is medium. A 1v1 fight between equally geared players lasts
  about 10-20 seconds of sustained damage. Potions and healing can extend it.
- PvP balances two feelings. The attacked player should feel able to escape or
  fight back. The attacker should feel able to catch the target and stop it from
  getting away.
- Movement impairment abilities exist, such as slows and roots.
- Counters to movement impairments exist, for example a potion that removes
  them.
- Skill decides the outcome in both directions. A skilled attacker can kill
  quickly, and a skilled defender can survive for a long time.
- Players can freely change individual equipment items during combat.
- Every equipment change has a small cost: the character cannot attack, use
  abilities, or cast spells for about 1 second. Switching stays possible and
  meaningful, for example to counter an opponent's combat style, but rapid
  high-APM switching as in Old School RuneScape gives no advantage.
- PvP and PvE use the same numbers. Abilities, weapons, and effects have no
  separate PvP values. PvE balance is adjusted on the Mob side, mainly through
  Mob health and defense.
- Logging out or disconnecting outside a safe city leaves the character in the
  world for a risk timer that combat resets, as defined in
  [MVP Specification](MVP_SPEC.md). Logout cannot be used to escape a fight.

### Proposed

- Player health and damage are tuned first for the PvP time-to-kill target.
  Mob values are then set around the player values.
- Mob definitions can resist or ignore movement impairments, for example bosses
  that cannot be rooted, so impairments tuned for PvP do not trivialize PvE
  encounters.

- An equipment change refreshes the lockout instead of stacking it. Changing
  several items in quick succession locks attacks until about 1 second after
  the last change.
- The lockout always applies, in and out of combat. The server then needs no
  separate in-combat state for equipment rules.
- Movement and consumables remain available during the lockout, so a player can
  still reposition or drink a potion while switching.

- Movement impairments are slows and roots. Long hard stuns that remove all
  control are avoided, because losing all control while full loot is at stake
  feels unfair.
- Repeated impairments on the same target have diminishing returns, so a target
  can never be kept rooted until it dies.
- A cleanse potion removes movement impairments and shares the potion cooldown
  with healing and mana potions. Every potion use becomes a choice between
  health, mana, and freedom of movement.
- A root does not stop attacks, because attacking already requires standing
  still. Roots therefore mainly stop escapes and melee chasers, while slows also
  punish players who reposition.
- The Cold slow and the Nature reduced healing are natural PvP tools, which
  gives elemental weapons a PvP role in addition to Mob weaknesses.
- Run energy that drains faster with carried weight makes a heavily loaded
  player easier to catch, which strengthens the Wilderness risk and reward.

## UI And UX Principles

### Proposed

RuneScape 3 shows what happens when interfaces accumulate over many years:
legacy and new layouts mixed, too many windows, too many abilities, and small
icons. This project should set rules from the start:

- Cap the number of active abilities. New abilities replace old ones on the bar
  instead of adding more bars.
- Show danger in the world, not in the interface: ground markers and clear
  animations instead of meters and popups.
- Use a minimal HUD by default and show more only when it is needed.
- Keep a fixed layout where each element always lives in the same place.
- Put consumables on dedicated hotkeys instead of clicking them in the
  inventory.

The current UI is explicitly temporary, so the final UI can follow these rules
without migration.

## Open Questions

- Which abilities exist, how many fit on the ability bar, and which of them are
  usable while moving?
- Which races, if any, are added later, and do they affect access to cultural
  technology?

## Implications For The Existing Codebase

This section is a planning aid. None of it is implemented.

Expected to carry over largely unchanged:

- Accounts, characters, topology, placement, join tickets, and simulation
  session leases in AuthService.
- The item catalog, slot inventory, Bags, carry weight and encumbrance, death
  partition, corpses, insurance, and Recovery Storage.
- World actors, NPC and Mob definitions, and authoritative interaction sessions.
- Interest management, the versioned protocol framework, and the stress and bot
  tooling.

Expected to change:

- Movement input changes from per-tick directional input and camera yaw to
  move-to-destination and attack-target commands.
- SimulationWorker needs server-side pathfinding with navigation data generated
  from World authoring data. A C# Recast and Detour port such as DotRecast is
  one option.
- Shared movement code can become shared path following, so the client starts
  moving immediately on click while the server stays authoritative.
- The third-person shooter camera is replaced by an orbit camera with zoom.
- Crosshair interaction targeting is replaced by cursor picking with a left
  click default action and a right click context menu.
- The projectile and fire-intent combat direction in the
  [MVP Specification](MVP_SPEC.md) is replaced by target-based combat where the
  server resolves hits. Shooter-style lag compensation is not needed.

Expected additions:

- An ammunition equipment slot. The current slots are Head, BodyArmor,
  PrimaryWeapon, SecondaryWeapon, Tool, Ring1, Ring2, and Bag.
- Item definition data for base damage, damage type, ammunition family,
  ammunition bonus, defensive stats such as armor, healing multipliers, and
  elemental resistance.
- Ability definitions with an optional equipped-weapon requirement and an
  explicit usable-while-moving flag. The server
  rejects other abilities while the character moves and cancels channeled and
  cast-time abilities when movement starts.
- Mana, per-ability cooldowns, the global cooldown, and consumable cooldown
  state.
- Status effects for movement impairments such as slows and roots, with
  diminishing returns per target and removal by a cleanse effect.
- A server-owned attack lockout timestamp that every equipment change
  refreshes. Attack, ability, and spell intents are rejected until it expires.
- Durable character level, combat experience, and talent point allocation, and
  separate durable experience and level per profession.
- Threat tables on Mobs, with threat from damage and healing, tank threat
  multipliers, taunt, and a target switch margin.
- Mob combat data for health, defense, elemental weaknesses, and movement
  impairment resistance.
- Review of existing item content with modern naming, such as Training Rifle
  and Training 5.56 Ammunition, against the fantasy setting.

## Superseded Direction

This document supersedes the modern third-person shooter, modern weapon, and
projectile combat direction in
[MMO Codex Project Brief](MMO_Codex_Project_Brief.md) and the combat direction
in [MVP Specification](MVP_SPEC.md). Architecture and feature documents still
describe the implemented WASD movement, shooter camera, and crosshair
interaction until the code changes.
