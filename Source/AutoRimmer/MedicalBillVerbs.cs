using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // MEDICAL BILLS — operation bills on a PAWN.
    //
    // A pawn IS an IBillGiver (Verse/Pawn.cs: `BillStack => health.surgeryBills`),
    // which is why 2.4's `bills` observer already reports pending operations
    // with `kind:"pawn"`. This file is the write half, in the same vocabulary.
    //
    // THE GATE LIVES IN THE WIDGET, AND HERE IT IS THE WHOLE VERB.
    // `Verse/BillStack.cs AddBill` is four lines and checks NOTHING —
    //     bill.billStack = this; bills.Add(bill);
    // — not the 15-bill cap, not the recipe's research prerequisite, not
    // whether the recipe applies to this pawn at all. DESIGN §Action model
    // names it as one of the three worked examples of the invariant. Every
    // check below therefore comes from RimWorld/HealthCardUtility.cs
    // DrawMedOperationsTab's `recipeOptionsMaker` and from
    // Verse/BillStack.cs DoListing, cited inline:
    //
    //   DoListing               `if (Count < 15)` — the Add Bill button is not
    //                           drawn at all on a full stack (BillStack.MaxCount)
    //   recipeOptionsMaker      `thingForMedBills.def.AllRecipes` is the universe
    //                           `recipe.AvailableNow`               (RE-DERIVED, see below)
    //                           `recipe.Worker.AvailableReport(pawn)` — accepted,
    //                             or a REASON, else the row is not drawn at all
    //                           `PotentiallyMissingIngredients(null, MapHeld)`:
    //                             any techHediff or drug missing hides the row;
    //                             `dontShowIfAnyIngredientMissing` hides it too
    //                           `targetsBodyPart` -> Worker.GetPartsToApplyOn +
    //                             `AvailableOnNow(pawn, part)`
    //                           else -> `!hediffSet.HasHediff(recipe.addsHediff)`
    //   GenerateSurgeryOption   a row with a REASON or with missing ingredients
    //                           gets a NULL action — it is drawn and unclickable.
    //                           So "visible" and "addable" are different, and
    //                           `surgery-options` publishes both.
    //
    // WHY AvailableNow IS RE-DERIVED (WorldSafe Class A): `RecipeDef.AvailableNow`
    // reads `ResearchProjectDef.IsFinished`, which is `ProgressReal >= Cost`,
    // which is `ResearchManager.GetProgress`, which INSERTS a zero entry into a
    // scribed dictionary on a miss. Asking "what surgeries are available?" would
    // otherwise add an entry per research-gated recipe to the save, permanently
    // — the identical trap 2.4 found and routed around for the `bills` and
    // `research` observers. WorldSafe.Finished is the shipped guarded route and
    // is reused here; the ideology and faction-tag clauses of AvailableNow do
    // not write and are evaluated normally.
    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // surgery-options {pawn, cap?}   READ-ONLY
        //
        // The Health tab's own operation float menu, as data: what the game
        // would offer for this pawn, whether each row is clickable, and — when
        // it is not — the game's own reason or the missing ingredients.
        //
        // This is the discovery surface for `surgery-add`, and it exists for the
        // same reason `orders` does: a hand-written list of recipes would be a
        // guess against a 38-mod bench, and `AddBill` will happily accept a bill
        // that can never be worked.
        // --------------------------------------------------------------------
        [Verb("surgery-options")]
        public static object SurgeryOptions(VerbContext ctx)
        {
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            int cap = ctx.Args.Int("cap", 40);
            if (cap < 1 || cap > 300) throw new VerbArgsException("cap must be 1..300");

            var rows = new List<object>();
            int total = 0, addable = 0;
            ScanSurgeries(pawn, (recipe, part, report, missing) =>
            {
                total++;
                bool ok = string.IsNullOrEmpty(report.Reason) && missing.Count == 0;
                if (ok) addable++;
                if (rows.Count >= cap) return;
                rows.Add(SurgeryRow(pawn, recipe, part, report, missing, ok));
            });

            return new Dictionary<string, object>
            {
                ["pawn"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["options"] = rows,
                ["total"] = total,
                ["more"] = Math.Max(0, total - rows.Count),
                ["addable"] = addable,
                ["bills"] = SurgeryStack(pawn),
                ["bill_slots_free"] = Math.Max(0, BillStack.MaxCount - (pawn.BillStack?.Count ?? 0)),
                ["source"] = WorldSafe.ResearchRefsOk ? "backing-field" : "unavailable",
                ["note"] = "`addable:false` rows are what the game DRAWS but leaves unclickable — a reason "
                    + "from RecipeWorker.AvailableReport, or a missing ingredient. BillStack.AddBill would "
                    + "accept one anyway and it would never be worked. Research state is read through "
                    + "WorldSafe's guarded route, never ResearchProjectDef.IsFinished (which writes).",
            };
        }

        // --------------------------------------------------------------------
        // surgery-add {pawn, recipe, part?}
        //
        // `part` names a BodyPartRecord by its LABEL ("left leg") — the string
        // the game's own option shows — or by its def's defName, first match.
        // A recipe with `targetsBodyPart` and more than one candidate part
        // REFUSES rather than guessing, and lists the candidates: picking a leg
        // for the caller is exactly the kind of silent choice this project's
        // "candidates + reasons, never bare booleans" rule exists to prevent.
        // --------------------------------------------------------------------
        [Verb("surgery-add")]
        public static object SurgeryAdd(VerbContext ctx)
        {
            const string V = "surgery-add";
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            var recipe = Dev.Named<RecipeDef>(ctx.Args.StrReq("recipe"), "recipe");
            string partArg = ctx.Args.Str("part");

            // Verse/BillStack.cs DoListing: the Add Bill button is not drawn at
            // all once the stack is full. AddBill itself does not check.
            int count = pawn.BillStack?.Count ?? 0;
            if (count >= BillStack.MaxCount)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["reason"] = $"this pawn already has {count} bills and BillStack.MaxCount is "
                        + BillStack.MaxCount + "; the game draws no Add Bill button on a full stack "
                        + "(BillStack.AddBill itself would accept it — that is the gate this verb reproduces)",
                    ["action"] = NoStamp(),
                };

            var matches = new List<object>();
            RecipeDef foundRecipe = null;
            BodyPartRecord foundPart = null;
            AcceptanceReport foundReport = AcceptanceReport.WasAccepted;
            List<object> foundMissing = null;
            int candidates = 0;

            ScanSurgeries(pawn, (r, part, report, missing) =>
            {
                if (r != recipe) return;
                candidates++;
                matches.Add(SurgeryRow(pawn, r, part, report,
                    missing, string.IsNullOrEmpty(report.Reason) && missing.Count == 0));
                if (partArg != null && !PartMatches(part, partArg)) return;
                if (foundRecipe != null && partArg == null && part != null) return; // ambiguous; handled below
                foundRecipe = r; foundPart = part; foundReport = report; foundMissing = missing;
            });

            if (candidates == 0)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["reason"] = "the Health tab's operation menu does not offer this recipe for this pawn "
                        + "(not in def.AllRecipes, not AvailableNow, no applicable body part, the hediff is "
                        + "already present, or a techHediff/drug ingredient is missing). "
                        + "Call `surgery-options` for what IS offered.",
                    ["action"] = NoStamp(),
                };

            // More than one body part and no `part` given: refuse rather than
            // pick a leg for the caller.
            if (partArg == null && candidates > 1)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["reason"] = $"'{recipe.defName}' targets a body part and {candidates} parts qualify; "
                        + "pass `part` with one of the labels below",
                    ["candidates"] = matches,
                    ["action"] = NoStamp(),
                };
            if (foundRecipe == null)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["reason"] = $"no offered body part matches '{partArg}'",
                    ["candidates"] = matches,
                    ["action"] = NoStamp(),
                };

            // RimWorld/HealthCardUtility.cs GenerateSurgeryOption: a row with a
            // reason, or with any missing ingredient, gets a NULL action — the
            // game draws it and it cannot be clicked.
            if (!string.IsNullOrEmpty(foundReport.Reason))
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["part"] = PartLabel(foundPart),
                    // The game's own AcceptanceReport string, verbatim.
                    ["reason"] = foundReport.Reason,
                    ["action"] = NoStamp(),
                };
            if (foundMissing.Count > 0)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["part"] = PartLabel(foundPart),
                    ["reason"] = "missing ingredients; the game's own option is drawn but unclickable",
                    ["missing_ingredients"] = foundMissing,
                    ["action"] = NoStamp(),
                };

            // sendMessages:FALSE, and this is not a style choice.
            // HealthCardUtility.CreateSurgeryBill's sendMessages block calls
            // `Bill.CreateNoPawnsWithSkillDialog(recipe)` whenever no free
            // colonist meets the recipe's skill requirement, and that method is
            // a bare `Find.WindowStack.Add(new Dialog_MessageBox(text))`.
            // Dialog_MessageBox sets forcePause, and per spec 1.7 a
            // force-pausing window makes EVERY subsequent `advance` halt at 0
            // ticks with reason:"dialog" — permanently, until 3.5 ships dialog
            // routing. So the ordinary "add a bill for a surgery nobody can
            // perform yet" input would wedge an unattended run.
            //
            // It also lets a MODDED RecipeWorker.CheckForWarnings raise
            // whatever it likes (32 mods on the bench).
            //
            // The four warnings that block loses are re-derived below and
            // returned as `warnings` — better information than a top-of-screen
            // message, which the agent never reads. Vanilla already treats this
            // path as unsafe unattended: Pawn_GuestTracker
            // .GuestTrackerTickInterval's own auto-bill call passes false too.
            var warnings = SurgeryWarnings(pawn, foundRecipe, foundPart);
            Bill_Medical bill;
            try { bill = HealthCardUtility.CreateSurgeryBill(pawn, foundRecipe, foundPart, null, sendMessages: false); }
            catch (Exception e)
            {
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["reason"] = e.GetType().Name + ": " + e.Message,
                    ["action"] = NoStamp(),
                };
            }

            long seq = Act(V, "add-bill", PawnSafe.Name(pawn) + ": " + recipe.defName,
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["part"] = PartLabel(foundPart),
                    ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["pawn"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["recipe"] = recipe.defName,
                ["part"] = PartLabel(foundPart),
                ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                ["index"] = (pawn.BillStack?.Count ?? 1) - 1,
                ["bills"] = SurgeryStack(pawn),
                ["bill_slots_free"] = Math.Max(0, BillStack.MaxCount - (pawn.BillStack?.Count ?? 0)),
                // The four warnings CreateSurgeryBill would have sent, re-derived
                // — see the sendMessages:false comment above for why they are
                // returned rather than messaged.
                ["warnings"] = warnings,
                ["action"] = Stamp(seq),
                ["note"] = "a surgery bill is not a job: the patient walks to a medical bed "
                    + "(WorkGiver_PatientGoToBedTreatment, which itself requires an available doctor) and a "
                    + "doctor then works it (WorkGiver_DoBill). Advance and read the journal; "
                    + "`rest-until-healed` puts the patient in the bed directly.",
            };
        }

        // --------------------------------------------------------------------
        // surgery-remove {pawn, index?|uid?|recipe?, all?}
        //
        // WIDGET GATE — Verse/BillStack.cs Delete(bill), which is what the X
        // button on a bill row calls; it flags the bill deleted and notifies the
        // giver. Removing from the list directly would leave a live Bill object
        // pointing at a stack it is no longer in.
        // --------------------------------------------------------------------
        [Verb("surgery-remove")]
        public static object SurgeryRemove(VerbContext ctx)
        {
            const string V = "surgery-remove";
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            var a = ctx.Args;
            var stack = pawn.BillStack;
            if (stack == null || stack.Count == 0)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["pawn"] = pawn.thingIDNumber,
                    ["reason"] = "this pawn has no surgery bills",
                    ["action"] = NoStamp(),
                };

            var doomed = new List<Bill>();
            var snapshot = new List<Bill>(stack.Bills);
            if (a.Bool("all", false)) doomed.AddRange(snapshot);
            else if (a.Has("index"))
            {
                int i = a.IntReq("index");
                if (i < 0 || i >= snapshot.Count)
                    throw new VerbArgsException($"index must be 0..{snapshot.Count - 1}");
                doomed.Add(snapshot[i]);
            }
            else if (a.Has("uid"))
            {
                string uid = a.StrReq("uid");
                foreach (var b in snapshot)
                    if (string.Equals(Safe(() => b.GetUniqueLoadID()), uid, StringComparison.Ordinal)) doomed.Add(b);
                if (doomed.Count == 0) throw new VerbArgsException($"no bill with uid '{uid}' on this pawn");
            }
            else if (a.Has("recipe"))
            {
                var recipe = Dev.Named<RecipeDef>(a.StrReq("recipe"), "recipe");
                foreach (var b in snapshot) if (b?.recipe == recipe) doomed.Add(b);
                if (doomed.Count == 0) throw new VerbArgsException($"no '{recipe.defName}' bill on this pawn");
            }
            else throw new VerbArgsException("pass index, uid, recipe, or all:true");

            var removed = new List<object>();
            foreach (var b in doomed)
            {
                var line = new Dictionary<string, object>
                {
                    ["uid"] = Safe(() => b.GetUniqueLoadID()),
                    ["recipe"] = b.recipe?.defName,
                    ["label"] = Safe(() => b.LabelCap),
                };
                stack.Delete(b);
                removed.Add(line);
            }

            long seq = Act(V, "remove-bill", PawnSafe.Name(pawn) + " x" + removed.Count,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["removed"] = removed.Count });
            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["pawn"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["removed"] = removed,
                ["bills"] = SurgeryStack(pawn),
                ["action"] = Stamp(seq),
            };
        }

        // ======================= surgery plumbing ============================

        // RimWorld/HealthCardUtility.cs DrawMedOperationsTab's recipeOptionsMaker,
        // reproduced. Calls `sink` once per DRAWN row — the disabled ones
        // included, since a disabled row is information and a hidden one is not.
        private static void ScanSurgeries(Pawn pawn,
            Action<RecipeDef, BodyPartRecord, AcceptanceReport, List<object>> sink)
        {
            List<RecipeDef> recipes;
            try { recipes = new List<RecipeDef>(pawn.def.AllRecipes); }
            catch { return; }
            var map = pawn.MapHeld;

            foreach (var recipe in recipes)
            {
                if (recipe == null) continue;
                try
                {
                    // RE-DERIVED, never RecipeDef.AvailableNow: that reads
                    // ResearchProjectDef.IsFinished, which INSERTS into a
                    // scribed dictionary (WorldSafe Class A).
                    if (!RecipeAvailableNow(recipe)) continue;

                    AcceptanceReport report;
                    try { report = recipe.Worker.AvailableReport(pawn); }
                    catch { continue; }
                    if (!report.Accepted && string.IsNullOrEmpty(report.Reason)) continue;

                    var missing = new List<object>();
                    bool hidesRow = false;
                    try
                    {
                        foreach (var md in recipe.PotentiallyMissingIngredients(null, map))
                        {
                            if (md == null) continue;
                            // The tab HIDES the whole row when a techHediff or a
                            // drug is missing; other missing ingredients only
                            // disable it.
                            if (md.isTechHediff || md.IsDrug) { hidesRow = true; break; }
                            missing.Add(md.defName);
                        }
                        if (!hidesRow && missing.Count > 0 && recipe.dontShowIfAnyIngredientMissing) hidesRow = true;
                    }
                    catch { }
                    if (hidesRow) continue;

                    if (recipe.targetsBodyPart)
                    {
                        foreach (var part in recipe.Worker.GetPartsToApplyOn(pawn, recipe))
                        {
                            bool onNow = false;
                            try { onNow = recipe.AvailableOnNow(pawn, part); } catch { }
                            if (!onNow) continue;
                            sink(recipe, part, report, new List<object>(missing));
                        }
                    }
                    else
                    {
                        bool has = false;
                        try { has = recipe.addsHediff != null && pawn.health.hediffSet.HasHediff(recipe.addsHediff); }
                        catch { }
                        if (has) continue;
                        sink(recipe, null, report, new List<object>(missing));
                    }
                }
                catch (Exception e)
                {
                    Journal.EmitWarning("surgery-options: recipe " + recipe.defName + " threw: " + e.Message);
                }
            }
        }

        // Verse/RecipeDef.cs AvailableNow, clause for clause, with
        // WorldSafe.Finished in place of ResearchProjectDef.IsFinished. The
        // ideology/faction clauses do not write and are evaluated as vanilla
        // does; the role-apparel `Check()` unlock is reproduced because without
        // it an ideo-unlocked recipe would read as unavailable.
        private static bool RecipeAvailableNow(RecipeDef recipe)
        {
            try
            {
                if (recipe.researchPrerequisite != null && !WorldSafe.Finished(recipe.researchPrerequisite)) return false;
                if (recipe.researchPrerequisites != null)
                    for (int i = 0; i < recipe.researchPrerequisites.Count; i++)
                        if (!WorldSafe.Finished(recipe.researchPrerequisites[i])) return false;

                if (recipe.memePrerequisitesAny != null)
                {
                    bool any = false;
                    foreach (var meme in recipe.memePrerequisitesAny)
                        if (Faction.OfPlayer.ideos.HasAnyIdeoWithMeme(meme)) { any = true; break; }
                    if (!any) return false;
                }

                if (recipe.factionPrerequisiteTags != null)
                {
                    bool anyMissing = false;
                    foreach (var tag in recipe.factionPrerequisiteTags)
                    {
                        var tags = Faction.OfPlayer.def.recipePrerequisiteTags;
                        if (tags == null || !tags.Contains(tag)) { anyMissing = true; break; }
                    }
                    if (anyMissing && !UnlockedByRoleApparel(recipe)) return false;
                }

                if (recipe.fromIdeoBuildingPreceptOnly
                    && (!ModsConfig.IdeologyActive || !IdeoUtility.PlayerHasPreceptForBuilding(recipe.ProducedThingDef)))
                    return false;
            }
            catch { return false; }
            return true;
        }

        // Verse/RecipeDef.cs AvailableNow's local Check(): a faction-tag-gated
        // recipe is still available when one of the player's ideo roles
        // REQUIRES a piece of apparel this recipe produces.
        private static bool UnlockedByRoleApparel(RecipeDef recipe)
        {
            try
            {
                if (!ModsConfig.IdeologyActive) return false;
                foreach (var ideo in Faction.OfPlayer.ideos.AllIdeos)
                {
                    foreach (var role in ideo.RolesListForReading)
                    {
                        if (role.apparelRequirements == null) continue;
                        foreach (var req in role.apparelRequirements)
                        {
                            ThingDef want = null;
                            foreach (var d in req.requirement.AllRequiredApparel()) { want = d; break; }
                            if (want == null) continue;
                            foreach (var product in recipe.products)
                                if (product.thingDef == want) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static Dictionary<string, object> SurgeryRow(Pawn pawn, RecipeDef recipe, BodyPartRecord part,
            AcceptanceReport report, List<object> missing, bool addable)
        {
            return new Dictionary<string, object>
            {
                ["recipe"] = recipe.defName,
                // RecipeWorker.GetLabelWhenUsedOn is the text the game's own
                // option shows.
                ["label"] = Safe(() => recipe.Worker.GetLabelWhenUsedOn(pawn, part)),
                ["part"] = PartLabel(part),
                ["part_def"] = part?.def?.defName,
                ["addable"] = addable,
                // The game's own AcceptanceReport string, verbatim, or null.
                ["reason"] = string.IsNullOrEmpty(report.Reason) ? null : report.Reason,
                ["missing_ingredients"] = missing,
                ["work_amount"] = WorldSafe.R(recipe.workAmount, 0),
                ["skill"] = recipe.workSkill?.defName,
                ["skill_required"] = recipe.skillRequirements != null && recipe.skillRequirements.Count > 0
                    ? (object)recipe.skillRequirements[0].minLevel : null,
                ["success_factor"] = WorldSafe.R(recipe.surgerySuccessChanceFactor, 2),
                ["deathrest_or_anesthetize"] = recipe.anesthetize,
            };
        }

        // 2.4's `bills` field names for a pawn's surgery stack — one vocabulary
        // across observe and act. `state` goes through WorldSafe.BillState,
        // never Bill.ShouldDoNow (which writes the scribed `paused` flag).
        private static List<object> SurgeryStack(Pawn pawn)
        {
            var list = new List<object>();
            var stack = pawn.BillStack;
            if (stack == null) return list;
            var snapshot = new List<Bill>(stack.Bills);
            for (int i = 0; i < snapshot.Count; i++)
            {
                var b = snapshot[i];
                if (b?.recipe == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["uid"] = Safe(() => b.GetUniqueLoadID()),
                    ["label"] = Safe(() => b.LabelCap),
                    ["recipe"] = b.recipe.defName,
                    ["suspended"] = b.suspended,
                    ["state"] = WorldSafe.BillState(b, -1),
                    ["part"] = (b as Bill_Medical) != null ? PartLabel(((Bill_Medical)b).Part) : null,
                    ["work_skill"] = b.recipe.workSkill?.defName,
                });
            }
            return list;
        }

        // The four warnings HealthCardUtility.CreateSurgeryBill's sendMessages
        // block would have raised, re-derived so they can be RETURNED instead —
        // the first of them is a force-pausing Dialog_MessageBox, which spec 1.7
        // makes unrecoverable on an unattended bench. Each carries a `key` a
        // program can switch on and a sentence a human can read.
        private static List<object> SurgeryWarnings(Pawn medPawn, RecipeDef recipe, BodyPartRecord part)
        {
            var list = new List<object>();
            var map = medPawn.MapHeld;
            if (map == null) return list;

            // 1. Bill.CreateNoPawnsWithSkillDialog — THE MODAL.
            try
            {
                bool anyCapable = false;
                foreach (var col in new List<Pawn>(map.mapPawns.PawnsInFaction(Faction.OfPlayer)))
                {
                    if (col == null) continue;
                    if (!col.IsFreeColonist && !col.IsColonyMechPlayerControlled) continue;
                    if (recipe.PawnSatisfiesSkillRequirements(col)) { anyCapable = true; break; }
                }
                if (!anyCapable)
                    list.Add(new Dictionary<string, object>
                    {
                        ["key"] = "no-pawn-with-skill",
                        ["text"] = "no free colonist or player-controlled mech meets this recipe's skill "
                            + "requirement",
                        ["min_skill"] = Safe(() => recipe.MinSkillString),
                        ["note"] = "in vanilla this raises a force-pausing Dialog_MessageBox "
                            + "(Bill.CreateNoPawnsWithSkillDialog); returned here instead",
                    });
            }
            catch { }

            // 2. MessageNoMedicalBeds / MessageNoAnimalBeds.
            try
            {
                if (!medPawn.InBed() && medPawn.RaceProps.IsFlesh)
                {
                    bool humanlike = medPawn.RaceProps.Humanlike;
                    bool anyBed = false;
                    foreach (var b in new List<Building>(map.listerBuildings.allBuildingsColonist))
                    {
                        if (!(b is Building_Bed bed)) continue;
                        if (!RestUtility.CanUseBedEver(medPawn, bed.def)) continue;
                        if (humanlike && !bed.Medical) continue;
                        anyBed = true;
                        break;
                    }
                    if (!anyBed)
                        list.Add(new Dictionary<string, object>
                        {
                            ["key"] = humanlike ? "no-medical-beds" : "no-animal-beds",
                            ["text"] = humanlike
                                ? "the colony has no medical bed this pawn can use; the surgery will not start"
                                : "the colony has no bed this animal can use; the surgery will not start",
                        });
                }
            }
            catch { }

            // 3. MessageMedicalOperationWillAngerFaction.
            try
            {
                if (medPawn.Faction != null && !medPawn.Faction.Hidden
                    && !medPawn.Faction.HostileTo(Faction.OfPlayer)
                    && recipe.Worker.IsViolationOnPawn(medPawn, part, Faction.OfPlayer))
                    list.Add(new Dictionary<string, object>
                    {
                        ["key"] = "angers-faction",
                        ["text"] = "this operation is a violation against " + medPawn.HomeFaction?.Name
                            + " and will cost goodwill",
                        ["faction"] = medPawn.HomeFaction?.def?.defName,
                    });
            }
            catch { }

            // 4. MessageWarningNoMedicineForRestriction — HealthCardUtility
            //    .CanDoRecipeWithMedicineRestriction is private; re-derived.
            try
            {
                if (medPawn.playerSettings != null && !CanDoRecipeWithMedicineRestriction(medPawn, recipe))
                    list.Add(new Dictionary<string, object>
                    {
                        ["key"] = "medicine-restriction",
                        ["text"] = "no medicine on the map is both an allowed ingredient and permitted by "
                            + "this pawn's medical care setting (" + medPawn.playerSettings.medCare + ")",
                        ["med_care"] = medPawn.playerSettings.medCare.ToString(),
                    });
            }
            catch { }
            return list;
        }

        // RimWorld/HealthCardUtility.cs CanDoRecipeWithMedicineRestriction
        // (private there). ThingsInGroup returns the real backing list, snapshot
        // before iterating per WorldSafe Class E.
        private static bool CanDoRecipeWithMedicineRestriction(Pawn pawn, RecipeDef recipe)
        {
            if (recipe.ingredients == null) return true;
            bool needsMedicine = false;
            foreach (var ing in recipe.ingredients)
            {
                var any = ing.filter?.AnyAllowedDef;
                if (any != null && any.IsMedicine) { needsMedicine = true; break; }
            }
            if (!needsMedicine) return true;
            var care = WorkGiver_DoBill.GetMedicalCareCategory(pawn);
            foreach (var med in new List<Thing>(pawn.MapHeld.listerThings.ThingsInGroup(ThingRequestGroup.Medicine)))
            {
                foreach (var ing in recipe.ingredients)
                    if (ing.filter != null && ing.filter.Allows(med) && care.AllowsMedicine(med.def)) return true;
            }
            return false;
        }

        private static string PartLabel(BodyPartRecord part)
        {
            if (part == null) return null;
            try { return part.Label; } catch { return part.def?.defName; }
        }

        private static bool PartMatches(BodyPartRecord part, string arg)
        {
            if (part == null) return false;
            string label = PartLabel(part);
            if (string.Equals(label, arg, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(part.def?.defName, arg, StringComparison.OrdinalIgnoreCase);
        }
    }
}
