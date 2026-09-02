---
name: a-glyph-is-topology-not-identity
trigger: any moment a rect is chosen off `map-view` — designating, siting a layout, picking a mining face
severity: Important
confidence: verified-in-source
source: run m1-20260901 — `designate mine` over a rect picked off `map-view`, aimed at compacted steel, accepted 14 cells of whatever rock was exposed; git-bug 855117a
---

**What.** `map-view` is a **fixed-width channel**: one ASCII character per cell,
so more defs than characters means glyphs are shared. Its own legend says so —

    "%": "sandstone | marble | compacted steel"

— one glyph for the thing worth mining and the two things that are not. That
collision is the documented cost of the channel and not a defect in it. What
follows from it is the rule: **a glyph answers a TOPOLOGY question, a def-keyed
query answers an IDENTITY question, and a designation is an identity question.**

**Why.** The channel publishes its own identity for exactly this reason.
`Spatial.Render`'s `channel` block carries `alphabet: "map-view/ascii-1"` and
`distinct_from: "baseviz-catalog/1"`, and 2.5's independence argument
(git-bug `e6faa51`) is that two channels which cannot be shown to share a symbol
table are not a cross-check — **never compare a glyph from one channel against a
glyph from another.** This lesson is the same argument one level down: never
compare a glyph against a *def*, either. The `|` in a legend entry is the channel
telling you it cannot answer the question you are about to ask it.

**What it cost.** `m1-20260901` aimed at the compacted-steel face and issued

    designate {type:"mine", rect:[131,116,6,10], max_cells:600}  ->  accepted 14 of 60

with 22 cells not designatable and 24 fogged. The read that would have aimed it
was already in hand and was used to *find* the face, then not used to
*designate* it:

    nearest {def:"MineableSteel", from:"114,129"}
      -> (133,122) (133,121) (134,120) (134,119), pool: 274

Checked against the run's own saves afterwards, the rect held no sandstone at
all — at day 17 its still-unmined cells were 13 `MineableSteel` and 8 `Marble`
— so the mix was better than the report feared. **That is the point.** Nobody
could tell, from `accepted: 14`, which it had been. The rect also *under*-covered
the ore: 13 steel cells inside it were never designated and were still standing
20 days later, because a rect drawn on a glyph catches the exposed face and stops.

**How to apply.**

1. **Find with a def-keyed query.** `nearest {def:"MineableSteel", from:…}` or
   `things {def:…}` return exact cells and a `pool` count.
2. **Seed, do not paint.** For ore, `designate {type:"mine-vein", cells:[…]}`
   from those cells — `Designator_MineVein.DesignateSingleCell` flood-fills the
   whole contiguous vein of the same def, so one seed does the body a rect can
   only nibble at. It takes ore only (`ThingDef.building.veinMineable`).
3. **Read `composition` on the way back.** `designate` now publishes a per-def
   rollup of what actually landed, with `mineable_thing` and the yield each cell
   produces. `MineableSteel: 4, Marble: 8` is the answer `accepted: 14` never was.
4. **Use `map-view` for what it is good at**: where the rock face is, how the
   room closes, what adjoins what. Then ask a def query before you act on it.

**The general rule:** a collapsed alphabet is a picture, and you do not aim from
a picture. `[[stockpile-scope-hides-your-own-supplies]]` is the same shape from a
different direction — a truthful number answering a different question than the
one you asked.

**Retire when.** `map-view` grows a per-cell identity channel that is not
collapsed, or a `designate` argument takes a def filter directly so a rect can be
narrowed to one def server-side. Until then the two-step (query, then designate)
is the only aimed route, and `composition` is what tells you afterwards whether
you hit.
