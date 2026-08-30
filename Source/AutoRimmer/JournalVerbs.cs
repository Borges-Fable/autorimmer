using System;
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
            // Long, not Int: journal seq is a long and narrowing it here would
            // wrap a large `since_seq` negative (2.6 nit).
            long sinceSeq = ctx.Args.Long("since_seq", 0);
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
        //
        // FIXTURE STEPS. `stockpile` (2.1) and `alerts` / `alerts-clear` /
        // `colonists` / `power` (2.6) exist because three acceptance lines need
        // game state no shipped verb can create and 3.1's dev layer does not
        // exist yet: 15 active alerts with a Critical arriving LAST, a
        // 20-colonist digest, and a power grid with generation, a battery and a
        // load. Each is declared as an undeclared addition on its own issue,
        // keeps the Prefs.DevMode gate, and journals a `dev` event per step for
        // provenance. All of them are superseded by 3.1.
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
                    case "alerts":
                    {
                        // 2.6 blocker 2's acceptance state: N active alerts with
                        // the Critical arriving LAST, which is exactly the case
                        // discovery-order truncation dropped.
                        //
                        // These go straight into AlertsReadout.activeAlerts —
                        // the same list the game draws from and the same list
                        // AlertScanner.Snapshot reads, so the digest's sort runs
                        // against real readout state, not a stub. Safe because
                        // Alert_Custom / Alert_CustomCritical subclasses are
                        // EXCLUDED from the readout's allAlertTypesCached
                        // (decompiled AlertsReadout.cs:64), so the readout never
                        // instantiates ours and its round-robin
                        // CheckAddOrRemoveAlert never removes them either.
                        int count = ctx.Args.Int("alert_count", 15);
                        if (count < 1 || count > 60) throw new VerbArgsException("alert_count must be 1..60");
                        bool criticalLast = ctx.Args.Bool("alert_critical_last", true);
                        int made = 0;
                        for (int i = 0; i < count; i++)
                        {
                            bool last = i == count - 1;
                            Alert alert;
                            if (last && criticalLast)
                                alert = new Alert_AutoRimmerFixtureCritical($"[fixture] critical #{i + 1}");
                            else
                                alert = new Alert_AutoRimmerFixture($"[fixture] medium #{i + 1}", AlertPriority.Medium);
                            alert.Recalculate(); // sets cachedActive/cachedLabel; Label is "" until it runs
                            if (AlertScanner.FixtureInject(alert)) made++;
                        }
                        target = made + " injected" + (criticalLast ? ", Critical last" : "");
                        extras["alerts"] = new Dictionary<string, object>
                        {
                            ["injected"] = made,
                            ["critical_last"] = criticalLast,
                        };
                        break;
                    }
                    case "alerts-clear":
                    {
                        int removed = AlertScanner.FixtureClear(
                            a => a is Alert_AutoRimmerFixture || a is Alert_AutoRimmerFixtureCritical);
                        target = removed + " removed";
                        extras["alerts_cleared"] = removed;
                        break;
                    }
                    case "colonists":
                    {
                        // 2.6 blocker 3's acceptance state: a colony large
                        // enough that an uncapped colonist list blows the digest
                        // budget. Vanilla PawnGenerator + GenSpawn, the same
                        // pair the game's own dev spawner uses.
                        if (map == null) throw new VerbArgsException("colonists needs a current map");
                        int want = ctx.Args.Int("colonist_target", 20);
                        if (want < 1 || want > 60) throw new VerbArgsException("colonist_target must be 1..60");
                        // Snapshot: the cached list rebuilds on every access.
                        var have = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
                        IntVec3 anchor = have.Count > 0 ? have[0].Position : map.Center;
                        int added = 0;
                        while (have.Count + added < want && added < 60)
                        {
                            var newPawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                            GenSpawn.Spawn(newPawn, CellFinder.RandomClosewalkCellNear(anchor, map, 12), map);
                            added++;
                        }
                        target = added + " spawned";
                        extras["colonists"] = new Dictionary<string, object>
                        {
                            ["before"] = have.Count,
                            ["spawned"] = added,
                            ["now"] = new List<Pawn>(map.mapPawns.FreeColonistsSpawned).Count,
                        };
                        break;
                    }
                    case "power":
                    {
                        // 2.6 should-fix 5's acceptance state: one grid carrying
                        // generation, storage and a load, so gen and draw are
                        // separately checkable against the game's own numbers.
                        // Wood-fired generator (-1000 basePowerConsumption =
                        // 1000 W produced), one battery (600 Wd capacity), and N
                        // standing lamps at 30 W each, all on a conduit run.
                        // Leaving the generator UNFUELLED is a legitimate second
                        // fixture: CompPowerPlant.UpdateDesiredPowerOutput zeroes
                        // PowerOutput with no fuel, which is the deficit case
                        // battery_days exists for.
                        if (map == null) throw new VerbArgsException("power needs a current map");
                        int lamps = ctx.Args.Int("power_lamps", 1);
                        if (lamps < 0 || lamps > 6) throw new VerbArgsException("power_lamps must be 0..6");
                        float fuel = (float)ctx.Args.Num("power_fuel", 75);
                        float stored = (float)ctx.Args.Num("power_stored", 300);
                        var colonistsNow = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
                        IntVec3 anchor = colonistsNow.Count > 0 ? colonistsNow[0].Position : map.Center;
                        int width = 6 + lamps * 2;
                        var area = FindClearRect(map, anchor, width, 3);

                        // Conduit run along the bottom row; everything else sits
                        // on the row above it, cardinally adjacent, so one net.
                        for (int x = area.minX; x <= area.maxX; x++)
                            SpawnPlayerBuilding(map, ThingDefOf.PowerConduit, new IntVec3(x, 0, area.minZ));
                        var gen = SpawnPlayerBuilding(map, ThingDefOf.WoodFiredGenerator,
                            new IntVec3(area.minX, 0, area.minZ + 1));
                        var battery = SpawnPlayerBuilding(map, ThingDefOf.Battery,
                            new IntVec3(area.minX + 3, 0, area.minZ + 1));
                        var lampList = new List<object>();
                        for (int i = 0; i < lamps; i++)
                        {
                            var lamp = SpawnPlayerBuilding(map, ThingDefOf.StandingLamp,
                                new IntVec3(area.minX + 5 + i * 2, 0, area.minZ + 1));
                            if (lamp != null) lampList.Add(Positions.Out(lamp.Position));
                        }
                        float fuelled = 0f, charged = 0f;
                        var refuelable = (gen as ThingWithComps)?.GetComp<CompRefuelable>();
                        if (refuelable != null && fuel > 0f) { refuelable.Refuel(fuel); fuelled = refuelable.Fuel; }
                        var batteryComp = (battery as ThingWithComps)?.GetComp<CompPowerBattery>();
                        if (batteryComp != null && stored > 0f) { batteryComp.AddEnergy(stored); charged = batteryComp.StoredEnergy; }

                        target = $"grid at {area.minX},{area.minZ} ({lamps} lamps)";
                        extras["power"] = new Dictionary<string, object>
                        {
                            ["at"] = Positions.Out(new IntVec3(area.minX, 0, area.minZ)),
                            ["conduits"] = area.Width,
                            ["generator"] = gen != null,
                            ["fuel"] = fuelled,
                            ["battery_stored_wd"] = charged,
                            ["lamps"] = lampList,
                            // The independent hand-computation, from the defs
                            // rather than from PowerNet: this is what the digest
                            // must agree with once the nets rebuild.
                            ["expect_gen_w"] = fuelled > 0f ? 1000f : 0f,
                            ["expect_draw_w"] = lamps * 30f,
                        };
                        break;
                    }
                    default:
                        throw new VerbArgsException($"unknown step '{step}' (letter|message|error|raid|downed|break|save|stockpile|alerts|alerts-clear|colonists|power)");
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

        // First clear w x h footprint at Chebyshev distance 0,1,2... from
        // `anchor`: unfogged, standable, no edifice, heavy-affordance terrain.
        // Deliberately not CellFinder: the fixture wants a RECT, and wants to
        // fail loudly rather than fall back to a cell that will not hold a grid.
        private static CellRect FindClearRect(Map map, IntVec3 anchor, int w, int h)
        {
            for (int ring = 0; ring < 60; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        var rect = new CellRect(anchor.x + dx, anchor.z + dz, w, h);
                        if (rect.minX < 1 || rect.minZ < 1
                            || rect.maxX >= map.Size.x - 1 || rect.maxZ >= map.Size.z - 1) continue;
                        bool ok = true;
                        foreach (var c in rect)
                        {
                            if (c.Fogged(map) || c.GetEdifice(map) != null || !c.Standable(map)
                                || !map.terrainGrid.TerrainAt(c).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                            {
                                ok = false;
                                break;
                            }
                        }
                        if (ok) return rect;
                    }
                }
            }
            throw new VerbArgsException($"no clear {w}x{h} area within 60 cells of ({anchor.x},{anchor.z})");
        }

        // Faction set BEFORE spawn (SetFactionDirect, the game's own dev-spawn
        // route) so the building is the player's from its first tick — power
        // nets and Building.DeconstructibleBy both key on it.
        private static Thing SpawnPlayerBuilding(Map map, ThingDef def, IntVec3 at)
        {
            var thing = ThingMaker.MakeThing(def);
            thing.SetFactionDirect(Faction.OfPlayer);
            return GenSpawn.Spawn(thing, at, map, Rot4.North, WipeMode.Vanish);
        }
    }

    // Fixture alerts for journal-selftest's `alerts` step (2.6 acceptance).
    // Subclasses of Alert_Custom / Alert_CustomCritical, which AlertsReadout
    // EXCLUDES from allAlertTypesCached (decompiled AlertsReadout.cs:64 filters
    // both by IsAssignableFrom), so the game never instantiates them, never
    // polls them and they cost exactly nothing when the fixture is not running.
    // Superseded by 3.1's dev layer.
    public class Alert_AutoRimmerFixture : Alert_Custom
    {
        public Alert_AutoRimmerFixture()
        {
            report = AlertReport.Inactive;
        }

        public Alert_AutoRimmerFixture(string text, AlertPriority priority)
        {
            label = text;
            explanation = "AutoRimmer fixture alert (spec 2.6 acceptance).";
            report = AlertReport.Active;
            defaultPriority = priority;
        }
    }

    public class Alert_AutoRimmerFixtureCritical : Alert_CustomCritical
    {
        // Alert_Critical.AlertActiveUpdate pops a ThreatBig message every other
        // frame while the alert is active. A fixture proving a SORT must not
        // flood the message stream (and the journal) to do it.
        protected override bool DoMessage => false;

        public Alert_AutoRimmerFixtureCritical()
        {
            report = AlertReport.Inactive;
        }

        public Alert_AutoRimmerFixtureCritical(string text)
        {
            label = text;
            explanation = "AutoRimmer fixture alert (spec 2.6 acceptance).";
            report = AlertReport.Active;
        }
    }
}
