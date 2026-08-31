using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // RESEARCH PROJECT SELECTION — one call, no window.
    //
    // Session-4 amendment item 5, dispatched to this spec: until now the only
    // way to move research was `dev:finish-research`, and spec 4.3 bans dev
    // verbs after staging — so an unattended colony could never pick its next
    // project, while `Alert_NeedResearchProject` fired on our own fixtures.
    //
    // THE GATE LIVES IN THE WIDGET, and this is DESIGN's own third worked
    // example of the invariant: RimWorld/ResearchManager.cs SetCurrentProject
    // tests `proj.baseCost > 0f` and NOTHING ELSE. No prerequisites, no
    // techprints, no bench, no tech level. Verse/ResearchProjectDef.cs
    // CanStartNow is the real gate and it lives in the research tab's UI. So a
    // model-only implementation would let the agent research ship reactors on
    // day one, silently, while looking correct.
    //
    // AND CanStartNow CANNOT BE CALLED (WorldSafe Class A): it bottoms out in
    // ResearchManager.GetProgress, which inserts a zero entry into a scribed
    // dictionary on a miss — so merely ASKING "can I start this?" about every
    // project would write one entry per project on the bench into the save,
    // permanently. `WorldSafe.CanStart` is 2.4's shipped guarded route that
    // re-derives the whole ladder without the insert, and it is REUSED here
    // rather than re-derived: one implementation, one place to be wrong.
    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // research-set {project}
        //
        // Reproduces CanStartNow and returns the blocking clause by name
        // ("finished" | "prerequisites" | "techprints" | "no-bench" |
        // "mechanitor" | "analysis" | "codex-hidden" | "grav-engine"), which is
        // the same vocabulary 2.4's `research` verb publishes in `blocked_by` —
        // so "why can't I start this" reads the same from the observer and from
        // the act.
        //
        // ANOMALY KNOWLEDGE PROJECTS ARE REFUSED, deliberately, for two
        // independent reasons:
        //   1. DESIGN's non-goals: DLC-specific colony management is not driven
        //      in v1, and Anomaly entities are named in that list.
        //   2. SetCurrentProject's own second half walks
        //      `CurrentAnomalyKnowledgeProjects`, whose getter runs
        //      EnsureKnowledgeProjectsInitialized and ADDS to a scribed list —
        //      the trap 2.4's `research` observer already declines to touch
        //      ("anomaly knowledge projects are not enumerated"). Refusing keeps
        //      one rule across observe and act instead of two.
        // A knowledge project also has `baseCost == 0`, so the FIRST half of
        // SetCurrentProject would silently not set it either: the verb would
        // report success and nothing would have happened.
        // --------------------------------------------------------------------
        [Verb("research-set")]
        public static object ResearchSet(VerbContext ctx)
        {
            const string V = "research-set";
            var mgr = Find.ResearchManager ?? throw new VerbArgsException("no research manager");
            var proj = Dev.Named<ResearchProjectDef>(ctx.Args.StrReq("project"), "project");

            if (proj.knowledgeCategory != null)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["project"] = proj.defName,
                    ["reason"] = "anomaly knowledge projects are not driven in v1 (DESIGN non-goals: "
                        + "DLC-specific colony management), and selecting one would initialise the scribed "
                        + "anomaly-knowledge list that the `research` observer deliberately does not touch. "
                        + "Its baseCost is 0, so ResearchManager.SetCurrentProject would not set it as the "
                        + "current project either.",
                    ["knowledge_category"] = proj.knowledgeCategory.defName,
                    ["action"] = NoStamp(),
                };

            // SetCurrentProject's ONLY check, stated plainly so a rejection here
            // is never mistaken for our own invention.
            if (!(proj.baseCost > 0f))
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["project"] = proj.defName,
                    ["reason"] = "baseCost is 0; ResearchManager.SetCurrentProject silently ignores such a "
                        + "project (its one and only check)",
                    ["action"] = NoStamp(),
                };

            // PlayerHasAnyAppropriateResearchBench walks every colonist building
            // on every map; asked once here, memoised the way 2.4 does it.
            var benchMemo = new Dictionary<ResearchProjectDef, bool>();
            Func<ResearchProjectDef, bool> benchOk = p =>
            {
                if (benchMemo.TryGetValue(p, out var v)) return v;
                bool ok = false;
                try { ok = p.PlayerHasAnyAppropriateResearchBench; } catch { }
                benchMemo[p] = ok;
                return ok;
            };

            // THE WIDGET GATE, via 2.4's shipped guarded route.
            if (!WorldSafe.CanStart(proj, out string blockedBy, benchOk))
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["project"] = proj.defName,
                    ["label"] = WorldSafe.Safe(() => proj.LabelCap.ToString()),
                    ["blocked_by"] = blockedBy,
                    ["reason"] = BlockedReason(proj, blockedBy),
                    ["prerequisites"] = Prereqs(proj),
                    ["source"] = WorldSafe.ResearchRefsOk ? "backing-field" : "unavailable",
                    ["action"] = NoStamp(),
                    ["note"] = "ResearchManager.SetCurrentProject would have ACCEPTED this "
                        + "(it checks only baseCost > 0); ResearchProjectDef.CanStartNow is the real gate "
                        + "and it lives in the research tab's UI. This verb reproduces it.",
                };

            ResearchProjectDef before = null;
            try { before = mgr.GetProject(); } catch { }
            mgr.SetCurrentProject(proj);
            ResearchProjectDef after = null;
            try { after = mgr.GetProject(); } catch { }

            long seq = Act(V, "set-project", proj.defName,
                new Dictionary<string, object>
                {
                    ["project"] = proj.defName,
                    ["before"] = before?.defName,
                });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                // Echo the DURABLE state, not a hope: read the manager back
                // rather than assert the call worked.
                ["ok"] = after == proj,
                ["project"] = proj.defName,
                ["label"] = WorldSafe.Safe(() => proj.LabelCap.ToString()),
                ["previous"] = before?.defName,
                ["current"] = after?.defName,
                ["cost"] = WorldSafe.R(proj.Cost, 0),
                // Guarded: WorldSafe.Progress reads the backing dictionary with
                // TryGetValue, never ResearchManager.GetProgress (which inserts).
                ["progress"] = WorldSafe.R(WorldSafe.Progress(proj), 0),
                ["pct"] = proj.Cost > 0f ? WorldSafe.Pct(WorldSafe.Progress(proj) / proj.Cost) : 0,
                ["tech_level"] = proj.techLevel.ToString(),
                ["bench_ok"] = proj.requiredResearchBuilding == null || benchOk(proj),
                ["source"] = WorldSafe.ResearchRefsOk ? "backing-field" : "unavailable",
                ["action"] = Stamp(seq),
                ["note"] = "selecting a project does no work: a colonist with the Research work type active "
                    + "must sit at a research bench. Check `work-priorities` and advance.",
            };
        }

        // --------------------------------------------------------------------
        // research-stop {project?}
        //
        // RimWorld/ResearchManager.cs StopProject nulls `currentProj` only when
        // it IS the current project; with no argument this verb stops whatever
        // is current, which is the button the research tab actually offers.
        // Progress is NOT lost — it stays in the scribed dictionary.
        // --------------------------------------------------------------------
        [Verb("research-stop")]
        public static object ResearchStop(VerbContext ctx)
        {
            const string V = "research-stop";
            var mgr = Find.ResearchManager ?? throw new VerbArgsException("no research manager");
            ResearchProjectDef current = null;
            try { current = mgr.GetProject(); } catch { }

            var proj = ctx.Args.Has("project")
                ? Dev.Named<ResearchProjectDef>(ctx.Args.StrReq("project"), "project")
                : current;

            if (proj == null)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["reason"] = "no research project is currently selected",
                    ["action"] = NoStamp(),
                };
            if (proj.knowledgeCategory != null)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["project"] = proj.defName,
                    ["reason"] = "anomaly knowledge projects are not driven in v1; StopProject's second half "
                        + "walks the scribed anomaly-knowledge list (see `research-set`)",
                    ["action"] = NoStamp(),
                };

            mgr.StopProject(proj);
            ResearchProjectDef after = null;
            try { after = mgr.GetProject(); } catch { }

            long seq = Act(V, "stop-project", proj.defName,
                new Dictionary<string, object> { ["project"] = proj.defName });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["project"] = proj.defName,
                ["was_current"] = current == proj,
                ["current"] = after?.defName,
                ["progress"] = WorldSafe.R(WorldSafe.Progress(proj), 0),
                ["cost"] = WorldSafe.R(proj.Cost, 0),
                ["action"] = Stamp(seq),
                ["note"] = "progress is kept — StopProject only clears the selection",
            };
        }

        // CanStart's clause word turned into a sentence, in the game's own
        // terms. The word itself is published beside it so a program can switch
        // on the word and a human can read the sentence — 2.4's `blocked_by`
        // rollup uses the same words.
        private static string BlockedReason(ResearchProjectDef proj, string blockedBy)
        {
            switch (blockedBy)
            {
                case "finished": return "already finished";
                case "prerequisites": return "one or more prerequisite projects are not finished";
                case "techprints":
                    int applied = 0;
                    try { applied = Find.ResearchManager.GetTechprints(proj); } catch { }
                    return $"needs {proj.TechprintCount} techprint(s), {applied} applied";
                case "no-bench":
                    return "the colony has no research bench of the required kind"
                        + (proj.requiredResearchBuilding != null ? " (" + proj.requiredResearchBuilding.defName + ")" : "");
                case "mechanitor": return "requires a mechanitor";
                case "analysis": return "requires more analysed items";
                case "codex-hidden": return "hidden in the entity codex";
                case "grav-engine": return "requires a gravship engine inspection";
                default: return "blocked (" + (blockedBy ?? "unknown") + ")";
            }
        }

        // The unfinished prerequisites, by name, so a rejection is actionable
        // rather than merely correct. Guarded: WorldSafe.Finished, never
        // ResearchProjectDef.IsFinished.
        private static List<object> Prereqs(ResearchProjectDef proj)
        {
            var list = new List<object>();
            try
            {
                if (proj.prerequisites != null)
                    foreach (var p in proj.prerequisites)
                        if (p != null && !WorldSafe.Finished(p)) list.Add(p.defName);
                if (proj.hiddenPrerequisites != null)
                    foreach (var p in proj.hiddenPrerequisites)
                        if (p != null && !WorldSafe.Finished(p)) list.Add(p.defName);
            }
            catch { }
            return list;
        }
    }
}
