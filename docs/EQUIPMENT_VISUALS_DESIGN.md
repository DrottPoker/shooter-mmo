# Equipment Visuals Design

This document is the source of truth for how characters look and why: what
clothes, armor, hair, and carried equipment are made of, how they fit every body
type, how they hide the body and each other, how loose cloth moves, and how
items show them. How the art is made, with the rules the tools enforce and every
command, is in [Art Pipeline](ART_PIPELINE.md). How the Unity client puts a
character together at runtime is in
[Player Bodies](UNITY_CLIENT_ARCHITECTURE.md#player-bodies), and the equipment
slots and item rules are in
[Equipment](INVENTORY_AND_DEATH_LOOT_DESIGN.md#equipment).

## Status

Implemented:

- The human male and female bodies, with faces and eye colors to choose from,
  and paint layers for underwear, a scar, and tattoos, each kept on a canvas of
  its own and moved to every body by projection.
- Garments made as templates on the garment cage and worn with textures: shirts
  with short and long sleeves, trousers, boots, gloves with five fingers, a robe
  with a skirt and a cloak on the cloth chains, shoulder-length hair with its
  form under headwear, a hood worn up or down, and a nasal helmet. Textures may
  have holes, such as a net shirt or a laced jerkin, and the wool is dyeable.
- Cover masks, pull-in, and the motion and pair checks that keep skin and inner
  garments from showing through.
- Cloth chains laid in a target pose on every visible body, with springs on the
  bodies largest on the screen.
- Attachments with hold poses and attack clips: a sword, a shield, a two-handed
  greataxe, a staff, and a bow that draws its string and looses its own arrow,
  and gathering tools held only while they are used.
- Items that show their garments in their dye colors and their attachments, and
  a look that is chosen at character creation, stored with the character, and
  sent to every client; development bots are dressed from their ids.

Planned:

- Other playable races as body types of their own; see
  [Future Races And Base Models](#future-races-and-base-models).
- Skin colors, and changing a look in the game, such as a tattoo at an NPC, hair
  at a barber, or a dye at a dyer.
- Soft see-through fabric, such as a veil.
- Shoulder garments, such as pauldrons and mantles, for the `shoulders`
  equipment slot, with a visual slot and layer of their own.
- More hold poses, such as a sword raised or a shield in guard, weapons sheathed
  at the hip or on the back out of combat, and a quiver or a backpack on the
  back.
- Levels of detail with fewer triangles; see [Budgets](#budgets).

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

| Question | Choice |
| --- | --- |
| What clothes and armor are made of | A 3D mesh per slot with its own volume and folds, skinned to the shared skeleton, as in The Elder Scrolls Online, Guild Wars 2, and New World. Paint over the skin is kept for what lies on the skin: tattoos, scars, makeup, and dirt. |
| How a garment hides the body | A cover mask per garment and body type over the body's faces, computed by the pipeline from where the garment really covers the body, reading the garment's transparency. Not whole body zones. A garment texture with transparent parts gets masks of its own. |
| How loose cloth moves | Standard cloth chains in the skeleton, laid at runtime in a target pose that follows the legs and collides with the body, on which springs swing the cloth of the bodies largest on the screen within limits. There is no cloth simulation, neither in the game nor in the pipeline: garments get their shape from the garment cage and from their makers. |
| Hair under headwear | Every hair has a form pressed flat for under headwear; headwear says whether hair is hidden, pressed flat, or shown. |
| Transparent fabric | Sharp holes: lace, net, holes, and torn cloth with alpha clipping. Soft see-through fabric, such as a veil, is planned. |
| Body variety | Fixed body types, the male and female humans now and other races later as bodies of their own. No body sliders. |
| Dyes | A garment texture can carry up to three dye channels that tint its regions, in colors that belong to the item definition. |
| Future races and base models | Every playable humanoid race, such as elves and dwarves, is a fixed body type per sex with its own proportions and its own rig on the shared skeleton. Garments, masks, cloth chains, and clips work for it without changes to the system, also when its body has a topology of its own and when a new base model replaces today's human bodies; see [Future Races And Base Models](#future-races-and-base-models). |
| What garments are fitted to | The garment cage: a sparse net of fixed topology that the pipeline fits to every body, smooth over the body's pits and following its figure. Garments are made and fitted on it, not on the body's own faces, so no garment depends on the topology of a body. |
| How new clothes are made | Garment templates: a garment kind's form, built by the pipeline from a recipe of standard lines on the cage, approved once, and worn with garment textures. A texture is an asset of its own and changes no template. |
| How garments meet | At standard lines with a fixed overlap: a top over the trousers, the trouser legs in the boots, gloves over every sleeve, each layer with its fixed order. |

## Layers Of A Character

Players change how their bodies look by layers that are made separately, as in
World of Warcraft, so a change to one layer leaves the others as they are. From
the inside out:

1. **Body**: the skinned body with its skin, face, and eye materials and the
   [paint layers](ART_PIPELINE.md#paint-layers) on the skin. Body faces that a
   worn garment covers are removed when the character is put together.
2. **Garments**: meshes in visual slots, ordered by layer. Each hides what it
   covers of the body and of the garments below it.
3. **Hair**: a garment of its own slot, in its normal form or pressed flat under
   headwear.
4. **Attachments**: objects on the skeleton's sockets: weapons and shields on
   `Socket_MainHand` and `Socket_OffHand`, and later a quiver or a backpack on
   `Socket_Back`.

Paint layers stay for skin-level detail; everything with volume is a garment.

### The Look On The Skin

A body's look on its skin has five kinds. The face and the eyes are whole images
of their materials, the face and eye variants; the others are paint layers of a
category, drawn in the order of the table, so a tattoo lies over a scar and the
underwear over both.

| Kind | Made as | Shown | Chosen |
| --- | --- | --- | --- |
| Face | A face image, such as `Stubble` and `Clean` for the man and `Blush` and `Freckles` for the woman | Exactly one | In character creation, never changed |
| Eyes | An eye image, such as `Teal` | Exactly one | In character creation, never changed |
| Scar | A `scar` paint layer, layers 100 to 199, such as `Scar` over the brow and down the cheek | At most one | In character creation, never changed |
| Tattoo | A `tattoo` paint layer, layers 200 to 299, such as `Tattoo` or the one-sided `ForearmRunes` | At most one | In character creation, and changed in the game later, such as at an NPC |
| Underwear | An `underwear` paint layer of a visual slot, layers 900 to 999: `Bra` in `body` for the woman, `Briefs` in `legs` for both | Exactly one per slot the body has underwear for, never taken off | In character creation, and changed in the game later |

Underwear is never taken off, but a garment can hide it: a template with
`hidesUnderwear`, such as a top that bares the shoulders or has a deep
neckline, hides the underwear of its own slot while it is drawn, so no bra
strap shows beside it, while the briefs stay, also without trousers. The skin
under the underwear is plain skin without detail. The net shirt does not hide
the bra: it shows through the meshes.

Paint does not depend on the bodies' topology. A paint layer keeps its paint on
a canvas of its own, a copy of the body it is painted on that never changes with
the bodies, and every body gets the paint baked into its own paint UV map when
the layer is synced: exactly through the shared topology where a body has the
canvas's, and through the garment cage and the rigs' landmarks otherwise. The
paint UV map is a second UV map of every body, only for paint: the mirrored
human skin keeps its texture, and the paint lays the two sides apart, so a
tattoo can be one-sided. When a new base model replaces the bodies, each layer's
canvas moves to its new first body and its paint is checked and touched up
there; see [Paint Layers](ART_PIPELINE.md#paint-layers).

Under the look lies the body model, `Human_Male` or `Human_Female`: exactly one,
chosen in character creation and never changed. One look serves both bodies: a
choice that the shown body lacks, such as the face `Stubble` on the woman or the
bra on the man, is kept but not shown, the body shows its own default there, and
the choice comes back with the body that has it, while the garments, the tattoo,
the scar, and the briefs carry over.

The rule of each kind, with the body model and hair, lives in
`content/characters/appearance.json`, which the server checks every look
against, and in `AppearanceRules` in the client; a Unity test holds both equal.
Changing a look in the game, such as at an NPC or a barber, is planned; until
then the Admin menu (F3) changes any look in Development.

## Garments

### Slots And Layers

A garment has one visual slot and a layer, a number from 0 to 1000 that orders
the garments of a character from the inside out. A body wears one garment per
visual slot: a shirt and a jerkin are both chest pieces and replace each other,
never one over the other. Where garments of different slots meet at a
[standard line](#standard-lines), the layers give the order that
[How Garments Meet](#how-garments-meet) fixes.

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
so `head` is the highest layer.

An item names the garments it shows, so one item may show several garments,
such as a robe with a hood. A garment may also hide whole slots below it, such
as a floor-length robe that hides the `legs` slot. The equipment slots `head`,
`body`, `legs`, `hands`, `feet`, and `back` map to the visual slots of the same
name. `main_hand` and `off_hand` show attachments, the `tool` slot shows its
tool only while it is used, `neck`, `ring_1`, `ring_2`, and `ammunition` show
nothing, `shoulders` shows nothing until shoulder garments exist (planned), and
`bag` will show an attachment, such as a backpack (planned).

One equipment slot holds one item, and a visual slot shows one garment: of two
garments of a slot that a look names, the first in the catalog is worn, and
trying one on takes off the other. When the garments of several items fill one
visual slot, the rule of [Items](#items) picks one. Sleeves are part of a `body`
garment, and gloves sit over them.

### The Garment Cage

The garment cage is the surface that clothes are made on: a sparse net with a
fixed topology of its own, the same on every body, that the pipeline lays around
each body, smooth over the body's pits and following its figure. Garments are
made and fitted on it, so no garment depends on a body's own topology. How the
pipeline builds, fits, and checks it is in
[Garment Cage](ART_PIPELINE.md#garment-cage).

- **Its regions** are `neck`, `trunk`, `arms`, `legs`, `hands`, `feet`, and
  `head`. A region is added when the first garments that need it come; it adds
  vertices and faces after the cage's own and changes none, so every garment
  made before keeps its shape.
- **Its topology** was taken once from the human base body, the faces of the
  neck, trunk, arms, and legs with their rings, and is the cage's own from then
  on: a new base model changes the bodies, not the cage. The cage keeps that
  first shape on a copy of that body's rig, its reference.
- **The hands** are the body's hand faces with five fingers, met at the cage's
  wrist rings, and each face knows the finger or the thumb it belongs to, so a
  glove's finger follows its own finger.
- **The feet** are a net of the cage's own, since a boot never shows the toes:
  rings from the ankle around the heel and forward to the toe tip, with the toes
  and the arch spanned and a flat sole.
- **The head** is a net of the cage's own from the neck to the crown that spans
  the ears, the nose, and the eye sockets smoothly, for hair, hoods, and
  helmets.
- **Fitting it to a body** works for any body of the skeleton, whatever its
  topology:
  1. The reference shape moves with the rig, every bone with its length, so a
     dwarf's shorter arms and an elf's longer legs carry the cage with them.
  2. Every vertex is drawn to the skin of its region.
  3. A ball whose radius is the figure scale rolls over the skin: where it does
     not fit, in a pit narrower than the ball, the cage is lifted until it does,
     so creases such as the one under the bust are softened, while larger forms
     keep the ball touching the skin and are followed.
  4. The rings around the trunk, the neck, the arms, and the legs are drawn taut
     like a band, outward only and in their own plane, as cloth is held taut
     around a body: every groove across a ring is spanned, such as the buttock
     cleft above the hips, the spine groove, the hollow between the shoulder
     blades, the navel, the abdominal muscles, and the cleavage, while the
     figure from top to bottom, the waist, the small of the back, the bust, the
     buttocks, and the shoulders, stays. The cage never webs between an arm and
     the trunk or between the legs, since the lift and the tension fade in from
     the borders between its regions and from its open ends.
  5. The cage is faired, and no vertex and no face centre lies closer to the
     skin than 1 mm. Nor does the skin come out through a face between its
     corners, where a large face cuts across a curve such as a woman's shoulder,
     so no garment on the cage shows skin through it at rest.

  The hands and the feet are fitted after the rest and keep the wrist and ankle
  rings where the rest left them, so adding them changed nothing of it: a hand
  follows each finger with the gaps between the fingers open, and a foot follows
  the convex outlines of its cross sections, so the toes and the arch are
  spanned and the sole is flat.
- **Its figure scale**, the radius of the ball, is one number of the cage asset:
  60 mm. The reviewer judges it on the cage's review sheet and asks for more or
  less figure. Not every groove has to go: the cage is approved with the lower
  end of the buttock cleft, below the hip line, spanned only in part, also for
  trousers.
- **Corrections** that a person makes on the cage of one body are kept as a
  difference to the fitted shape, like the corrections of a garment, so the next
  fit keeps them.

The cage is an art asset of its own, reviewed and approved like every other. It
has no export, since Unity never needs it, so garments record the approved cage
file they were fitted to, and a change of the cage needs new exports of its
garments.

### Standard Lines

A standard line is a place where clothes end: a short sleeve, a long sleeve, a
trouser hem, or a top's hem. Each line is a plane across one region of the cage
at a share of a bone's length, at right angles to the bone, or level across the
trunk, which stands upright at rest while its bones lean. A line therefore lies
at the corresponding place on every body: a dwarf's short sleeve ends where it
ends on a dwarf's arm. Moving a line means changing its share. A line of the
neck may also rise toward the sides, by its rise times the square of the
distance to the side, so it curves round the neck: the neckline runs just above
the hollow of the throat, climbs to where the shoulders meet the neck, and runs
round the nape, where the neck's base, the border of the cage's `neck` region,
reaches out over the shoulders.

| Line | Region | Bone and share | Where | Used by |
| --- | --- | --- | --- | --- |
| `CollarStand` | `neck` | `Neck` 0.3 | on the neck, about 3 cm above its base | a stand collar's top |
| `Neckline` | `neck` | `Neck` 0.19, rise 5.3 | round the neck's base: just above the hollow of the throat, up at the sides of the neck where the shoulders meet it, and at the nape | a round neckline: the shirts, the robe, the cloak |
| `CloakNape` | `trunk` | `UpperChest` 0.85 | a few centimeters below the base of the neck at the back | where a cloak's back hangs from |
| `CloakYoke` | `trunk` | `UpperChest` 0.55 | a few centimeters below the collarbones, above the armpits | where a cloak's yoke ends in front |
| `HemCrop` | `trunk` | `Chest` 0.5 | under the bust, above the navel | a crop top's hem |
| `SkirtWaist` | `trunk` | `Spine` 0.45 | just below the navel: 2 to 3 cm on the male, about 1 cm on the female | a robe's or a dress's waist seam, where its skirt starts |
| `Waist` | `trunk` | `Spine` 0.1 | at the hip bones | the trousers' waistband |
| `HemHip` | `trunk` | `Hips` 0.72 | about 5 cm under `Waist`, the overlap | a top's hem, over the trousers |
| `SleeveShort` | `arms` | `UpperArm` 0.6 | over half the upper arm, about 9 cm below the armpit | a short sleeve |
| `SleeveElbow` | `arms` | `LowerArm` 0.05 | just below the elbow | an elbow sleeve |
| `GloveCuff` | `arms` | `LowerArm` 0.65 | on the forearm, about 5 cm toward the elbow from `SleeveLong`, the overlap | a glove's cuff, over the sleeve |
| `SleeveLong` | `arms` | `LowerArm` 0.92 | at the wrist | a long sleeve |
| `LegShorts` | `legs` | `UpperLeg` 0.5 | at half the thigh | shorts |
| `LegKnee` | `legs` | `LowerLeg` 0.1 | below the knee | knee breeches |
| `BootCalf` | `legs` | `LowerLeg` 0.55 | at the calf | a boot's top, over the trouser leg |
| `LegAnkle` | `legs` | `LowerLeg` 0.9 | at the ankle | a trouser hem, in the boot |

The lines and their shares are part of the cage asset and are approved with it;
the borders of the cage's regions are lines too, such as the trunk's edge at the
shoulder for a sleeveless top.

### How Garments Meet

Where two garments meet, the outer one ends past the inner one's edge by a fixed
overlap, about 50 mm, so no gap opens in motion, and the part of the inner
garment under it is removed by its underlay when the character is put together,
so no double layer shows. The overlap is the distance between their lines.

| Where | Inner | Outer |
| --- | --- | --- |
| Waist | trousers, to `Waist` | a top, to `HemHip` |
| Wrist | a long sleeve, to `SleeveLong` | a glove, to `GloveCuff` |
| Calf | trousers, to `LegAnkle` | a boot, to `BootCalf` |

Over the overlap, the outer garment lies outside the inner one: its band there
is measured from the inner garment's surface instead of the cage, which its
recipe says with `over`. The pipeline's pair check puts every template on the
bodies over every exported template of another visual slot and a lower layer,
such as a shirt over the trousers, at rest, in the test poses, and in the clips:
of the inner garment's faces that stay drawn, no vertex that the outer one is
over at rest may come out through it, and none may already lie outside it at
rest; see [Garments](ART_PIPELINE.md#garments). Templates of the same slot are
never worn together, so they are not checked against each other.

### Templates

A garment template is the form of a garment kind, without its look: a
short-sleeved top, a long-sleeved shirt, or trousers. There may be several
templates per kind, such as a fitted and a loose shirt. A new item of clothing
is a template and a new [texture](#textures), so it fits every body without new
modeling. What changes the silhouette or needs an edge with thickness is a
template of its own: the length of a sleeve or a hem, a standing collar, a
V-neck, the width. What only opens the surface with sharp edges, such as holes,
net, lace, torn edges, or a laced opening, is the transparency of a texture; an
edge cut by transparency has no thickness. Parts worn on top, such as a belt,
shoulder guards, or a fur collar, are small templates of their own.

The pipeline builds a template from its recipe: the cage regions it covers and
the standard lines where it ends in them, each with the side it keeps, such as
the `trunk` above `HemHip` and the `arms` toward the trunk from `SleeveShort`,
its [band](#layer-bands), and its ease. A region without a line is covered
whole, and a region that is not listed is left open, such as the arms of a
sleeveless top. The pipeline cuts the cage's faces at the lines, offsets them
into the band, turns every open edge inward by a few millimeters, so a hem shows
the cloth's thickness instead of a paper edge, and unfolds the UVs along seams
where clothes have them; see [Building A Template](ART_PIPELINE.md#building-a-template).

The result is the template's version on the first body of its cage, the male;
every other body's version is fitted from it, as
[Fitting Every Body Type](#fitting-every-body-type) describes. A person may
shape the first version by hand, such as folds or a flared hem; the pipeline
keeps that shape as a difference over the cage, so it follows to every body and
survives a refit. A recipe that changes builds the template anew: the handmade
shape is carried over to the new build by projection, each new vertex taking
the difference at its place on the cage, and the textures move to the new UVs by
projection.

Ease is more room where the body folds in motion, at the front and the back of
the lower armholes. The close-fitting templates use none: ease there lets more
skin come through in the clips, because the cloth then follows the arm's bones
less. It is meant for templates whose cloth stands off the body.

Unique garments, such as a ceremonial robe, are templates with one texture. A
form that no recipe of lines gives, such as a hairstyle or a helmet, is a
modeled template: its first version is modeled by hand on the first body over
the cage region it covers, and from then on it is bound to the cage, fitted,
checked, approved, exported, and worn with textures like every other template.
A modeled template follows a smooth field of the cage's moves to the other
bodies rather than its nearest face, because hair or a hood stands far off the
cage, where the nearest face changes from one vertex to the next and would
crumple it. A person may shape another body's version by hand, such as the
helmet's nasal over a smaller nose, and the pipeline keeps that shape.

### Loose Parts

Loose parts, such as the skirt of a robe or the back of a cloak, hang from a
line instead of lying on the cage and take the cloth chains. A recipe names the
trunk line a part hangs from, its side, all around for a skirt or across the
back for a cloak, its chains, and its flare, how much wider it grows toward its
hem. The template's open edge is cut at that line, and rings of cloth hang below
it down to the chains' tips.

On every body the rings follow a scaffold, and the cloth does not follow the
figure under it: every column is stretched like a string from the edge over the
body's convex outline, a margin outside it, to where the body reaches out
farthest, and hangs straight down from there, spanning the hollows. A skirt's
columns lie around the belly, the hips, and the legs 30 mm outside the outline,
so the thighs of a jog stay under it, and below where its chains hang from at
least as far out as they, and flare toward the hem, so it falls from the widest
of the hips and the legs and never narrows. A cloak's columns lie across the
back, 20 mm behind it and a skirt's margin and 10 mm behind the hips and the
legs, so a cloak hangs clear of a skirt from the waist, which bulges over the
hips above its chains, where the game does not lay the cloak over the skirt,
and grow wider toward the hem by the flare. A part across the back may hang high, from the
nape: with a top, the template keeps only its middle that wide, so a cloak's
yoke lies on the trapezius and not on the shoulder muscle, and its back only
above the part's line; with a width, the part fans out to that width by the
first joint of its chains. Both are shares of how far the shoulder joints lie
from the middle, so they fit every body.

A loose part takes the weights of its chains, two neighbouring chains blended
across, and blends into the weights of the edge it hangs from over its first
ring, or down to where its chains hang from when its edge lies above them, such
as a skirt from the waist, which moves with the hips there. Loose parts have no
band and cover nothing, since they swing off what they hang over. The motion and
pair checks follow them with their chains laid as the game lays them, so they
report a leg that comes through a skirt, and their swing is reviewed in
[the Wardrobe](ART_PIPELINE.md#the-wardrobe).

### Textures

A garment texture is the look of a template: its painted image with color,
folds, wear, trim, and stitches in the template's UVs, its dye mask, its
smoothness and metallic, and its transparency. It is an asset of its own with
one review stage, so a new texture never changes the template's file and its
approvals.

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
not share a topology: each gets the masks of its own faces. For every body type
a garment fits, the pipeline computes:

- **The cover mask**: one bit per body face, set when the garment covers the
  face. Rays go out from the face in a cone around its normal up to the
  garment's reach, which is the distance from the skin to the cage there and the
  band's reach above it, so rays from the bottom of a spanned pit reach the
  cloth over it. The face is covered when every ray hits the garment where its
  texture is opaque, or the body itself, as in an armpit, and at least one ray
  hits the garment. A ray that leaves through a hole, a crop top's open belly,
  or a transparent part of a lace texture leaves the face visible, with its skin
  and everything painted on it.
- **The skin at the garment's edges is never hidden.** A look into a sleeve,
  under a hem, or into a laced neckline sees farther than the cone of rays, and
  a hidden face there shows as a hole in the body. A face within the edge reach
  of an open edge of the garment, a border of its mesh or a line where its
  texture is cut, stays visible. The edge reach is the band's, 20 mm for
  `tight`, 40 mm for `mid` and `hair`, 80 mm for `outer`, and 120 mm for
  `loose`, about four times the band's gap between skin and cloth, or four times
  the gap at that edge where the cloth spans a pit and hangs farther from the
  skin, up to 120 mm.
- **The underlay**: for every face of the garment, the body face under it, the
  closest body face along the garment's inward normal.
- **The pull-in**: for every body vertex that stays visible but lies closer to
  the garment than its band allows, such as the skin right at a tight collar or
  a sleeve hem, how far to move it inward, at most 3 mm, so it cannot poke
  through the hem.
- **In motion**, visible skin under the garment can come through it in the test
  poses and the clips. Skinning blends the bones of a fold, such as an armpit,
  so the gap between skin and cloth closes there in motion whatever the weights.
  Such skin lies at an opening that shows it, and hiding it would leave a hole
  there in every pose, so the pipeline reports it instead, and the template's
  shape or weights fix it. A fold also turns the skin along the cloth, so moving
  it inward along its normal does not keep it under. Skin that only passes a
  hem's edge, with no cloth over it, does not come through. Skin that lies
  through the garment already at rest shows in every pose, so that breaks a rule
  instead of being reported; the cage keeps the skin inside it, so a template on
  it has none.
- **Loose parts cover nothing.** A robe's skirt or a cloak's back swings off
  what it hangs over at rest, and a face hidden under it would show as a hole,
  so the skin and the inner garments under loose parts stay drawn, and the line
  a loose part hangs from counts as an open edge of the cloth that lies on the
  cage.
- **A garment may cover nothing at all.** A template whose manifest says
  `"covers": false` keeps an empty cover mask: it lies on top of the body and
  the garments under it and hides none of them, so it can swing open or be
  animated. The cloak is such a garment. Its pull-in still keeps the skin under
  its edges from poking through.

A mask is 545 bytes for the human bodies (4,358 faces). When the character is
put together, every body face that a worn garment covers is removed, the
pull-in vertices move, and every garment face whose underlay face a garment of a
higher layer covers is removed. A shirt over trousers thereby hides the
trousers' waist under its hem but keeps their legs, and a floor-length robe
hides the `legs` slot, so trousers under it are not drawn at all.

### Layer Bands

Every garment declares a band: how far from the cage its surface lies. The
pipeline checks every garment vertex and every face centre against its band on
every body type. The cage lies on the skin at the body's high points and over
its pits, so a band measured from it lets cloth span a pit, and cloth that sinks
into a pit leaves its band.

Clothes lie as close to the body as they can, without air between them and the
skin: a recipe builds a `tight` template 2 mm from the cage, which itself lies on
the skin at the body's high points.

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
reaches through an outer one where the outer one does not hide it. Where
garments overlap at a meeting line, the outer one's band is measured from the
inner one, as [How Garments Meet](#how-garments-meet) says.

### Transparent Fabric

A garment texture may have an alpha channel. The garment shader clips at alpha
0.5, which draws lace, net, holes, and torn edges sharp and without sorting
problems, and the cover mask reads the same alpha. Soft see-through fabric, such
as a veil, needs dithered transparency and comes when a garment needs it.

### Dye Channels

A garment texture may carry a dye mask, an image whose red, green, and blue
channels mark up to three regions that a dye color tints, such as cloth, trim,
and leather. The garment shader multiplies the painted color by the dye color as
far as the region's channel reaches, so a dyeable region is painted in a light,
neutral color with its folds and wear, and the dye gives it its hue. Every
dyeable region's middle tone lies at the same level, 0.7 in linear values, so
one dye color gives the same cloth on every garment. A dyeable texture names the
natural color of each channel, which its undyed garment wears, so an existing
texture becomes dyeable without being painted again; see
[Dyeable Textures](ART_PIPELINE.md#dyeable-textures). The wool of the cloak, the
robe, and the hood is dyeable, one channel each, with the cloak's bronze clasps
left undyed. The dye colors belong to the item definition: a Blue Wool Cloak is
an item of its own; see [Items](#items).

## Fitting Every Body Type

A template is made once, on the first body of its cage, the male human. The
pipeline fits it to every other body of the cage, also those of
[future races](#future-races-and-base-models):

- **Shape**: every garment vertex is bound to the closest point of the first
  body's cage, as a face, barycentric coordinates, and an offset along the
  cage's normal and tangents, and placed at the same point and offset of the
  other body's cage. A template is bound to every region of the cage but the
  hands and the feet that it does not cover, so a sleeve's or a trouser leg's
  hem follows its limb and not the hand or the foot beyond it. Every body's cage
  has the same faces, so this needs nothing of the bodies' own topology. The
  first version keeps its binding to the cage too, so when the first body or its
  cage changes, the first version follows. A vertex or a face centre that leaves
  the garment's band moves back into it. Garments stay out of the body's armpits
  and crotch by the same rule as they do on the first body.
- **Corrections**: where the fitted shape needs a hand, a person shapes it on the
  other body's version. The pipeline keeps the correction as a difference over
  the cage, so the next fit keeps it.
- **Weights**: a garment vertex takes the skin weights of the nearest skin of
  the body part its cage face lies on, the region and side of the cage, and on a
  hand the finger or thumb with the palm, not smoothed, so it moves like the
  skin it covers: a sleeve's inside at the armpit takes the arm's weights, not
  the trunk's next to it. Smoothed weights let the skin under a short sleeve
  come through farther, 20 instead of 7 mm, and weights from a ray inward along
  the cage's normal or from the bottom of a spanned pit gave no less skin
  through the cloth than the nearest skin. Loose parts take the cloth chains
  instead. Weights painted by hand on the first version are kept, and the other
  versions copy them, because every version of a garment has the same topology;
  rules in the recipe are preferred to painted weights, since they carry over to
  new bodies by themselves.

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
  model, so a dwarf's legs and arms are shorter and an elf's longer, and the
  cloth chains are placed from the model too: a dwarf's skirt chains end at a
  dwarf's ankles.
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
their images and are exported again, with new masks where they have transparent
parts. Each paint layer's canvas moves to the new first body through the cage,
its paint is checked and touched up there, and the other bodies of the new
model's topology get the exact move. A better cage of another topology is a new
cage asset. Templates with a recipe of lines are built again from it, keeping
their handmade shape by projection onto the new cage, and their textures move to
the new UVs by projection. A modeled template keeps its mesh, its UVs, and its
painted weights: each vertex is bound to the new cage at its place on the first
body, and its textures stay as they are.

The tooling for the first new race is planned: making a body type from a human
body, the human base tools working for every body of the race, and adding the
body to the cage.

## Cloth Chains

### Skeleton

The `Humanoid` skeleton has standard chains of bones for loose cloth: eight
skirt chains of four bones around the hips, for skirts, robes, coat tails, and
tabards; five cloak chains of five bones across the back, for cloaks and capes;
and a hood chain of two bones behind the head, for the back and tip of a hood.
[Skeletons](ART_PIPELINE.md#skeletons) lists them. They are not Unity Humanoid
bones, so Humanoid clips never move them, and the body is not weighted to them.

The rig places them from each body: the skirt chains on a ring at the hips that
hangs to that body's ankles, the cloak chains from its shoulder blades down its
back to the middle of its calves, halfway from the knees to the ankles, and the
hood chain behind its head. The cloth profiles give lengths as shares of the
chains, so the same cloth behaves alike on a dwarf and an elf. Adding chains
changes the rig, so the bodies and the animation libraries are approved again
once; their clips do not change.

### Runtime Solver

Animation and the legs decide where the cloth is; springs only add swing within
limits, so the cloth stays on the body also in hectic motion, such as the spin
of an attack, without a cloth simulation. The client lays the chains every
frame, after the Animator posed the body and the camera moved, and before the
bodies are drawn. Every visible body shows the **target pose**: where the chains
hang in a pose, without memory, so the same pose always lays them alike,
whatever the frame rate, and exactly as the art pipeline's reviews and checks
lay them. On the bodies largest on the screen, **springs** swing the cloth on
it, with inertia, sway, and flare, within limits. The target pose is `ClothPose`
in the assembly `Mmorpg.Client.Cloth`:

- **Hang and follow**: every chain hangs from its animated parent with its bones
  at rest. Each bone beside a thigh or a shin, as the rig places the chains,
  turns as that part of each leg turns against the hips, but only as far as that
  leg pushes the cloth outward, the way the chain faces from the hips, beyond the
  slack its cloth profile leaves the leg, about 6 degrees of a turn for a robe
  and 3 for a cloak, so a breathing body leaves the cloth at rest. Each leg
  pushes on its own, in full on its own side and in front and behind, and less
  toward the other side, which it does not reach: a side chain follows only its
  own leg. The bone turns toward both legs in the share of the squares of their
  pushes, so a front chain follows the leg that pushes it forward, a back chain
  the leg that kicks it back, and nothing changes at once when the legs pass
  each other. So a skirt lies over raised thighs and drapes over the knees when
  sitting, a cloak is pushed back by a leg that kicks back in a jog, and both
  hang down over a leg that moves away from them. Hood chains hang from their
  parent.
- **Swing out of the body**: then each bone, from the root, swings outward in
  the plane of its direction and the way its chain faces, by the smallest angle
  that leaves its samples outside what its kind collides with: a skirt the trunk
  and the legs, a cloak the arms too, a hood the trunk. A bone's samples are its
  end, its middle, and the cloth toward each neighbouring chain: half way to
  where the neighbour hangs as it follows the legs, at the end and at the middle
  of the quad between them, moved with the bone as if the neighbour swung alike,
  so two neighbours that clear the cloth between them each swing only as far as
  it needs, and every chain is laid on its own. At the open edges of a cloak the
  cloth lies as far beyond the outer chain as toward its inner neighbour. The
  legs are always inside a skirt and in front of a cloak, so the cloth is only
  ever pushed outward and cannot end on the wrong side of a leg. A sample's angle
  is advanced in steps that never pass where it first leaves a capsule, its
  depth over 1.5 times the farthest it moves per radian, eight times, and a last
  secant step, so it never slips past a narrow gap. The angle is taken in the
  share the swing moves the sample straight out of the capsule where it leaves,
  so a capsule the swing would only slide along does not fling the chain, and
  less and less toward the largest swing of 165 degrees. The cloth starts to
  give way when a capsule comes within 20 mm of it and lies on it once the
  capsule is as far into it, so its motion starts and ends without a jolt. An
  arm only lifts a cloak's cloth out as far as it comes into it, saturating at
  8 cm: farther, the arm comes through the edge rather than throw the chain.
- **Collision**: capsules on the thighs, shins, feet, upper arms, forearms,
  hips, spine, chest, neck, and head. Around the trunk a capsule's radius is the
  middle distance of the body's vertices that its bone moves most along its
  segment, in each of 16 directions around the bone from the body's front toward
  its right, with narrow grooves filled, so a torso that is deep in front of the
  spine and shallow behind it keeps a cloak on its back, and the groove along
  the spine does not catch the cloth; a round capsule of the chest's width would
  stand out 12 cm behind the back and push a cloak out in the middle of it. The
  limbs are about round and move under the cloth, which slides around them, so
  their capsules are round and reach out to 90 percent of their vertices, so the
  calf, the heel, and the back of the upper arm do not stand out of them; an
  arm's are the vertices along its segment, so the shoulder does not widen it. A
  chain need not lie in its cloth: when a garment goes on, the client measures
  from its model how far its cloth reaches beyond and within each particle of
  its chains (see Layers), and a sample keeps from a capsule the margin its
  cloth keeps, the profile's margin and how far the cloth lies within it, but no
  more than it has in the bind pose less the 20 mm band, so a body in its bind
  pose wears its garments exactly as they were made and approved, and nothing
  gives way there. A cloak will also clear a backpack or a quiver on
  `Socket_Back`.
- **Layers**: a cloak worn over a robe lies over its skirt. The vertices that a
  chain's bones move most lie at places along the chain, such as the rings of a
  skirt, and each particle takes the farthest out and in between the places on
  either side of it. The skirt chains are laid first; their particles moved out
  by the skirt cloth's reach, joined in triangles to the next chain around, are
  the skirt's outer side, and a cloak's end and middle samples keep outside it by
  a gap of 10 mm and by how far the cloak's own cloth lies within them, seen
  from the hips' own upright axis, so the layers hold also when the body lies
  down. A ray along the seam of two triangles crosses both, so a cloak chain
  right behind a skirt chain never slips between them. The art pipeline's pair
  check lays both garments' chains the same way, at rest too, and a robe that
  lies through the cloak at rest breaks a rule.
- **Springs**: `ClothSprings` gives every particle of a chain a place and a
  speed in the frame of its anchor bone, the hips for a skirt, the upper chest
  for a cloak, and the head for a hood, and springs it toward where the target
  pose lays it, at a fixed 90 Hz whatever the frame rate, at most four steps a
  frame, so a longer frame plays slower rather than throw the cloth. Each step
  takes the bones and the target between the last frame and this one, and what
  is shown lies between the last two steps. The anchor's motion in the world is
  filtered over about 25 ms, its acceleration again, and reaches the cloth as
  forces of inertia that change no faster than the limits allow: 20 m/s², 540
  degrees a second, and 60 rad/s², smoothly across the steps of a frame. Below
  the limits the cloth lags, swings, and sways as with real inertia, and above
  them it feels only the limit: a stop from a jog swings the hem with the whole
  stop, spread over a quarter of a second, and a spin of an attack at 1440
  degrees a second flares the cloth only as a spin at the limit does. Gravity
  counts only as far as the anchor leans away from the bind pose, damping counts
  against the anchor, not the air, and the anchor's speed leans the cloth back
  as a wind, the more the farther it hangs from the anchor, up to a cap. Then
  each step keeps neighbouring chains between 0.6 and 1.4 of how far apart the
  target lays them, so a skirt hangs together and a knee tents it; the chains
  within their twist around the anchor's upright axis from the target, the
  skirt 18 and the cloak 9 degrees, and toward each neighbour within 0.45 of the
  angle to its target, so two chains never meet however close the legs push
  their targets; the bones at their lengths and within a cone around where the
  target points them; and the speed under 8 m/s against the anchor. Last, the
  body pushes the cloth out: the target pose is a floor, and no bone of the
  springs lies nearer the body than the target pose, the way its chain faces,
  because springs that lagged behind it let the legs through a cloak between
  its samples. So the legs and the body decide where the cloth is, and the
  springs only swing it out and to the side. A cloak over a robe is kept outside
  the robe's swinging skirt in every step, so its pushes add up over the steps.
  Such a push takes the speed into the body out of the cloth, slides it with a
  friction of 0.3, and gives it at most 0.3 m/s, so a jolt of a clip, as the
  `Jog` clip's knee that snaps straight, moves the cloth where it pushes but
  does not fling it. The springs' own steps collide only to keep a cloak out of
  the skirt: what is shown is faded in over the target and laid with the target
  pose's collision as the body stands this frame, without the contact band and
  the arms, which lift a cloak by a share of how far they come into it rather
  than out of it, and every sample may lie as deep in a collider as it lies in
  the target pose, so a body in its target pose shows it exactly. The contact
  band and an arm's lift are not idempotent, so collision fed back into the
  springs would push the cloth on and on. A chain with a broken value starts
  again on its target, and a body whose cloth lay within 1 mm of its target for
  half a second sleeps on it until a step would move it farther.
- **Jumps**: a frame in which the body or an anchor moves more than 2 m, faster
  than 20 m/s, or turns more than 150 degrees is a jump, and so is any frame the
  game tells the solver of: the local player's hard corrections and teleports,
  and every time a remote player or a Mob is presented from a new start or a
  restarted clock. The cloth is carried along: the anchors take their new place
  without it reaching their filters, and last frame's bones move with the body's
  own jump, so the steps see the clips move and not the jump.
- **Profiles**: a garment names a cloth profile, `robe`, `cloak`, or `hood`, with
  its margin and slack, which the target pose reads, and its springs in SI
  units, from the chain's root to its tip:

  | Profile | Margin, slack | Hz root, tip | Damping root, tip | Cone root, tip | Twist | Wind per m/s, at most |
  | --- | --- | --- | --- | --- | --- | --- |
  | Robe | 12 mm, 0.1 | 4, 2.5 | 0.6, 0.4 | 35, 60 degrees | 18 degrees | 0.6, 4 m/s² |
  | Cloak | 20 mm, 0.05 | 2.5, 2.5 | 0.6, 0.6 | 40, 55 degrees | 9 degrees | 8, 30 m/s² |
  | Hood | 10 mm, none | 6, 6 | 0.7, 0.7 | 20, 20 degrees | none | 0.3, 2 m/s² |

  The cloak is as stiff at its hem as at its top and more damped, so it stays
  taut rather than flick up at its hem, and the wind of its speed leans it back
  from the heels. Every profile feels gravity in full. The Wardrobe tunes them;
  see [The Wardrobe](ART_PIPELINE.md#the-wardrobe).
- **Level of detail and jobs**: one `ClothWorld` holds every dressed body in
  play in fixed blocks, and `ClothChainScheduler` steps it once a frame from the
  main camera, after it moved. Three Burst jobs run on the worker threads: one
  reads every body's bones, one lays every body in view in its target pose and
  swings its springs, a body a job, and one turns the chain bones. Every remote
  player is a root of its World scene, so the transform jobs take the players
  side by side. A body whose cube of 4 m around it lies outside the camera's
  view moves nothing; beyond 60 m the chains rest without a fade, and a body
  already at rest writes nothing. Of the bodies in view within 60 m, the largest
  on the screen have springs, about 48 of them, a robe and a cloak counting two,
  and a body that has them keeps them against one up to a quarter larger, so no
  body flickers between the levels; every other body shows its target pose. The
  springs fade in over 0.3 s, always from the target, and out over 0.3 s; a body
  out of view or at rest starts again from the target when it has springs again.

The levels rest on the cost. In the Editor, with Burst's safety checks on, a
body jogging in a robe and a cloak takes 0.29 ms of one core in its target pose
and 0.53 ms with its springs; 100 such bodies take about 3.5 ms of a frame on 15
worker threads in their target pose, and about 5.8 ms with springs on 48 of
them. Without the safety checks it is about a third less. In the game, with
about 80 players in interest, 76 of them dressed, and the camera low over them,
the cloth took 0.79 ms of the main thread on average and 1.27 ms at the 95th
percentile with every body in its target pose, and 1.22 and 1.90 ms with springs
on the six bodies within 60 m in view. A body with springs is one job, so the
main thread waits for the slowest of them. The target pose of every visible body
stays within the goal of 1 ms of the main thread, so a body beyond 60 m needs no
fade.

The solver is presentation only. It runs the same for the local player, other
players, and bots, and never reaches the server. It moves a few bones per chain
and is not a cloth simulation.

### Target Pose And Parity

`ClothPose` works on plain data, and the art pipeline lays the same target pose
in Python, step by step, from the same constants, the `cloth` section of
`art/Skeletons/Humanoid.json`, so the review sheets, the review files, and the
checks of garments show the cloth as every visible body in the game shows it.
Cases captured from the client keep the two within 0.01 mm of each other in CI;
see [Cloth Parity](ART_PIPELINE.md#cloth-parity). The Blender checks of `Robe`
and `Cloak` in the target pose meet every rule: the legs come through the robe
only at the hip crease in `Death`, by up to 30 mm, and through the cloak
nowhere.

### Measuring The Cloth

`ClothStressMotion` lists the motions loose cloth is measured and reviewed in:
Idle, Walk, Jog, and Death, and stress motions that move and turn the root over
them, with no clip of their own. They are spins of 720, 1440, and 2880 degrees a
second with a stop, turns of 90 and 180 degrees at the simulation's 720 degrees
a second while jogging, a jog with a stop, a zig-zag of sharp corners, the
zig-zag as a remote body shows it at 15 snapshots a second, a jog in jittery
frame times between 1/240 and 1/20 s, a teleport, a hard correction of 3 m, and
a turn of 180 degrees at once. `ClothMotionMeter` measures every frame:

- how many of the body's leg and arm vertices come through the loose cloth, and
  how far: a vertex comes through when the cloth crosses the way to it from the
  bone it hangs on;
- how many of a robe's loose cloth come through a cloak over it, seen from the
  hips' upright axis;
- how much the chains jerk, the third difference of their particles in the frame
  of the bone they hang from, as RMS over the steady phases;
- how far a spin flares the chains' tips out, and how long they take to settle
  after a stop or a spin, which counts the cloth settled when its swing on the
  target changes no faster than 10 cm/s beyond how fast the target itself moves;
- whether the skirt's chains keep their order around the hips;
- how far the frame of a jump moves the chains in the frame of their bone;
- how often the legs push a cloak's lower half in steady motion.

`MMORPG > Tools > Art Pipeline > Measure Cloth` runs every motion on both bodies
in the robe, the cloak, and both, and steps a zig-zag at 1/30, 1/60, and 1/144 s
and in jittery frames against the same at 1/240 s, playing the clips and with the
body's pose held, into `artifacts/cloth-measurements.md`, which Git ignores.
Each run starts the clips and the chains again, so runs compare. The Wardrobe
shows the same measures while it plays.

The solver measures as follows, the worst of both bodies and the three outfits.
`Legs` counts only the legs and feet, whereas `Through cloth` also counts an arm
through a cloak's edge; `Other` gives how far a spin flares the tips out, how
long the cloth's swing on its target takes to settle after a stop or a spin, and
how far the frame of a jump moves the chains:

| Motion | Through cloth | Legs | Robe through cloak | Jerk RMS | Skirt out of order | Other |
| --- | --- | --- | --- | --- | --- | --- |
| Idle | none | none | none | 17 m/s³ | never | |
| Walk | 27 vertices, 23 mm | 6 mm | none | 2,375 m/s³ | never | |
| Jog | 45, 62 mm | 18 mm | 2, 8 mm | 7,188 m/s³ | never | |
| Zig-zag | 51, 56 mm | 18 mm | 6, 23 mm | 7,274 m/s³ | never | |
| Network 15 Hz | 49, 63 mm | 16 mm | 6, 34 mm | 7,308 m/s³ | never | |
| Turn 180 | 45, 58 mm | 18 mm | 10, 119 mm | 7,134 m/s³ | never | |
| Spin 720 | none | none | none | 17 m/s³ | never | flares 17.5 cm, settles in 0.6 s |
| Spin 1440 | none | none | none | 17 m/s³ | never | flares 17.5 cm, settles in 0.6 s |
| Spin 2880 | none | none | 3, 8 mm | 17 m/s³ | never | flares 17.7 cm, settles in 0.6 s |
| Jog stop jog | 45, 58 mm | 18 mm | 2, 4 mm | 5,959 m/s³ | never | settles in 0.6 s |
| Death | 122, 98 mm | 98 mm | 6, 12 mm | 21,804 m/s³ | 9 frames | |
| Teleport, snap 3 m | 48, 62 mm | 18 mm | 2, 52 mm | 7,192 m/s³ | never | the jump moves the chains 57 mm |
| Snap 180 | none | none | none | 17 m/s³ | never | the jump moves the chains 2 mm |

The legs come through the cloth no farther than in the target pose alone, since
no bone of the springs lies nearer the body than it, and never through a cloak;
`ClothStressMotionTests` holds the legs out of a cloak and within 25 mm of a
robe while walking and jogging. What still comes through: an arm through a
cloak's edge at the far end of its swing, after it lifted the edge 8 cm; the
legs through the waist of a robe in `Death`, where the skirt's roots follow the
hips while the thighs fold up 90 degrees; and a robe through a cloak where a
knee lifts both, by up to 12 cm only in a turn of 180 degrees in a quarter of a
second, where the robe flares out below the cloak's hem. The skirt's chains keep
their order in every spin, a spin flares the hems out by 17 to 18 cm, where the
target pose alone flares them 2 cm, and the cloth settles within 0.6 s of its
end. A jump moves the cloth no more than the jog's own legs move it in that
frame: `ClothSpringTests` holds the jump's frame and the next within a
centimeter of the same motion without the jump.

The cloth jerks less than in the target pose alone while walking, 2,375 against
3,545 m/s³, but as much while jogging and more while dying: where the legs push
the cloth it goes with them, and the `Jog` clip's knee snaps straight at the toe
off, its thighs turning up to 15 degrees in a frame where the gait model's leg
is straight. The springs smooth everything the legs do not push. In about half
of a jog's steady frames the legs push a cloak's lower half, by up to 15
degrees: the heel comes up behind nearly level in a jog and reaches a cloak to
the middle of the calf however it hangs.

How far the chains' tips in a zig-zag lie from where they lie stepped at 1/240 s,
in centimeters at most and as RMS over the frames, the worst of both bodies and
the three outfits, playing the clips and with the body's pose held so only its
root moves:

| Pose | 1/30 s | 1/60 s | 1/144 s | Jittery |
| --- | --- | --- | --- | --- |
| Clips | 22.7, 7.8 | 11.7, 3.7 | 10.8, 1.3 | 18.1, 5.8 |
| Held | 4.3, 0.6 | 2.9, 0.4 | 3.5, 0.5 | 3.3, 0.7 |

Playing the clips, the target pose alone differs as much, since the Animator
samples the clips differently at different frame times; with the pose held, the
springs keep their course within a centimeter as RMS at every frame time, which
`ClothSpringTests` holds for a zig-zag and a spin.

## Hair And Headwear

Hair is a garment of its own visual slot, `hair`, with a shape key
`UnderHeadwear`, the hair pressed flat to fit under hoods and helmets, made by
hand like the hair itself. Headwear says what happens to the hair: `hide` for a
closed helmet, `compress` for a hood or a hat, so the fringe and the neck hair
show under it, and `show` for a circlet or a hood down. Headwear covers the scalp
like any garment covers the body. A hood that can be up or down is two garments
of the same item. A hairstyle is a modeled template of the `hair` slot with its
textures, made on the head region of the cage.

The pipeline fits the pressed form to every body like the hair itself and checks
every template worn over hair against the form the hair takes under it. Unity
takes the pressed form in full when it puts a character together under headwear
that compresses the hair, so it costs nothing per frame.

The first hairstyle, `ShoulderHair`, is straight and thick in stylized locks
with a middle parting and a short fringe, covers the ears, and ends where it
lands on the shoulders; `DarkBrownHair` paints it dark brown with lighter
strands. The gugel `HoodUp` covers the head and the pressed hair with an opening
for the face and a short collar over the neck and the shoulders that ends above
the cloak's clasps, over the cloak. It has no point at the back, so it hangs on
no chain and the hood chains stay unused. `HoodDown` is the same hood pushed
back: its collar stays around the neck with its top rolled over into a turned
band, and the hood lies folded on the upper back over the cloak and the hair as
it is. A helmet is a modeled garment weighted to the head, not a rigid
attachment, and so will a mask be: `NasalHelm` is a pointed spangenhelm of iron
plates held by four riveted bands under a round cap, with a riveted brow band, a
leather lining that shows below the iron, and a nasal riveted to the brow band
that bends forward over the nose's root; it sits above the ears and compresses
the hair.

## Attachments

Weapons, shields, quivers, and backpacks are models on the skeleton's sockets.
A model's root object `Grip` marks where it is held, and the socket's position
and turn on each body type place it. A static model follows the hands and the
back without weights; a model with moving parts of its own, such as a bow's
limbs and string, is skinned to a skeleton of its own below `Grip`.

- **The model is the socket's**: an attachment is an art asset of its own with
  its socket in `asset.json`, modeled in the socket's axes around `Grip`; see
  [Attachments](ART_PIPELINE.md#attachments). The sockets stay as the rig places
  them, since a socket that moved would change the rig every garment, the cage,
  and the clips are fitted to; an item that needs another place carries it in
  its own model.
- **One renderer**: the body combines an attachment into its one mesh like a
  garment, every vertex weighted fully to the socket bone, so a crowd of armed
  bodies costs no renderer more, and a corpse is baked with what it still holds.
  A skinned attachment, such as the Training Shortbow, keeps its own bones: the
  body holds a copy of the model's bones below the socket, as they stand below
  the model's root, binds the combined mesh to them, and lets go of them when it
  puts the attachment away. A body holds one model of a skeleton. The body's
  bounds leave room for the hand to swing with it.
- **The hand closes**: the clip `Grip` closes the fingers around a handle of
  29 mm on the socket. `HumanLocomotion.controller` plays it on the layers
  `MainHandGrip` and `OffHandGrip`, each masked to one hand's fingers, and the
  body turns a layer on while that hand's socket holds an attachment, over every
  other clip, also `Death`.
- **One per socket**: an item shows its attachment only in the equipment slot of
  its socket, `main_hand` on `Socket_MainHand` and `off_hand` on
  `Socket_OffHand`, so a body holds one attachment per socket. A weapon in the
  main hand that the left hand holds, such as a bow, shows on `Socket_OffHand`;
  its item blocks the off hand, so no other item claims that socket.
- **A shield lies along the forearm**: the Heater Shield hangs from a handle in
  the fist and a strap around the forearm, so it hangs outward at the side with
  its point down in the gaits, and will face forward once a clip raises the arm
  in guard (planned). A round shield with a centre grip would lie flat under a
  hanging hand until such a clip exists.

Sheathed weapons are planned.

### Weapon Poses And Attack Clips

A weapon may name how its arm holds it and what its body does when it attacks:

- **A hold pose**: a staff held along the fist's axis from a hanging arm would
  point straight ahead at hip height, so the Oak Staff names the hold clip
  `HoldStaff`. The clip bends the right elbow so the forearm points forward with
  the thumb up, and the staff stands upright beside the right hip, its crystal
  leaning in toward the head and its foot out to the side, at least 31 mm from
  the thighs in every frame of `Walk` and `Jog` on both bodies.
  `HumanLocomotion.controller` plays a hold clip on the layer `MainHandHold`,
  masked to the right arm, at full weight over the locomotion clips while the
  main hand holds the weapon and the body lives and uses no tool, so the arm
  keeps the pose while the legs walk and jog.
- **An attack clip**: the staff names `Cast`, which the body plays once whenever
  its attack starts: a short wind-up, a thrust that tips the crystal about 30
  degrees toward the target while the open left hand points at it, and the way
  back to the hold pose, 0.9 s in all, striking 0.15 s in. The layer `Action`
  plays it over the hold pose, fading in over 0.06 s and out over 0.15 s. The
  fingers stay closed by `MainHandGrip`.
- **A launch point**: the staff names where its bolt leaves, the crystal 70 cm
  along the shaft in the socket's axes, as the thrust carries the crystal
  forward, and the bolt lands at the server's impact tick.
- **Timed to the release**: the server releases an attack when its weapon
  family's windup ends, so a blow lands, an arrow leaves, and an attack can be
  cancelled before it does; see
  [Target-Based Combat](SERVICE_FEATURES.md#target-based-combat). Each weapon
  with an attack clip names `attackRelease`, the seconds into the clip when its
  blow lands or its arrow or bolt leaves, equal to its family's windup. Every
  client starts the attacker's clip from the `AttackStarted` event, so the clip
  comes to its release as the batch of the release arrives: from its start,
  later for a longer windup, or further in when the event comes late, but never
  past the release. `AttackCancelled` ends the clip with its fade, and a body
  that starts to move during the clip ends it too; a body that still moves as
  the clip begins, such as one that just came into reach, keeps it.
- **Facing the target**: the character's facing is part of its movement, which
  turns only while it walks, so a body that stands and attacks turns itself to
  face its target, at 720 degrees per second, while its attack clip plays and
  for 1.5 s after it, so attacks in a row do not turn it back and forth; it faces
  its character's way again once it moves. The archer aims at its mark this way,
  on every client, from the target that `AttackStarted` names.
- **The whole body**: the archer's and the one-handed attacks move the whole
  body, the feet too. The layer `Action` is masked to the root, the spine, head,
  both legs, and both arms without the fingers, so the clip's steps, the turn and
  shift of its hips, and its arms play together. Without the root in the mask,
  Unity takes the hips from the lower layers, and the archer's turned stance
  would aim 40 degrees off the target.
- **The Iron Sword** names `SwordSlash`: a cock, a step in with the left foot
  into the cut, a follow-through, and the way back, 1.0 s, striking 0.35 s in.
  It has no hold clip; the arm hangs with the sword.
- **One-handed weapons** without a clip of their own, a melee weapon in one main
  hand, strike with `OneHandStrike`: a cock, a step into the blow, and the way
  back, 1.07 s, striking 0.4 s in.
- **The Iron Greataxe** names `HoldGreataxe`, the haft resting on the right
  shoulder with the edge up, and `GreataxeSwing`, a cock, a two-handed swing
  turning the torso, and a follow-through, 1.33 s, striking 0.6 s in. It names
  where the other hand holds the haft. Clips are keyed on the male body and
  retargeted, so while the swing plays, the other hand takes the haft wherever
  the clip brings it within 6 cm of that point, and lets it be beyond 18 cm,
  with the fingers of that hand closing as it takes it, like a pickaxe's.
- **The Training Shortbow** is held in the left hand: its item is a main-hand
  weapon that blocks the off hand, and its attachment's socket is `off_hand`. It
  names `HoldBow`, the left arm holding it down and forward, which the layer
  `OffHandHold`, masked to the left arm, plays like `MainHandHold`, and
  `BowShot`, 1.6 s: the archer's stance, with the hips turning 40 degrees, the
  left foot pivoting, and the right foot stepping back, the right hand on the
  string at 0.27 s, full draw to the jaw by 0.63 s, the loose at 0.8 s, and a
  follow-through.
- **The bow draws**: the bow is skinned to the `Bow` skeleton, a riser, two bones
  for each limb, a bone for each straight half of the string, and the nock. Its
  clip `Draw` bends the limbs from brace to full draw, and its draw length,
  0.62 m, says how far behind the arrow pass the nock comes at full draw. After
  the Animator, the drawing hand takes the string where it rests, the nock
  follows the hand's socket, the limbs bend by how far the string comes back,
  and each half of the string turns from its tip to the nock and stretches to
  reach it. The fingers of the drawing hand close on the string with
  `MainHandGrip` while they hold it. On the loose the string springs back to
  brace. Clips are keyed on the male body, so the drawing arm reaches for the
  resting string and then draws it along the arrow's line, from the bow straight
  back, by the same arm solver; a body with shorter arms draws less far.
- **The arrow is its own**: the Bronze Arrow is the arrow of every weapon that
  shoots arrows, not part of the bow. While the drawing hand holds the string an
  arrow lies on it, its nock at the string and its point through the arrow pass,
  and when the attack releases that arrow flies on from there to the target,
  where it lands at the server's impact tick. The archer may move while it
  flies.
- **Everyone sees it.** Every client plays the attack of the body that started
  it, from the `AttackStarted`, `AttackReleased`, and `AttackCancelled` events
  that name the attacker.

### Tools In Use

A tool is shown only while its character uses it: the Mining Pickaxe and the
Woodcutting Axe, the attachments of the Starter Pickaxe and the Woodcutting Axe
in the Tool slot, are held while the character gathers and put away when it
stops, and everyone sees it.

- **A tool is an attachment with a use clip**: it names the clip of its socket's
  animation library that the body plays meanwhile, `Mine` for the pickaxe and
  `Chop` for the axe, and, for a tool held with both hands, the point of the
  model where the other hand holds it, 33 cm down the haft from the right fist
  on the leather wrap near the butt. That hand's socket is turned half around
  the haft from the right hand's, so both thumbs point toward the head and both
  hands' knuckles toward the stroke.
- **It takes both hands.** A tool in use stands in for every attachment in the
  hands, so a sword and a shield are put away while their character gathers and
  come back when it stops. Only an item of the Tool slot is a tool; the Unity
  tests hold every tool item to it.
- **The swing.** `Mine` strikes the pickaxe down in front of the body at about
  knee height, lifts it over the right shoulder with its head behind the
  shoulder, and strikes again, and `Chop` lifts the axe over the right shoulder
  and hews diagonally down to the left at waist height, each 1.5 s, so two
  strokes fall in each 3 s gathering cycle, without matching the yield. Both
  keep every arm inside the humanoid muscle ranges and turn the torso, bend the
  knees, and look at the stroke. A body plays the clip while it stands and
  lives, and walking stops the swing at once, before the server's stop arrives.
- **The other hand holds the haft on every body.** The clips are keyed on the
  male body and retargeted to the female's other proportions, which moves her
  other hand by up to about 8 cm. After the Animator posed a body, its other
  hand is pinned on the tool's grip point with two-bone IK, and its collar bone
  turns forward by up to 15 degrees where the haft passes beyond the arm's
  reach; see [Player Bodies](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).
- **Everyone sees it.** The worker marks a gathering player as using its Tool
  slot, and the player's appearance carries the definition of the item there as
  `ToolInUse` while it does; see
  [Character Appearance](REALTIME_PROTOCOL.md#character-appearance). The Tool
  slot stays out of the visible equipment, so others see a tool only in use. The
  local body takes the tool from its own gathering state at once.

## Items

Items show their garments and attachments:

- **An item names its garments** in the client's item presentation, by the
  names of their garment textures, such as `WoolCloak` on `Cloak`, which name a
  template and its texture together, with the colors of their dye channels, or
  none for the textures' natural colors. The presentation is the client's, next
  to the item's icon and name; the server knows only the definition ids. Every
  item of an equipment slot that has a visual slot of its name, `head`, `body`,
  `legs`, `hands`, `feet`, and `back`, shows garments, and no other item does.
  The Unity tests hold every item to this, and every garment it names to every
  body. How the presentation is authored is in
  [Item Catalog Authoring](../content/items/README.md#unity-editor-workflow).
- **Dye colors belong to the definition**: a Blue Wool Robe and a Red Wool Robe
  are items of their own on the same garment. Dyeing an item in the game, such
  as at a dyer, would put colors on each item, and the look would take them from
  there instead (planned).
- **One garment per visual slot**: of two items whose garments fill one visual
  slot, the garment of the item in the equipment slot of the visual slot's name
  wins, such as a helmet in `head` over a robe's hood, and otherwise the item
  earlier in the equipment's order.
- **The hood is its own item**, the Wool Hood in `head`, that can be lowered: an
  item may name lowered garments, which it shows while its wearer has lowered
  it, such as `WoolHoodDown` for `WoolHoodUp`. The player lowers and raises the
  hood with a button in the inventory, and the server keeps the choice with the
  character's look.
- **Hair is part of the look**, not an item: a garment of the `hair` slot or
  none, chosen in character creation and changed in the game later, such as at
  a barber. Every other garment comes from the equipment.
- **An item names its attachment** in the presentation the same way, by the
  name of its attachment asset, such as `IronSword`; see
  [Attachments](#attachments). Every equipment slot of such an item is the
  attachment's socket, which the Unity tests hold every item to. A Tool slot
  item names a tool instead; see [Tools In Use](#tools-in-use).

The clothing and armor of the item catalog show garments this way, the wool set
also in blue and in red, and the weapons and the shield show attachments.

### The Look On The Server

Every client sees a character as the server stores it:

- **Content names what a look can be.** `content/characters/appearance.json`
  lists the kinds of a look, `face`, `eyes`, `scar`, `tattoo`, `underwear`, and
  `hair`, each with its rule, the body models, the default first, with the
  options each offers per kind and visual slot, the first of each its default,
  and the equipment slots whose items other players see. Options are named as
  the bodies' appearance catalogs name them; a Unity test holds the content to
  both catalogs and to `AppearanceRules`. How the file is loaded and validated
  is in [Character Appearance](../content/README.md#character-appearance).
- **GameService stores the look with the character**: the body model, the
  choices, and whether the headwear is lowered, in the Characters module's
  `character_appearances`; see
  [Table Ownership](GAME_SERVICE_MODULES.md#table-ownership). Character creation
  chooses the body model and the look and checks them strictly against what the
  body model offers; a character created without a look gets the default look of
  the default body model. GameService refuses to start when the content no
  longer offers what a stored look names, as it does for the item catalog.
- **One look serves every body.** A stored choice that the body model lacks is
  kept, as [The Look On The Skin](#the-look-on-the-skin) says, so a body change
  in Development keeps the rest of the look.
- **The worker sends the look to every client**: the join ticket carries the
  look, every spawn of a player carries its body model, choices, the items of
  its visible equipment slots in content order, and its headwear, and a change
  of equipment, a lowered or raised headwear, the tool it starts or stops using,
  or in Development another body model or look sends the new look to everyone
  who sees the player and to the player itself; see
  [Character Appearance](REALTIME_PROTOCOL.md#character-appearance).
- **The player lowers and raises the hood** through the worker, which asks
  GameService to store it. Only a GameService in the Development environment
  changes a body model or look in the game, for the Admin menu (F3); character
  creation chooses them otherwise.
- **Development bots** have no stored look and no items: the worker gives each a
  body model, a look, and an outfit of catalog items picked from its character id
  by a stable hash, so a crowd shows every body, face, and garment, and each bot
  keeps its own. The outfit is only shown. The visible slots include `main_hand`
  and `off_hand`, so bots hold weapons and shields too, and an item stays out of
  a slot that an item of an earlier slot blocks, such as a shield beside a
  two-handed bow.
- **A dying body keeps its clothes** until its character respawns, though its
  items already lie in its corpse. A player corpse shows the dead character's
  body model and look, with its headwear, wearing the items still in the
  corpse's equipment section, so a looted cloak leaves the corpse.

## Putting A Character Together In Unity

A character's look is its body type, its face, eyes, and paint, the garments per
visual slot, each a template with its texture and dye colors, its hair, and its
attachments. When the look changes, such as when equipment changes, the client
puts the character together again, never per frame:

- It leaves out the body faces that the worn garments cover, with the masks of
  their textures where a texture has its own, and moves the pull-in vertices
  inward.
- It leaves out the garment faces whose underlay a garment of a higher layer
  covers, and every slot a worn garment hides.
- It draws the hair in its normal or pressed form, or leaves it out under
  headwear that hides it.
- It joins the body, the garments, and the attachments into one skinned mesh on
  the character's skeleton, with the cloth chains, and one submesh per material.
  Materials are shared copies keyed by their texture, dye colors, and paint
  layers, and every character with the same look shares one mesh, so characters
  that look alike batch.

If a crowd needs fewer draw calls than one per material, garment textures move
into texture arrays that one garment material reads (planned). Unity imports a
model's triangles in an order of its own, so the body faces are found again by
position: every body exports its editable mesh next to its model, and the
appearance catalog maps every triangle of the body's parts and of each garment
to the face it comes from when the catalog is written in the Editor; see
[Unity Import Rules](ART_PIPELINE.md#unity-import-rules). How the client does
all this is in [Player Bodies](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).

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

A template's manifest may name another budget where its form needs it, such as
the nasal helmet's 2,500. A character in a full outfit draws about 15,000 to
18,000 triangles near the camera, because the covered body faces are gone.
Gloves with five fingers are made at the resolution of the body's hands, 1,520
triangles for both, and boots hide the body's feet, 1,328 triangles, so even
their budgets leave the character lighter than without them. Levels of detail
at half and a quarter of the triangles come with the first crowd measurement
that needs them.

## Open Questions

None at the moment. Planned work is listed under [Status](#status).
