using System.Globalization;
using System.IO;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // The def catalog: colors, footprints and labels for every ThingDef and
    // TerrainDef the bench has loaded. It is what makes the PNG channel (2.5)
    // mod-aware rather than hardcoded — `baseviz/catalog.py` resolves a cell's
    // colour and 2-char glyph out of this, so a modded wall renders as itself
    // and not as "unknown".
    //
    // ---------------------------------------------------------------------
    // PROVENANCE. This is BaseVizCatalogDumper, folded in. Upstream it was a
    // separate 20-file mod at rimworld-tools/baseviz/BaseVizCatalogDumper
    // (pinned at eabba3eb9fbc435bbdcb2a6250d1e3734170d992), with its own
    // About.xml, packageId, csproj and a prebuilt DLL. The dump logic is 143
    // lines with no Harmony and no dependency beyond DefDatabase, so as one
    // source file here it needs none of that apparatus: no second assembly to
    // ship, and no second assembly-provenance ritual — this repo commits a DLL
    // only via a standalone `Build:` commit whose pdb path is checkable, and a
    // vendored third-party binary satisfies none of that.
    //
    // TWO DELIBERATE CHANGES ON THE WAY IN, both of which move the seam:
    //
    // 1. WHEN. Upstream ran on [StaticConstructorOnStartup], so the catalog
    //    appeared as a side effect of launching the game. That is exactly the
    //    shape AutoRimmer exists to replace — observation happens through
    //    verbs, on request, with a result the caller can key on. It is a verb
    //    now. The cost is honest and stated: nothing writes the catalog
    //    automatically any more, so `rwa render` must be able to say "no
    //    catalog yet, call catalog-dump" rather than silently rendering grey.
    //    It does; see rwa's cmd_render.
    //
    // 2. WHERE. Upstream wrote GenFilePaths.ConfigFolderPath/baseviz_catalog.json
    //    — RimWorld's own config directory. This writes <protocol root>/catalog.json,
    //    beside commands/, results/, journal/ and status.json, because that is
    //    where this system's artifacts live and because `rwa` already resolves
    //    that root. The render path therefore needs no RIMWORLD_ROOT, no
    //    RIMWORLD_TOOLS and no knowledge of RimWorld's config layout at all.
    //    Consequence worth knowing: an existing
    //    <bench>/config/.../baseviz_catalog.json is now a stale orphan. Nothing
    //    reads it; delete it if it confuses you.
    //
    // The [DebugAction] entry is gone with the startup hook — one route in, and
    // it is the verb.
    //
    // Runs on the main thread and so needs a loaded game. Upstream could dump
    // from the main menu (defs load before it), but a poller-thread verb may
    // never touch Verse, and DefDatabase is Verse. In practice every render
    // workflow has a game loaded, so this costs nothing real.
    public static class CatalogDump
    {
        public const string FileName = "catalog.json";

        // Written by the dumper and read by baseviz/catalog.py's Catalog.load.
        // Bumped only when the JSON SHAPE changes; the def set moving with the
        // modlist is not a schema change.
        public const int SchemaVersion = 1;

        [Verb("catalog-dump")]
        public static object Dump(VerbContext ctx)
        {
            var sb = new StringBuilder(1 << 20);
            sb.Append("{\"defs\":{");
            bool first = true;
            int things = 0, terrains = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs)
            {
                first = WriteThing(sb, d, first);
                things++;
            }
            foreach (TerrainDef t in DefDatabase<TerrainDef>.AllDefs)
            {
                first = WriteTerrain(sb, t, first);
                terrains++;
            }
            sb.Append("}}");

            string path = Path.Combine(Poller.Root, FileName);
            string json = sb.ToString();
            AtomicWrite(path, json);
            Log.Message($"[AutoRimmer] catalog dumped to {path} ({things + terrains} defs)");

            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["path"] = path,
                ["file"] = FileName,
                ["schema"] = SchemaVersion,
                ["defs"] = things + terrains,
                ["things"] = things,
                ["terrains"] = terrains,
                ["bytes"] = json.Length,
            };
        }

        // Same tmp+rename discipline the poller uses for results: `rwa render`
        // may be reading this file while a dump runs, and a half-written
        // catalog parses as a JSON error rather than as "try again".
        private static void AtomicWrite(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static bool WriteThing(StringBuilder sb, ThingDef d, bool first)
        {
            if (!first) sb.Append(',');
            Key(sb, d.defName);
            sb.Append('{');
            Str(sb, "kind", "thing");
            Str(sb, "thingCategory", d.category.ToString());
            Str(sb, "designationCategory", d.designationCategory?.defName ?? "");
            sb.Append("\"size\":[").Append(d.size.x).Append(',').Append(d.size.z).Append("],");
            Bool(sb, "rotatable", d.rotatable);
            Bool(sb, "stuffable", d.MadeFromStuff);
            Bool(sb, "isStuff", d.IsStuff);
            Color(sb, "color", d.graphicData?.color);
            Color(sb, "stuffColor", d.IsStuff ? d.stuffProps?.color : (Color?)null);
            Str(sb, "mod", d.modContentPack?.PackageId ?? "");
            StrLast(sb, "label", d.label ?? "");
            sb.Append('}');
            return false;
        }

        private static bool WriteTerrain(StringBuilder sb, TerrainDef t, bool first)
        {
            if (!first) sb.Append(',');
            Key(sb, t.defName);
            sb.Append('{');
            Str(sb, "kind", "terrain");
            Str(sb, "thingCategory", "");
            Str(sb, "designationCategory", t.designationCategory?.defName ?? "Floors");
            sb.Append("\"size\":[1,1],");
            Bool(sb, "rotatable", false);
            Bool(sb, "stuffable", false);
            Bool(sb, "isStuff", false);
            Color(sb, "color", t.color);
            sb.Append("\"stuffColor\":null,");
            Str(sb, "mod", t.modContentPack?.PackageId ?? "");
            StrLast(sb, "label", t.label ?? "");
            sb.Append('}');
            return false;
        }

        // ---- JSON helpers (hand-rolled: 3849 defs through MiniJson's object
        // model would allocate a dictionary per def for no gain) ----
        private static void Key(StringBuilder sb, string k) => Esc(sb, k).Append(':');

        private static void Str(StringBuilder sb, string k, string v) =>
            Esc(Esc(sb, k).Append(':'), v).Append(',');

        private static void StrLast(StringBuilder sb, string k, string v) =>
            Esc(Esc(sb, k).Append(':'), v);

        private static void Bool(StringBuilder sb, string k, bool v) =>
            Esc(sb, k).Append(':').Append(v ? "true" : "false").Append(',');

        private static void Color(StringBuilder sb, string k, Color? c)
        {
            Esc(sb, k).Append(':');
            if (c == null) { sb.Append("null,"); return; }
            Color col = c.Value;
            sb.Append('[')
              .Append(Mathf.RoundToInt(col.r * 255f)).Append(',')
              .Append(Mathf.RoundToInt(col.g * 255f)).Append(',')
              .Append(Mathf.RoundToInt(col.b * 255f)).Append("],");
        }

        private static StringBuilder Esc(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ')
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"');
        }
    }
}
