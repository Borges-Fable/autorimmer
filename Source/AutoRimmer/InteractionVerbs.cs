using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // `interactions` (spec 2.4): everything awaiting player input — the open
    // letters with their EXACT option labels, and every window on the stack.
    // Read-only and inert: nothing here closes, focuses, reorders or answers a
    // window. Deciding what a dialog MEANS is 3.5's job; 1.7's job was to stop
    // and report; this verb's job is to say what there is to decide.
    //
    // ONE WINDOW VOCABULARY. 1.7 shipped TimeDriver.ForcePausePayload and its
    // `{count, windows:[{type,type_full,title?,layer}], letters}` shape, and
    // rwtest will assert on one shape, so the force-pause half of this result is
    // that public method's output VERBATIM under `force_pause`, and the
    // per-window field names here are the same four with additions — never a
    // second vocabulary. `type` is the short class name (the assertable
    // identifier), `type_full` separates a modded window from a vanilla one that
    // happens to share it.
    //
    // ===================== THE OPAQUE-DIALOG LADDER =========================
    // Open question 3, and it is on the critical path rather than a nicety: the
    // bench runs Hospitality, Gastronomy, Storefront, CashRegister, Guests and
    // RegisterLanes on top of all five DLCs, and every one of them ships its own
    // windows. Three tiers, most specific first:
    //
    //  1. `node-tree` — Dialog_NodeTree and every subclass, which is what EVERY
    //     ChoiceLetter opens (ChoiceLetter.OpenLetter builds a DiaNode from
    //     Choices and stacks a Dialog_NodeTreeWithFactionInfo). `options` carries
    //     the real labels, read off the CURRENT node.
    //  2. `message-box` — Dialog_MessageBox, whose three button texts are public
    //     fields. Mods reach for it constantly (see the charity confirmation in
    //     ChoiceLetter_AcceptVisitors).
    //  3. `opaque` — anything else. The window is scraped for candidate text:
    //     INSTANCE FIELDS ONLY, walking the type chain up to Window, collecting
    //     strings, TaggedStrings and the labels of any List<DiaOption> or
    //     List<FloatMenuOption> it holds. **Fields, never properties** — a field
    //     read cannot execute game code, and a property getter on an unknown
    //     modded window is arbitrary code that may lazily build, allocate or
    //     mutate. That restriction is the whole reason this tier is safe to run
    //     against a mod we have never seen.
    //
    // Every tier reports the same `{type, type_full, title?, layer}` head, so a
    // consumer routes on `kind` and never has to branch on whether it recognised
    // the class.
    //
    // ================== WHY THE OPTION LABELS NEED REFLECTION ===============
    // DiaOption.text is `protected string` (Verse/DiaOption.cs) — there is no
    // public accessor for the words on the button. AccessTools field-ref, bound
    // once and tolerantly, exactly as PawnSafe binds the policy fields.
    //
    // ChoiceLetter.Choices is an ITERATOR that CONSTRUCTS DiaOptions (each with
    // a closure it does not run), and vanilla's construction is read-only —
    // translations, a lookTargets scan, a CameraJumper.CanJump test. It is still
    // arbitrary code on a modded letter, and ChoiceLetter.Option_ViewInQuestsTab
    // dereferences `quest.name` BEFORE its own null check (WorldSafe Class D), so
    // every enumeration sits in a try/catch that degrades the ONE letter to
    // `options_error` rather than the verb.
    public static class InteractionVerbs
    {
        public const int LetterCap = 12;
        public const int OptionCap = 10;
        public const int WindowCap = 16;
        public const int ScrapeCap = 8;
        public const int TextClip = 400;
        public const int LabelClip = 160;

        private static bool refTried;
        private static AccessTools.FieldRef<DiaOption, string> optionTextRef;
        private static FieldInfo nodeTreeCurNode;
        private static FieldInfo nodeTreeTitle;

        private static void EnsureRefs()
        {
            if (refTried) return;
            refTried = true;
            try { optionTextRef = AccessTools.FieldRefAccess<DiaOption, string>("text"); }
            catch (Exception e) { Journal.EmitWarning("interactions: DiaOption text field ref failed: " + e.Message); }
            try { nodeTreeCurNode = AccessTools.Field(typeof(Dialog_NodeTree), "curNode"); }
            catch (Exception e) { Journal.EmitWarning("interactions: Dialog_NodeTree curNode field failed: " + e.Message); }
            try { nodeTreeTitle = AccessTools.Field(typeof(Dialog_NodeTree), "title"); }
            catch { }
        }

        [Verb("interactions")]
        public static object Interactions(VerbContext ctx)
        {
            EnsureRefs();
            int letterCap = ctx.Args.Int("letter_cap", LetterCap);
            if (letterCap < 1 || letterCap > 100) throw new VerbArgsException("letter_cap must be 1..100");
            bool scrape = ctx.Args.Bool("scrape", true);

            var stack = Find.WindowStack;
            var data = new Dictionary<string, object>();

            // 1.7's payload, verbatim and unmodified. One shape, one assertion.
            try { data["force_pause"] = TimeDriver.ForcePausePayload(stack); }
            catch (Exception e)
            {
                data["force_pause"] = null;
                Journal.EmitWarning("interactions: force-pause payload threw: " + e.Message);
            }
            data["blocking"] = WorldSafe.SafeObj(() => (object)(stack != null && stack.WindowsForcePause)) ?? false;

            // ------------------------------ windows -------------------------
            var windows = new List<object>();
            int windowTotal = 0;
            try
            {
                if (stack != null)
                {
                    // WindowStack.Windows is windows.AsReadOnly() — a live view
                    // of the real list. Snapshot it: the describe pass below
                    // reflects over modded window instances.
                    var live = new List<Window>(stack.Windows);
                    windowTotal = live.Count;
                    for (int i = 0; i < live.Count && windows.Count < WindowCap; i++)
                    {
                        var w = live[i];
                        if (w == null) continue;
                        // ImmediateWindow is the game's own throwaway wrapper for
                        // an inline draw callback; it is never something to
                        // answer. Excluded, and counted as such.
                        if (w is ImmediateWindow) { windowTotal--; continue; }
                        windows.Add(Describe(w, scrape));
                    }
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning("interactions: window enumeration threw: " + e.Message);
            }
            data["windows"] = windows;
            data["windows_total"] = windowTotal;
            data["windows_more"] = Math.Max(0, windowTotal - windows.Count);

            // ------------------------------ letters -------------------------
            var letters = new List<object>();
            int letterTotal = 0, choiceCount = 0;
            try
            {
                var stackList = Find.LetterStack?.LettersListForReading;
                if (stackList != null)
                {
                    var snapshot = new List<Letter>(stackList);
                    letterTotal = snapshot.Count;
                    var scored = new List<KeyValuePair<int, Dictionary<string, object>>>();
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        var l = snapshot[i];
                        if (l == null) continue;
                        if (l is ChoiceLetter) choiceCount++;
                        scored.Add(new KeyValuePair<int, Dictionary<string, object>>(
                            LetterAttention(l), LetterLine(l, i)));
                    }
                    // Ordered before the cut: a letter that TIMES OUT first, then
                    // one that carries a real choice, then arrival order. A cap
                    // that cut in arrival order would drop the ransom demand
                    // expiring this hour and keep six "a wanderer joins".
                    scored.Sort((a, b) =>
                    {
                        int c = b.Key.CompareTo(a.Key);
                        return c != 0 ? c : ((int)a.Value["index"]).CompareTo((int)b.Value["index"]);
                    });
                    for (int i = 0; i < scored.Count && i < letterCap; i++) letters.Add(scored[i].Value);
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning("interactions: letter enumeration threw: " + e.Message);
            }
            data["letters"] = letters;
            data["letters_total"] = letterTotal;
            data["letters_more"] = Math.Max(0, letterTotal - letters.Count);
            data["letters_with_choices"] = choiceCount;
            data["order"] = "timeout-then-choice-then-arrival";
            data["source"] = optionTextRef != null ? "backing-field" : "unavailable";
            data["awaiting_input"] = letters.Count > 0 || windows.Count > 0;
            return data;
        }

        private static int LetterAttention(Letter l)
        {
            int score = 0;
            try
            {
                var timed = l as LetterWithTimeout;
                if (timed != null && timed.TimeoutActive)
                {
                    score += 10000;
                    int left = timed.disappearAtTick - Find.TickManager.TicksGame;
                    // Sooner = more urgent. 60000 ticks is a day.
                    score += Math.Max(0, 60000 - Math.Max(0, left)) / 100;
                }
                if (l is ChoiceLetter) score += 2000;
                if (l.def != null && (l.def == LetterDefOf.ThreatBig || l.def == LetterDefOf.ThreatSmall)) score += 5000;
            }
            catch { }
            return score;
        }

        private static Dictionary<string, object> LetterLine(Letter l, int index)
        {
            var d = new Dictionary<string, object>
            {
                ["index"] = index,
                ["id"] = l.ID,
                ["def"] = l.def?.defName,
                ["label"] = WorldSafe.Safe(() => Journal.Truncate(l.Label.ToString(), LabelClip)),
                ["arrival_tick"] = l.arrivalTick,
                ["faction"] = l.relatedFaction?.def?.defName,
                ["faction_name"] = l.relatedFaction?.Name,
                ["type"] = l.GetType().Name,
                ["type_full"] = l.GetType().FullName,
                // Vanilla's own "should this still be shown" test; a
                // ChoiceLetter_AcceptVisitors whose pawns have all left the map
                // answers false, and that is the difference between a live
                // decision and a stale one.
                ["can_show"] = WorldSafe.SafeObj(() => (object)l.CanShowInLetterStack),
                ["dismissable"] = WorldSafe.SafeObj(() => (object)l.CanDismissWithRightClick),
            };

            var timed = l as LetterWithTimeout;
            if (timed != null && WorldSafe.SafeObj(() => (object)timed.TimeoutActive) as bool? == true)
            {
                int now = 0;
                try { now = Find.TickManager.TicksGame; } catch { }
                d["timeout"] = new Dictionary<string, object>
                {
                    ["disappear_at_tick"] = timed.disappearAtTick,
                    ["ticks_left"] = timed.disappearAtTick - now,
                    // ShouldAutomaticallyOpenLetter IS this flag: on its last
                    // tick the letter opens ITSELF from LetterStackTick, which
                    // is 1.7's whole mechanism and halts an advance.
                    ["opens_itself_next_tick"] = WorldSafe.SafeObj(() => (object)timed.LastTickBeforeTimeout) ?? false,
                    ["passed"] = WorldSafe.SafeObj(() => (object)timed.TimeoutPassed) ?? false,
                };
            }

            try
            {
                var lt = l.lookTargets;
                if (lt != null && lt.IsValid)
                {
                    var target = lt.PrimaryTarget;
                    if (target.IsValid && target.Cell.IsValid && target.Map != null)
                        d["at"] = Positions.Out(target.Cell);
                    if (target.HasThing && target.Thing != null)
                        d["target"] = WorldSafe.Safe(() => target.Thing.LabelShort);
                }
            }
            catch { }

            var choice = l as ChoiceLetter;
            if (choice == null)
            {
                d["kind"] = "plain";
                return d;
            }
            d["kind"] = "choice";
            d["title"] = choice.title;
            d["radio_mode"] = choice.radioMode;
            d["text"] = WorldSafe.Safe(() => Journal.Truncate(choice.Text.ToString(), TextClip));

            // THE acceptance line: "interactions lists the letter with exact
            // option labels". Enumerating Choices constructs DiaOptions and runs
            // none of their actions.
            var options = new List<object>();
            try
            {
                int i = 0;
                foreach (var opt in choice.Choices)
                {
                    if (opt == null) continue;
                    if (options.Count >= OptionCap) { i++; continue; }
                    options.Add(OptionLine(opt, i));
                    i++;
                }
                d["options"] = options;
            }
            catch (Exception e)
            {
                // Degrade the ONE letter, never the verb — and say so, because a
                // missing `options` list and an empty one are different states.
                d["options"] = options;
                d["options_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120);
                Journal.EmitWarning("interactions: Choices threw for letter "
                    + l.GetType().Name + ": " + e.Message);
            }
            return d;
        }

        private static Dictionary<string, object> OptionLine(DiaOption opt, int index)
        {
            string label = null;
            try { label = optionTextRef != null ? optionTextRef(opt) : null; }
            catch { }
            return new Dictionary<string, object>
            {
                ["index"] = index,
                // The literal words on the button. Null means the field ref did
                // not bind — see `source` — not that the button is unlabelled.
                ["label"] = Journal.Truncate(label, LabelClip),
                ["disabled"] = opt.disabled,
                ["disabled_reason"] = opt.disabledReason,
                // resolveTree = picking it closes the dialog. A link means it
                // opens a follow-up node instead, which 3.5 must expect.
                ["closes"] = opt.resolveTree,
                ["opens_node"] = opt.link != null || opt.linkLateBind != null,
            };
        }

        // ------------------------- window description -----------------------
        // The 1.7 head verbatim, plus `kind` and whatever that kind can carry.
        private static Dictionary<string, object> Describe(Window w, bool scrape)
        {
            var t = w.GetType();
            var d = new Dictionary<string, object>
            {
                ["type"] = t.Name,
                ["type_full"] = t.FullName,
                ["layer"] = WorldSafe.Safe(() => w.layer.ToString()),
                ["force_pause"] = w.forcePause,
                ["absorbs_input"] = w.absorbInputAroundWindow,
            };
            try { if (!string.IsNullOrEmpty(w.optionalTitle)) d["title"] = w.optionalTitle; }
            catch { }

            if (w is Dialog_NodeTree tree)
            {
                d["kind"] = "node-tree";
                try
                {
                    if (nodeTreeTitle != null)
                    {
                        var title = nodeTreeTitle.GetValue(tree) as string;
                        if (!string.IsNullOrEmpty(title)) d["title"] = title;
                    }
                }
                catch { }
                try
                {
                    var node = nodeTreeCurNode?.GetValue(tree) as DiaNode;
                    if (node != null)
                    {
                        d["text"] = Journal.Truncate(node.text.ToString(), TextClip);
                        var options = new List<object>();
                        var src = node.options;
                        for (int i = 0; i < src.Count && options.Count < OptionCap; i++)
                            if (src[i] != null) options.Add(OptionLine(src[i], i));
                        d["options"] = options;
                        d["options_total"] = src.Count;
                    }
                    else
                    {
                        d["opaque"] = true;
                        d["hint"] = "Dialog_NodeTree with no readable curNode";
                    }
                }
                catch (Exception e)
                {
                    d["opaque"] = true;
                    d["hint"] = "curNode read threw: " + Journal.Truncate(e.Message, 120);
                }
                return d;
            }

            if (w is Dialog_MessageBox box)
            {
                d["kind"] = "message-box";
                var buttons = new List<object>();
                AddButton(buttons, box.buttonAText, 0);
                AddButton(buttons, box.buttonBText, 1);
                AddButton(buttons, box.buttonCText, 2);
                d["buttons"] = buttons;
                if (!string.IsNullOrEmpty(box.title)) d["title"] = box.title;
                d["text"] = WorldSafe.Safe(() => Journal.Truncate(box.text.ToString(), TextClip));
                return d;
            }

            // Tier 3. Everything the bench's visitor cluster and the DLCs open
            // that we have never seen.
            d["kind"] = "opaque";
            d["opaque"] = true;
            if (scrape)
            {
                var found = Scrape(w);
                if (found.Count > 0) d["fields"] = found;
                d["hint"] = "unrecognised window class; `fields` is a read-only "
                    + "scrape of its instance FIELDS (never properties) for candidate labels";
            }
            return d;
        }

        private static void AddButton(List<object> into, string text, int index)
        {
            if (string.IsNullOrEmpty(text)) return;
            into.Add(new Dictionary<string, object>
            {
                ["index"] = index,
                ["label"] = Journal.Truncate(text, LabelClip),
            });
        }

        // FIELDS ONLY, walking the type chain up to and including Window. A
        // field read cannot run game code; a property getter can, and on an
        // unknown modded window that is exactly the lazy-getter hazard class the
        // whole project is written around. Bounded on every axis: types walked,
        // fields per type, values kept, characters per value.
        private static Dictionary<string, object> Scrape(Window w)
        {
            var result = new Dictionary<string, object>();
            try
            {
                var t = w.GetType();
                int depth = 0;
                while (t != null && depth < 6)
                {
                    var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length && result.Count < ScrapeCap; i++)
                    {
                        var f = fields[i];
                        object v;
                        try { v = f.GetValue(w); }
                        catch { continue; }
                        if (v == null) continue;

                        if (v is string s)
                        {
                            if (s.Length == 0 || s.Length > TextClip) continue;
                            result[f.Name] = Journal.Truncate(s, LabelClip);
                            continue;
                        }
                        if (v is TaggedString ts)
                        {
                            string str = null;
                            try { str = ts.ToString(); } catch { }
                            if (string.IsNullOrEmpty(str) || str.Length > TextClip) continue;
                            result[f.Name] = Journal.Truncate(str, LabelClip);
                            continue;
                        }
                        if (v is List<DiaOption> dias)
                        {
                            var labels = new List<object>();
                            for (int j = 0; j < dias.Count && labels.Count < OptionCap; j++)
                                if (dias[j] != null) labels.Add(OptionLine(dias[j], j));
                            if (labels.Count > 0) result[f.Name] = labels;
                            continue;
                        }
                        if (v is List<FloatMenuOption> menu)
                        {
                            var labels = new List<object>();
                            for (int j = 0; j < menu.Count && labels.Count < OptionCap; j++)
                            {
                                var o = menu[j];
                                if (o == null) continue;
                                // FloatMenuOption.Label and .Disabled are plain
                                // reads over labelInt / a bool (Verse/
                                // FloatMenuOption.cs) — no lazy build.
                                labels.Add(new Dictionary<string, object>
                                {
                                    ["index"] = j,
                                    ["label"] = Journal.Truncate(WorldSafe.Safe(() => o.Label), LabelClip),
                                    ["disabled"] = WorldSafe.SafeObj(() => (object)o.Disabled) ?? false,
                                });
                            }
                            if (labels.Count > 0) result[f.Name] = labels;
                            continue;
                        }
                    }
                    if (t == typeof(Window)) break;
                    t = t.BaseType;
                    depth++;
                }
            }
            catch { }
            return result;
        }
    }
}
