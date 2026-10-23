# Equipment Visuals Design

Last updated: 2026-10-23

Status: accepted by Victor on 2026-10-22, with the support of future races added.
It is implemented in the order of [Delivery Plan](#delivery-plan); steps 1 to 3,
this document, the cloth chains with their solver, and the garment kind with its
fitting, weights, bands, masks, review, export, and Unity import, are done, with
the leather jerkin as the first garment. Of step 4, putting a character together
with its garments and trying them on in F2 is done.

This document is the source of truth for how clothes, armor, hair, and carried
equipment look on the player bodies: what they are made of, how they fit every
body type, how they hide the body and each other, how loose cloth moves, how the
art pipeline makes and checks them, and how Unity puts a character together.
The art pipeline itself is in [Art Pipeline](ART_PIPELINE.md), the human bodies
in [Human Base Characters](ART_PIPELINE.md#human-base-characters), and the
equipment slots in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md#equipment).

## Goals

- Equipment looks like objects with volume, not paint on a body: a shirt does not
  show the abdominal muscles, trousers do not show the buttock cleft, and boots
  do not show toes.
- Skin shows wherever a garment leaves it open, with everything painted on the
  skin: a crop top shows the belly, a net shirt or a torn sleeve shows the skin
  through its holes, and a tattoo or scar under them shows too.
- Garments over garments never cut through each other, and a garment never
  shows the body through itself, standing, walking, jogging, or dying.
- Long robes, coats, skirts, cloaks, and hoods follow the legs and the body and
  sway, in every clip, without anyone animating the cloth by hand.
- One design fits every body type: a garment is made once and fitted to the
  others by the pipeline, with handmade corrections kept. This holds for future
  races too, such as taller elves and shorter, broader dwarves.
- A crowd stays cheap: a character with a full outfit draws with few draw calls
  and costs little CPU per frame.
- Every garment goes through the same review, approval, and export as all other
  art, with automatic checks that catch clipping before a reviewer sees it.

## Decisions

| # | Decision | Choice |
| --- | --- | --- |
| E1 | What clothes and armor are made of | A 3D mesh per slot with its own volume and folds, skinned to the shared skeleton, as in The Elder Scrolls Online, Guild Wars 2, and New World. Paint over the skin is kept for what lies on the skin: tattoos, scars, makeup, and dirt. |
| E2 | How a garment hides the body | A cover mask per garment and body type over the body's faces, computed by the pipeline from where the garment really covers the body, reading the garment's transparency. Not whole body zones. |
| E3 | How loose cloth moves | Standard cloth chains in the skeleton, driven at runtime by a solver that follows the legs, adds springs, and collides with the body. Real cloth simulation may be added for the own character later, on the same chains. |
| E4 | Hair under headwear | Every hair has a compressed under-headwear form; headwear says whether hair is hidden, compressed, or shown. |
| E5 | Transparent fabric | Sharp holes now: lace, net, holes, and torn cloth with alpha clipping. Soft see-through fabric such as veils later. |
| E6 | Body variety | Fixed body types, the male and female humans now and other races later as bodies of their own with the same topology. No body sliders. |
| E7 | Dyes | Prepared from the start: every garment can carry dye channels that tint its regions. |
| E8 | Future races | Every playable humanoid race, such as elves and dwarves, is a fixed body type per sex with the human base topology, its own proportions, and its own rig. Garments, paint, masks, cloth chains, and clips work for it without changes to the system; see [Future Races](#future-races). |

## Layers Of A Character

From the inside out:

1. **Body**: the skinned body with its skin, face, and eye materials and the
   [paint layers](ART_PIPELINE.md#paint-layers) on the skin. Body faces that a
   worn garment covers are removed when the character is put together.
2. **Garments**: meshes in visual slots, ordered by layer. Each hides what it
   covers of the body and of the garments below it.
3. **Hair**: a mesh per hairstyle, in its normal or compressed form.
4. **Attachments**: rigid objects on the skeleton's sockets: weapons and shields
   on `Socket_MainHand` and `Socket_OffHand`, and a quiver or a backpack on
   `Socket_Back`.

Paint layers stay for skin-level detail. The leather jerkin, painted today as a
proof of paint layers, becomes a garment in the proof set, and its paint layer
is then removed.

## Garments

### Slots And Layers

A garment has one visual slot and a layer, a number from 0 to 1000 that orders
the garments of a character from the inside out.

| Visual slot | Covers | Typical layers |
| --- | --- | --- |
| `head` | hoods, hats, helmets, masks | 400 |
| `body` | shirts, tops, jerkins, chest armor, robes, coats | 100 shirt, 200 jerkin or chest armor, 300 robe or coat |
| `legs` | trousers, skirts, leggings | 100 |
| `hands` | gloves, gauntlets | 150 |
| `feet` | shoes, boots | 120 |
| `back` | cloaks, capes | 500 |

An item names the garments it shows, so one item may show several garments,
such as a robe with a hood. A garment may also hide whole slots below it, such
as a floor-length robe that hides the `legs` slot. The item equipment slots
`head`, `body`, `legs`, `hands`, and `feet` map to the visual slots of the same
name. A cloak needs a `back` equipment slot, which the catalog does not have yet;
it comes with the first cloak item. `neck`, `ring_1`, and `ring_2` show nothing,
and `main_hand`, `off_hand`, and `bag` show attachments.

A visual slot holds every worn garment of that slot, ordered by layer, and no two
of them share a layer. One equipment slot holds one item, so the garments of a
visual slot come from the items that show them: one item can be several garments
of a slot, such as a brigand's outfit made of a shirt at 100 and a vest at 200,
and garments of other items join them, such as a robe's hood in `head` under a
helmet from the `head` item. Sleeves are part of a `body` garment; gloves sit
over them (layer 150 over a shirt at 100) or under the sleeves of a coat (300).

### Cover Masks

The human bodies have identical topology, so every body face has the same index
on every body type: the body's faces are a coordinate system that every garment
can refer to. For every body type a garment fits, the pipeline computes:

- **The cover mask**: one bit per body face, set when the garment covers the face.
  From sample points on the face, rays go outward in a cone around the face
  normal up to the garment's reach. A sample is covered when every ray hits the
  garment where its texture is opaque, or the body itself, as in an armpit; a
  face is covered when all its samples are and at least one ray hits the
  garment. A ray that leaves through a hole, a crop top's open belly, or a
  transparent part of a lace texture leaves the face visible, with its skin and
  everything painted on it.
- **In motion**, the mask also hides the visible skin under the garment that
  comes out of it in the test poses and the clips. Skinning blends the bones of
  a fold, such as an armpit, so the gap between skin and cloth closes there in
  motion whatever the weights, and the skin there would show through. The hidden
  faces lie under the garment; a look into an opening sees the garment's inside.
- **The underlay**: for every face of the garment, the index of the body face
  under it, the closest body face along the garment's inward normal.
- **The pull-in**: for every body vertex that stays visible but lies closer to
  the garment than its band allows, such as the skin right at a tight collar or a
  sleeve hem, how far to move it inward, at most 3 mm, so it cannot poke through
  the hem in motion.

A mask is 545 bytes for the human bodies (4,358 faces). When Unity puts a
character together, it removes every body face that a worn garment covers, moves
the pull-in vertices, and removes every garment face whose underlay face is
covered by a garment of a higher layer. A vest over a shirt thereby hides the
shirt's torso but keeps its sleeves, and a robe over trousers hides the
trousers where the robe is.

### Layer Bands

Every garment declares a band: how far from the skin its surface lies. The
pipeline checks every garment vertex against its band on every body type.

| Band | Distance from the skin | For |
| --- | --- | --- |
| `tight` | 0 to 8 mm | leggings, a shirt, gloves |
| `mid` | 5 to 25 mm | a jerkin, trousers, boots, padded armor |
| `outer` | 10 to 60 mm | plate armor, a coat's upper body |
| `loose` | from 10 mm, no limit | skirts, robes, cloaks, hoods |

A garment of a higher layer lies in an outer band, so an inner garment never
reaches through an outer one where the outer one does not hide it.

### Transparent Fabric

A garment texture may have an alpha channel. The garment shader clips at alpha
0.5, which draws lace, net, holes, and torn edges sharp and without sorting
problems, and the cover mask reads the same alpha. Soft see-through fabric, such
as a veil, needs dithered transparency and comes when a garment needs it.

### Dye Channels

A garment may carry a dye mask, an image whose red, green, and blue channels mark
up to three regions that a dye color tints, such as cloth, trim, and leather. The
undyed garment shows its painted colors. Which colors an item has, per definition
or per item, is decided with the items in delivery step 6.

## Fitting Every Body Type

A garment is made once, on the first body it fits, the male human. The pipeline
fits it to every other body type of the same topology, also those of
[future races](#future-races):

- **Shape**: every garment vertex is bound to the closest point of the first
  body's surface, as a face, barycentric coordinates, and an offset along the
  surface normal and tangents, and placed at the same point and offset on the
  other body. A vertex or a face centre that then leaves the garment's band,
  because the other body curves differently, moves back into it. Garments stay
  out of the body's armpits and crotch by the same rule as they do on the first
  body.
- **Corrections**: where the fitted shape needs a hand, a person sculpts it on the
  other body's version. The pipeline keeps the correction as a difference to the
  fitted shape, so the next fit after a change of the first version keeps it.
- **Weights**: a garment vertex takes the skin weights of the body at its bound
  point, not smoothed, so it moves like the skin it covers; measured on the
  jerkin, smoothed weights let the skin under a sleeve come through further, 20
  instead of 7 mm. Loose parts take the cloth chains instead. Weights painted by
  hand on the first version are kept, and the other versions copy them, because
  every version of a garment has the same topology.

## Future Races

The system treats a race as more body types, so adding elves or dwarves adds
bodies, not code:

- **One topology for every humanoid race.** A new race starts as a copy of a
  human body and is reshaped in Blender, taller and slender for an elf, shorter
  and broader for a dwarf, longer ears and all, without adding or removing
  vertices. Body faces keep their indices, so paint layers, cover masks,
  underlays, and garment fitting work for it like for the humans, and the checks
  hold every body of the topology to it. What a shape cannot give, such as tusks
  or horns, is a separate mesh in the body's file, outside the shared topology.
- **Its own rig.** The rig places the same `Humanoid` bones from the race's own
  model, so a dwarf's legs and arms are shorter and an elf's longer, and the cloth
  chains are placed from the model too: a dwarf's skirt chains end at a dwarf's
  ankles.
- **Clips.** The library clips play on every race through Unity's Humanoid
  retargeting, which scales them to the body. The walk and jog play at a rate
  from the race's own stride length, so the feet stay planted on short and long
  legs. A race that should move differently, such as a heavier dwarf gait, gets
  clips of its own in a library keyed on its rig, chosen per body type by an
  override of the controller.
- **Garments.** A garment's `bodies` list grows with the new body types. Each new
  version is fitted from the body type closest in shape, a dwarf from a human,
  which keeps the corrections small; a version can be fitted from another fitted
  version, and a version that needs a different shape altogether can be modeled
  by hand and keeps its own weights. Masks and the clipping checks run per body
  type, so a garment is exported for a race only when it passes on that race.
- **Race features.** Headwear says per body type whether ears show or are
  covered, so an elf's hood is corrected around long ears or lets them through.
  Hair, such as a dwarf's beard, is a hair garment like any other.
- **Budgets and the game** are the same for every race. Equipment shows on every
  body type that has a version of its garments; the tools report a garment that
  lacks a version for a playable body type.

## Cloth Chains

### Skeleton

The `Humanoid` skeleton gets standard chains of bones for loose cloth. They are
not Unity Humanoid bones, so Humanoid clips never move them, and the body is not
weighted to them.

| Chains | Parent | Bones | For |
| --- | --- | --- | --- |
| `SkirtFront1` to `4`, `SkirtFrontSide1_L` to `4_L` and `_R`, `SkirtSide`, `SkirtBackSide`, and `SkirtBack`: eight chains around the hips | `Hips` | 32 | skirts, robes, coat tails, tabards |
| `CloakCenter1` to `5`, `CloakInner1_L` to `5_L` and `_R`, and `CloakOuter`: five columns across the back | `UpperChest` | 25 | cloaks and capes |
| `Hood1`, `Hood2` | `Head` | 2 | the back and tip of a hood |

They are implemented: [Skeletons](ART_PIPELINE.md#skeletons) lists them, and
`ClothChainSolver` moves them; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies). The rig
places them from each body: the skirt chains on a ring at the hips that
hangs to that body's ankles, the cloak chains from its shoulders down its back,
the hood chain behind its head. The solver's profiles give lengths as shares of
the chains, so the same cloth behaves alike on a dwarf and an elf. Adding them changes the rig, so the bodies and the
animation libraries are approved again once; their clips do not change.

### Runtime Solver

A solver in the Unity client moves the chains every frame, after the Animator and
before the bodies are drawn:

- **Follow**: every skirt chain has a target from the legs: the front chains
  follow the leg in front, the side chains the leg on their side, the back
  chains the leg behind, blended by their angle around the hips. Cloak and hood
  chains hang from their parent.
- **Springs**: every bone springs toward its target with stiffness, damping, and
  gravity, so cloth lags behind a turn, swings when a character stops, and
  settles.
- **Collision**: capsules on the thighs, shins, hips, and spine push the chains
  out of the body; a cloak also clears a backpack or quiver on `Socket_Back`.
- **Profiles**: a garment names a cloth profile, such as `robe`, `coat`, `cloak`,
  or `hood`, with its stiffness, damping, gravity, and chains; the solver reads
  its values.
- **Level of detail**: near the camera the springs run; farther away the chains
  only follow their targets; far away they rest. A clip may also key chain bones
  for a special motion, and the solver blends toward the clip.

The solver is presentation only. It runs the same for the local player, other
players, and bots, and never reaches the server.

## Hair And Headwear

Hair is a garment of its own visual slot, `hair`, with a shape key
`UnderHeadwear`, the hair pressed flat to fit under hoods and helmets, made by
hand like the hair itself. Headwear says what happens to the hair: `hide` for a
closed helmet, `compress` for a hood or a hat, so the fringe and the neck hair
show under it, and `show` for a circlet. Headwear covers the scalp like any
garment covers the body. A hood that can be up or down is two garments of the
same item.

## Attachments

Weapons, shields, quivers, and backpacks are static models on the skeleton's
sockets. A static model's root object `Grip` marks where it is held, and the
socket's position and turn on each body type place it. They follow the hands and
the back without weights.

## Putting A Character Together In Unity

A character's look is its body type, its face, eyes, and paint, the garments per
visual slot with their dye colors, its hair, and its attachments. When the look
changes, such as when equipment changes, the client puts the character together:

1. The visible body faces: every face but those the worn garments cover. The
   pull-in vertices move inward.
2. The visible garment faces: every face but those whose underlay a garment of a
   higher layer covers, and every slot a worn garment hides is left out.
3. Hair in its normal or compressed form.
4. One combined skinned mesh on the character's skeleton, the cloth chains with
   it, with one submesh per material. Materials are shared copies keyed by their
   source, dye colors, and paint layers, so characters that look alike batch.
5. The combined mesh is cached by the look's key, so every character with the
   same look shares one mesh, as development bots do.

Putting a character together happens on equipment changes, never per frame. If a
crowd needs fewer draw calls than one per material, garment textures move into
texture arrays that one garment material reads.

Unity imports a model's triangles in an order of its own, so the body faces are
found again in Unity by position: every body exports its editable mesh next to
its model, and the appearance catalog maps every triangle of the body's parts and
of each garment to the face it comes from when it is written, in the Editor; see
[Art Pipeline](ART_PIPELINE.md#unity-import-rules). Hair in its compressed form
comes with the first hair garment, which brings the shape key.

## Art Pipeline

### The Garment Kind

A garment is an art asset of kind `garment` below `art/Garments/<Name>`:

| Field | Meaning |
| --- | --- |
| `slot` | The visual slot: `head`, `body`, `legs`, `hands`, `feet`, `back`, or `hair`. |
| `layer` | 0 to 1000, the order from the inside out. |
| `band` | `tight`, `mid`, `outer`, or `loose`. |
| `bodies` | The skinned assets it fits, the one it is made on first. |
| `hides` | Visual slots it hides, such as `["legs"]` for a floor-length robe. |
| `hair` | Headwear only: `hide`, `compress`, or `show`. |
| `cloth` | The cloth profile of its loose parts, or none. |
| `dyes` | The number of dye channels, 0 to 3. It stays 0 until the garment shader brings dyes in step 4. |
| `triangleBudget` | The most triangles of one body type's version. |

Its file holds, per body type, a collection `Export_<Label>` with the body's
armature and the garment's version on it: the version on the first body is the
garment a person makes, and every other version is fitted from it with the
person's corrections kept. The bodies themselves are in a `Reference`
collection. Export writes one FBX per body type, the garment skinned to that
body's rig, with its textures and a data file with the cover mask, underlay, and
pull-in, all recorded with their hashes like every export.

### Making A Garment

1. `New-ArtAsset.ps1 -Kind garment` creates the asset with the bodies and a
   starting shell in its band, from the body parts of its slot or from a paint
   layer.
2. A person models the garment on the male body in Blender and paints its
   textures and dye mask.
3. `Sync-GarmentAsset.ps1` fits it to the other bodies, transfers the weights,
   computes the masks, and runs the checks.
4. The reviewer looks at the review sheet and the garment in the Wardrobe
   preview in Unity, then approves the `model` and `rig` stages, and the export
   makes it ready for items.

### Checks

The sync stops on a broken rule and reports the rest:

- Every version has the garment's topology and stays within its triangle budget.
- Every vertex and every face centre lies within its band on every body type, so
  a flat face over a round shoulder cannot dip under the skin.
- In the rig review's test poses and at every frame of `Idle`, `Walk`, `Jog`,
  and `Death`, no visible body vertex comes out of the garment where it is under
  it: such a vertex has its faces hidden for motion, and the report names how
  many and the deepest place. No garment of a lower layer reaches through a
  higher one; this is checked when a character wears garments together, in
  step 4.
- The faces that only the cone of rays leaves visible are the garment's
  openings, and the review sheet shows the mask, so a mask that is too small
  shows there; a check that the faces under opaque cloth are covered would only
  repeat the rays.
- Only a garment whose texture has an alpha channel is clipped.

### Review

The review sheet shows the garment on every body type from four sides, the
garment in the test poses, and the body without it, the faces covered at rest
orange, those hidden for motion red, and the pulled-in vertices blue. Cloth motion is reviewed in the Wardrobe preview, a
Unity Editor window that dresses a body in chosen garments, plays a clip on a
moving ground, and runs the cloth solver.

## Budgets

| Slot | Triangles per body type |
| --- | --- |
| `head` | 1,500 |
| `body` | 3,000; a robe or coat 4,000 |
| `legs` | 2,000 |
| `hands` | 800 |
| `feet` | 1,000 |
| `back` | 1,500 |
| `hair` | 2,500 |

A character in a full outfit draws about 15,000 to 18,000 triangles near the
camera, because the covered body faces are gone. Levels of detail at half and a
quarter of the triangles come with the first crowd measurement that needs them.

## Delivery Plan

1. This document.
2. Cloth chains: the chains in `Humanoid.json`, the rig placing them, the bodies
   and libraries approved again, and the runtime solver with tests on a test
   garment made in code.
3. The garment kind: `New-ArtAsset.ps1 -Kind garment`, `Sync-GarmentAsset.ps1`
   with fitting, weights, masks, pull-in, bands, and the clipping report, the
   review sheet, the export, the Unity import rules, and their tests. Done; the
   leather jerkin is its first garment, still the shell of its paint layer.
4. Putting a character together in Unity: masks, garment layers, pull-in, the
   garment shader with clipping and dyes, the Wardrobe preview, and trying
   garments on in F2. Putting the character together and trying garments on are
   done; the garment shader and the Wardrobe preview come next.
5. The proof set, each approved by Victor: the leather jerkin as a garment with
   its collar, trousers, boots, a garment that shows skin (a crop top or a net
   shirt) over a tattoo paint layer, a robe with a hood, a cloak, and one hair
   with its compressed form.
6. Items: items show their garments, the `back` slot, dye colors on items, and
   the look sent to every client with the body type.
7. With the first new race: making a body type from a human body, generalizing
   `Sync-HumanBase.ps1` to every body of the topology, and fitting the garments to
   it.

## Open Questions

- Whether a hood is toggled up and down by the player, and where.
- Whether dye colors belong to the item definition or to each item.
- Whether helmets and masks are rigid attachments on the head or skinned
  garments; this design treats them as garments.
