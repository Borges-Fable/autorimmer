using System.Collections.Generic;
using System.IO;
using System.Text;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    public static class JournalVerbs
    {
        // Reads the CURRENT session's journal file. Off-thread: file I/O only,
        // no Verse. The file is also directly tail-able from a shell; this verb
        // exists for filtered, protocol-shaped reads (rwa journal, spec 1.4).
        [Verb("journal", MainThread = false)]
        public static object Read(VerbContext ctx)
        {
            long sinceSeq = ctx.Args.Int("since_seq", 0);
            int sinceTick = ctx.Args.Int("since_tick", -1);
            int limit = ctx.Args.Int("limit", 500);
            if (limit < 1) limit = 1;
            if (limit > 2000) limit = 2000;
            var types = ctx.Args.StrList("types");

            var events = new List<object>();
            long lastSeq = 0;
            bool truncated = false;
            using (var fs = new FileStream(Journal.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var evt = MiniJson.Parse(line);
                    if (evt == null) continue;
                    if (!evt.TryGetValue("seq", out var s) || !(s is double seq)) continue;
                    if (seq > lastSeq) lastSeq = (long)seq;
                    if (seq <= sinceSeq) continue;
                    if (sinceTick >= 0 && evt.TryGetValue("tick", out var t) && t is double tick && tick < sinceTick) continue;
                    if (types.Count > 0 && (!evt.TryGetValue("type", out var ty) || !(ty is string type) || !types.Contains(type))) continue;
                    if (events.Count >= limit) { truncated = true; break; }
                    events.Add(evt);
                }
            }
            return new Dictionary<string, object>
            {
                ["file"] = Journal.CurrentFile,
                ["count"] = events.Count,
                ["truncated"] = truncated,
                ["last_seq"] = lastSeq,
                ["events"] = events,
            };
        }

        // Deliberate stimulus for the journal's acceptance (spec 1.2): fires the
        // spec's scripted sequence through real game APIs, because nobody can
        // click dev buttons on an unattended bench. This verb MUTATES game state
        // by design — it is a minimal forerunner of the 3.1 dev layer, gated on
        // dev mode, and every step it takes lands in the journal as a dev event
        // (the provenance rule 3.1 will inherit). 3.1 supersedes it.
        [Verb("journal-selftest")]
        public static object Selftest(VerbContext ctx)
        {
            if (!Prefs.DevMode)
                throw new VerbArgsException("journal-selftest requires devMode=True (it mutates game state)");

            var steps = ctx.Args.StrList("steps");
            if (steps.Count == 0)
                steps = new List<string> { "letter", "message", "error", "downed", "break" };

            var executed = new List<object>();
            var extras = new Dictionary<string, object>();
            var map = Find.CurrentMap;
            // Snapshot: the underlying cached list rebuilds on every access
            // (see DigestVerb.ColonistSection).
            var colonists = map == null ? null : new List<Pawn>(map.mapPawns.FreeColonistsSpawned);

            foreach (var step in steps)
            {
                string target = null;
                switch (step)
                {
                    case "letter":
                    {
                        // letter_delay_ticks queues the letter for future
                        // arrival (LetterStackTick drains the queue per tick) —
                        // the deterministic mid-advance stimulus for 1.3's
                        // until:letter acceptance.
                        int delay = ctx.Args.Int("letter_delay_ticks", 0);
                        Find.LetterStack.ReceiveLetter("AutoRimmer selftest",
                            "Deliberate selftest letter (journal acceptance, spec 1.2).",
                            LetterDefOf.NeutralEvent, null, delay);
                        if (delay > 0) target = $"delayed {delay} ticks";
                        break;
                    }
                    case "message":
                        Messages.Message("[AutoRimmer] selftest message", MessageTypeDefOf.NeutralEvent, historical: false);
                        break;
                    case "error":
                        Log.Error("[AutoRimmer] selftest-induced red error (deliberate)");
                        break;
                    case "raid":
                    {
                        if (map == null) throw new VerbArgsException("raid needs a current map");
                        var parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                        parms.forced = true;
                        bool fired = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
                        target = fired ? "fired" : "refused";
                        break;
                    }
                    case "downed":
                    {
                        Pawn pawn = null;
                        if (colonists != null)
                            foreach (var c in colonists) { if (!c.Downed) { pawn = c; break; } }
                        if (pawn == null) throw new VerbArgsException("downed needs a standing free colonist");
                        HealthUtility.DamageUntilDowned(pawn, allowBleedingWounds: false);
                        target = pawn.LabelShortCap.ToString();
                        break;
                    }
                    case "break":
                    {
                        var pawn = colonists != null && colonists.Count > 1 ? colonists[1]
                            : (colonists != null && colonists.Count > 0 ? colonists[0] : null);
                        if (pawn == null) throw new VerbArgsException("break needs a spawned free colonist");
                        bool started = pawn.mindState.mentalStateHandler.TryStartMentalState(
                            MentalStateDefOf.Berserk, "selftest", forced: true, forceWake: true);
                        target = pawn.LabelShortCap.ToString() + (started ? "" : " (refused)");
                        break;
                    }
                    case "save":
                        GameDataSaveLoader.SaveGame(ctx.Args.Str("save_name", "journal-accept"));
                        target = ctx.Args.Str("save_name", "journal-accept");
                        break;
                    case "stockpile":
                    {
                        // Zone the crash scatter so the vanilla resource
                        // counter (stockpiles only — SlotGroup-based) has
                        // something to count, and return raw-thing tallies as
                        // the independent hand-computation for 2.1's food-days
                        // check. Counts here come from ListerThings directly,
                        // NOT from ResourceCounter — two readers, one truth.
                        if (map == null) throw new VerbArgsException("stockpile needs a current map");
                        var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                        map.zoneManager.RegisterZone(zone);
                        int cells = 0, meals = 0, steel = 0, silver = 0, wood = 0, meds = 0;
                        float nutrition = 0f;
                        foreach (var def in new[] { ThingDefOf.MealSurvivalPack, ThingDefOf.Steel,
                                                    ThingDefOf.Silver, ThingDefOf.WoodLog, ThingDefOf.MedicineHerbal })
                        {
                            foreach (var thing in map.listerThings.ThingsOfDef(def).ToArray())
                            {
                                if (!thing.Spawned || thing.Position.Fogged(map)) continue;
                                if (map.zoneManager.ZoneAt(thing.Position) == null)
                                {
                                    zone.AddCell(thing.Position);
                                    cells++;
                                }
                                if (def == ThingDefOf.MealSurvivalPack)
                                {
                                    meals += thing.stackCount;
                                    nutrition += def.GetStatValueAbstract(StatDefOf.Nutrition) * thing.stackCount;
                                }
                                else if (def == ThingDefOf.Steel) steel += thing.stackCount;
                                else if (def == ThingDefOf.Silver) silver += thing.stackCount;
                                else if (def == ThingDefOf.WoodLog) wood += thing.stackCount;
                                else if (def == ThingDefOf.MedicineHerbal) meds += thing.stackCount;
                            }
                        }
                        extras["stockpile"] = new Dictionary<string, object>
                        {
                            ["cells"] = cells,
                            ["meals"] = meals,
                            ["nutrition"] = nutrition,
                            ["steel"] = steel,
                            ["silver"] = silver,
                            ["wood"] = wood,
                            ["meds_herbal"] = meds,
                        };
                        target = cells + " cells";
                        break;
                    }
                    default:
                        throw new VerbArgsException($"unknown step '{step}' (letter|message|error|raid|downed|break|save)");
                }
                Journal.Emit("dev", new Dictionary<string, object>
                {
                    ["verb"] = "journal-selftest",
                    ["step"] = step,
                    ["target"] = target,
                }, Find.TickManager.TicksGame);
                executed.Add(step);
            }
            var data = new Dictionary<string, object> { ["executed"] = executed };
            foreach (var kv in extras) data[kv.Key] = kv.Value;
            return data;
        }
    }
}
