# Game Design

This document is the product source of truth: what the game is, its setting and
world, progression, movement, combat, PvP and crime, professions and economy,
UI principles, and the MVP with its slices. Item, inventory, currency, vendor,
and death rules are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), and the
world actor model of NPCs, Mobs, and resource nodes is in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md). What is implemented
is described from the player's side in [Game Features](GAME_FEATURES.md) and
from the server's side in [Service Features](SERVICE_FEATURES.md).

## Status

Implemented:

- Accounts with several characters, a body model and look chosen at character
  creation, and Shard selection with one active session per character.
- Click-to-move movement, the orbit camera, and the right-click world menu with
  Examine.
- The item foundation: inventory, equipment, Bags, the Bank, the Secure
  Container, Recovery Storage, insurance, carry weight, the death partition, and
  corpses.
- One currency, Gold, and one NPC vendor.
- Gathering from shared resource nodes: Mining Iron Veins with a pickaxe and
  Woodcutting Oak Trees with a Woodcutting Axe, without profession experience.
- Target-based auto attack with four weapons: the Training Shortbow (Ranged,
  spends arrows), the Iron Sword (Melee, one hand), the Iron Greataxe (Melee,
  both hands), and the Oak Staff (Magic, spends mana). Attacks wind up per
  weapon family, equipment changes hold attacks briefly, health and mana
  regenerate out of combat, dead characters respawn, and player versus player
  follows the Shard's rule set. All damage is Normal.
- Mobs that patrol, detect players, chase, attack, and walk home past their
  leash, with loot, corpses, and respawn: the Feral Wolf and the Feral Alpha
  boss.
- A durable character level that gates equipment. Every character is level 1.
- Worn clothes, armor, weapons, and shields show on the body, and a gathering
  tool shows in the hands while it is used, as
  [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md) describes.

Planned, as this document describes: abilities and the ability bar, the combat
triangle with armor and misses, elemental damage and resistances, threat,
healing abilities, potions, food, and drinking, movement impairments,
experience and talents, profession levels and crafting, the other gathering
professions, gameplay rule areas with the logout risk timer, the hostile flag
and criminal reputation, quests, player trading, and social systems. The status
of the whole project is in [Project Overview](PROJECT_OVERVIEW.md).

Each section says what of it is implemented. Its rules are grouped as
**Decided** product rules, **Proposed** baselines that fit the decisions and
still need prototypes and playtests before they are locked, and **Open**
questions. Numbers are starting values for tuning, not final balance.

## Core Decisions

| Area | Decision |
| --- | --- |
| Identity | A classless, profession and economy driven open-world MMORPG with safe cities and a dangerous full-loot wilderness |
| Setting | World of Warcraft style high fantasy; technology is a cultural trait of some peoples; fantasy weapons only |
| Races | Humans only for now |
| Progression | No classes; gear, perks, and talents form the build; one character level that only unlocks; every profession levels to 100 |
| Movement and camera | Click to move without visible tiles; a freely rotating camera with zoom |
| Combat | Target based, a blend of Old School RuneScape and RuneScape 3 without tiles or a tick feel; stand still to attack; few active abilities |
| Combat styles | Melee beats Ranged, Ranged beats Magic, Magic beats Melee, through armor types |
| Combat resources | Mana and ammunition only; every ability has its own cooldown |
| Damage types | Normal by default; Fire, Cold, Nature, and Shadow are situational and come from the weapon |
| Roles | Tank, damage, and healer come from the build and the threat system |
| PvP | The same numbers as PvE; about 10-20 seconds to kill in an even duel; loot is the reward, never experience |
| Death | Full loot with exceptions; death never costs experience |
| Professions | Five gathering and eight crafting professions; trading is a playstyle |
| Economy | NPC driven at first, player driven later |

## Vision And Identity

### Core Identity

The game is a classless, profession and economy driven, persistent open-world
MMORPG. It draws on RuneScape for its world, professions, and click-to-move
combat, on World of Warcraft for its city hubs, setting, dungeons, and roles,
and on full-loot games for the risk of the wilderness. It should feel like:

- A large persistent open world.
- Safe cities with NPCs, quests, banks, crafting, trade, and social systems.
- Classless character progression.
- Professions and the economy as progression pillars as important as combat.
- Third-person combat in a readable, stylized world.
- PvP as one path to progression, not the whole game.
- A wilderness that is a high-risk, high-reward area with full loot and social
  tension.

### Design Philosophy

> Civilization protects progression. Wilderness accelerates progression. Crime
> enables shortcuts, but creates long-term consequences.

The game must not become a kill-on-sight game. The wilderness is dangerous, but
players need reasons to cooperate, trade, escort, negotiate, hunt criminals, or
form temporary alliances. PvP creates risk and stories. The long-term glue of
the game is character progression, professions, crafting, the economy, social
systems, exploration, and decisions about risk and reward.

### Why Target-Based Combat

Aim-and-shoot combat leaves PvE without decisions: shooting a stationary boss
through magazine after magazine, or shooting ordinary Mobs, feels boring and
awkward. The goal is the RuneScape, World of Warcraft, and Final Fantasy
feeling, where the player is active, uses a few actions at the right moments,
and makes decisions, and where dungeons are deliberate encounters instead of a
stream of enemies. Combat and movement are therefore RuneScape-like, with a
smooth modern presentation: the feel is a blend of Old School RuneScape and
RuneScape 3, without tiles and without a clunky tick-based feel.

### The Game Loop

A player prepares in a safe city, leaves to gather, hunt, and quest, and takes
greater risks in the wilderness for rarer resources and better loot. An
encounter with another player can end in cooperation, flight, or a fight. The
player returns to a city, or as a criminal to an outlaw camp, to sell, craft,
bank, and progress. The first playable version of this loop is the
[MVP](#mvp).

## Setting

Implemented: the human base characters, fantasy items, and the first fantasy
models. The world map itself is not authored yet; the two development Worlds
are test maps, described in [Development Worlds](DEVELOPMENT_WORLDS.md).

### Decided

- The world is World of Warcraft style high fantasy with magic everywhere. Its
  base is medieval fantasy: castles, forests, taverns, and magic.
- Technology is cultural. Specific peoples, such as goblins, dwarves, and
  gnomes, build firearms and machines; the rest of the world does not. The
  "modern" feeling exists in pockets tied to those cultures.
- Magic and technology exist side by side without being treated as opposites.
- Weapons are fantasy weapons. Firearms are fantasy firearms, such as an
  improvised goblin pistol. Real-world firearm models such as an M4A1 or an
  AK-47 do not exist in this world.
- Art is stylized low to mid poly with exaggerated proportions, strong
  silhouettes, and clear colors, so characters and actors stay readable from a
  zoomed-out camera that turns freely. Models and textures lean toward fantasy
  instead of copying real objects. The human base characters are the exception
  and stay realistic. The rules are in
  [Art Pipeline](ART_PIPELINE.md#art-direction-for-models-and-textures).
- Only humans are playable for now, with a male and a female body. Later races
  are fixed body types of their own, as
  [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#future-races-and-base-models)
  describes.

### Proposed

- The tone mixes epic threats with humor, for example goblin inventions that
  occasionally misfire.
- Each culture can own a city hub with its own style, professions, and weapon
  craft.

## World Structure

Implemented as the topology foundation: Worlds, Shards, global character data,
and one active session per character.

### Decided

- The game is one large persistent open world, not a set of extraction
  instances. Instances may exist later, but the main wilderness is a permanent
  explorable area.
- The World is the shared map and content. Players choose a Shard, a copy of
  the World's simulation that may have its own rule set. There are no realms.
- Accounts, characters, items, the Bank, currency, progression, professions,
  quest state, and reputation are global across every region and Shard, and so
  are the later marketplace, friends, guilds, and social systems.
- Live state belongs to one Shard: positions, combat, NPCs and Mobs, resource
  node charges, interactions, live corpses, encounters, local world events, and
  loot containers in the world.
- A character can play on every Shard, but only on one at a time, and switches
  only after it fully left its current Shard.
- The canonical terms and the topology are in
  [Project Architecture](PROJECT_ARCHITECTURE.md#topology-model). Zones and
  Layers are future scaling systems inside a Shard, never gameplay areas.

## Progression And Roles

Implemented: the durable character level, which gates equipment through item
level requirements. Every character stays at level 1 until experience exists;
a Development tool sets the level for testing. Everything else in this section
is planned.

### Decided

- There are no classes. Everyone has access to the same kinds of actions, and
  the build, made of gear, perks, and talents, decides the play style.
- Tank, damage, and healer roles come from the build, not from a class. Combat
  style (Melee, Ranged, or Magic) and role are separate choices: a tank can be a
  melee or a ranged build. Roles work through [Threat](#threat).
- Each character has one character level. Combat experience from Mobs, quests,
  and completed bosses and dungeons raises it.
- PvP kills give no experience. The reward for PvP is loot. This prevents kill
  trading between friends or between a player's own characters.
- Dying never costs experience. Full loot and the corpse are already the death
  penalty.
- The character level gives no stats by itself. It only unlocks things, and
  power comes from gear and the build. An item can require a character level,
  which stops a new character from equipping top-tier gear at once.
- Each character level grants one talent point. Talents form a large open
  talent library: points go into stats, perks, or other options, with no fixed
  path. Builds can differ completely at similar levels: one character puts its
  points into raw stats while another takes only perks.
- Talents have ranks with escalating cost. Rank 1 costs 1 point, rank 2 costs 2
  points, and so on up to rank 10. A talent at rank 10 costs 55 points in total,
  so every further point in the same talent gives less.
- Talents can be respecced for a fee at a city NPC. The fee grows with the
  number of spent talent points, which makes respecs a currency sink while still
  allowing mistakes to be corrected.
- There is no intended long-term level cap, and each level needs more
  experience than the one before. The first release uses a temporary cap of
  200, set as content, to observe how progression plays out before the cap is
  raised or removed.
- A higher level gives a slight advantage through more talent points, as a
  reward for progress. Because a level gives no direct strength and talent ranks
  get more expensive, a well-played lower-level character, for example level 60,
  can still beat a level 90 character.
- Every profession has its own experience and level, independent of the
  character level and of other professions. The maximum profession level is
  100. Every character can learn and level every profession, with no limit on
  how many.

### Proposed

- Progression mainly adds options, such as new abilities, perks, and tools,
  rather than only larger numbers. Scaling Mob health purely with level creates
  long fights against damage sponges.
- Character levels unlock abilities in the shared ability pool.
- Stat talents and perk talents are balanced against each other so neither is
  the obvious choice. A point in stats gives a modest bonus, which also keeps
  level differences from becoming large raw power gaps in PvP.
- The escalating rank cost applies to every ranked talent, stats and perks
  alike. Some perks may have only one rank.
- Because every profession is open to everyone, interdependence between players
  comes from time and risk rather than restrictions. Reaching level 100 in a
  profession is a large time investment, and the best recipes need rare
  materials from dangerous areas such as the Wilderness and bosses.

## Movement And Camera

Implemented: click-to-move, one walk speed scaled by carry weight, the orbit
camera, and the world menu. The controls and their values are in
[Player Movement](GAME_FEATURES.md#player-movement),
[Orbit Camera](GAME_FEATURES.md#orbit-camera), and
[World Menu](GAME_FEATURES.md#world-menu).

### Decided

- Movement is click to move. It is smooth and continuous, with no visible
  tiles. Routes are planned around obstacles on a fine internal grid sampled
  from the baked collision, which the player never sees or feels. Holding the
  button after a click on the ground steers the walk toward the pointer, so
  the character can be guided without clicking again and again.
- The client plans the route and moves at once. SimulationWorker follows the
  same waypoints with authoritative collision, so prediction matches the
  server. Mobs plan their routes with the same shared planner.
- There is one walking speed, scaled by carry weight. There is no sprint, jump,
  or aim.
- The camera orbits the character freely and zooms, as in RuneScape. The cursor
  is always free.
- Every world action goes through one menu, as in RuneScape. A right click lists
  every option for every target under the cursor, nearest first, then Walk here
  and Examine. A left click runs the top option at once: walk on the ground,
  attack a Mob, talk to an NPC, gather from a resource node, or loot a corpse.
  An option walks into range before it starts, and Examine shows a short
  description from content.
- A character must stop moving before attacking. Auto attacks and most
  abilities require standing still.
- Starting to move cancels channeled and cast-time abilities.
- A small number of abilities are explicitly usable while moving, for example a
  dash or blink, defensive abilities, and selected short instant abilities. This
  creates a constant choice between dealing damage and staying safe, gives
  telegraphed boss attacks real counterplay, and limits kiting so the combat
  triangle holds.

### Proposed

- Running uses run energy that drains faster with higher carried weight, which
  connects directly to the
  [carry-weight rules](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carry-weight-and-encumbrance).
- Buildings hide roofs or occluding walls when the camera would otherwise lose
  the character. Today geometry only pulls the camera in front of walls.
- Examine also shows a Mob's elemental weakness once resistances exist.
- A baked navigation grid replaces the on-demand sampling of the collision if
  larger Worlds need it.

## Combat Model

Implemented: [Auto Attack](#auto-attack),
[Health, Death, And Respawn](#health-death-and-respawn), and
[Equipment Changes](#equipment-changes), with Normal damage only. Everything
else in this section is planned. The implemented behavior and its current
values are in [Combat](GAME_FEATURES.md#combat).

### Auto Attack

#### Decided

- Combat is target based. Selecting a Mob, or another character where player
  versus player is allowed, walks into the weapon's range, stops, and
  attacks on the weapon's interval until the target dies or becomes invalid, or
  the player chooses another action, such as walking elsewhere. Moving between
  attacks is allowed; standing still is the price of every attack. Walking into
  range never clears the target.
- SimulationWorker resolves range, line of sight, attack timing, hits, and
  damage. The client never predicts combat, and shooter-style lag compensation
  is not needed.
- Every attack has a windup that its weapon family sets. The attack starts at
  once and releases its blow, arrow, or bolt when the windup ends, so the body's
  attack animation winds up before the hit. The weapon's interval runs from the
  start, so the windup leaves the damage per second unchanged, and an arrow or
  mana is spent only at the release. Anything that stops an attack before it
  releases, such as a step or a new target, cancels it at no cost, and the next
  attack may start at once. A target that walks on during the windup stays in
  reach within a small margin beyond the weapon's range. A Mob's attack lands at
  once.
- Every attack hits for now. Damage is a uniformly random whole number between
  the weapon's minimum and maximum, plus the ammunition bonus. Misses and
  accuracy arrive with armor and defense.
- Arrows and bolts fly at their weapon family's speed and hit after their
  flight time. An arrow is spent even when its target dies before it arrives.
  Melee blows hit at once.
- There is no unarmed attack. Without a weapon, without arrows of the bow's
  ammunition family, or without enough mana for a staff, the character keeps its
  target, shows why it cannot attack, and attacks again once the cause is gone.

### Health, Death, And Respawn

#### Decided

- Every character has health and mana. Both regenerate slowly and only out of
  combat, and a combatant leaves combat a few seconds after its last attack or
  hit. Mobs do not regenerate; a Mob heals fully when it walks home after a
  fight. The starting values are content, listed in
  [Combat Rules](../content/README.md#combat-rules).
- A character at 0 health dies. It stays dead for a few seconds and then
  respawns with full health and mana at the World spawn. Its items follow the
  [death partition](INVENTORY_AND_DEATH_LOOT_DESIGN.md#player-death-partition),
  and its corpse stays where it fell.
- Every death, of a Mob or a player, produces one authoritative death event that
  records its killer, a Mob or a character, for audit and for future reputation
  rules.
- Mana potions restore mana. How potions, food, and drinking let a character
  recover is in [Magic And Mana](#magic-and-mana) and
  [Combat Cost By Style](#combat-cost-by-style); none of them is implemented
  yet.

### Equipment Changes

#### Decided

- Individual equipment items can be changed freely during combat.
- Every change of the item in an equipment slot holds attacks for about 1
  second, so switching stays possible and meaningful, for example to counter an
  opponent's combat style, while rapid high-APM switching as in Old School
  RuneScape gives no advantage.
- A change refreshes the lockout instead of stacking it: several changes in
  quick succession hold attacks until about 1 second after the last one.
- The lockout applies in and out of combat, so the server needs no separate
  in-combat state for equipment rules.
- The lockout starts whenever a different item, or no item, sits in any
  equipment slot, also when the last arrow of the equipped stack is spent.
  Topping up or splitting the equipped arrow stack keeps the same item and does
  not lock. While an item operation that may change equipment runs, attacks wait
  for its result.
- Movement stays available during the lockout, and so will consumables, so a
  player can still reposition or drink a potion while switching.
- Abilities and spells follow the same lockout once they exist.

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
  a RuneScape 3 style rotation across many ability bars. Decisions come from
  timing, positioning, and reacting to the encounter, not from managing a large
  number of buttons.
- The exact abilities and how many a character can use are decided later.
- Abilities come from one shared pool that every character can access. Players
  choose freely which abilities go on their ability bar.
- Some abilities require a matching equipped weapon, for example a staff or a
  melee weapon. An ability definition names its weapon requirement, if any, and
  whether it is usable while moving; the server rejects every other ability
  while the character moves.
- Each ability has its own cooldown. There is no shared combat resource such as
  RuneScape 3 adrenaline, because a build-and-spend resource drives the
  rotation feel this design avoids. Mana and ammunition are the only combat
  resources.
- A weapon special attack with its own energy, as in Old School RuneScape, may
  be added later if weapons need more identity.

#### Proposed

- Active abilities share one small ability bar.
- Attack abilities generally require a weapon of their combat style: Melee
  abilities need a melee weapon, Ranged abilities a ranged weapon, and Magic
  abilities a staff or another magic weapon. Healing, defensive, movement, and
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
  swap includes the matching ammunition. It is a feature of its own and needs no
  second weapon slot, because the equipment slots are `main_hand` and
  `off_hand`. A loadout swap is one equipment change and has the same short
  attack lockout as changing a single item.

### Defense

Implemented: nothing yet. Armor items such as the leather armor and the Heater
Shield carry no defensive values.

#### Decided

- There is no active defensive mechanic for now: no protection prayers and no
  auras. This can be revisited later.
- Defense comes from gear stats, such as armor.

#### Proposed

- Telegraphed attacks are countered through movement, positioning, and the few
  abilities that are usable while moving, not through a defensive toggle.
- Both elements and combat styles are handled through preparation before a
  fight: gear, potions, and the choice of armor type.

### Threat

Implemented: not yet. Until threat tables exist, a Mob whose target dies or
becomes invalid turns to the attacker that dealt it the most damage, as
[Mobs And Encounters](#mobs-and-encounters) describes.

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
  it stays small and fits the central Mob scheduling.

### Healing

Implemented: only regeneration out of combat. No healing abilities exist yet.

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

Implemented: the five damage types exist in content, and every implemented
weapon and Mob attack deals Normal damage. No damage modifier applies yet.

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

Each element has a recognizable effect and a consistent color used in damage
numbers, visual effects, ground telegraphs, and UI:

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

Implemented: the first two steps.

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

Implemented: the bow family with Bronze Arrows, the sword and axe families for
Melee, the staff family for Magic, the ammunition slot, and per-shot ammunition
consumption. Crossbows, fantasy firearms, more ammunition tiers, and elemental
weapons are planned. The weapons and their values are in
[Combat](GAME_FEATURES.md#combat).

### Decided

- The weapon decides the damage type. Ammunition never changes the element, so
  changing element means changing weapon.
- Most weapons deal Normal damage, especially early in progression.
- Elemental weapons are special items with a built-in damage type that players
  find, loot, or craft. Crafting creates a new weapon whose item definition
  carries the element. It never modifies an existing weapon.
- A weapon's damage type is fixed by its item definition. It can never be
  changed, added, or removed. There is no enchanting or other element
  modification.
- A two-handed weapon, such as a bow, the greataxe, or a staff, keeps the off
  hand empty. A one-handed weapon can be paired with an off-hand item such as a
  shield. The slot rules are in
  [Equipment](INVENTORY_AND_DEATH_LOOT_DESIGN.md#equipment).
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
  large enough to make better ammunition worth using. The bonus is a percentage
  of the weapon's damage roll, not a flat value: a flat bonus would make
  expensive ammunition a huge upgrade on a weak weapon, while a percentage keeps
  it proportional, so top-tier ammunition only pays off with a good weapon.
- The lowest ammunition tier is very cheap. Ranged is the only style that costs
  money per attack, and new players must not be priced out of it.
- A dedicated equipment slot holds the ammunition as one weightless stack, like
  every other equipped item, while ammunition carried in the inventory has
  normal weight. The slot's cap and its death rules are in
  [Equipment](INVENTORY_AND_DEATH_LOOT_DESIGN.md#equipment).

### Proposed

Weapon families and their feel:

| Family | Ammunition | Feel |
| --- | --- | --- |
| Bow | Arrows | Fast, quiet, lower damage per shot |
| Crossbow | Bolts | Slower, stronger, piercing |
| Fantasy firearm | Shot and powder | Slow reload, high damage, loud |

- Ammunition bonuses grow with the tier, for example from 5% to 15%.
- Gear or perks may later grant a chance to save ammunition on a shot. A saved
  unit is simply not consumed. It never becomes a ground item.
- Weapon tiers can be expressed through the culture and craftsmanship behind
  them, for example:
  1. Goblin scrap pistol: cheap and unreliable.
  2. Human flintlock: stable and dependable.
  3. Dwarven rifled gun: precise and heavy.
  4. Rune-etched rifle: magic and powder combined at the highest tier.

## Magic And Mana

Implemented: the Oak Staff with its mana cost, mana regeneration, and the
magic bolt. Mana potions and drinking are planned.

### Decided

- Magic auto attacks cost mana instead of materials. Each magic weapon sets the
  mana of one attack, and a character's mana pool is fixed; both are content.
- Mana regenerates slowly and only out of combat.
- Mana potions restore mana.
- The cost of magic is time and risk: a caster has to stop and drink, and deals
  no damage while doing so.
- The first staff deals Normal damage. Elemental staffs are special weapons.

### Proposed

| | During combat | Between fights |
| --- | --- | --- |
| Source | Mana potion | Drink while resting |
| Effect | Fast, partial restore | Slow, large restore |
| Limit | Shared potion cooldown | Interrupted by attacking or taking damage |
| Cost | More expensive, crafted by Alchemy | Cheap |

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

## Mobs And Encounters

Implemented: the Feral Wolf and the Feral Alpha boss with the Mob rules below,
one attack each, and no abilities. How a Mob is defined, scheduled, and
respawned is in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md), and
the wolves' behavior in [Combat](GAME_FEATURES.md#combat).

### Decided

- A Mob spawns, idles or patrols, detects nearby players, moves toward its
  target, attacks, takes damage, dies, drops loot, and respawns after a delay.
- An aggressive Mob, such as a wolf, attacks a living player it can see within
  its detection range. A defensive Mob fights only once it is damaged. Damage
  always starts a fight.
- A Mob gives up and walks home when it is farther than its leash range from
  where the fight started, or when it neither hit nor was hit for a while. It
  cannot be damaged on the way and arrives with full health, so it cannot be
  pulled away and shot down for free. A patrolling Mob then resumes its patrol.
- Until threat tables exist, a Mob whose target dies or becomes invalid turns
  to the valid attacker that dealt it the most damage, then to the nearest
  player it detects, and otherwise walks home.
- Mob loot is rolled from the Mob's loot table at each death and lies in its
  corpse, where everyone may loot it. Loot feeds the economy, such as the wolf
  pelts that the vendor buys. Normal Mob corpses are short-lived, and a
  boss can keep a durable corpse like a player's; the corpse rules are in
  [Mob Corpse Lifecycle](INVENTORY_AND_DEATH_LOOT_DESIGN.md#mob-corpse-lifecycle).
- The Feral Alpha is the first boss: stronger than a wolf, slower to return, and
  with a durable corpse.
- Player and Mob values are tuned together: the starting weapon values keep a
  duel between equally equipped characters inside the PvP time-to-kill target,
  and an ordinary Mob dies to a few attacks.

### Proposed

- Ordinary Mobs appear in small groups of about 3-5 that are a real threat,
  instead of large crowds that die instantly.
- Mobs have jobs: casters that must be interrupted, healers that must be
  controlled or killed first, patrols that must be timed, and armored Mobs that
  reward the right combat style.
- Mobs of a group assist each other.
- Dungeons vary their pace with rests between pulls, exploration, and possibly
  puzzles. Mana and food recovery naturally create the pull, fight, recover
  rhythm of classic World of Warcraft dungeons.
- Elite Mobs and bosses carry the long fights and the mechanics. Bosses have
  phases and telegraphed attacks that players avoid by moving and positioning.
- Attacks the player is expected to avoid have a windup of at least about
  300-500 ms, so players can react through network latency on a
  server-authoritative simulation.
- Mobs gain an investigate behavior once sound or ability content exists.

## PvP And Crime

### PvP Combat

Implemented: player versus player follows the Shard's rule set, which allows it
on the development Shard; PvP and PvE use the same numbers; the equipment
lockout; and the killer recorded with every death. Movement impairments, their
counters, and the logout risk timer are planned.

#### Decided

- Time to kill is medium. A 1v1 fight between equally geared players lasts
  about 10-20 seconds of sustained damage. Potions and healing can extend it.
- PvP balances two feelings. The attacked player should feel able to escape or
  fight back. The attacker should feel able to catch the target and stop it from
  getting away.
- Movement impairment abilities exist, such as slows and roots, and so do
  counters to them, for example a potion that removes them.
- Skill decides the outcome in both directions. A skilled attacker can kill
  quickly, and a skilled defender can survive for a long time.
- Players can change equipment during a fight at the cost of the
  [equipment lockout](#equipment-changes).
- PvP and PvE use the same numbers. Abilities, weapons, and effects have no
  separate PvP values. PvE balance is adjusted on the Mob side, mainly through
  Mob health and defense.
- Logging out or disconnecting outside a safe city leaves the character in the
  world for a risk timer that combat resets, as
  [Logout And Disconnect Rules](#logout-and-disconnect-rules) describes. Logout
  cannot be used to escape a fight.
- Where player versus player is allowed is a Shard rule today. Gameplay rule
  areas later restrict it to the open risk area, as
  [Gameplay Rule Areas](#gameplay-rule-areas) describes.

#### Proposed

- Player health and damage are tuned first for the PvP time-to-kill target. Mob
  values are then set around the player values.
- Mob definitions can resist or ignore movement impairments, for example bosses
  that cannot be rooted, so impairments tuned for PvP do not trivialize PvE
  encounters.
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

### Hostile Flag

Planned. PvP should be possible, but the game should encourage interesting
social decisions rather than constant kill-on-sight behavior. The direction:

- Players receive a hostile flag when they attack or damage another
  non-criminal player first.
- The flag lasts around 15 minutes.
- Hostile players cannot safely enter larger cities.
- Other players can attack hostile players without penalty.
- Guards may stop or attack hostile players near lawful areas.

### Criminal Reputation

Planned. Criminal status is a reputation with several levels, not a simple
boolean. The levels could be Lawful or Trusted, Neutral, Suspicious, Criminal,
Outlaw, and Notorious or Most Wanted; the names can change. Criminal status
deepens when a player repeatedly commits crimes, such as attacking or killing
neutral or lawful players.

### Criminal Consequences And Recovery

Planned. Consequences can include:

- Some NPCs refuse to trade or help.
- Higher prices and taxes.
- Restricted access to large cities, where guards attack or block entry.
- Reduced or more expensive insurance.
- Bounty hunter gameplay.
- Other players can kill criminals without penalty.
- Main city services become unavailable at higher criminal levels.

Players can reduce criminal status over time or through payment or atonement:
paying fines, completing atonement tasks, waiting out lower-level reputation
damage, turning in contraband or bounty targets, or using special NPCs.

### Criminal Gameplay

Planned. Criminal status is punishing but still playable, a dangerous lifestyle
rather than a dead end. Criminal players get alternate systems: outlaw camps,
black markets, fence NPCs, smuggling quests, illegal vendors, and later
criminal factions. An NPC that turns hostile toward a criminal, such as a city
guard, stays an NPC; disposition changes, not actor kind, as
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md#faction-disposition-and-invulnerability)
describes.

## Gameplay Rule Areas

Not implemented. Rule areas wait until the project has a larger authored map
with meaningful locations and boundaries; the development Worlds are test maps
and define no areas. Until then, player versus player follows the Shard's rule
set everywhere, NPC services and resource nodes work wherever they are placed,
and the death rules are the same everywhere.

### Decided

- Gameplay rule areas are authored content policies inside the World. They are
  not the future topological Zone and must never be represented as Zones,
  SimulationAssignments, or worker ownership boundaries.
- The first version keeps two areas: the Safe City Rule Area and the Open Risk
  Rule Area. Everything outside the safe city is unsafe. Civilized and
  wilderness areas can be split into separate policies later.

The **Safe City Rule Area** has:

- No PvP.
- Instant logout.
- NPC vendor, bank, Recovery Storage, and insurance NPC access.
- Equipment and inventory management.
- Later crafting stations, quest NPCs, and social services.

The **Open Risk Rule Area** has:

- PvP.
- Simple Mobs.
- Gathering and loot containers.
- Management of carried inventory and eligible Secure Container items, under
  the server's action and policy rules.
- Durable corpse custody at death under the current protection rules.
- A risk timer that keeps the character in the world after logout.

### Direction

The world is divided into legal and safety areas rather than only PvP-on and
PvP-off areas:

- **Cities** are safe hubs: no PvP, quest givers, the bank, crafting stations,
  the marketplace, profession trainers and vendors, social areas, guards, and
  later guild and player services. Large cities restrict or deny access to
  heavily criminal players.
- **Civilized areas** are safer open-world areas outside the cities, with
  regular quests, professions, gathering, Mobs, and medium-tier progression.
  PvP may be technically possible there, but attacking or killing a player
  causes severe criminal penalties, so killing someone needs a strong reason.
- **The Wilderness** is a permanent high-risk area with full PvP, full loot on
  death with the usual exceptions, rare resources, dangerous Mobs, high-value
  loot, criminal activity, outlaw camps and black markets, and later world
  bosses or other contested content. It also gives players reasons to
  cooperate, not only to kill on sight.

## Logout And Disconnect Rules

Not implemented. Today a leave or a disconnect removes the character from the
world at once, so a player can still escape a fight by disconnecting; only the
release of the session waits until its spent arrows and a pending death have
committed. The server already tracks when each character was last in combat,
which the risk timer will use.

### Decided

Logout behavior depends on the character's current gameplay rule area:

| Rule area | The character's body stays in the Shard after logout or disconnect |
| --- | --- |
| Safe City | No, logout is instant |
| Open Risk | 5 minutes |
| Civilized, later | About 30 seconds |
| Wilderness, later | About 5 minutes |

- A player who reconnects while the body is still in the world resumes control
  of that same body.
- Combat resets or extends the timer, so players cannot use logout or a
  disconnect to avoid danger.

## Professions

Implemented: Mining and Woodcutting gathering without experience or levels. No
other profession exists yet. Profession definitions, recipes, crafting
stations, and resource nodes for the other gathering professions are planned.

### Decided

Gathering professions:

| Profession | Produces |
| --- | --- |
| Mining | Ore, stone, and gems |
| Woodcutting | Wood for bows, crossbows, and staffs |
| Herbalism | Herbs for Alchemy |
| Hunting | Hides, meat, and bones, including skinning |
| Fishing | Fish for Cooking |

Crafting professions:

| Profession | Produces |
| --- | --- |
| Smithing | Metal weapons, heavy armor for Melee, and arrowheads |
| Leatherworking | Light armor for Ranged |
| Tailoring | Cloth armor for Magic |
| Fletching | Bows, crossbows, arrows, and bolts |
| Tinkering | Fantasy firearms, shot and powder, and gadgets such as bombs |
| Artificing | Staffs, other magic weapons, and relics |
| Alchemy | Healing, mana, resistance, and cleanse potions |
| Cooking | Food |

- Profession levels and experience are defined in
  [Progression And Roles](#progression-and-roles).
- Each armor type in the combat triangle has its own crafting profession:
  Smithing for heavy armor, Leatherworking for light armor, and Tailoring for
  cloth.
- Elemental weapons are crafted in the normal weapon profession and require
  rare elemental materials, for example fire essence from fire Mobs.
- Tinkering belongs to the technology cultures, such as goblins, dwarves, and
  gnomes.
- Trading is a playstyle, not a profession with experience.
- There is no Salvaging for now. Breaking items down into materials can be
  added later, either as its own profession or as part of the crafting
  professions.

### Gathering

#### Decided

- Gathering requires a tool of the resource node's tool kind in the Tool
  equipment slot, such as a pickaxe for an Iron Vein or a Woodcutting Axe for an
  Oak Tree. A better tool later needs content only.
- One click gathers continuously: one guaranteed yield every cycle until the
  node depletes, carried storage is full, the weight cap is reached, the tool is
  unequipped, or the player moves or closes the interaction.
- Resource nodes are shared by every player on the Shard. A node holds a few
  charges, depletes for everyone when the last is taken, and returns after a
  respawn delay. Everyone gathering it stops when it depletes.
- Node charges are live Shard state, so a server restart refills every node; a
  durable node state can become an opt-in later, as for boss corpses.
- A yield goes into carried storage by the
  [carried acquisition](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carried-acquisition)
  rule, and gathered resources can be sold to an NPC vendor.
- There is no gathering success chance and no profession experience yet; both
  belong to profession progression.

The resource node rules of the actor model are in
[Resource Nodes And Gathering](NPC_AND_MOB_SYSTEM_DESIGN.md#resource-nodes-and-gathering).

### Proposed

- Healing gear is mainly made by Tailoring and Artificing.
- Farming may be added later.

### Interdependence

Professions create real interdependence between players. Gatherers bring rare
materials from dangerous places, crafters turn them into weapons, armor,
potions, and food, traders move goods between cities, criminals smuggle or rob,
bounty hunters hunt criminals, and guilds may control resource areas.

## Economy

Implemented: one currency, Gold, with a starting balance, and one NPC vendor
with fixed prices. The currency and vendor rules are in
[Character Currency](INVENTORY_AND_DEATH_LOOT_DESIGN.md#character-currency) and
[Vendor Trades](INVENTORY_AND_DEATH_LOOT_DESIGN.md#vendor-trades).

### Decided

- The economy starts simple and NPC driven, with vendors and simple crafting,
  and becomes player driven later.
- There is one currency, a balance on the character instead of an item. It
  stays with the character on death and is global across every Shard.
- Vendors sell at an item's full value and buy at a smaller share of it, so
  buying and selling back never creates currency.
- Currency leaves the economy through vendor purchases and insurance, and later
  through talent respec fees. Consumed ammunition, potions, and food keep a
  steady demand for crafted goods.
- An auction house, direct player trading, regional markets, and advanced
  crafting wait until the core item and economy transactions are stable.

## Account And Character Rules

Implemented, except character deletion and changing a look in the game.

- Accounts support multiple characters from the beginning. The MVP allows at
  most 5 characters per account, a GameService setting.
- Character data is persistent.
- Each character owns its inventory, equipment, Bag custody, bank, Secure
  Container contents, Recovery Storage, currency, profession progress, quest
  state, and reputation state.
- The account owns the entitlement that selects the Secure Container name, tier,
  and slot capacity for its characters.
- Bank contents and capacity are per character, not account-wide.
- An account has one active client session, and one account and one character
  have at most one active simulation session at a time across every Fleet and
  Shard.
- Shard switching is allowed only after the character has fully left its
  current simulation session.
- Character creation chooses a body model and a look. Hair can later be changed
  in the game, such as at a barber; the rules are in
  [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server).
- A new character starts with the starting currency balance.

## UI And UX Principles

### Proposed

RuneScape 3 shows what happens when interfaces accumulate over many years:
legacy and new layouts mixed, too many windows, too many abilities, and small
icons. This project sets rules from the start:

- Cap the number of active abilities. New abilities replace old ones on the bar
  instead of adding more bars.
- Show danger in the world, not in the interface: ground markers and clear
  animations instead of meters and popups.
- Use a minimal HUD by default and show more only when it is needed.
- Keep a fixed layout where each element always lives in the same place.
- Put consumables on dedicated hotkeys instead of clicking them in the
  inventory.

The current UI is explicitly temporary, so the final UI can follow these rules
without migration. The inventory layout rules are in
[Inventory UI Layout](INVENTORY_AND_DEATH_LOOT_DESIGN.md#inventory-ui-layout).

## MVP

The MVP is a working agreement, not a permanent design lock. It keeps the first
build focused enough to become playable without losing the long-term identity,
and it avoids becoming a large feature collection before its loop works.

### Core Loop

The MVP proves this loop:

1. The player creates or selects a character.
2. The player selects and joins a Shard running the shared World content.
3. The player spawns in a safe city.
4. The player prepares inventory, equipment, Bag, Secure Container, and bank.
5. The player leaves the city into an unsafe open-world area.
6. The player gathers or loots a resource.
7. The player sells it to a simple NPC vendor or keeps it for later crafting.
8. The player fights a simple Mob or another player.
9. A player death applies transactional protection and corpse-loot rules.
10. The player returns to a city to claim Recovery Storage, bank, sell, equip,
    and prepare again.

Every step works today in the development Worlds, except that the safe city and
the unsafe area are not yet rule areas.

### MVP Slices

The MVP is built in named slices:

| Slice | Status | Scope |
| --- | --- | --- |
| Project foundation | Complete | Repository structure, backend solution, local infrastructure, configuration, and canonical topology |
| Account, character, and Shard join | Complete | Registration, login, characters, placement, exact-runtime tickets, and one active session per character |
| Unity connection and movement | Complete | Unity API and UDP flow, entity lifecycle, spatial interest, click-to-move with client-planned paths, prediction, reconciliation, interpolation, and checksummed collision |
| SimulationWorker performance baseline | Complete | Headless stress clients, active bots, visibility and packet reuse, bounded backpressure, and repeatable baselines |
| Items, inventory, equipment, and carry weight | Complete | The slot inventory, equipment with the ammunition slot, Bags, the Bank, the Secure Container, Recovery Storage, carry weight and encumbrance, item policies, and insurance, as [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) decides them, with the temporary Unity inventory UI |
| Vendor and gathering | Complete | One NPC vendor, persistent Gold, shared resource nodes gathered with a tool in the Tool slot, gathering through the item transaction service, and buying from and selling to the vendor |
| Target-based combat and simple Mob | Complete | Ranged, Melee, and Magic weapons with consumed ammunition or mana, target selection and auto attack, health and Normal damage, authoritative death events, aggressive Mobs with live loot, and a boss with a durable corpse |
| Death, Recovery, and corpse looting | Complete, with a temporary corpse loot interface | The death partition, Recovery Storage, durable and live corpses, concurrent looting, Bag swaps, Loot all, insurance, and the combat death producer |
| Gameplay rule areas and presence lifecycle | Deferred until a larger authored map exists | Content-authored Safe City and Open Risk rule areas without Zones or Layers, instant logout in the Safe City, a five-minute body after a disconnect in the Open Risk area, and reconnect to the same body |

## Deferred Features

These do not block the first playable MVP:

- The civilized and wilderness rule-area split.
- Topological Zones, cross-zone handoff, and Layers.
- The hostile flag, criminal reputation, and bounty hunting.
- Guilds and social systems: chat, friends, party and guild chat, private
  messages, presence, cross-Shard communication, and basic moderation. They
  live outside the Shard simulation, so they stay online when a simulation
  process restarts.
- An auction house, direct player trading, and regional markets.
- Crafting and professions beyond the first gathering loop.
- Advanced Mobs and bosses beyond the first boss.
- Complex quests.
- Insured stack quantities.
- Armor types and armor stats.

## Open Questions

- Should character deletion be available in the MVP?
- Should Shard switching have a cooldown after logout?
- Which abilities exist, how many fit on the ability bar, and which of them are
  usable while moving?
- Which races, if any, are added later, and do they affect access to cultural
  technology?
- What are the names and thresholds of the criminal reputation levels?
- Does gathering get a success chance per cycle once profession levels exist?
- The item balance questions, such as Secure Container eligibility, Bank
  unlocks, and Recovery Storage expiry, are in the
  [open questions of the inventory design](INVENTORY_AND_DEATH_LOOT_DESIGN.md#open-questions).
