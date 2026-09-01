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
        // Prefix on every letter the `timeout-letter` fixture creates, so
        // `dialogs-clear` can drop exactly those and nothing the colony
        // actually cares about.
        public const string FixtureLetterLabel = "[AutoRimmer] timeout letter";

        // The ONE text both the `error` and `error-at` steps log, because
        // 1.5 blocker 3's acceptance turns entirely on the journal's per-text
        // dedupe counting them as the same key.
        public const string SelftestErrorText = "[AutoRimmer] selftest-induced red error (deliberate)";

        // -1 = disarmed. Set by the `main-menu` fixture step, polled by
        // AgentGameComponent, cleared at every game boundary so a fixture
        // armed against one colony can never fire against the next.
        public static int MainMenuAtTick = -1;

        // Same shape, for `error-at`. Polled from GameComponentTick rather
        // than GameComponentUpdate, deliberately: GameComponentTick runs
        // INSIDE DoSingleTick, so the error fires from inside the advance
        // loop, which is where a real one would.
        public static int ErrorAtTick = -1;
        private static int errorRepeats = 1;
        private static string errorText = SelftestErrorText;

        // Main thread, from AgentGameComponent.GameComponentTick.
        public static void TickErrorFixture()
        {
            if (ErrorAtTick < 0) return;
            int now;
            try { now = Find.TickManager.TicksGame; }
            catch { return; }
            if (now < ErrorAtTick) return;
            ErrorAtTick = -1;
            for (int i = 0; i < errorRepeats; i++) Log.Error(errorText);
        }

        // -1 = disarmed. Same shape as `error-at`, for git-bug 722c951's
        // casualty halt (`down-at` step). Polled from GameComponentTick, which
        // runs INSIDE DoSingleTick, so the downing happens from inside the
        // advance the way a real one does — a `dev:damage` issued from the
        // protocol lands while the game is PAUSED and would prove nothing about
        // an advance halting.
        public static int DownAtTick = -1;
        private static int downTargetId = -1;
        private static bool downKill;

        public static void TickCasualtyFixture()
        {
            if (DownAtTick < 0) return;
            int now;
            try { now = Find.TickManager.TicksGame; }
            catch { return; }
            if (now < DownAtTick) return;
            DownAtTick = -1;
            try
            {
                var map = Find.CurrentMap;
                if (map == null) return;
                Pawn victim = null;
                foreach (var p in map.mapPawns.AllPawnsSpawned)
                    if (p != null && p.thingIDNumber == downTargetId) { victim = p; break; }
                if (victim == null || victim.Dead) return;
                // The game's own dev-menu route, so the `downed`/`death` journal
                // hooks see exactly what a real casualty produces —
                // Pawn_HealthTracker.MakeDowned and SetDead, not a synthetic
                // event. allowBleedingWounds:false keeps the fixture from
                // starting a second, slower death the suite did not ask for.
                if (downKill) HealthUtility.DamageUntilDead(victim, DamageDefOf.Cut);
                else HealthUtility.DamageUntilDowned(victim, allowBleedingWounds: false);
            }
            catch { }
        }

        // Main thread, once per frame from AgentGameComponent AFTER
        // TimeDriver.FrameStep.
        //
        // GenScene.GoToMainMenu disposes the Game, and this runs inside
        // Game.UpdatePlay — so it is QUEUED as a long event instead of called
        // inline. Queued events are pumped by LongEventHandler.LongEventsUpdate
        // at the top of Root.Update, above and outside this call stack, which
        // is the same phase the vanilla quit-to-menu button unloads from.
        public static void TickMainMenuFixture()
        {
            if (MainMenuAtTick < 0) return;
            int now;
            try { now = Find.TickManager.TicksGame; }
            catch { return; }
            if (now < MainMenuAtTick) return;
            MainMenuAtTick = -1;
            Journal.Emit("dev", new Dictionary<string, object>
            {
                ["verb"] = "journal-selftest",
                ["step"] = "main-menu",
                ["target"] = "firing at tick " + now,
                ["advance_in_flight"] = TimeDriver.Active,
                ["advance_id"] = TimeDriver.ActiveId,
            }, now);
            LongEventHandler.QueueLongEvent(GenScene.GoToMainMenu, null,
                doAsynchronously: false, exceptionHandler: null);
        }

        // Reads the CURRENT session's journal file. Off-thread: file I/O only,
        // no Verse. The file is also directly tail-able from a shell; this verb
        // exists for filtered, protocol-shaped reads (rwa journal, spec 1.4).
        //
        // ============ THIS VERB IS THE ONLY THING THAT CLEARS AN ADVANCE ======
        // git-bug 722c951. Calling this moves `Journal.ReadWatermark`, and the
        // watermark is what `advance` compares against before it will run again
        // (TimeDriver.Start). NOTHING ELSE MOVES IT, and the two things that
        // most look like they should are named here because M1 proves they must
        // not:
        //
        //   * The ADVANCE'S OWN `journal_seq` ECHO. Run m1-20260831 step 148
        //     returned `journal_seq:[125,128]` — the advance result CARRIED the
        //     news that Table had gone down — and the run advanced again, five
        //     more times, while he bled for 11,335 ticks and died. An echo the
        //     caller can ignore is exactly what was already there.
        //   * A DIGEST READ. `DigestVerb.ChangedSection` publishes
        //     `Journal.CountsSince` — a COUNT PER TYPE and a `last_seq`. "downed:
        //     1" does not name the pawn, the faction or the tick, so a digest
        //     cannot discharge an obligation whose whole content is which
        //     colonist it was. The M1 run made 10 digest calls and zero journal
        //     calls.
        //
        // HOW FAR IT MOVES. To `last_seq` — the highest seq in the FILE, not
        // `Journal.CurrentSeq` — when the read was unfiltered and untruncated,
        // because only then has the caller been handed everything there was.
        // Otherwise to the highest seq actually RETURNED, which for a
        // `types:`-filtered or `since_tick`-filtered or limit-truncated read is
        // as far as the caller can honestly claim to have looked. A
        // `journal {types:["letter"]}` therefore does NOT discharge a `downed`
        // it never asked for, and the result says so via `unread_after`.
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
            long maxReturned = 0;
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
                    if ((long)seq > maxReturned) maxReturned = (long)seq;
                }
            }

            // The watermark, per this verb's header. `filtered` is the honest
            // predicate for "the caller asked for a subset": a `types` list or a
            // `since_tick` floor both hide events that WERE in the window.
            bool filtered = types.Count > 0 || sinceTick >= 0;
            long claim = (!filtered && !truncated) ? lastSeq : maxReturned;
            long before = Journal.ReadWatermark;
            long after = Journal.NoteRead(claim);

            return new Dictionary<string, object>
            {
                ["file"] = Journal.CurrentFile,
                ["count"] = events.Count,
                ["truncated"] = truncated,
                ["last_seq"] = lastSeq,
                // What `advance` will compare against next (git-bug 722c951).
                // Published on EVERY read, not only when it moved, so a client
                // can see another client move it — the shared-watermark hazard
                // Journal's own header names.
                ["read_watermark"] = after,
                ["watermark_was"] = before,
                ["watermark_moved"] = after > before,
                // Events the journal has that this read did not hand over. Zero
                // is the state in which `advance` will run; nonzero after a
                // filtered read is the point of publishing it.
                ["unread_after"] = Math.Max(0L, Journal.CurrentSeq - after),
                ["filtered"] = filtered,
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
        //
        // Four more added for 1.5/1.7, same discipline, disclosed on both
        // issues. Every one exists because the acceptance has to be driveable
        // from OUTSIDE the game — the worker cannot launch it and the
        // orchestrator runs the bench through the file protocol:
        //   `timeout-letter` — a real vanilla LetterWithTimeout armed to open
        //       ITSELF a few ticks out, which is 1.7's whole mechanism.
        //   `dialogs`        — read-only report of the force-pause stack.
        //   `dialogs-clear`  — close force-pausing windows and drop the
        //       fixture letter. 1.7 deliberately ships no dismiss verb (3.5
        //       owns that), but its second acceptance bullet requires a CLEAR
        //       stack, so the escape hatch lives here, dev-gated, and nowhere
        //       a player verb can reach.
        //   `main-menu`      — arm a deferred return to the main menu, so
        //       1.5's "kill a game mid-advance" is a command and not a
        //       human clicking a menu on a parked window.
        //   `error-at`       — arm a red error to fire from inside a TICK at a
        //       future tick. 1.5 blocker 3's acceptance needs an error during
        //       an advance, after the journal's per-text cap has gone silent
        //       for that text; nothing else can produce that state.
        //   `weird-result`   — attach a deliberately unserializable payload
        //       (null string, Verse object, a ToString() that throws, a cyclic
        //       tree, NaN) so 1.5 defect 4's acceptance has something to fail
        //       on. It must still produce exactly one result file.
        // Every argument `journal-selftest` reads, for the refusal message only
        // — the DETECTION is VerbArgs' read log and does not consult this. A
        // name missing here costs a worse message on a call that is refused
        // anyway (post-dispatch), never a refused legitimate call, which is why
        // three of these are acceptable where 120 were not.
        private static readonly string[] SelftestArgs =
        {
            "steps", "letter_delay_ticks", "save_name", "alert_count",
            "alert_critical_last", "colonist_target", "power_lamps", "power_fuel",
            "power_stored", "power_generator", "timeout_ticks_letter", "letter_tag",
            "drop_fixture_letters", "error_delay_ticks", "error_repeats",
            "error_text", "main_menu_delay_ticks",
            // git-bug 722c951's `down-at` step. THIS LIST IS WHY THAT MERGE
            // NEEDED A HUMAN: `spec/arg-names` added the guard and
            // `spec/advance-halt` added the step, git merged both cleanly
            // because they touch different lines, and the result would have
            // refused every `down-at` call BEFORE any step ran — the fixture
            // that phases 3, 4 and 5 of accept/722c951 are built on, and
            // phase 0's own precondition. An allowlist is a declaration, and a
            // declaration is a thing two branches can each be right about
            // separately.
            "down_delay_ticks", "down_pawn", "down_kill",
        };

        [Verb("journal-selftest")]
        public static object Selftest(VerbContext ctx)
        {
            if (!Prefs.DevMode)
                throw new VerbArgsException("journal-selftest requires devMode=True (it mutates game state)");

            var steps = ctx.Args.StrList("steps");
            // THE PRE-MUTATION GUARD (git-bug 7382bdd comment #7). This verb is
            // the worked instance of the shape that comment names — "a
            // defaulted list argument whose default is non-empty and whose
            // steps mutate". `journal-selftest {kind:"save"}` dropped `kind`
            // without a word, fell through to the default list below, and
            // `downed` + `break` put all three colonists on the ground and one
            // of them into a berserk while the result said ok:true. Four calls
            // went by before it was noticed; in a ten-day unattended run that
            // is a colony-ender caused by a typo.
            //
            // Execute's post-dispatch check would catch it, but only after the
            // damage. This runs BEFORE the first step, so the call is refused
            // with nothing mutated — which is the acceptance bullet that
            // comment asked for, verbatim. Unconditional rather than inside the
            // `steps.Count == 0` branch: every step here mutates, so an
            // explicit step list carrying a typo deserves the same refusal.
            ctx.Args.RefuseStray("journal-selftest", SelftestArgs,
                "Refused BEFORE any step ran, so nothing was mutated.");
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
                        Log.Error(SelftestErrorText);
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
                        // power_generator:false builds the battery-only grid
                        // that exposes the `nets` rider: PowerNet.hasPowerSource
                        // counts a bare battery as a source, so such a grid
                        // reads as powered while nets_with_generator says 0.
                        bool withGenerator = ctx.Args.Bool("power_generator", true);
                        var colonistsNow = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
                        IntVec3 anchor = colonistsNow.Count > 0 ? colonistsNow[0].Position : map.Center;
                        int width = 6 + lamps * 2;
                        var area = FindClearRect(map, anchor, width, 3);

                        // Conduit run along the bottom row; everything else sits
                        // on the row above it, cardinally adjacent, so one net.
                        for (int x = area.minX; x <= area.maxX; x++)
                            SpawnPlayerBuilding(map, ThingDefOf.PowerConduit, new IntVec3(x, 0, area.minZ));
                        Thing gen = withGenerator
                            ? SpawnPlayerBuilding(map, ThingDefOf.WoodFiredGenerator,
                                new IntVec3(area.minX, 0, area.minZ + 1))
                            : null;
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
                            ["expect_gen_w"] = (withGenerator && fuelled > 0f) ? 1000f : 0f,
                            ["expect_draw_w"] = lamps * 30f,
                        };
                        break;
                    }
                    case "timeout-letter":
                    {
                        // 1.7's acceptance mechanism, on the REAL vanilla path
                        // rather than a stub. LetterDef.letterClass defaults to
                        // StandardLetter, which is a ChoiceLetter and therefore
                        // a LetterWithTimeout; StartTimeout arms disappearAtTick;
                        // ShouldAutomaticallyOpenLetter => LastTickBeforeTimeout
                        // makes the letter open ITSELF from LetterStackTick —
                        // inside DoSingleTick, inside the advance loop. Its
                        // ChoiceLetter.OpenLetter stacks a
                        // Dialog_NodeTreeWithFactionInfo, whose Dialog_NodeTree
                        // base sets forcePause = true. That is the whole chain
                        // the issue documents, driven end to end.
                        int timeout = ctx.Args.Int("timeout_ticks_letter", 120);
                        if (timeout < 2 || timeout > 600000)
                            throw new VerbArgsException("timeout_ticks_letter must be 2..600000");
                        string tag = ctx.Args.Str("letter_tag", "");
                        var letter = LetterMaker.MakeLetter(
                            FixtureLetterLabel + (tag.Length > 0 ? " " + tag : ""),
                            "Deliberate timing-out letter (spec 1.7 acceptance). It opens itself on its "
                            + "last tick before timeout, which stacks a force-pausing dialog under the "
                            + "advance loop.",
                            LetterDefOf.NeutralEvent);
                        if (letter == null)
                            throw new VerbArgsException("LetterMaker refused NeutralEvent as a choice letter");
                        letter.StartTimeout(timeout);
                        Find.LetterStack.ReceiveLetter(letter, null, 0, playSound: false);
                        int now = Find.TickManager.TicksGame;
                        // LastTickBeforeTimeout is disappearAtTick <= TicksGame+1.
                        int opensAt = letter.disappearAtTick - 1;
                        target = $"opens itself at tick {opensAt} (timeout {timeout})";
                        extras["timeout_letter"] = new Dictionary<string, object>
                        {
                            ["label"] = letter.Label.ToString(),
                            ["now_tick"] = now,
                            ["disappear_at_tick"] = letter.disappearAtTick,
                            ["opens_at_tick"] = opensAt,
                            ["in_stack"] = Find.LetterStack.LettersListForReading.Contains(letter),
                        };
                        break;
                    }
                    case "dialogs":
                    {
                        // Read-only. The same payload shape status.json and the
                        // advance result use, so an assertion written against
                        // one holds against all three.
                        var stack = Find.WindowStack;
                        extras["dialogs"] = TimeDriver.ForcePausePayload(stack);
                        target = stack != null && stack.WindowsForcePause ? "force-pause up" : "clear";
                        break;
                    }
                    case "dialogs-clear":
                    {
                        // The fixture-only escape hatch. It also drops the
                        // fixture letter by default, and that is not tidiness:
                        // a LetterWithTimeout still inside its last tick
                        // re-opens the SAME dialog on the very next tick, which
                        // would read as a poisoned queue when nothing is wrong.
                        var stack = Find.WindowStack;
                        var closed = new List<object>();
                        if (stack != null)
                        {
                            var doomed = new List<Window>();
                            var live = stack.Windows;
                            for (int i = 0; i < live.Count; i++)
                                if (live[i] != null && live[i].forcePause) doomed.Add(live[i]);
                            foreach (var w in doomed)
                            {
                                string name = w.GetType().Name;
                                if (stack.TryRemove(w, doCloseSound: false)) closed.Add(name);
                            }
                        }
                        int dropped = 0;
                        if (ctx.Args.Bool("drop_fixture_letters", true))
                        {
                            var ls = Find.LetterStack;
                            var doomedLetters = new List<Letter>();
                            foreach (var l in ls.LettersListForReading)
                            {
                                if (l == null) continue;
                                if (l.Label.ToString().StartsWith(FixtureLetterLabel, StringComparison.Ordinal))
                                    doomedLetters.Add(l);
                            }
                            foreach (var l in doomedLetters) { ls.RemoveLetter(l); dropped++; }
                        }
                        target = $"{closed.Count} closed, {dropped} fixture letters dropped";
                        extras["dialogs_cleared"] = new Dictionary<string, object>
                        {
                            ["closed"] = closed,
                            ["letters_dropped"] = dropped,
                            ["still_force_paused"] = stack != null && stack.WindowsForcePause,
                        };
                        break;
                    }
                    case "error-at":
                    {
                        // 1.5 blocker 3's acceptance needs a red error to fire
                        // DURING an advance, after the journal's per-text cap
                        // has already gone silent for that text — which is the
                        // exact state in which halt_on_error used to do
                        // nothing. Run `error` nine times first, then arm this.
                        int delay = ctx.Args.Int("error_delay_ticks", 200);
                        if (delay < 0 || delay > 600000)
                            throw new VerbArgsException("error_delay_ticks must be 0..600000");
                        int repeats = ctx.Args.Int("error_repeats", 1);
                        if (repeats < 1 || repeats > 50)
                            throw new VerbArgsException("error_repeats must be 1..50");
                        errorText = ctx.Args.Str("error_text", SelftestErrorText);
                        errorRepeats = repeats;
                        int armedAt = Find.TickManager.TicksGame;
                        ErrorAtTick = armedAt + delay;
                        target = $"{repeats}x at tick {ErrorAtTick}";
                        extras["error_at"] = new Dictionary<string, object>
                        {
                            ["armed_at_tick"] = armedAt,
                            ["fires_at_tick"] = ErrorAtTick,
                            ["repeats"] = repeats,
                            ["text"] = errorText,
                        };
                        break;
                    }
                    case "weird-result":
                    {
                        // 1.5 defect 4's acceptance: a verb returning a null
                        // string and an arbitrary object must still produce
                        // exactly one result file. Everything in here is a
                        // shape that used to take the serializer — and with it
                        // the rest of the poller cycle — down.
                        var cycleOuter = new Dictionary<string, object>();
                        cycleOuter["self"] = new List<object> { cycleOuter };
                        extras["weird"] = new Dictionary<string, object>
                        {
                            ["null_string"] = (string)null,
                            ["null_in_list"] = new List<string> { "a", null, "c" },
                            ["verse_object"] = (object)map ?? "no map",
                            ["verse_def"] = ThingDefOf.Steel,
                            ["throws_on_tostring"] = new ThrowingToString(),
                            ["cycle"] = cycleOuter,
                            ["nan"] = double.NaN,
                            ["infinity"] = double.PositiveInfinity,
                        };
                        target = "hostile payload attached";
                        break;
                    }
                    case "down-at":
                    {
                        // git-bug 722c951's casualty halt, and it is the ONLY
                        // way the acceptance can be driven from outside the
                        // game. The halt is on a `downed`/`death` that happens
                        // WHILE TIME RUNS; every existing route (`dev:damage`,
                        // the `downed` step above) fires from the command drain
                        // with the game paused, which produces the event but
                        // never inside an advance. Armed here, fired from
                        // GameComponentTick — i.e. inside DoSingleTick, inside
                        // the advance — through the game's own
                        // HealthUtility.DamageUntilDowned / DamageUntilDead.
                        //
                        // `down_pawn` is a thingIDNumber, so the SAME step
                        // proves both halves of the faction filter: a colonist
                        // id halts the advance, a hostile id (spawn one with
                        // `dev:spawn-pawn`) does not. Declared as an undeclared
                        // addition on 722c951, dev-gated like every other step
                        // here, journaled as a `dev` event, superseded by 3.1.
                        int delay = ctx.Args.Int("down_delay_ticks", 200);
                        if (delay < 0 || delay > 600000)
                            throw new VerbArgsException("down_delay_ticks must be 0..600000");
                        if (map == null) throw new VerbArgsException("down-at needs a current map");
                        downKill = ctx.Args.Bool("down_kill", false);
                        Pawn victim = null;
                        if (ctx.Args.Has("down_pawn"))
                        {
                            int want = ctx.Args.Int("down_pawn", -1);
                            foreach (var p in map.mapPawns.AllPawnsSpawned)
                                if (p != null && p.thingIDNumber == want) { victim = p; break; }
                            if (victim == null)
                                throw new VerbArgsException($"no spawned pawn with id {want} on this map");
                        }
                        else
                        {
                            if (colonists != null)
                                foreach (var c in colonists) { if (!c.Downed && !c.Dead) { victim = c; break; } }
                            if (victim == null)
                                throw new VerbArgsException("down-at needs a standing free colonist, "
                                    + "or an explicit down_pawn id");
                        }
                        downTargetId = victim.thingIDNumber;
                        int armedAtTick = Find.TickManager.TicksGame;
                        DownAtTick = armedAtTick + delay;
                        target = $"{PawnSafe.Name(victim)} at tick {DownAtTick}";
                        extras["down_at"] = new Dictionary<string, object>
                        {
                            ["armed_at_tick"] = armedAtTick,
                            ["fires_at_tick"] = DownAtTick,
                            ["pawn"] = victim.thingIDNumber,
                            ["name"] = PawnSafe.Name(victim),
                            ["faction"] = victim.Faction?.Name,
                            ["player_faction"] = victim.Faction != null && victim.Faction.IsPlayer,
                            ["kill"] = downKill,
                        };
                        break;
                    }
                    case "main-menu":
                    {
                        // Arms only. It fires from the GameComponent AFTER the
                        // advance loop has run for the frame, so the game
                        // really does unload with a command in flight — which
                        // is the only state 1.5's blockers 1 and 2 live in.
                        int delay = ctx.Args.Int("main_menu_delay_ticks", 0);
                        if (delay < 0 || delay > 600000)
                            throw new VerbArgsException("main_menu_delay_ticks must be 0..600000");
                        int armedAt = Find.TickManager.TicksGame;
                        MainMenuAtTick = armedAt + delay;
                        target = $"armed for tick {MainMenuAtTick}";
                        extras["main_menu"] = new Dictionary<string, object>
                        {
                            ["armed_at_tick"] = armedAt,
                            ["fires_at_tick"] = MainMenuAtTick,
                        };
                        break;
                    }
                    default:
                        throw new VerbArgsException($"unknown step '{step}' (letter|message|error|error-at|weird-result|raid|downed|down-at|break|save|stockpile|alerts|alerts-clear|colonists|power|timeout-letter|dialogs|dialogs-clear|main-menu)");
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

        // Deliberately hostile, for the `weird-result` step. A Verse object's
        // ToString() is arbitrary game code; this is the shape that used to
        // lose a result file AND abort the rest of the poller cycle.
        private sealed class ThrowingToString
        {
            public override string ToString()
                => throw new InvalidOperationException("deliberate ToString failure (1.5 acceptance)");
        }

        // Faction set BEFORE spawn (SetFactionDirect, the game's own dev-spawn
        // route) so the building is the player's from its first tick — power
        // nets and Building.DeconstructibleBy both key on it.
        //
        // THROUGH THE GATE SINCE 3a5ff6c item 3. This used to be a bare
        // `GenSpawn.Spawn(..., WipeMode.Vanish)` with no validator of any kind,
        // not even the `CanSpawnAt` that `dev:spawn-thing` uses — so the `power`
        // step could lay a conduit run through a wall and report a grid. It now
        // asks `GenConstruct.CanPlaceBlueprintAt` and REFUSES; the widget half
        // (research, tech level) is reported and not honoured, which is what
        // keeps this fixture legal on a fresh map where Electricity is
        // unresearched. FixtureSite's header carries the argument.
        private static Thing SpawnPlayerBuilding(Map map, ThingDef def, IntVec3 at)
            => FixtureSite.Spawn(map, def, GenStuff.DefaultStuffFor(def), at, Rot4.North,
                "journal-selftest", out _);
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
