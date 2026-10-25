# Equipment Visuals Design

Last updated: 2026-10-25

Status: accepted by Victor on 2026-10-22, with the support of future races added.
Extended on 2026-10-23, accepted by Victor, with the garment cage, garment
templates, and garment textures, so that clothes keep a shape of their own and
everything adapts to new body types and a replaced base model. It is
implemented in the order of [Delivery Plan](#delivery-plan); steps 1 to 5 are
done: this document; the cloth chains with their solver; the garment
kind with its fitting, weights, bands, masks, review, export, and Unity import;
putting a character together with its garments, with the garment shader, the
Wardrobe preview, and trying garments on in F2; the garment cage with its
standard lines, approved by Victor on 2026-10-23; and templates built from
recipes on the cage with textures of their own, with the template `Jerkin` and
its texture `LeatherJerkin`, approved by Victor on 2026-10-23, in place of the
first leather jerkin. Step 5c's first step adds `Shirt`, `LongShirt`, and
`Trousers` with their textures, the tattoo, and the pair check; `Jerkin` had
become the same form as `Shirt` once both lay as close to the body as they can,
so `LeatherJerkin` is a texture of `Shirt` and `Jerkin` is removed. Its second
step adds the cage's regions `hands` and `feet`, the templates `Boots` and
`Gloves` with five fingers, and their leather textures, approved by Victor on
2026-10-24. Its third step adds loose parts, which hang on the cloth chains,
with the templates `Robe` and `Cloak`, their wool textures, and cloak chains
that hang to the middle of the calves. Its fourth step adds the cage's region
`head`, the hairstyle `ShoulderHair` with its form under headwear and its
texture `DarkBrownHair`, the robe's hood up and down, `HoodUp` and `HoodDown`,
with the wool textures `WoolHoodUp` and `WoolHoodDown`, and the helmet
`NasalHelm` with its texture `IronNasalHelm`, approved by Victor on 2026-10-24.
Step 5d makes paint layers independent of the bodies' topology, as Victor decided
on 2026-10-24: each layer keeps its own canvas, a copy of the body it is painted
on, and every body gets the paint by projection, through the bodies' new paint UV
map, which lays the two sides of a mirrored body apart, so paint can be one-sided,
as the new tattoo `ForearmRunes` on the left forearm is. Step 6, as Victor decided
on 2026-10-25, makes items show their garments: its first part gives the items
their garments and dye colors, the `back` slot, dyeable wool, and the local
player dressed by its equipment, and its second stores the body model and the
look with the character and sends them to every client with the visible
equipment and the lowered hood; see [Items](#items).

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
- Clothes keep the figure but not the body's small details: a top follows the
  waist and the small of the back a little, so the body's shape shows, and spans
  the buttock cleft, the spine groove, the navel, and the muscles, so it never
  looks plastered to the skin like a wet shirt.
- Skin shows wherever a garment leaves it open, with everything painted on the
  skin: a crop top shows the belly, a net shirt or a torn sleeve shows the skin
  through its holes, and a tattoo or scar under them shows too.
- Garments over garments never cut through each other, and a garment never
  shows the body through itself, standing, walking, jogging, or dying.
- Long robes, coats, skirts, cloaks, and hoods follow the legs and the body and
  sway, in every clip, without anyone animating the cloth by hand.
- One design fits every body type: a garment is made once and fitted to the
  others by the pipeline, with handmade corrections kept. This holds for future
  races too, such as taller elves and shorter, broader dwarves, and for a base
  model that replaces today's human bodies, with another topology.
- New clothes cost a texture, not a model: standard garment templates per kind,
  such as a short-sleeved top, a long-sleeved shirt, or trousers, end at
  standard lines of the body and are reused with new textures.
- A crowd stays cheap: a character with a full outfit draws with few draw calls
  and costs little CPU per frame.
- Every garment goes through the same review, approval, and export as all other
  art, with automatic checks that catch clipping before a reviewer sees it.

## Decisions

| # | Decision | Choice |
| --- | --- | --- |
| E1 | What clothes and armor are made of | A 3D mesh per slot with its own volume and folds, skinned to the shared skeleton, as in The Elder Scrolls Online, Guild Wars 2, and New World. Paint over the skin is kept for what lies on the skin: tattoos, scars, makeup, and dirt. |
| E2 | How a garment hides the body | A cover mask per garment and body type over the body's faces, computed by the pipeline from where the garment really covers the body, reading the garment's transparency. Not whole body zones. A garment texture with transparent parts gets masks of its own. |
| E3 | How loose cloth moves | Standard cloth chains in the skeleton, driven at runtime by a solver that follows the legs, adds springs, and collides with the body. There is no cloth simulation, neither in the game nor in the pipeline: garments get their shape from the garment cage and from their makers. |
| E4 | Hair under headwear | Every hair has a compressed under-headwear form; headwear says whether hair is hidden, compressed, or shown. |
| E5 | Transparent fabric | Sharp holes now: lace, net, holes, and torn cloth with alpha clipping. Soft see-through fabric such as veils later. |
| E6 | Body variety | Fixed body types, the male and female humans now and other races later as bodies of their own. No body sliders. |
| E7 | Dyes | Prepared from the start: every garment can carry dye channels that tint its regions. |
| E8 | Future races and base models | Every playable humanoid race, such as elves and dwarves, is a fixed body type per sex with its own proportions and its own rig on the shared skeleton. Garments, masks, cloth chains, and clips work for it without changes to the system, also when its body has a topology of its own and when a new base model replaces today's human bodies; see [Future Races And Base Models](#future-races-and-base-models). |
| E9 | What garments are fitted to | The garment cage: a sparse net of fixed topology that the pipeline fits to every body, smooth over the body's pits and following its figure. Garments are made and fitted on it, not on the body's own faces, so no garment depends on the topology of a body. |
| E10 | How new clothes are made | Garment templates: a garment kind's form, built by the pipeline from a recipe of standard lines on the cage, approved once, and worn with garment textures. A texture is an asset of its own and changes no template. |
| E11 | How garments meet | At standard lines with a fixed overlap: a top over the trousers, the trouser legs in the boots, gloves over every sleeve, each layer with its fixed order. |

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

Paint layers stay for skin-level detail. The leather jerkin, painted first as a
proof of paint layers, became the first garment template, `Jerkin`, with its
leather texture, `LeatherJerkin`, in step 5b, and a texture of `Shirt` in step
5c; its paint layer is removed in step 5c, when the tattoo paint layer `Tattoo`
takes over as the proof of paint layers.

### The Look On The Skin

A body's look on its skin has five kinds, as Victor decided on 2026-10-24. The
face and the eyes are whole images of their materials, the face and eye
variants; the others are paint layers of a category, drawn in the order of the
table, so a tattoo lies over a scar and the underwear over both.

| Kind | Made as | Shown | Chosen |
| --- | --- | --- | --- |
| Face | A face image, such as `Stubble` and `Clean` for the man and `Blush` and `Freckles` for the woman | Exactly one | In character creation, never changed |
| Eyes | An eye image, such as `Teal` | Exactly one | In character creation, never changed |
| Scar | A `scar` paint layer, layers 100 to 199, such as `Scar` over the left eye | At most one | In character creation, never changed |
| Tattoo | A `tattoo` paint layer, layers 200 to 299, such as `Tattoo` or the one-sided `ForearmRunes` | At most one | In character creation, and changed in the game later, such as at an NPC |
| Underwear | An `underwear` paint layer of a visual slot, layers 900 to 999: `Bra` in `body` for the woman, `Briefs` in `legs` for both | Exactly one per slot the body has underwear for, never taken off | In character creation, and changed in the game later |

Underwear is never taken off, but a garment can hide it: a template with
`hidesUnderwear`, such as a top that bares the shoulders or has a deep
neckline, hides the underwear of its own slot while it is drawn, so no bra
strap shows beside it, while the briefs stay, also without trousers. The skin
under the underwear is plain skin without detail. The net shirt does not hide
the bra: it shows through the meshes.

Paint does not depend on the bodies' topology, as Victor decided on 2026-10-24
for step 5d: a paint layer keeps its paint on a canvas of its own, a copy of the
body it is painted on that never changes with the bodies, and every body gets the
paint baked into its own paint UV map when the layer is synced, exactly through
the shared topology where a body has the canvas's, and through the garment cage
and the rigs' landmarks otherwise. The paint UV map is a second UV map of every
body, only for paint: the mirrored human skin keeps its texture, and the paint
lays the two sides apart, so a tattoo can be one-sided. When a new base model
replaces the bodies, each layer's canvas moves to its new first body and its
paint is checked and touched up there; see
[Paint Layers](ART_PIPELINE.md#paint-layers).

Character creation, the look a server stores, and the NPC who changes a tattoo
come later and follow these rules, which `AppearanceRules` in the client holds
until then. F2 and the Wardrobe show them: one choice per kind, with no scar or
tattoo among the choices, and the rule of each.

Under the look lies the body model, `Human_Male` or `Human_Female`: exactly one,
chosen in character creation and never changed (`AppearanceRules.BodyModel`).
F2 chooses it until then. One look serves both bodies, as Victor decided on
2026-10-24: what the other body lacks, such as the face `Stubble` on the woman
or the bra on the man, is not shown and comes back with the body that has it,
while the garments, the tattoo, the scar, and the briefs carry over.

## Garments

### Slots And Layers

A garment has one visual slot and a layer, a number from 0 to 1000 that orders
the garments of a character from the inside out. A body wears one garment per
visual slot: a shirt and a jerkin are both chest pieces and replace each other,
never one over the other, as Victor decided on 2026-10-23. Where garments of
different slots meet at a [standard line](#standard-lines), the layers give the
order that [How Garments Meet](#how-garments-meet) fixes.

| Visual slot | Covers | Typical layers |
| --- | --- | --- |
| `legs` | trousers, skirts, leggings | 100 |
| `feet` | shoes, boots, over the trouser legs | 120 |
| `body` | shirts, tops, jerkins, chest armor, robes, coats | 150 a top, over the trousers; 300 a robe or coat, over the boots |
| `hands` | gloves, gauntlets, over every sleeve | 350 |
| `back` | cloaks, capes | 500 |
| `hair` | hairstyles, beards, over the cloak | 550 |
| `head` | hoods, hats, helmets, masks, over the hair | 600 |

Hair lies over the cloak and falls on its yoke, and headwear lies over the hair,
so `head` is the highest layer; Victor accepted this order on 2026-10-24.

An item names the garments it shows, so one item may show several garments,
such as a robe with a hood. A garment may also hide whole slots below it, such
as a floor-length robe that hides the `legs` slot. The item equipment slots
`head`, `body`, `legs`, `hands`, and `feet` map to the visual slots of the same
name, and the cloak's `back` slot is an equipment slot too since 2026-10-25.
`neck`, `ring_1`, and `ring_2` show nothing, and `main_hand`, `off_hand`, and
`bag` will show attachments.

One equipment slot holds one item, and a visual slot shows one garment: of two
garments of a slot that a look names, the first in the catalog is worn, and
trying one on takes off the other. An item may show garments of several slots,
such as a robe with its hood in `head`; when another item's garment, such as a
helmet, fills that slot too, the garment of the item in the equipment slot of
that visual slot's name is worn, and otherwise the item earlier in the
equipment's order; see [Items](#items). Sleeves are part of a `body` garment, and
gloves sit over them.

### The Garment Cage

The garment cage is the surface that clothes are made on. It is a sparse net
with a fixed topology of its own, the same on every body, that the pipeline
lays around each body: over the neck, the trunk, the arms, the hands, the legs,
and the feet. Regions are added to it when the first garments that need them
come, the hands and the feet with the first gloves and boots on 2026-10-24, the
head with the first hair and hoods the same day; they add vertices and faces
after the cage's own and change none, so every garment made before keeps its
shape.

- **Its topology** was taken once from the human base body as it was when the
  cage was made, the faces of the neck, trunk, arms, and legs with their rings,
  and is the cage's own from then on: a new base model changes the bodies, not
  the cage. The cage keeps that first shape on a copy of that body's rig, its
  reference, and its regions: `neck`, `trunk`, `arms`, `legs`, `hands`, and
  `feet`.
- **The hands** are the body's faces of its hands, with five fingers, met at the
  cage's wrist rings, and each face knows the finger or the thumb it belongs to,
  so a glove's finger follows its own finger.
- **The feet** are a net of the cage's own, since a boot never shows the toes:
  rings from the ankle ring around the heel and forward to the toe tip, closed
  there, with the toes and the arch spanned and a flat sole.
- **Fitting it to a body** works for any body of the skeleton, whatever its
  topology:
  1. The reference shape moves with the rig: every cage vertex follows the
     bones of the reference rig to the body's rig, with each bone's length, so
     a dwarf's shorter arms and an elf's longer legs carry the cage with them.
  2. Every vertex is drawn to the skin of its region.
  3. A ball whose radius is the figure scale rolls over the skin: where it does
     not fit, in a pit narrower than the ball, the cage is lifted until it does,
     so creases such as the one under the bust are softened, while larger forms
     keep the ball touching the skin and are followed.
  4. The rings around the trunk, the neck, the arms, and the legs are drawn taut
     like a band around the body, outward only and in their own plane, as cloth
     is held taut around a body: every groove across a ring is spanned, such as
     the buttock cleft above the hips, the spine groove, the hollow between the
     shoulder blades, the navel, the abdominal muscles, and the cleavage, while
     the figure from top to bottom, the waist, the small of the back, the bust,
     the buttocks, and the shoulders, stays. The cage never webs between an arm
     and the trunk or between the legs, since the lift and the tension fade in
     from the borders between its regions and from its open ends.
  5. The cage is faired, and no vertex and no face centre lies closer to the
     skin than 1 mm; one that would is moved out along the skin's normal. Nor
     does the skin come out through a face between its corners, where a large
     face cuts across a curve such as a woman's shoulder: such a face moves out
     along its own normal until the skin lies 1 mm inside it, so no garment on
     the cage shows skin through it at rest.

  These steps fit the neck, the trunk, the arms, and the legs together. The
  hands and the feet are fitted after them and keep the rings they share with
  the arms and the legs as those lie, so adding them changed nothing of the
  rest: a hand's vertices are drawn to the skin of their own finger or thumb and
  the palm, so the gaps between the fingers stay open, and a foot is laid on the
  convex outlines of its cross sections, half-planes that turn about an axis in
  front of the ankle from the ankle ring down until they stand across the foot,
  then planes across the foot to the toe tip, so the toes and the arch are
  spanned and the sole is flat. Both keep 1 mm from the skin like the rest.
- **Its figure scale** is one number of the cage asset, in meters, the radius of
  the ball: 60 mm. The reviewer judges it on the cage's review sheet and asks for
  more or less figure. Not every groove has to go: Victor approved the cage with
  the lower end of the buttock cleft, below the hip line, spanned only in part,
  also for trousers.
- **Corrections** that a person sculpts on the cage of one body are kept as a
  difference to the fitted shape, like the corrections of a garment, so the next
  fit keeps them.

The cage is an art asset of its own, reviewed and approved by Victor like every
other. It has no export, so garments record the hash of the approved cage
file they were fitted to, and a change of the cage needs new exports of its
garments.

### Standard Lines

A standard line is a place where clothes end: a short sleeve, a long sleeve, a
trouser hem, or a top's hem. Each line is a plane across one region of the cage,
at a share of a bone's length, at right angles to the bone, or level across the
trunk, which stands upright at rest while its bones lean, such as the short
sleeve at a share of the upper arm. A line therefore lies at
the corresponding place on every body: a dwarf's short sleeve ends where it
ends on a dwarf's arm. Moving a line means changing its share. A line of the
neck may also rise toward the sides, by its rise times the square of the
distance to the side, so it curves round the neck: the neckline runs just above
the hollow of the throat, climbs to where the shoulders meet the neck, and runs
round the nape, as Victor asked on 2026-10-24, where the neck's base, the border
of the cage's `neck` region, reaches out over the shoulders.

| Line | Region | Where | Used by |
| --- | --- | --- | --- |
| `CollarStand` | `neck` | on the neck, 2 to 3 cm above its base | a stand collar's top |
| `Neckline` | `neck` | round the neck's base, just above the throat's hollow, up its sides | a round neckline: the shirts, the robe, the cloak |
| `CloakYoke` | `trunk` | a few centimeters below the collarbones, above the armpits | a cloak's yoke, where its back hangs from between the arms |
| `HemCrop` | `trunk` | under the bust, above the navel | a crop top's hem |
| `Waist` | `trunk` | at the hip bones | the trousers' waistband |
| `HemHip` | `trunk` | below `Waist` by the overlap | a top's hem, over the trousers |
| `SleeveShort` | `arms` | over half the upper arm, about 9 cm below the armpit | a short sleeve |
| `SleeveElbow` | `arms` | at the elbow | an elbow sleeve |
| `GloveCuff` | `arms` | on the forearm, toward the elbow from `SleeveLong` by the overlap | a glove's cuff, over the sleeve |
| `SleeveLong` | `arms` | at the wrist | a long sleeve |
| `LegShorts` | `legs` | at half the thigh | shorts |
| `LegKnee` | `legs` | below the knee | knee breeches |
| `BootCalf` | `legs` | at the calf | a boot's top, over the trouser leg |
| `LegAnkle` | `legs` | at the ankle | a trouser hem, in the boot |

The lines and their shares are part of the cage asset and are approved with it;
the borders of the cage's regions are lines too, such as the trunk's edge at the
shoulder for a sleeveless top.

### How Garments Meet

Where two garments meet, the outer one ends past the inner one's edge by a
fixed overlap, about 50 mm, so no gap opens in motion, and the part of the
inner garment under it is removed by its underlay when the character is put
together, so no double layer shows. The overlap is the distance between their
lines.

| Where | Inner | Outer |
| --- | --- | --- |
| Waist | trousers, to `Waist` | a top, to `HemHip` |
| Wrist | a long sleeve, to `SleeveLong` | a glove, to `GloveCuff` |
| Calf | trousers, to `LegAnkle` | a boot, to `BootCalf` |

Over the overlap, the outer garment lies outside the inner one: its band there is
measured from the inner garment's surface instead of the cage. A template's
recipe says this with `over`: beyond a line, on the side the inner garment
keeps, it lies over a garment of a given band, such as a top over the trousers
below `Waist`, and it rises to it over 30 mm before the line. The pair check
tests every template over every exported template of another visual slot and a
lower layer of its cage, such as a shirt over the trousers, at rest, in the test
poses, and in the clips: of the inner garment's faces that stay drawn, those
whose body face the outer one leaves visible at its openings, no vertex that the
outer one is over at rest may come out through it, and none may already lie
outside it at rest. It reports like the motion
check, and the outer template's export records the inner templates' exports it
was checked over. Templates of the same slot are never worn together, so they are
not checked against each other.

### Templates

A garment template is the form of a garment kind, without its look: a
short-sleeved top with a stand collar, a long-sleeved shirt, or trousers. There
may be several templates per kind, such as a fitted and a loose shirt. A new
item of clothing is a template and a new [texture](#textures), so it fits every
body without new modeling. What changes the silhouette or needs an edge with
thickness is a template of its own: the length of a sleeve or a hem, a standing
collar, a V-neck, the width. What only opens the surface with sharp edges, such
as holes, net, lace, torn edges, or a laced opening, is the transparency of a
texture; an edge cut by transparency has no thickness. Parts worn on top, such
as a belt, shoulder guards, or a fur collar, are small templates of their own.

A template is built by the pipeline from its recipe:

- **The regions and lines**: the cage regions it covers and the lines where it
  ends in them, each with the side it keeps, such as the `neck` to
  `CollarStand`, the `trunk` above `HemHip`, and the `arms` toward the trunk
  from `SleeveShort`. A region without a line is covered whole, and a region
  that is not listed is left open, such as the arms of a sleeveless top.
- **The band**: how far from the cage it lies, [below](#layer-bands).
- **Ease**: more room where the body folds in motion, in meters, added at the
  front and the back of the lower armholes, fading out over 60 mm. Measured on
  the jerkin, ease there let more skin come through in the clips, because the
  cloth then follows the arm's bones less, so the jerkin uses none; the field
  stays for templates whose cloth stands off the body.
- **Edges with thickness**: every open edge of its mesh, the hems, the sleeve
  ends, a neckline, and the top of a collar, is turned inward by a few millimeters, so a hem shows the
  cloth's thickness instead of a paper edge.

The pipeline cuts the cage's faces at the lines, keeps the faces of the
template, offsets them into its band, adds the ease and the turned edges, and
unfolds its UVs along its own edges and along seams that the pipeline places
where clothes have them: down the sides of the trunk, under the upper arms and
along the inside of the forearms, along the inside of the legs, at the back of a
collar, where a hand or a foot meets its limb, between a glove's palm and its
back along the sides of the fingers, and around a boot's sole and up the back
of its heel. The result is the
template's version on the first body, the male; every other body's version is
fitted from it. A person may then shape the first version by hand, such as
folds or a flared hem; the pipeline keeps that shape as a difference over the
cage, so it follows to every body and survives a refit. A recipe that changes
builds the template anew: the handmade shape, the difference between the first
version and the build it was shaped on, is carried over to the new build by
projection, each new vertex taking the difference at its place on the cage, and
the textures move to the new UVs by projection.

Unique garments, such as a ceremonial robe, are templates with one texture. A
form that no recipe of lines gives, such as a hairstyle or a helmet, is a
modeled template: its recipe is `modeled`, its first version is modeled by hand
on the first body over the cage region it covers, and from then on it is bound
to the cage, fitted, checked, approved, exported, and worn with textures like
every other template. A modeled template follows a smooth field of the cage's
moves to the other bodies rather than its nearest face, because hair or a hood
stands far off the cage, where the nearest face changes from one vertex to the
next and would crumple it. A person may shape another body's version by hand,
such as the helmet's nasal over a smaller nose, and the sync keeps that shape.
Loose parts, such as the skirt of a robe or the back of a cloak, hang from a
line instead of lying on the cage and take the cloth chains. A recipe's `hang`
entry names the trunk line a part hangs from, its side, all around for a skirt
or across the back for a cloak, its chains, and its flare, how much wider it
grows toward its hem. The recipe cuts the template's open edge at that line and
hangs rings of cloth below it down to the chains' tips. On every body the rings
follow a scaffold, and the cloth does not follow the figure under it, as Victor
asked on 2026-10-24: every column is stretched like a string from the edge over
the body's convex outline, a margin outside it, to where the body reaches out
farthest, and hangs straight down from there, spanning the hollows. A skirt's
columns lie around the hips between the chains, 30 mm outside the outline, so
the thighs of a jog stay under it, and flare by the part's flare toward the
hem, so it falls from the widest of the hips and the legs and never narrows. A
cloak's columns lie across the back, 20 mm behind it, and grow wider toward the
hem by the flare. A loose part takes the weights
of its chains, two neighbouring chains blended across, and blends into the
weights of the edge it hangs from over its first rings. Its first version is
built on the first body's scaffold; every other version is bound to that
scaffold and placed on its own body's. Loose parts have no band; the motion and
pair checks follow them with their chains laid as the solver lays them, so they
report a leg that comes through a skirt, and their swing is reviewed in the
Wardrobe, where the solver's springs move them.

### Textures

A garment texture is the look of a template: its painted image with color,
folds, wear, trim, and stitches in the template's UVs, its dye mask, its
smoothness and metallic, and its transparency. It is an asset of its own with
one review stage, `texture`, so a new texture never changes the template's file
and its approvals.

- A texture belongs to one template, since the trim at a hem sits where that
  template's hem is.
- A texture with transparent parts, such as a net or a laced V, has a cover
  mask, edges, and pull-in of its own per body, computed by the pipeline with
  the same rules as the template's, because its holes change what the body
  shows, and the skin its holes show is checked in motion like the template's,
  in the test poses and the clips. An opaque texture uses the template's. The
  leather jerkin's V is such a texture: the skin shows between its laces, and
  the cover mask leaves the skin under the V and along its cut edges visible.
- A texture records the template export it was checked against, so a new
  export of the template needs a new export of its textures.
- The template's UVs stay when its bodies, the cage, or the base model change,
  so a texture is never painted again for a new body.

### Cover Masks

Every body exports its editable mesh, its vertices and faces, so the pipeline
computes a garment's masks for each body in that body's own faces. Bodies need
not share a topology: each gets the masks of its own faces. For every body
type a garment fits, the pipeline computes:

- **The cover mask**: one bit per body face, set when the garment covers the face.
  From sample points on the face, rays go outward in a cone around the face
  normal up to the garment's reach, which is the distance from the skin to the
  cage there and the band's reach above it, so rays from the bottom of a spanned
  pit reach the cloth over it. A sample is covered when every ray hits the
  garment where its texture is opaque, or the body itself, as in an armpit; a
  face is covered when all its samples are and at least one ray hits the
  garment. A ray that leaves through a hole, a crop top's open belly, or a
  transparent part of a lace texture leaves the face visible, with its skin and
  everything painted on it.
- **The skin at the garment's edges is never hidden.** A look into a sleeve, under
  a hem, or into a laced neckline sees farther than the cone of rays, and a
  hidden face there shows as a hole in the body. A face within the edge reach of
  an open edge of the garment, a border of its mesh or a line where its texture
  is cut, stays visible. The edge reach is the band's, 20 mm for `tight`, 40 mm
  for `mid`, 80 mm for `outer`, and 120 mm for `loose`, about four times the
  band's gap between skin and cloth, or four times the gap at that edge where the
  cloth spans a pit and hangs farther from the skin.
- **The underlay**: for every face of the garment, the index of the body face
  under it, the closest body face along the garment's inward normal.
- **The pull-in**: for every body vertex that stays visible but lies closer to
  the garment than its band allows, such as the skin right at a tight collar or a
  sleeve hem, how far to move it inward, at most 3 mm, so it cannot poke through
  the hem.
- **In motion**, visible skin under the garment can come through it in the test
  poses and the clips. Skinning blends the bones of a fold, such as an armpit, so
  the gap between skin and cloth closes there in motion whatever the weights.
  Such skin lies at an opening that shows it, and hiding it would leave a hole
  there in every pose, so the pipeline reports it instead, and the template's
  ease, shape, or weights fix it. A fold also turns the skin along the cloth, so
  moving it inward along its normal does not keep it under. Skin that only passes
  a hem's edge, with no cloth over it, does not come through. Skin that lies
  through the garment already at rest shows in every pose, so that breaks a
  rule instead of being reported; the cage keeps the skin inside it, so a
  template on it has none.

- **Loose parts cover nothing.** A robe's skirt or a cloak's back swings off
  what it hangs over at rest, and a face hidden under it would show as a hole,
  so the skin and the inner garments under loose parts stay drawn, and the line
  a loose part hangs from counts as an open edge of the cloth that lies on the
  cage.
- **A garment may cover nothing at all.** A template whose manifest says
  `"covers": false` keeps an empty cover mask: it lies on top of the body and
  the garments under it and hides none of them, so it can swing open or be
  animated. The cloak is such a garment, as Victor decided on 2026-10-24. Its
  pull-in still keeps the skin under its edges from poking through.

A mask is 545 bytes for the human bodies (4,358 faces). When Unity puts a
character together, it removes every body face that a worn garment covers, moves
the pull-in vertices, and removes every garment face whose underlay face is
covered by a garment of a higher layer. A shirt over trousers thereby hides the
trousers' waist under its hem but keeps their legs, and a floor-length robe
hides the `legs` slot, so trousers under it are not drawn at all.

### Layer Bands

Every garment declares a band: how far from the cage its surface lies. The
pipeline checks every garment vertex and every face centre against its band on
every body type. The cage lies on the skin at the body's high points and over
its pits, so a band measured from it lets cloth span a pit, and cloth that sinks
into a pit leaves its band.

Clothes lie as close to the body as they can, without air between them and the
skin, as Victor decided on 2026-10-23: a recipe builds a `tight` template 2 mm
from the cage, which itself lies on the skin at the body's high points.

| Band | Distance from the cage | For |
| --- | --- | --- |
| `tight` | 0 to 8 mm | shirts, a jerkin, trousers, leggings, gloves |
| `mid` | 5 to 25 mm | boots, padded armor |
| `outer` | 10 to 60 mm | plate armor, a coat's upper body, helmets |
| `loose` | from 10 mm, no limit | skirts, robes, cloaks, hoods |
| `hair` | from 2 mm, no limit | hairstyles |

The lower bound is measured straight to the cage, the upper bound along the
normal of the cage's nearest face, as the recipes build the cloth: in front of a
sharp edge of the cage, such as the nose, the distance along a face's normal
falls far short of the true one and would call a helmet's nasal too close.

A garment of a higher layer lies in an outer band, so an inner garment never
reaches through an outer one where the outer one does not hide it. Where garments
overlap at a meeting line, the outer one's band is measured from the inner one,
as [How Garments Meet](#how-garments-meet) says.

### Transparent Fabric

A garment texture may have an alpha channel. The garment shader clips at alpha
0.5, which draws lace, net, holes, and torn edges sharp and without sorting
problems, and the cover mask reads the same alpha. Soft see-through fabric, such
as a veil, needs dithered transparency and comes when a garment needs it.

### Dye Channels

A garment texture may carry a dye mask, an image whose red, green, and blue
channels mark up to three regions that a dye color tints, such as cloth, trim,
and leather. The garment shader `Mmorpg/Garment` multiplies the painted color by
the dye color as far as the region's channel reaches, so a dyeable region is
painted in a light, neutral color with its folds and wear, and the dye gives it
its hue. Every dyeable region's middle tone lies at the same level, 0.7 in
linear values, so one dye color gives the same cloth on every garment. A dyeable
texture names the natural color of each channel, which its undyed garment
wears, so an existing texture becomes dyeable without being painted again:
`Set-TextureDyes.ps1` divides its image by its natural colors where its mask
reaches; see [Garment Textures](ART_PIPELINE.md#garment-textures). The wool of
`WoolCloak`, `WoolRobe`, `WoolHoodUp`, and `WoolHoodDown` was the first, on
2026-10-25, with one channel each, the clasps of the cloak left undyed. The dye
colors belong to the item definition, as Victor decided on 2026-10-25: a Blue
Wool Cloak is an item of its own; see [Items](#items).

## Fitting Every Body Type

A template is made once, on the first body of its cage, the male human. The
pipeline fits it to every other body of the cage, also those of
[future races](#future-races-and-base-models):

- **Shape**: every garment vertex is bound to the closest point of the first
  body's cage, as a face, barycentric coordinates, and an offset along the
  cage's normal and tangents, and placed at the same point and offset of the
  other body's cage. A template is bound to every region of the cage but the
  hands and the feet that it does not cover, so a sleeve's or a trouser leg's
  hem follows its limb and not the hand or the foot beyond it. Every body's cage has the same faces, so this needs nothing
  of the bodies' own topology. The first version keeps its binding to the cage
  too, so when the first body or its cage changes, the first version follows. A
  vertex or a face centre that leaves the garment's band moves back into it.
  Garments stay out of the body's armpits and crotch by the same rule as they do
  on the first body.
- **Corrections**: where the fitted shape needs a hand, a person sculpts it on the
  other body's version. The pipeline keeps the correction as a difference over
  the cage, so the next fit keeps it.
- **Weights**: a garment vertex takes the skin weights of the nearest skin of
  the body part its cage face lies on, the region and side of the cage, not
  smoothed, so it moves like the skin it covers: a sleeve's inside at the
  armpit takes the arm's weights, not the trunk's next to it. Measured on the
  jerkin, smoothed weights let the skin under a sleeve come through further, 20
  instead of 7 mm, and weights from a ray inward along the cage's normal or from
  the bottom of a spanned pit gave no less skin through the cloth than the
  nearest skin. Loose parts take the cloth chains instead. Weights
  painted by hand on the first version are kept, and the other versions copy
  them, because every version of a garment has the same topology; rules in the
  recipe, such as ease, are preferred to painted weights, since they carry over
  to new bodies by themselves.

## Future Races And Base Models

The system treats a race as more body types, so adding elves or dwarves adds
bodies, not code:

- **Its own body.** A new race starts as a copy of a human body and is reshaped
  in Blender, taller and slender for an elf, shorter and broader for a dwarf,
  longer ears and all. Keeping the human topology gives it the exact move of the
  paint layers; a body of its own topology gets them through the cage, and
  garments, cover masks, and underlays are fitted on the cage and computed in
  each body's own faces either way. What a shape cannot give, such as tusks or
  horns, is a separate mesh in the body's file.
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
- **Garments.** The new body joins the cage's bodies. The cage is fitted to it
  and reviewed, and every template of the cage gets a version for it, fitted
  from the first body's version through the cage, with corrections where it
  needs them. Masks and the clipping checks run per body type, so a template is
  exported for a race only when it passes on that race. Its textures keep their
  images; a texture with transparent parts is checked and exported again, which
  computes its masks for the new body, and an opaque one is exported again
  against the new template export.
- **Paint.** Every paint layer that lists the new body moves to it from its
  canvas, exactly when the body keeps the canvas's topology and through the cage
  and the landmarks otherwise, and is reviewed and exported for it.
- **Race features.** Headwear says per body type whether ears show or are
  covered, so an elf's hood is corrected around long ears or lets them through.
  Hair, such as a dwarf's beard, is a hair garment like any other.
- **Budgets and the game** are the same for every race. Equipment shows on every
  body type that has a version of its garments; the tools report a garment that
  lacks a version for a playable body type.

A new base model that replaces today's human bodies, with another topology, is
handled the same way: the new bodies join the cage, the cage is fitted to them,
the templates are fitted and their masks computed anew, and the textures keep
their images and are exported again, with new masks where they have
transparent parts. Each paint layer's canvas moves to the new first body through
the cage, `Sync-PaintAsset.ps1 -Canvas`, its paint is checked and touched up
there, and the other bodies of the new model's topology get the exact move.
A better cage of another topology is a new cage asset. Templates with a recipe of
lines are built again from it, keeping their handmade shape by projection onto
the new cage, and their textures move to the new UVs by projection. A modeled
template keeps its mesh, its UVs, and its painted weights: each vertex is bound
to the new cage at its place on the first body, and its textures stay as they
are.

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
hangs to that body's ankles, the cloak chains from its shoulder blades down its
back to the middle of its calves, halfway from the knees to the ankles, as
Victor chose on 2026-10-24,
the hood chain behind its head. The solver's profiles give lengths as shares of
the chains, so the same cloth behaves alike on a dwarf and an elf. Adding them changes the rig, so the bodies and the
animation libraries are approved again once; their clips do not change.

### Runtime Solver

A solver in the Unity client moves the chains every frame, after the Animator and
before the bodies are drawn:

- **Follow**: every skirt and cloak chain has a target from the legs: the front
  chains follow the leg in front, the side chains the leg on their side, the
  back chains and the cloak the leg behind, blended by their angle around the
  hips. Each bone beside a thigh or a shin, as the rig places the chains, turns
  as that part of the leg turns against the hips, but only as far as the leg
  pushes the cloth outward, the way the chain faces from the hips, beyond the
  slack its cloth profile leaves the leg, about 6 degrees of a turn for a robe
  and 3 for a cloak, so a breathing body leaves the cloth at rest. So a skirt
  lies over raised thighs and drapes over the knees when sitting, a cloak is
  pushed back by a leg that kicks back in a jog, and both hang down over a leg
  that moves away from them, such as a cloak behind a sitting body. Hood chains
  hang from their parent. The art pipeline lays the chains the same way in the
  test poses and clips of its review sheets, review files, and checks
  (`tools/blender/art_pipeline/cloth.py`), without the springs.
- **Springs**: every bone springs toward its target with stiffness, damping, and
  gravity, so cloth lags behind a turn, swings when a character stops, and
  settles.
- **Collision**: capsules on the thighs, shins, hips, spine, neck, and head push
  the chains out of the body. A capsule's radius is the middle distance of the
  body's vertices that its bone moves most. Around the trunk it is taken for the
  vertices along the bone's segment in each of 16 directions around the bone
  from the body's front toward its right, with narrow grooves filled, so a torso
  that is deep in front of the spine and shallow behind it keeps a cloak on its
  back, and the groove along the spine does not catch the cloth and swing it
  from side to side. A round capsule of the chest's width stood out 12 cm behind
  the back and pushed the cloak out in the middle of it, which Victor saw on
  2026-10-24. The legs are about round and move under the cloth, which slides
  around them, so their capsules stay round and push it straight away; a long
  profile around a shin let a cloak's hem slide round to its underside. A chain need not lie in its
  cloth: when a garment goes on, the solver measures from its model how far its
  cloth reaches beyond and within each particle of its chains (see Layers), and
  the margin a particle keeps from the body is the one its cloth keeps, the
  profile's margin and how far the cloth lies within the particle. The trunk
  carries the chains, and no capsule follows its shape exactly, such as the
  hips' capsule at the small of the back, so against the trunk's capsules a
  particle keeps no more than it has in the bind pose: a body standing still
  wears its garments exactly as they were made and approved. The legs move
  under the cloth, so it keeps its margin from them, and a leg that kicks back
  still pushes a cloak's hem over it. A cloak will also clear a backpack or
  quiver on `Socket_Back`.
- **Layers**: a cloak worn over a robe lies over its skirt, as Victor decided on
  2026-10-24. When a garment goes on, the solver measures from its model how far
  its cloth reaches beyond and within each particle of its chains, the way the
  chain faces from the hips: the vertices that a chain's bones move most lie at
  places along the chain, such as the rings of a skirt, and each particle takes
  the farthest out and in between the places on either side of it. The skirt
  chains are solved first; their particles moved out by the skirt cloth's reach
  are the skirt's envelope, which runs between the chains around the hips'
  upright axis by angle. Every cloak particle with cloth is then kept outside
  the envelope by a gap of 10 mm and by how far the cloak's own cloth lies
  within its chains, so the cloak drapes over the skirt and, without a robe,
  hangs taut as it was made. The art pipeline's pair check lays both garments'
  chains the same way, at rest too, and a robe that lies through the cloak at
  rest breaks a rule. In `Death`, where the legs swing up, the shins still come
  through the cloak and the skirt under it, as they come through the cloak
  alone.
- **Profiles**: a garment names a cloth profile, such as `robe`, `coat`, `cloak`,
  or `hood`, with its stiffness, damping, gravity, and chains; the solver reads
  its values.
- **Level of detail**: near the camera the springs run; farther away the chains
  only follow their targets; far away they rest. A clip may also key chain bones
  for a special motion, and the solver blends toward the clip. In play one
  `ClothChainScheduler` steps every body's solver once a frame from the main
  camera, so a crowd costs about as much as a few bodies:
  - A body whose cube of 4 m around it lies outside the camera's view moves
    nothing; its chains keep their pose and start again from their targets when
    it comes into view.
  - Beyond 60 m the chains rest, and a body already at rest writes nothing.
  - The nearest eight bodies in view within 25 m spring every frame.
  - The other bodies in view within 60 m follow their targets in turns, the ones
    that waited longest first: at most 16 a frame, and no more once their steps
    took 1.5 ms. Between its turns a chain keeps its pose against the bone it
    hangs from.
  - Every body of one model shares what the solver measured of it, its bind pose,
    capsules, legs, and rest depths, and every garment model what was measured of
    its cloth; measuring took about 6 ms a body, and a crowd spawns many. The
    capsules fit the body's own parts, never the combined renderer with its
    garments, so every body of a model collides alike whatever it wore first.
    Out of play, as in the Wardrobe, each body measures its own, so a model
    exported again is measured again.

  Measured on 2026-10-25 with 400 development bots in Development World 2 and
  about 170 dressed players in interest, in the Editor: before the scheduler the
  solver took 78 ms of a profiled 110 ms frame (0.5 to 0.9 ms a dressed body, also
  behind the camera); with it, the cloth takes about 2 to 3 ms, and the whole
  frame, unprofiled, went from
  41.6 ms (24 FPS) to 12.1 ms (83 FPS), see
  [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#crowd-performance).

The solver is presentation only. It runs the same for the local player, other
players, and bots, and never reaches the server. It moves a few bones per chain
and is not a cloth simulation; see E3.

## Hair And Headwear

Hair is a garment of its own visual slot, `hair`, with a shape key
`UnderHeadwear`, the hair pressed flat to fit under hoods and helmets, made by
hand like the hair itself. Headwear says what happens to the hair: `hide` for a
closed helmet, `compress` for a hood or a hat, so the fringe and the neck hair
show under it, and `show` for a circlet or a hood down. Headwear covers the scalp
like any garment covers the body. A hood that can be up or down is two garments
of the same item. A hairstyle is a modeled template of the `hair` slot with its
textures, and it comes with the head region of the cage.

The sync fits the form under headwear to every body like the hair itself and
checks the band in both forms. A template worn over hair is checked over the
form the hair takes under it: the compressed form under `compress`, the hair as
it is under `show`, and no hair under `hide`. Unity takes the blend shape in
full when it puts a character together under headwear that compresses the hair.

The first hairstyle, `ShoulderHair`, is straight and thick in stylized locks
with a middle parting and a short fringe, covers the ears, and ends where it
lands on the shoulders; `DarkBrownHair` paints it dark brown with lighter
strands. The gugel `HoodUp` covers the head and the compressed hair with an
opening for the face and a short collar over the neck and the shoulders that ends
above the cloak's clasps, over the cloak. Victor had its short point at the back
taken away, so it hangs on no chain and the hood chains stay unused. `HoodDown`
is the same hood pushed back: its collar stays around the neck with its top
rolled over into a turned band, and the hood lies folded on the upper back over
the cloak and the hair as it is. A helmet is a modeled garment weighted to the
head, not a rigid attachment: `NasalHelm` is a pointed spangenhelm of iron plates
held by four riveted bands under a round cap, with a riveted brow band, a
leather lining that shows below the iron, and a nasal riveted to the brow band
that bends forward over the nose's root; it sits above the ears and compresses
the hair.

## Attachments

Weapons, shields, quivers, and backpacks are static models on the skeleton's
sockets. A static model's root object `Grip` marks where it is held, and the
socket's position and turn on each body type place it. They follow the hands and
the back without weights.

## Items

Items show their garments, as Victor decided on 2026-10-25:

- **An item names its garments** in the client's item presentation, by the
  names of their garment textures, such as `WoolCloak` on `Cloak`, which name a
  template and its texture together, with the colors of their dye channels, or
  none for the textures' natural colors. The presentation is the client's, next to
  the item's icon and name; the server knows only the definition ids. Every item
  of an equipment slot that has a visual slot of its name, `head`, `body`, `legs`,
  `hands`, `feet`, and `back`, shows garments, and no other item does; an item
  without a garment of its own shows nothing. The Unity tests hold every item to
  this, and every garment it names to every body.
- **Dye colors belong to the definition**: a Blue Wool Robe and a Red Wool Robe are
  items of their own on the same garment. Dyeing an item in the game, such as at
  a dyer, would put colors on each item later, and the look would take them from
  there instead.
- **One garment per visual slot**: of two items whose garments fill one visual
  slot, the garment of the item in the equipment slot of the visual slot's name
  wins, such as a helmet in `head` over a robe's hood, and otherwise the item
  earlier in the equipment's order.
- **The hood is its own item**, the Wool Hood in `head`, that can be lowered: an
  item may name lowered garments, which it shows while its wearer has lowered
  it, such as `WoolHoodDown` for `WoolHoodUp`. The player lowers and raises the
  hood with a button in the inventory, and the server keeps the choice with the
  character's look.
- **Hair is part of the look**, not an item: a garment of the `hair` slot or none,
  chosen in character creation and changed in the game later, such as at a
  barber. Every other garment comes from the equipment.

The items are the Leather Jerkin, Linen Shirt, Long-Sleeved Linen Shirt, Net
Shirt, Wool Trousers, Leather Boots, Leather Gloves, Nasal Helm, and the Wool
Robe, Wool Hood, and Wool Cloak, each also blue and red, with the new category
`clothing` for the linen and wool and `armor` for the leather and iron. The
General Store sells them, and the development package `outfits` holds them all.

### The Look On The Server

Every client sees a character as the server stores it, as Victor decided on
2026-10-25:

- **Content names what a look can be.** `content/characters/appearance.json`
  lists the kinds of a look, `face`, `eyes`, `scar`, `tattoo`, `underwear`, and
  `hair`, each with its rule, the body models, the default first, with the
  options each offers per kind and visual slot, the first of each its default,
  and the equipment slots whose items other players see. Options are named as
  the bodies' appearance catalogs name them; a Unity test holds the content to
  both catalogs and to `AppearanceRules`.
- **GameService stores the look with the character**: the body model, the
  choices, and whether the headwear is lowered, in `character_appearances`,
  owned by Characters. Character creation chooses the body model and the look
  and checks them strictly against what the body model offers; a character
  created without a look gets the default look of the default body model.
  Characters that existed before the look was stored got `Human_Male` with its
  default look. GameService refuses to start when the content no longer offers
  what a stored look names, as the item catalog does.
- **One look serves every body.** A stored choice that the body model lacks,
  such as a face of the other body, is kept, and the body shows its own default
  there, so a body change in Development keeps the rest of the look.
- **The worker sends the look to every client.** The join ticket carries the
  look; every `EntitySpawn` carries the player's body model, choices, the items
  of its visible equipment slots in content order, and its headwear, and
  `EntityAppearanceChanged` sends a new look to everyone who sees the player and
  to the player itself, which gets its own look after the join. A change of
  equipment, a lowered or raised headwear, or in Development another body model
  or look sends it.
- **The player lowers and raises the hood** with `AppearanceIntent`; GameService
  stores it, and the worker answers with `AppearanceIntentResult`. Only a
  GameService in the Development environment changes a body model or look in the
  game, for the World Debug panel; character creation chooses them otherwise.
- **Development bots** have no stored look and no items: the worker gives each a
  body model, a look, and an outfit of catalog items picked from its character id
  by a stable hash, so a crowd shows every body, face, and garment, and each bot
  keeps its own. The outfit is only shown.
- **A dying body keeps its clothes** until its character respawns, though its
  items already lie in its corpse. A player corpse shows the dead character's
  body model and look, with its headwear, wearing the items still in the corpse's
  equipment section, so a looted cloak leaves the corpse.

## Putting A Character Together In Unity

A character's look is its body type, its face, eyes, and paint, the garments per
visual slot, each a template with its texture and dye colors, its hair, and its
attachments. When the look changes, such as when equipment changes, the client
puts the character together:

1. The visible body faces: every face but those the worn garments cover, with
   the masks of their textures where a texture has its own. The pull-in vertices
   move inward.
2. The visible garment faces: every face but those whose underlay a garment of a
   higher layer covers, and every slot a worn garment hides is left out.
3. Hair in its normal or compressed form, or none under headwear that hides it.
4. One combined skinned mesh on the character's skeleton, the cloth chains with
   it, with one submesh per material. Materials are shared copies keyed by their
   texture, dye colors, and paint layers, so characters that look alike batch.
5. The combined mesh is cached by the look's key, so every character with the
   same look shares one mesh, as development bots do.

Putting a character together happens on equipment changes, never per frame. If a
crowd needs fewer draw calls than one per material, garment textures move into
texture arrays that one garment material reads.

Unity imports a model's triangles in an order of its own, so the body faces are
found again in Unity by position: every body exports its editable mesh next to
its model, and the appearance catalog maps every triangle of the body's parts and
of each garment to the face it comes from when it is written, in the Editor; see
[Art Pipeline](ART_PIPELINE.md#unity-import-rules). Hair keeps its shape key as
a blend shape when it is imported, and the combined mesh takes it in full under
headwear that compresses the hair, so its compressed form costs nothing per
frame.

## Art Pipeline

### The Garment Cage Kind

The cage is an art asset of kind `cage` below `art/Cages/<Skeleton>`, one per
skeleton, such as `art/Cages/Humanoid`:

| Field | Meaning |
| --- | --- |
| `bodies` | The skinned assets it is fitted to, the first one first; every template of the cage fits them. |
| `figure` | The figure scale in meters: the size of a detail the cage still spans. |
| `lines` | The standard lines: per line its region, its bone, and its share of the bone. |

Its file holds the reference shape on its reference rig, and per body the
body's armature and source body as reference and the cage's version on that
body. `New-ArtAsset.ps1 -Kind cage` makes it from the first body, and
`Sync-CageAsset.ps1` fits it to every body, keeps the corrections, checks it, and
writes its review sheet. It has one review stage, `model`, and no export: Unity
never needs it, and garments record its approved file.

### The Garment Kind

A garment template is an art asset of kind `garment` below `art/Garments/<Name>`:

| Field | Meaning |
| --- | --- |
| `cage` | The cage it is made on; it fits every body of the cage. |
| `slot` | The visual slot: `head`, `body`, `legs`, `hands`, `feet`, `back`, or `hair`. |
| `layer` | 0 to 1000, the order from the inside out. |
| `band` | `tight`, `mid`, `outer`, or `loose`. |
| `recipe` | The regions and lines it covers, what it lies over, its loose parts, its ease, and its edges' thickness, or `modeled` for a form modeled by hand, such as a hairstyle. |
| `hides` | Visual slots it hides, such as `["legs"]` for a floor-length robe. |
| `covers` | `false` for a garment that hides nothing under it, such as a cloak; `true` by default. |
| `hair` | Headwear only: `hide`, `compress`, or `show`. |
| `cloth` | The cloth profile of its loose parts, or none. |
| `dyes` | The number of dye channels its textures may use, 0 to 3. |
| `triangleBudget` | The most triangles of one body type's version. |

Its file holds, per body type, a collection `Export_<Label>` with the body's
armature and the template's version on it, the version on the first body being
the one a person shapes, and the cage's versions it is fitted on. The bodies
themselves are in a `Reference` collection. The template's own material is a
neutral grey, which its review sheet shows with a grid in its UVs, so the sheet
shows how they stretch; its textures bring their own materials. Export
writes one FBX per body type, the template skinned to that body's rig, and a
data file with the cover mask, underlay, and pull-in, all recorded with their
hashes, with the hash of the cage's approved file, and with the bodies'
exports, like every export.

### The Texture Kind

A garment texture is an art asset of kind `texture` below
`art/GarmentTextures/<Name>`, with the template it belongs to in `garment`. Its
file holds the template's first version as a preview and the texture's images,
painted by hand in Texture Paint mode or by a script, with the custom
properties `roughness` and `metallic`. Its one review stage, `texture`, checks
that the images fit the template's UVs, and for a texture with transparent parts
computes its masks and runs the template's motion check with them, in the rig
review's test poses and at every frame of the clips, so skin that its holes show
and that comes through the cloth around them is reported before the approval.
Export writes its images and its material `M_Garment_<Name>`, and its masks per
body when it has transparent parts, in the data format of its template's, next
to the template's models. In Unity, the appearance catalog lists every texture
of a template as a garment named after the texture, with the template's model
for the body, the texture's material, and the texture's masks or else the
template's; a character wears a garment by the texture's name.

### Making A Garment

1. `New-ArtAsset.ps1 -Kind garment` creates a template from its recipe on the
   cage: the template's version on the first body, in its band, with its ease,
   turned edges, and UVs.
2. A person shapes the first version in Blender where it needs more than the
   recipe gives, such as folds or a flared hem.
3. `Sync-GarmentAsset.ps1` fits it to the other bodies through the cage,
   transfers the weights, computes the masks, and runs the checks.
4. The reviewer looks at the review sheet and the template in the Wardrobe
   preview in Unity, then approves the `model` and `rig` stages, and the export
   makes it ready for textures.
5. `New-ArtAsset.ps1 -Kind texture` creates a texture for the template, a person
   or a script paints it, and the reviewer approves its `texture` stage on its
   review sheet and in the Wardrobe; its export makes the template and the
   texture ready for items.

### Checks

The sync stops on a broken rule and reports the rest:

- The cage of every body has the cage's topology, no vertex or face centre
  inside the skin, no skin that comes out through a face, and no face that
  crosses another, and every line crosses its region on every body.
- Every version of a template has the first version's topology and stays within
  its triangle budget.
- Every vertex and every face centre lies within its band from the cage on every
  body type, so a flat face over a round shoulder cannot dip under the skin and
  cloth cannot sink into a pit.
- In the rig review's test poses and at every frame of `Idle`, `Walk`, `Jog`,
  and `Death`, no visible body vertex under the garment comes through the cloth.
  At rest none may, and no vertex of an inner template either: that stops the
  sync. Skin that comes through at the armpits is weighed later: the base bodies'
  armpits are to be fixed, as Victor said on 2026-10-23.
  The report names how many do and the deepest place, and the review sheet
  shows their faces red, for the reviewer to weigh and the template's ease,
  shape, or weights to fix. No garment of a lower layer reaches through a higher
  one where they overlap; the pair check tests this from the first pair of
  templates that overlap.
- The faces that only the cone of rays leaves visible are the garment's
  openings, and the review sheet shows the mask with the skin kept visible at
  the garment's edges, so a mask that is too small or too large shows there; a
  check that the faces under opaque cloth are covered would only repeat the
  rays.
- Only a garment whose texture has an alpha channel is clipped.

### Review

The cage's review sheet shows every body from the front, the side, and the back
with the cage over it, colored by its distance from the skin, and the standard
lines drawn on it, and its hands and feet up close.

The template's review sheet shows the template on every body type from four
sides with its grid, the template in the test poses, and the body without it,
the covered faces orange, the skin kept visible at the garment's edges yellow,
the pulled-in vertices blue, and skin that comes through the garment in motion
red. The texture's review sheet shows the template with the texture on every
body type from four sides, and for a texture with transparent parts also the
body without it in the colors of the template's sheet, with its own mask and
the skin that comes through it in motion.

Cloth motion is reviewed in the Wardrobe preview, a Unity Editor window that
dresses a body in chosen garments with their textures, plays a clip on a moving
ground, and runs the cloth solver.

## Budgets

| Slot | Triangles per body type |
| --- | --- |
| `head` | 1,500 |
| `body` | 3,000; a robe or coat 4,000 |
| `legs` | 2,000 |
| `hands` | 2,000 |
| `feet` | 1,500 |
| `back` | 1,500 |
| `hair` | 2,500 |

A character in a full outfit draws about 15,000 to 18,000 triangles near the
camera, because the covered body faces are gone. Gloves with five fingers are
made at the resolution of the body's hands, 1,520 triangles for both, and boots
hide the body's feet, 1,328 triangles, so their budgets were raised from 800 and
1,000 on 2026-10-24 and still leave the character lighter than without them. Levels of detail at half and a
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
   garments on in F2. Done.
5. The garment cage, templates, and textures, each approved by Victor:
   - 5a. Done. The garment cage: the `cage` kind, its fitting to every body, its
     standard lines, its checks, and its review sheet.
   - 5b. Done. Templates and textures: the `garment` kind built from recipes on
     the cage, with its bands, weights, and masks from the cage, ease, turned
     edges, and UVs; the `texture` kind with its own masks; the Unity catalog,
     the Wardrobe, and F2 wearing a texture on its template; and the leather
     jerkin as the first template, `Jerkin`, with its old look moved to the
     template's UVs as its first texture, `LeatherJerkin`, with its laced V
     transparent. The first leather jerkin garment is removed.
   - 5c. The proof set, in four steps, as Victor accepted on 2026-10-23:
     1. Done. Shirts, trousers, and a tattoo: the templates `Shirt` and `LongShirt`
        over `Trousers` at the waist, the textures `LinenShirt`,
        `LinenLongShirt`, `WoolTrousers`, and `NetShirt`, a coarse net with
        transparent meshes over the tattoo paint layer `Tattoo`, which replaces
        the leather jerkin paint layer, `LeatherJerkin` as a texture of `Shirt`,
        and the pair check.
     2. Done. Boots and five-finger gloves, with the cage's regions for the
        feet and the hands: the templates `Boots`, to the calf in the `mid`
        band over the trousers below `LegAnkle`, and `Gloves`, to `GloveCuff`
        over every sleeve below `SleeveLong`, with the leather textures
        `LeatherBoots` and `LeatherGloves`.
     3. Done. A robe with the skirt chains and a cloak with the cloak chains:
        the templates `Robe`, a long-sleeved top with a skirt from `HemHip` to
        the ankles that hides the `legs` slot, and `Cloak`, a yoke over the
        shoulders down to `CloakYoke` below the collarbones with a taut sheet
        down the back to the middle of the calves, which covers nothing; the
        textures `WoolRobe` and `WoolCloak`, whose front keeps only the cloth
        over the shoulders, held by a large bronze clasp at the front of each
        shoulder; loose parts; skirt and cloak chains that follow the legs; and
        a cage that keeps all skin inside it, so no garment shows skin through
        it at rest.
     4. Done. The cage's head region, a shoulder-length hairstyle with its
        compressed form, the robe's hood up and down, and a nasal helmet that
        Victor added: the templates `ShoulderHair`, `HoodUp`, `HoodDown`, and
        `NasalHelm` with the textures `DarkBrownHair`, `WoolHoodUp`,
        `WoolHoodDown`, and `IronNasalHelm`.
   - 5d. Done. Paint layers independent of the bodies' topology, moved between
     bodies by projection: a canvas of its own per layer, the bodies' paint UV map
     `UVMap_Paint`, which lays the two sides of a mirrored body apart, the move
     through the shared topology or through the cage and the rigs' landmarks, the
     canvas moved to another body, the images cropped to their paint, the skin
     shader reading the paint UVs, the four layers moved to their canvases, and the
     one-sided tattoo `ForearmRunes`.
6. Done. Items: items show their garments as templates with textures, the `back`
   slot, dye colors on items, and the look sent to every client with the body
   type, as Victor decided on 2026-10-25, in two parts:
   - 6a. Done. Items show garments: the garments and dye colors of every item in
     the item presentation, the `back` slot, the category `clothing`, the wool set
     dyeable with its items in three colors, the General Store and a development
     package with them, and the local player dressed by its equipment, with its
     hair from F2.
   - 6b. Done. The look on the server: the body model and the look stored with
     the character and chosen at its creation, sent to every client with the
     visible equipment and the lowered hood, development bots dressed from their
     ids, and player corpses wearing what is left in them; see
     [The Look On The Server](#the-look-on-the-server).
7. With the first new race: making a body type from a human body, generalizing
   `Sync-HumanBase.ps1` to every body of the race, and adding it to the cage.

## Open Questions

Both questions of step 6 were answered on 2026-10-25: the hood is an item of its
own that the player lowers and raises in the inventory, and dye colors belong to
the item definition; see [Items](#items).

Helmets are skinned garments weighted to the head, as the first helmet,
`NasalHelm`, shows; masks will be too.
