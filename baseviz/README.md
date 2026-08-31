# baseviz — def catalog, layout IR, and the PNG render channel

Vendored into this repo by spec 2.5 (git-bug `f7b6207`). MIT, same as the rest
of the repo; the root `LICENSE` covers it and there is deliberately no second
LICENSE file here, because a nested one would imply a boundary that does not
exist — same author, same terms.

## Provenance

Origin: `/home/dorian/projects/rimworld-tools/baseviz/`, pinned at

    eabba3eb9fbc435bbdcb2a6250d1e3734170d992   ("Initial import: rimworld-tools source")

`rimworld-tools` is a local-only git repo with **no remote**, which is the whole
reason this code is here. Spec 2.5 was the one spec that could only be done on
one machine, and the cause was not the platform — it was that baseviz had never
left that disk and an unversioned dependency cannot be pinned. Vendoring ends
that: the code now lives in a repo with a remote, at a known commit, reviewable
in the same history as the verb it draws.

**What this does *not* fix**, stated so nobody reads more into it later: the
render channel still runs only where python runs, and BORGES has no python at
all, so `rwa` cannot run there. That is a pre-existing condition of spec 1.4,
not something 2.5 introduced or was asked to solve.

`rimworld-tools` itself stays exactly as it was — unversioned in spirit,
reference-only, nothing published. Nothing decompiled crossed; nothing here
derives from decompiled RimWorld source. `CatalogDump.cs` (now
`Source/AutoRimmer/CatalogDump.cs`) only ever compiled against the shipped
assemblies, which is ordinary modding.

## What came across, and what did not

Upstream was 58 files / 780 KB. About 93% of that was sample data, browser
screenshots, scratch iterations and build intermediates. What crossed is the
library:

| file | role |
|---|---|
| `catalog.py` | the def catalog: colours, footprints, the 2-char glyph, the token parser |
| `ir.py` | KCSG `StructureLayoutDef` XML ⇄ layout IR (spec 3.3 will want this) |
| `canvas.py` | hand-editable ASCII canvas ⇄ IR, plus a composite view |
| `server.py` + `static/` | the localhost browser viewer |
| `png.py` | **new** — a deterministic stdlib PNG encoder and drawing surface |
| `render.py` | **new** — draws a `map-dump` result; the actual PNG channel |

Deliberately left behind: `__pycache__/`, `BaseVizCatalogDumper/Source/obj/`
(15 files, four of which publish absolute `/home/dorian/.steam` and
`/home/dorian/.nuget` paths — this repo is public), the prebuilt
`BaseVizCatalogDumper.dll` and its `.pdb`, the three `designs/*/final_render.png`
browser screenshots, the three one-off `designs/*/build.py` authoring scripts,
and `work/` (scratch `.v2/.v3/.v4` iterations, one of which is byte-identical
to a file in `designs/`).

The dumper mod itself did not cross as a mod. Its 143 lines are
`Source/AutoRimmer/CatalogDump.cs` now, which is why `profile/make-profile-agent.sh`
no longer links `Mods/BaseVizCatalogDumper`. See that file's header for the two
deliberate behaviour changes (it is a verb, not a startup hook; it writes into
the protocol root, not RimWorld's config directory).

## Changes made on the way in

Each is marked `Vendored-in fix/change (2.5)` at its site.

1. **`catalog.py`'s `RIMWORLD_ROOT` default is gone.** It used to default to a
   hardcoded `_RimWorld-Testing` path — the one install this workspace forbids
   every agent to touch. A default pointing at a forbidden install is worse than
   no default: it works quietly until it does the wrong thing to the wrong game.
   `rw_root()` now raises and names the variable. Only the offline-XML path
   needs it; `Catalog.load` takes an explicit path, so the render channel needs
   no environment at all.

2. **Three iteration-order defects fixed**, because "same dump twice →
   byte-identical PNG" runs straight through them:
   - `Catalog.__init__` sorted defNames by length with a stable sort, so
     equal-length names tie-broke on dict insertion order — i.e. on the C#
     `DefDatabase.AllDefs` order baked into the dump — and `parse_token` returns
     the *first* prefix match. Now `(-len, name)`, a total order.
   - `Catalog.build` walked `rglob("*.xml")` unsorted into a last-writer-wins
     dict. Now sorted. (`server.py` already sorted its rglob; this one didn't.)
   - `_discover_mod_paths` walked `iterdir()` unsorted, same last-writer-wins.
     Now sorted.

3. **`server.py` no longer needs `RIMWORLD_ROOT` at import time.** Its layout
   search root was a module-level constant built from the old default, so
   merely importing the package required the variable. It is a function now.

4. **`Catalog.spec_for(defName, stuff, rot)` added**, and `render_spec` delegates
   to it. `render_spec` takes a KCSG layout *token* and has to guess where the
   defName ends; the map dump has def and stuff as separate fields already, so
   the render path skips that guess and its ambiguity entirely.

## The PNG channel

There was no PNG renderer upstream, and this is worth stating plainly because
both DESIGN.md and spec 2.5 said there was. `canvas.py` is ASCII on both its
surfaces (its own docstring: "Two surfaces, both ASCII"); the colour grid was
`static/viewer.js`, an HTML5 canvas in a browser; and the three
`final_render.png` files were browser screenshots — three differently-sized
layouts, all exactly 1400×1100, which nothing in the tree could regenerate.

So `png.py` and `render.py` are new. `png.py` writes PNG with `zlib` and
`struct` and nothing else. Pillow was available on the box where this was
written and was still the wrong choice: `rwa`'s house rule is stdlib-only so it
runs from a bare shell on either bench, and — the deciding reason — Pillow's
encoder output is not pinned across its own versions, which would make the
determinism acceptance an article of faith instead of a property of sixty lines
we control.

    rwa catalog-dump                                        # colours, once per bench session
    rwa render --rect 100,110,40,30 --out base.png --scale 16
    rwa render --dump saved.json --out base.png             # offline, no bench

`rwa render --help` (or `rwa help`) has the full flag list.

## Using it directly

Relative imports throughout, so it is a package, not a script collection:

    cd <repo root> && python3 -m baseviz --help

`python3 baseviz/__main__.py` does not work and never did.

Python floor is 3.7 (`from __future__ import annotations`, `dataclasses`,
`ThreadingHTTPServer`). No third-party dependencies anywhere.
