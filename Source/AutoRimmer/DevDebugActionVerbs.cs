using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace AutoRimmer
{
    // ========================================================================
    // dev:debug-action {path[], category?, pawn?, list?}
    //
    // Runs one entry of the game's own dev menu (Dialog_Debug > Actions) by its
    // label path, the way a click would, and returns what it said. This is the
    // generic route to every mod's [DebugAction] tooling, which otherwise needs
    // a human at the menu.
    //
    // Provenance:
    //   LudeonTK/DebugTabMenu_Actions.InitActions builds the tree the menu shows
    //   from every [DebugAction] method (plus [DebugActionYielder]); a method
    //   returning List<DebugActionNode> becomes a submenu via childGetter.
    //   LudeonTK/DebugActionNode.Enter is what a click does: TrySetupChildren,
    //   descend if there are children, else run by actionType -
    //     Action          -> action()
    //     ToolMapForPawns -> a DebugTool whose click runs pawnAction on every
    //                        pawn under UI.MouseCell()
    //     ToolMap/World   -> a DebugTool whose click runs action, which reads
    //                        UI.MouseCell() (or the world tile) itself.
    //   So Action runs directly, ToolMapForPawns runs pawnAction(pawn) with the
    //   `pawn` arg standing in for what the click would resolve to, and
    //   ToolMap/ToolWorld are REFUSED: the action reads the mouse itself and
    //   there is nothing to hand it.
    //   Visibility follows DebugActionNode.VisibleNow (the attribute's allowed
    //   game states and any visibilityGetter), as the menu does.
    //
    // The tree is rebuilt on every call. TrySetupChildren caches a submenu's
    // childGetter result on the node for good, so a cached tree would serve a
    // stale list (of pawns, factions, generated languages...).
    //
    // Output. Most debug actions report only through Log.Message or an on-screen
    // Messages.Message. Verse.Log stops forwarding everything once 10000 Unity
    // log messages have been counted (Log.Notify_MessageReceivedThreadedInternal),
    // which a noisy mod list reaches within minutes of play - after that a
    // debug action's output is silently gone. So for the duration of the call
    // Log.Message/Warning/Error and Messages.Message are captured by PREFIXES,
    // which run before Log's own PreventLogging check.
    // ========================================================================
    public static class DevDebugActionVerbs
    {
        // List<object>, not List<Dictionary<..>>: MiniJson.Write serializes only List<object>
        // and IEnumerable<string>, and prints any other list as its type name.
        internal static List<object> capture;

        internal static void Capture(string type, string text)
        {
            if (capture == null || text == null) return;
            if (capture.Count >= 500) return;
            capture.Add(new Dictionary<string, object> { ["type"] = type, ["text"] = text });
        }

        [Verb("dev:debug-action")]
        public static object DebugAction(VerbContext ctx)
        {
            const string V = "dev:debug-action";
            Dev.Gate(V);
            var a = ctx.Args;
            var path = a.Has("path") ? a.StrList("path") : new List<string>();
            string category = a.Str("category", null);
            bool list = a.Bool("list", false);

            DebugActionNode node = BuildTree();
            var walked = new List<string>();
            for (int i = 0; i < path.Count; i++)
            {
                var candidates = VisibleChildren(node);
                if (i == 0 && category != null)
                    candidates = candidates.Where(c => Same(c.category, category)).ToList();
                node = Match(candidates, path[i], walked, category, i == 0);
                walked.Add(node.LabelNow);
            }

            var children = VisibleChildren(node);
            if (path.Count == 0)
                return Listing(node, category, walked);
            if (children.Count > 0)
            {
                if (list) return Listing(node, null, walked);
                throw new VerbArgsException($"'{Here(walked)}' is a submenu; add one more path element."
                    + Options(children));
            }
            if (list)
                throw new VerbArgsException($"'{Here(walked)}' is an action, not a submenu (list:true lists submenus)");

            Pawn pawn = null;
            switch (node.actionType)
            {
                case DebugActionType.Action:
                    if (node.action == null)
                        throw new VerbArgsException($"'{Here(walked)}' has no action to run");
                    break;
                case DebugActionType.ToolMapForPawns:
                    if (node.pawnAction == null)
                        throw new VerbArgsException($"'{Here(walked)}' has no pawn action to run");
                    pawn = Dev.PawnArg(Dev.CurrentMap(V), a, "pawn");
                    break;
                default:
                    throw new VerbArgsException($"'{Here(walked)}' is a {node.actionType} tool: its action "
                        + "reads the mouse cell (or world tile) itself, so there is no argument to hand it. "
                        + "Use the matching dedicated verb instead.");
            }

            var windowsBefore = new HashSet<Window>(Find.WindowStack.Windows);
            var output = new List<object>();
            string threw = null;
            capture = output;
            try
            {
                if (pawn != null) node.pawnAction(pawn);
                else node.action();
            }
            catch (Exception ex)
            {
                threw = ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
            }
            finally
            {
                capture = null;
                node.DirtyLabelCache();
            }

            var opened = Find.WindowStack.Windows.Where(w => !windowsBefore.Contains(w))
                .Select(w => w.GetType().Name).ToList();

            string target = Here(walked) + (pawn != null ? " -> " + pawn.LabelShort : "");
            long seq = Dev.Emit(V, "debug-action", target, new Dictionary<string, object>
            {
                ["action_type"] = node.actionType.ToString(),
                ["output_lines"] = output.Count,
                ["threw"] = threw != null,
            });

            var result = new Dictionary<string, object>
            {
                ["path"] = walked,
                ["category"] = node.category,
                ["action_type"] = node.actionType.ToString(),
                ["output"] = output,
                ["windows_opened"] = opened,
                ["threw"] = threw,
                ["dev"] = Dev.Stamp(seq),
            };
            if (pawn != null)
                result["pawn"] = new Dictionary<string, object>
                {
                    ["id"] = pawn.thingIDNumber,
                    ["name"] = pawn.LabelShort,
                };
            if (opened.Count > 0)
                result["note"] = "the action opened a window; a later advance may halt on it until it is dismissed";
            return result;
        }

        private static DebugActionNode BuildTree()
        {
            var menu = new DebugTabMenu_Actions(null, null, null);
            return menu.InitActions(new DebugActionNode());
        }

        private static List<DebugActionNode> VisibleChildren(DebugActionNode node)
        {
            node.TrySetupChildren();
            return node.children.Where(c => c != null && c.VisibleNow).ToList();
        }

        private static bool Same(string x, string y)
            => string.Equals(x?.Trim(), y?.Trim(), StringComparison.OrdinalIgnoreCase);

        // "T: " is the prefix InitActions adds to tool actions and "..." the
        // suffix it adds to submenus; a caller may give the label either way.
        private static string Bare(string label)
        {
            if (label == null) return "";
            var s = label.Trim();
            if (s.StartsWith("T: ")) s = s.Substring(3);
            if (s.EndsWith("...")) s = s.Substring(0, s.Length - 3);
            return s.Trim();
        }

        private static DebugActionNode Match(List<DebugActionNode> candidates, string wanted,
            List<string> walked, string category, bool top)
        {
            var hits = candidates.Where(c => Same(c.LabelNow, wanted) || Same(c.label, wanted)).ToList();
            if (hits.Count == 0)
                hits = candidates.Where(c => Same(Bare(c.LabelNow), Bare(wanted))).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
                throw new VerbArgsException($"'{wanted}' is ambiguous under '{Here(walked)}' - "
                    + (top ? "pass category: one of " + string.Join(", ", hits.Select(h => h.category).Distinct().ToArray())
                           : hits.Count + " entries share that label"));
            throw new VerbArgsException($"no dev-menu entry '{wanted}' under '{Here(walked)}'"
                + (top && category != null ? $" in category '{category}'" : "") + Options(candidates));
        }

        private static string Here(List<string> walked)
            => walked.Count == 0 ? "Actions" : string.Join(" > ", walked.ToArray());

        private static string Options(List<DebugActionNode> nodes)
        {
            if (nodes.Count == 0) return " (it has no visible entries)";
            var labels = nodes.Select(n => n.LabelNow).Take(40).ToArray();
            return " - entries: " + string.Join(" | ", labels) + (nodes.Count > 40 ? $" | ...{nodes.Count - 40} more" : "");
        }

        private static object Listing(DebugActionNode node, string category, List<string> walked)
        {
            var children = VisibleChildren(node);
            if (walked.Count == 0 && category == null)
            {
                return new Dictionary<string, object>
                {
                    ["at"] = "Actions",
                    ["categories"] = children.GroupBy(c => c.category ?? "General")
                        .OrderBy(g => g.Key)
                        .Select(g => (object)new Dictionary<string, object> { ["category"] = g.Key, ["entries"] = g.Count() })
                        .ToList(),
                    ["note"] = "pass category to list one, then path to walk into an entry",
                };
            }
            if (walked.Count == 0)
                children = children.Where(c => Same(c.category, category)).ToList();
            return new Dictionary<string, object>
            {
                ["at"] = Here(walked),
                ["entries"] = children.Select(c => (object)new Dictionary<string, object>
                {
                    ["label"] = c.LabelNow,
                    ["category"] = c.category,
                    ["action_type"] = c.actionType.ToString(),
                    ["submenu"] = c.childGetter != null || c.children.Count > 0,
                }).ToList(),
            };
        }

        [HarmonyPatch(typeof(Log), nameof(Log.Message), typeof(string))]
        public static class Patch_CaptureMessage
        {
            public static void Prefix(string text) => Capture("message", text);
        }

        [HarmonyPatch(typeof(Log), nameof(Log.Warning), typeof(string))]
        public static class Patch_CaptureWarning
        {
            public static void Prefix(string text) => Capture("warning", text);
        }

        [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
        public static class Patch_CaptureError
        {
            public static void Prefix(string text) => Capture("error", text);
        }

        [HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
        public static class Patch_CaptureOnScreen
        {
            public static void Prefix(Message msg) => Capture("on_screen", msg?.text);
        }
    }
}
