using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // THE POLICY DATABASES — create, edit, delete, set-default.
    //
    // FIVE databases, not four. The spec body names outfit / food / drug; the
    // session-4 amendment's item 8 adds READING, which is a fifth database of
    // exactly the same shape (RimWorld/ReadingPolicyDatabase.cs, driven by
    // Dialog_ManageReadingPolicies and PawnColumnWorker_Reading). It is minor
    // and it is here, because a policy vocabulary that is missing one of five
    // makes the caller guess which.
    //
    // WIDGET GATES — RimWorld/Dialog_ManagePolicies.cs and its five subclasses:
    //   create   CreateNewPolicy()  -> <db>.MakeNew…()
    //   default  SetDefaultPolicy() -> <db>.SetDefault(policy)   (slot 0 IS the
    //                                  default; SetDefault swaps into it)
    //   delete   TryDeletePolicy()  -> <db>.TryDelete(policy), which returns an
    //                                  AcceptanceReport naming the PAWN still
    //                                  using it — that string is what a player
    //                                  sees and it is what this verb returns
    //   rename   Dialog_ManagePolicies.ValidateName: a policy whose label is
    //                                  emptied becomes "UnnamedPolicy"
    //   edit     DoContentsRect -> ThingFilterUI over the policy's filter,
    //                                  bounded by a GLOBAL parent filter per
    //                                  kind (apparel: the Apparel category)
    //
    // THE PARENT FILTER IS THE GATE, and it is the one a naive implementation
    // drops: Dialog_ManageApparelPolicies hands ThingFilterUI an
    // ApparelGlobalFilter of `ThingCategoryDefOf.Apparel`, so a player CANNOT
    // put steel in an apparel policy. `allow` here refuses anything the parent
    // filter does not contain, with that reason.
    //
    // NOT A CHEAT. A policy is player-authored data; nothing here bypasses a
    // simulation rule, so these are `action` rows like every other player verb.
    internal static partial class PolicyKinds
    {
        // The five kind words, and the one place they are spelled.
        public const string Apparel = "apparel";
        public const string Food = "food";
        public const string Drug = "drug";
        public const string Reading = "reading";
        public const string Words = "apparel|food|drug|reading";
    }

    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // policy-new {kind, label?, copy_from?, allow?, disallow?,
        //             allow_all?, disallow_all?}
        //
        // Creates through the database's own MakeNew… (so the id allocation and
        // the starting filter are the game's), then applies the same edits
        // `policy-edit` does. `copy_from` uses Policy.CopyFrom, which is the
        // game's own copy and carries the filter allowances (and, for reading,
        // the effect filter too).
        // --------------------------------------------------------------------
        [Verb("policy-new")]
        public static object PolicyNew(VerbContext ctx)
        {
            const string V = "policy-new";
            var game = Current.Game ?? throw new VerbArgsException("no active game");
            string kind = KindArg(ctx.Args);
            var a = ctx.Args;

            Policy created;
            switch (kind)
            {
                case PolicyKinds.Apparel: created = game.outfitDatabase.MakeNewOutfit(); break;
                case PolicyKinds.Food: created = game.foodRestrictionDatabase.MakeNewFoodRestriction(); break;
                case PolicyKinds.Drug: created = game.drugPolicyDatabase.MakeNewDrugPolicy(); break;
                default: created = game.readingPolicyDatabase.MakeNewReadingPolicy(); break;
            }

            if (a.Has("copy_from"))
            {
                var src = FindPolicy(kind, a.Raw("copy_from"));
                try { created.CopyFrom(src); }
                catch (Exception e) { throw new VerbArgsException("copy_from failed: " + e.Message); }
            }

            string label = a.Str("label");
            if (!string.IsNullOrEmpty(label)) created.label = label;

            var edits = ApplyPolicyEdits(kind, created, a, out List<object> refused);
            long seq = Act(V, "create", kind + ":" + created.label,
                new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["policy"] = created.id,
                    ["label"] = created.label,
                });

            var d = PolicyLine(kind, created, DefaultOf(kind) == created);
            d["verb"] = V;
            d["kind"] = kind;
            d["edits"] = edits;
            d["refused"] = refused;
            d["action"] = Stamp(seq);
            d["note"] = "created through the database's own MakeNew… so the id and the starting filter are "
                + "the game's; assign it with `assign {" + kind + "_policy: " + created.id + "}`";
            return d;
        }

        // --------------------------------------------------------------------
        // policy-edit {kind, policy, label?, allow?, disallow?, allow_all?,
        //              disallow_all?, drugs?:[{drug, …}]}
        //
        // `allow`/`disallow` take defNames OR ThingCategoryDef names OR
        // SpecialThingFilterDef names — the three things ThingFilter.SetAllow is
        // overloaded on (Verse/ThingFilter.cs), which is also the three kinds of
        // row the ThingFilterUI tree draws. Resolution order is
        // ThingDef → ThingCategoryDef → SpecialThingFilterDef, and an unknown
        // word is a bad-args naming what was tried rather than a silent no-op.
        //
        // `drugs` edits DrugPolicyEntry rows in place (RimWorld/DrugPolicyEntry
        // .cs is plain public fields, and Dialog_ManageDrugPolicies writes them
        // directly) using 2.4's `policies` field names, so an edit and a read
        // are one vocabulary.
        // --------------------------------------------------------------------
        [Verb("policy-edit")]
        public static object PolicyEdit(VerbContext ctx)
        {
            const string V = "policy-edit";
            Current.Game.NullCheck();
            string kind = KindArg(ctx.Args);
            var policy = FindPolicy(kind, ctx.Args.Raw("policy")
                ?? throw new VerbArgsException("missing required arg 'policy' (an id or a label)"));

            string before = policy.label;
            string label = ctx.Args.Str("label");
            if (label != null)
            {
                // Dialog_ManagePolicies.ValidateName: an emptied label becomes
                // "UnnamedPolicy" rather than staying blank.
                policy.label = string.IsNullOrEmpty(label.Trim())
                    ? Tr("UnnamedPolicy", "Unnamed policy")
                    : label;
            }

            var edits = ApplyPolicyEdits(kind, policy, ctx.Args, out List<object> refused);
            if (label != null) edits.Add("label:" + before + " -> " + policy.label);

            long seq = edits.Count > 0
                ? Act(V, "edit", kind + ":" + policy.label,
                      new Dictionary<string, object>
                      {
                          ["kind"] = kind,
                          ["policy"] = policy.id,
                          ["edits"] = edits.Count,
                      })
                : 0;

            var d = PolicyLine(kind, policy, DefaultOf(kind) == policy);
            d["verb"] = V;
            d["kind"] = kind;
            d["edits"] = edits;
            d["refused"] = refused;
            d["action"] = edits.Count > 0 ? Stamp(seq) : NoStamp();
            return d;
        }

        // --------------------------------------------------------------------
        // policy-delete {kind, policy}
        //
        // WIDGET GATE — the database's own TryDelete, which returns an
        // AcceptanceReport whose Reason names the pawn still using the policy
        // ("OutfitInUse".Translate(pawn)). That report IS the player's error
        // message, so it is returned verbatim rather than paraphrased.
        //
        // Note the asymmetry the game itself has and this verb preserves:
        // TryDelete refuses when a LIVE pawn on a map/caravan uses the policy,
        // but then clears the policy from every OTHER pawn (dead, off-map)
        // before removing it. That second sweep is a real mutation and it is
        // echoed as `cleared_from_others`.
        // --------------------------------------------------------------------
        [Verb("policy-delete")]
        public static object PolicyDelete(VerbContext ctx)
        {
            const string V = "policy-delete";
            var game = Current.Game ?? throw new VerbArgsException("no active game");
            string kind = KindArg(ctx.Args);
            var policy = FindPolicy(kind, ctx.Args.Raw("policy")
                ?? throw new VerbArgsException("missing required arg 'policy' (an id or a label)"));

            bool wasDefault = DefaultOf(kind) == policy;
            AcceptanceReport report;
            switch (kind)
            {
                case PolicyKinds.Apparel: report = game.outfitDatabase.TryDelete((ApparelPolicy)policy); break;
                case PolicyKinds.Food: report = game.foodRestrictionDatabase.TryDelete((FoodPolicy)policy); break;
                case PolicyKinds.Drug: report = game.drugPolicyDatabase.TryDelete((DrugPolicy)policy); break;
                default: report = game.readingPolicyDatabase.TryDelete((ReadingPolicy)policy); break;
            }

            if (!report.Accepted)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["kind"] = kind,
                    ["policy"] = policy.id,
                    ["label"] = policy.label,
                    // The game's own string, verbatim.
                    ["reason"] = string.IsNullOrEmpty(report.Reason) ? "the database refused the delete" : report.Reason,
                    ["action"] = NoStamp(),
                };

            long seq = Act(V, "delete", kind + ":" + policy.label,
                new Dictionary<string, object> { ["kind"] = kind, ["policy"] = policy.id });
            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["kind"] = kind,
                ["policy"] = policy.id,
                ["label"] = policy.label,
                ["was_default"] = wasDefault,
                ["remaining"] = PolicyList(kind),
                ["action"] = Stamp(seq),
                ["cleared_from_others"] = "TryDelete also nulls this policy on every pawn NOT on a map or "
                    + "caravan before removing it; those pawns fall back to the database default",
            };
        }

        // --------------------------------------------------------------------
        // policy-default {kind, policy}
        //
        // WIDGET GATE — Dialog_ManagePolicies.SetDefaultPolicy -> the database's
        // SetDefault, which SWAPS the policy into slot 0. Slot 0 IS the default
        // (2.4's `policies` verb publishes `default: i == 0` on exactly that
        // basis), so this verb reorders the list and the result says so.
        // --------------------------------------------------------------------
        [Verb("policy-default")]
        public static object PolicyDefault(VerbContext ctx)
        {
            const string V = "policy-default";
            var game = Current.Game ?? throw new VerbArgsException("no active game");
            string kind = KindArg(ctx.Args);
            var policy = FindPolicy(kind, ctx.Args.Raw("policy")
                ?? throw new VerbArgsException("missing required arg 'policy' (an id or a label)"));

            if (DefaultOf(kind) == policy)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = true,
                    ["kind"] = kind,
                    ["policy"] = policy.id,
                    ["label"] = policy.label,
                    ["changed"] = false,
                    ["action"] = NoStamp(),
                };

            switch (kind)
            {
                case PolicyKinds.Apparel: game.outfitDatabase.SetDefault((ApparelPolicy)policy); break;
                case PolicyKinds.Food: game.foodRestrictionDatabase.SetDefault((FoodPolicy)policy); break;
                case PolicyKinds.Drug: game.drugPolicyDatabase.SetDefault((DrugPolicy)policy); break;
                default: game.readingPolicyDatabase.SetDefault((ReadingPolicy)policy); break;
            }
            long seq = Act(V, "default", kind + ":" + policy.label,
                new Dictionary<string, object> { ["kind"] = kind, ["policy"] = policy.id });
            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["kind"] = kind,
                ["policy"] = policy.id,
                ["label"] = policy.label,
                ["changed"] = true,
                ["list"] = PolicyList(kind),
                ["action"] = Stamp(seq),
                ["note"] = "SetDefault SWAPS into slot 0, so the list order changed; slot 0 is the default "
                    + "and that is what the `policies` observer reports as `default:true`",
            };
        }

        // ======================== policy plumbing ============================

        private static string KindArg(VerbArgs args)
        {
            string k = args.StrReq("kind").ToLowerInvariant();
            switch (k)
            {
                case "apparel": case "outfit": case "outfits": return PolicyKinds.Apparel;
                case "food": return PolicyKinds.Food;
                case "drug": case "drugs": return PolicyKinds.Drug;
                case "reading": return PolicyKinds.Reading;
            }
            throw new VerbArgsException($"kind must be {PolicyKinds.Words} (aliases: outfit, drugs)");
        }

        // The database's list, always the real backing list (WorldSafe's Class E
        // note names all four as safe to read).
        private static List<Policy> Policies(string kind)
        {
            var game = Current.Game ?? throw new VerbArgsException("no active game");
            var result = new List<Policy>();
            switch (kind)
            {
                case PolicyKinds.Apparel:
                    foreach (var p in game.outfitDatabase.AllOutfits) result.Add(p);
                    break;
                case PolicyKinds.Food:
                    foreach (var p in game.foodRestrictionDatabase.AllFoodRestrictions) result.Add(p);
                    break;
                case PolicyKinds.Drug:
                    foreach (var p in game.drugPolicyDatabase.AllPolicies) result.Add(p);
                    break;
                default:
                    foreach (var p in game.readingPolicyDatabase.AllReadingPolicies) result.Add(p);
                    break;
            }
            return result;
        }

        // Slot 0 is the default. Read positionally rather than through
        // DefaultOutfit()/DefaultFoodRestriction()/… , which MAKE a policy when
        // the list is empty — the same reason 2.4's `policies` verb reads the
        // position instead of calling them.
        private static Policy DefaultOf(string kind)
        {
            var all = Policies(kind);
            return all.Count > 0 ? all[0] : null;
        }

        private static Policy FindPolicy(string kind, object raw)
        {
            var all = Policies(kind);
            if (raw is double d)
            {
                int id = (int)d;
                foreach (var p in all) if (p != null && p.id == id) return p;
                throw new VerbArgsException($"no {kind} policy with id {id} — see the `policies` verb");
            }
            if (raw is string s)
            {
                foreach (var p in all)
                    if (p != null && string.Equals(p.label, s, StringComparison.OrdinalIgnoreCase)) return p;
                var known = new List<string>();
                foreach (var p in all) if (p != null) known.Add(p.label);
                throw new VerbArgsException(
                    $"no {kind} policy named '{s}' — known: "
                    + (known.Count > 0 ? string.Join(", ", known.ToArray()) : "(none)"));
            }
            throw new VerbArgsException($"a {kind} policy is named by its id (number) or its label (string)");
        }

        // The per-kind lookups `assign` uses.
        private static ApparelPolicy FindApparelPolicy(object raw) => (ApparelPolicy)FindPolicy(PolicyKinds.Apparel, raw);
        private static FoodPolicy FindFoodPolicy(object raw) => (FoodPolicy)FindPolicy(PolicyKinds.Food, raw);
        private static DrugPolicy FindDrugPolicy(object raw) => (DrugPolicy)FindPolicy(PolicyKinds.Drug, raw);
        private static ReadingPolicy FindReadingPolicy(object raw) => (ReadingPolicy)FindPolicy(PolicyKinds.Reading, raw);

        // The filter a policy of this kind actually owns. Reading has TWO
        // (defFilter over books, effectFilter over BookEffects special filters);
        // `filter` here is the def one, which is what the tree in
        // Dialog_ManageReadingPolicies edits.
        private static ThingFilter FilterOf(string kind, Policy p)
        {
            switch (kind)
            {
                case PolicyKinds.Apparel: return ((ApparelPolicy)p).filter;
                case PolicyKinds.Food: return ((FoodPolicy)p).filter;
                case PolicyKinds.Reading: return ((ReadingPolicy)p).defFilter;
                default: return null; // a drug policy is entries, not a filter
            }
        }

        // The PARENT filter the dialog bounds its tree with. Dropping this is
        // the classic gate-in-the-widget bug for a policy editor: without it an
        // apparel policy could be told to allow steel, which the game's own
        // dialog cannot express.
        //
        // Each is the game's own global filter, reproduced from its own
        // definition — and NOTE that two of the three are NOT category-based,
        // which is exactly the sort of thing a plausible-looking guess gets
        // wrong:
        //   apparel  Dialog_ManageApparelPolicies.ApparelGlobalFilter
        //              = SetAllow(ThingCategoryDefOf.Apparel, true)
        //   food     Dialog_ManageFoodPolicies.FoodGlobalFilter
        //              = every ThingDef with Nutrition > 0 (NOT the Foods
        //                category — kibble, meals, raw meat, chocolate and
        //                nutrient paste do not share one category)
        //   reading  Dialog_ManageReadingPolicies.PolicyGlobalFilter
        //              = every ThingDef with a CompBook
        //   drug     no filter at all; a drug policy is DrugPolicyEntry rows
        //
        // Cached statically for the life of the process, as the game caches its
        // own three — the food one is a Nutrition GetStatValueAbstract per def
        // over the whole database (PawnSafe Class F: a non-scribed memo write,
        // and the identical call the vanilla dialog makes on first open).
        private static ThingFilter apparelParent, foodParent, readingParent;

        private static ThingFilter ParentOf(string kind)
        {
            switch (kind)
            {
                case PolicyKinds.Apparel:
                    if (apparelParent == null)
                    {
                        apparelParent = new ThingFilter();
                        apparelParent.SetAllow(ThingCategoryDefOf.Apparel, allow: true);
                    }
                    return apparelParent;
                case PolicyKinds.Food:
                    if (foodParent == null)
                    {
                        foodParent = new ThingFilter();
                        foreach (var d in DefDatabase<ThingDef>.AllDefsListForReading)
                        {
                            if (d == null) continue;
                            try { if (d.GetStatValueAbstract(StatDefOf.Nutrition) > 0f) foodParent.SetAllow(d, allow: true); }
                            catch { }
                        }
                    }
                    return foodParent;
                case PolicyKinds.Reading:
                    if (readingParent == null)
                    {
                        readingParent = new ThingFilter();
                        foreach (var d in DefDatabase<ThingDef>.AllDefsListForReading)
                        {
                            if (d == null) continue;
                            try { if (d.HasComp(typeof(CompBook))) readingParent.SetAllow(d, allow: true); }
                            catch { }
                        }
                    }
                    return readingParent;
                default:
                    return null;
            }
        }

        // allow / disallow / allow_all / disallow_all / drugs, applied in the
        // order the dialog's own controls sit in: the sweep buttons first, then
        // the individual rows, so `{disallow_all:true, allow:["Parka"]}` reads
        // as "only parkas" and does what it says.
        private static List<object> ApplyPolicyEdits(string kind, Policy policy, VerbArgs a, out List<object> refused)
        {
            var edits = new List<object>();
            refused = new List<object>();
            var filter = FilterOf(kind, policy);
            var parent = ParentOf(kind);

            if (filter != null)
            {
                if (a.Bool("disallow_all", false)) { filter.SetDisallowAll(); edits.Add("disallow_all"); }
                if (a.Bool("allow_all", false))
                {
                    filter.SetAllowAll(parent, includeNonStorable: kind == PolicyKinds.Reading);
                    edits.Add("allow_all");
                }
                SetFilterWords(kind, filter, parent, a, "allow", true, edits, refused);
                SetFilterWords(kind, filter, parent, a, "disallow", false, edits, refused);
            }
            else if (a.Has("allow") || a.Has("disallow") || a.Has("allow_all") || a.Has("disallow_all"))
            {
                refused.Add(new Dictionary<string, object>
                {
                    ["arg"] = "allow/disallow",
                    ["reason"] = "a drug policy has no ThingFilter; it is a list of DrugPolicyEntry rows — "
                        + "use `drugs:[{drug:…, for_joy:…, scheduled:…, days_frequency:…}]`",
                });
            }

            if (a.Has("drugs"))
            {
                if (!(policy is DrugPolicy dp))
                {
                    refused.Add(new Dictionary<string, object>
                    {
                        ["arg"] = "drugs",
                        ["reason"] = "only a drug policy has DrugPolicyEntry rows",
                    });
                }
                else if (!(a.Raw("drugs") is List<object> rows))
                {
                    throw new VerbArgsException("'drugs' must be an array of {drug, …} objects");
                }
                else
                {
                    foreach (var raw in rows)
                    {
                        if (!(raw is Dictionary<string, object> row))
                            throw new VerbArgsException("each 'drugs' entry must be an object");
                        var rargs = new VerbArgs(row);
                        var def = Dev.Named<ThingDef>(rargs.StrReq("drug"), "drug");
                        DrugPolicyEntry entry = null;
                        try { entry = dp[def]; } catch { }
                        if (entry == null)
                        {
                            refused.Add(new Dictionary<string, object>
                            {
                                ["drug"] = def.defName,
                                ["reason"] = "this drug has no row in the policy "
                                    + "(DrugPolicy builds its rows from the drug defs that exist at creation)",
                            });
                            continue;
                        }
                        // 2.4's `policies` field names, so an edit and a read
                        // are one vocabulary.
                        if (row.ContainsKey("for_addiction")) entry.allowedForAddiction = rargs.Bool("for_addiction", entry.allowedForAddiction);
                        if (row.ContainsKey("for_joy")) entry.allowedForJoy = rargs.Bool("for_joy", entry.allowedForJoy);
                        if (row.ContainsKey("scheduled")) entry.allowScheduled = rargs.Bool("scheduled", entry.allowScheduled);
                        if (row.ContainsKey("days_frequency")) entry.daysFrequency = (float)rargs.Num("days_frequency", entry.daysFrequency);
                        if (row.ContainsKey("only_if_mood_below")) entry.onlyIfMoodBelow = (float)rargs.Num("only_if_mood_below", entry.onlyIfMoodBelow * 100f) / 100f;
                        if (row.ContainsKey("only_if_joy_below")) entry.onlyIfJoyBelow = (float)rargs.Num("only_if_joy_below", entry.onlyIfJoyBelow * 100f) / 100f;
                        if (row.ContainsKey("take_to_inventory")) entry.takeToInventory = rargs.Int("take_to_inventory", entry.takeToInventory);
                        edits.Add("drug:" + def.defName);
                    }
                }
            }
            return edits;
        }

        // `allow` / `disallow` words: a ThingDef, a ThingCategoryDef or a
        // SpecialThingFilterDef — the three ThingFilter.SetAllow overloads and
        // the three row kinds ThingFilterUI draws.
        private static void SetFilterWords(string kind, ThingFilter filter, ThingFilter parent,
            VerbArgs a, string key, bool allow, List<object> edits, List<object> refused)
        {
            if (!a.Has(key)) return;
            foreach (var word in a.StrList(key))
            {
                var td = DefDatabase<ThingDef>.GetNamedSilentFail(word);
                if (td != null)
                {
                    // THE PARENT-FILTER GATE. Dialog_ManageApparelPolicies bounds
                    // its tree with an Apparel-only global filter, so the player
                    // cannot reach a non-apparel def at all.
                    if (allow && parent != null && !parent.Allows(td))
                    {
                        refused.Add(new Dictionary<string, object>
                        {
                            ["def"] = td.defName,
                            ["reason"] = $"outside the {kind} policy's global filter; the game's own "
                                + "manage dialog does not offer this def",
                        });
                        continue;
                    }
                    filter.SetAllow(td, allow);
                    edits.Add((allow ? "allow:" : "disallow:") + td.defName);
                    continue;
                }
                var cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(word);
                if (cat != null)
                {
                    filter.SetAllow(cat, allow);
                    edits.Add((allow ? "allow:" : "disallow:") + "category " + cat.defName);
                    continue;
                }
                var sf = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(word);
                if (sf != null)
                {
                    filter.SetAllow(sf, allow);
                    edits.Add((allow ? "allow:" : "disallow:") + "special " + sf.defName);
                    continue;
                }
                throw new VerbArgsException(
                    $"'{word}' is not a ThingDef, a ThingCategoryDef or a SpecialThingFilterDef "
                    + $"(arg '{key}')");
            }
        }

        // One policy, in 2.4's `policies` field vocabulary — id, label, default,
        // filter — so an echo here and a read there are the same shape.
        private static Dictionary<string, object> PolicyLine(string kind, Policy p, bool isDefault)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = p.id,
                ["label"] = p.label,
                ["default"] = isDefault,
            };
            var filter = FilterOf(kind, p);
            if (filter != null)
            {
                try { d["filter"] = FilterSummary.Build(filter, null, "all-thingdefs"); }
                catch { }
            }
            if (p is DrugPolicy dp)
            {
                var entries = new List<object>();
                try
                {
                    for (int i = 0; i < dp.Count; i++)
                    {
                        var e = dp[i];
                        if (e?.drug == null) continue;
                        if (!e.allowedForAddiction && !e.allowedForJoy && !e.allowScheduled && e.takeToInventory == 0) continue;
                        entries.Add(new Dictionary<string, object>
                        {
                            ["drug"] = e.drug.defName,
                            ["for_addiction"] = e.allowedForAddiction,
                            ["for_joy"] = e.allowedForJoy,
                            ["scheduled"] = e.allowScheduled,
                            ["days_frequency"] = WorldSafe.R(e.daysFrequency, 2),
                            ["only_if_mood_below"] = WorldSafe.Pct(e.onlyIfMoodBelow),
                            ["only_if_joy_below"] = WorldSafe.Pct(e.onlyIfJoyBelow),
                            ["take_to_inventory"] = e.takeToInventory,
                        });
                    }
                }
                catch { }
                d["entries"] = entries;
                d["entries_note"] = "only entries that allow or stock something are listed";
            }
            return d;
        }

        private static List<object> PolicyList(string kind)
        {
            var all = Policies(kind);
            var list = new List<object>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = all[i].id,
                    ["label"] = all[i].label,
                    ["default"] = i == 0,
                });
            }
            return list;
        }
    }

    internal static class GameNullCheckExtensions
    {
        // A one-line guard that reads at the call site; the protocol's
        // no-active-game code is produced upstream, so a bad-args here is the
        // right shape for "the verb ran with no game".
        public static void NullCheck(this Game game)
        {
            if (game == null) throw new VerbArgsException("no active game");
        }
    }
}
