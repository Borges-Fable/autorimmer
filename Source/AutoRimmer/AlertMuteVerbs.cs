using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= git-bug 280fb78 ==
    // THE ALERTS THE AGENT HAS DELIBERATELY DECIDED NOT TO BE WOKEN BY.
    //
    // 280fb78 makes `alert_on` halt an advance unconditionally, on the same
    // ground as the letter wake: the rule is "is there something I might act
    // on", not "is this bad". But an alert is not a letter. A letter is an
    // event that HAPPENS ONCE; an alert is a STANDING CONDITION, and
    // `alert_on` is a transition precisely so a chronic one wakes you once per
    // on-cycle instead of continuously. A condition the colony has decided not
    // to fix still flickers — `Alert_NeedWarmClothes` goes off the moment a
    // pawn picks up a parka and back on when they drop it — and each flicker
    // would wake the agent for a decision it already made.
    //
    // Evan's ruling (2026-09-01, on the issue): "the agent should have the
    // ability to blacklist what alerts wake them up, while they're playing."
    // RUNTIME, mid-run, not static config.
    //
    // This is session 13's `threat-pardon` ruling applied a second time: THE
    // DECISION MUST BE A RECORDED ACT, NOT A SILENT EXEMPTION. So `reason` is
    // required, the act is journaled with its ids and its reason, the set is
    // scribed so it survives save/load and can be audited afterwards, and
    // `digest.alerts` publishes the whole list beside the alerts it modifies —
    // `active[*].muted` on each live row AND a standalone `muted` list, because
    // an alert muted on day 2 that is not live on day 8 must still be visible
    // on day 8. That last part is the `[[seek-off-is-a-decision-to-flee]]`
    // failure closed: a standing decision the agent cannot see is a decision it
    // will forget it made.
    //
    // ------------------------- THE GATE, HONESTLY ---------------------------
    // DESIGN §Action model says a player verb re-implements the precondition
    // its UI widget holds and cites it. THERE IS NO SUCH WIDGET, and this was
    // checked rather than assumed: `RimWorld/AlertsReadout.cs` has no mute, no
    // dismiss and no hide — its only per-alert interaction is
    // `Alert.OnClick`'s jump-to-target — and `Verse/Prefs.cs` carries no alert
    // preference of any kind. RimWorld has no concept of an alert you have
    // stopped caring about, because a human simply looks past one. Citing a
    // member here would be dressing AutoRimmer's invention as the game's, which
    // is the exact failure the rule exists to prevent (`threat-pardon`'s header
    // made the same call for the same reason). So the precondition is stated as
    // OURS and made narrow instead:
    //
    //   * an id must name a REAL `Alert` subclass in the loaded assemblies
    //     (`Verse/GenTypes.AllSubclassesNonAbstract` over `RimWorld/Alert`),
    //     which is the same identity `alert_on` publishes and
    //     `until:{alert:"…"}` matches — `alert.GetType().Name`. A typo is
    //     refused rather than stored, because a mute that silently matches
    //     nothing is worse than no mute: the agent believes it is covered.
    //   * an id does NOT have to be live. Pre-muting is the normal case — "we
    //     are about to burn three days building, do not wake me for
    //     `Alert_ColonistsIdle`" — and requiring liveness would make the verb
    //     unusable exactly when it is wanted. The listing publishes `live` per
    //     row so the agent can see which of its mutes are currently doing
    //     anything.
    //   * `reason` is required and non-empty on a mute.
    //
    // MODDED ALERTS ARE COVERED FREE, since the subclass scan is over every
    // loaded assembly and not over `AlertsReadout.allAlertTypesCached` — that
    // cache deliberately EXCLUDES `Alert_Custom`/`Alert_CustomCritical`
    // subclasses (decompiled `RimWorld/AlertsReadout.cs`, its static ctor), and
    // AutoRimmer's OWN fixture alerts are exactly those, so keying on the cache
    // would make the acceptance suite unable to mute the only alerts it can
    // deterministically produce.
    //
    // ------------------- PERMANENT UNTIL RELEASED, AND WHY -------------------
    // `threat-pardon` LAPSES on its own: the game reifies "still asleep" twice
    // (`LordToil_Sleep`, `CompCanBeDormant.Awake`), so a pardon can be tied to
    // a real game-side fact and expire when that fact changes. THE ANALOGUE
    // DOES NOT EXIST HERE, and the obvious candidate was checked and killed on
    // evidence: an escalation rule ("un-mute when the alert comes back at a
    // higher priority") is dead code, because `Alert.Priority` is
    // `public virtual AlertPriority Priority => defaultPriority` and a grep of
    // the whole decompiled 1.6 tree for `AlertPriority Priority` returns
    // exactly that one declaration — NO vanilla alert overrides it. Priority is
    // a per-class constant set in the constructor, so it cannot change under a
    // mute and a rule keyed on it would never fire once.
    //
    // A TIMED EXPIRY WAS ALSO REJECTED, and for the stronger reason: an expiry
    // the agent did not choose is a wake it did not ask for, arriving at a tick
    // it has no way to predict, for a decision it already made and recorded.
    // That is the noise the mute exists to remove, deferred rather than
    // removed. The mute is therefore permanent until released, `digest` is what
    // keeps it from being forgotten, and `alert-mute {ids:[…], release:true}`
    // is the one way it ends. If a run wants a mute to lapse, it can release it
    // — releasing is an act too, and it is journaled the same way.
    //
    // ------------- MUTING AN ALERT THAT IS ALREADY ON ------------------------
    // Nothing happens NOW and something happens LATER, which is worth saying
    // out loud rather than leaving to be discovered. `alert_on` fires on the
    // TRANSITION (`AlertScanner.Tick` diffs the readout's own `activeAlerts`
    // against its `known` set), so an alert that is already on has already
    // emitted its event and will not emit another until it goes off and comes
    // back. Muting it therefore changes nothing about the current on-cycle —
    // there is no pending wake to cancel — and takes effect on the NEXT
    // on-cycle. The listing publishes `live:true` for exactly this case, and
    // `already_on` on the applied row, so the agent is told rather than left to
    // infer it from an absence.
    public class AlertMuteComponent : GameComponent
    {
        // Alert class name -> the stated reason. Scribed by value, so a mute
        // outlives the session that set it — the same disposition as
        // `threat-pardon`'s pardons and `posture`'s three settings.
        private Dictionary<string, string> mutes = new Dictionary<string, string>();

        // ---- THE OFF-THREAD MIRROR, and it is not an optimisation ----------
        //
        // `TimeDriver.Notice` is documented "any thread, called synchronously
        // from Journal.Emit" and MAY NOT TOUCH VERSE. `Current.Game` is Verse.
        // So the wake check cannot ask the component; it reads this static
        // snapshot instead, which is replaced WHOLESALE on every mutation
        // (reference assignment is atomic, and `volatile` publishes the write)
        // and never mutated in place. A reader therefore always sees a complete
        // set, old or new, and never a half-built one.
        //
        // In practice `alert_on` is emitted from `AlertScanner.Tick`, which is
        // main-thread — but "in practice" is how the observer bans get broken,
        // and the mirror costs one field.
        private static volatile HashSet<string> mirror =
            new HashSet<string>(StringComparer.Ordinal);

        public AlertMuteComponent(Game game)
        {
        }

        public static AlertMuteComponent Current
            => Verse.Current.Game?.GetComponent<AlertMuteComponent>();

        /// <summary>Any thread. The wake check's whole question.</summary>
        public static bool Muted(string id)
            => id != null && mirror.Contains(id);

        /// <summary>Any thread. A copy, so a caller cannot mutate the mirror.</summary>
        public static List<string> MirrorIds()
        {
            var snap = mirror;
            var list = new List<string>(snap);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public Dictionary<string, string> All => mutes;

        public bool Has(string id) => id != null && mutes.ContainsKey(id);

        public string Reason(string id)
            => id != null && mutes.TryGetValue(id, out var r) ? r : null;

        public void Add(string id, string reason) { mutes[id] = reason; Publish(); }

        public bool Remove(string id)
        {
            bool had = mutes.Remove(id);
            if (had) Publish();
            return had;
        }

        public int Clear()
        {
            int n = mutes.Count;
            mutes.Clear();
            if (n > 0) Publish();
            return n;
        }

        // EVERY write to `mutes` ends here, so the mirror cannot drift from the
        // scribed truth by a later edit that adds a fourth mutation site — the
        // same discipline `TimeDriver.NoteActive` uses for the speed high-water
        // mark.
        private void Publish()
            => mirror = new HashSet<string>(mutes.Keys, StringComparer.Ordinal);

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref mutes, "autoRimmerAlertMutes", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (mutes == null) mutes = new Dictionary<string, string>();
                Publish();
            }
        }

        // Published on every route into a game, not only on load: a NEW colony
        // gets a fresh component with an empty dictionary, and without this the
        // mirror would still hold the previous colony's mutes and silently
        // swallow that colony's wakes. Ordering-independent by construction —
        // three hooks that all do the same idempotent thing, rather than one
        // hook whose position among the other GameComponents would have to be
        // reasoned about.
        public override void FinalizeInit() => Publish();

        public override void LoadedGame() => Publish();

        public override void StartedNewGame() => Publish();

        // Every `Alert` subclass the game could ever instantiate, by the same
        // identity `alert_on` publishes (`alert.GetType().Name`). Built once —
        // `AllSubclassesNonAbstract` walks every loaded assembly — and used
        // only by the verb's precondition, never on the wake path.
        private static Dictionary<string, string> knownAlerts;

        public static bool IsKnownAlert(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return KnownAlerts().ContainsKey(id);
        }

        public static Dictionary<string, string> KnownAlerts()
        {
            if (knownAlerts != null) return knownAlerts;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var t in typeof(Alert).AllSubclassesNonAbstract())
                    if (t != null && !d.ContainsKey(t.Name)) d[t.Name] = t.FullName;
            }
            catch { }
            knownAlerts = d;
            return d;
        }

        // Nearest known ids to a mis-typed one, so a refusal can say what the
        // caller probably meant. Prefix and substring only — no edit distance,
        // because a wrong suggestion is worse than none and this is a message,
        // not a matcher.
        public static List<object> NearMisses(string id, int cap = 5)
        {
            var hits = new List<object>();
            if (string.IsNullOrEmpty(id)) return hits;
            string needle = id.ToLowerInvariant();
            foreach (var name in KnownAlerts().Keys)
            {
                if (hits.Count >= cap) break;
                string n = name.ToLowerInvariant();
                if (n.Contains(needle) || needle.Contains(n)) hits.Add(name);
            }
            hits.Sort((a, b) => string.CompareOrdinal((string)a, (string)b));
            return hits;
        }
    }

    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // alert-mute
        //   {}                                    -> list the set and what is live
        //   {ids:["Alert_LowFood"], reason:"…"}   -> mute (reason REQUIRED)
        //   {ids:[…], release:true}               -> un-mute those
        //   {release_all:true}                    -> un-mute everything
        //
        // Shaped deliberately like `threat-pardon`, argument for argument: the
        // two verbs are the same ruling applied to two subjects, and an agent
        // that has learned one should not have to learn the other.
        // --------------------------------------------------------------------
        [Verb("alert-mute")]
        public static object AlertMute(VerbContext ctx)
        {
            const string V = "alert-mute";
            var store = AlertMuteComponent.Current
                ?? throw new VerbArgsException("no mute store (no game loaded?)");
            var a = ctx.Args;

            if (a.Bool("release_all", false))
            {
                var was = new List<object>();
                foreach (var kv in store.All) was.Add(kv.Key);
                int n = store.Clear();
                // Nothing held is nothing mutated, and a journal row for a
                // no-op dilutes the audit trail this verb exists to keep.
                long s = n > 0
                    ? Act(V, "unmute-all", n + " alerts",
                        new Dictionary<string, object> { ["ids"] = was, ["released"] = n })
                    : 0;
                return AlertMuteListing(V, store, n > 0 ? Stamp(s) : NoStamp(), "un-muted " + n);
            }

            if (!a.Has("ids"))
                return AlertMuteListing(V, store, NoStamp(), null);

            if (!(a.Raw("ids") is List<object> rawIds) || rawIds.Count == 0)
                throw new VerbArgsException(
                    "'ids' must be a non-empty list of Alert class names, e.g. "
                    + "[\"Alert_LowFood\"] — the same id `alert_on` journals and "
                    + "`until:{alert:\"…\"}` matches. Call `alert-mute {}` for the live ones.");
            bool release = a.Bool("release", false);
            string reason = a.Str("reason");
            if (!release && string.IsNullOrEmpty(reason?.Trim()))
                throw new VerbArgsException(
                    "'reason' is required to mute: a mute with no stated reason is the silent "
                    + "exemption this verb exists to prevent (session 13's threat-pardon ruling — "
                    + "the decision is a recorded ACT). Say why this alert should stop waking the "
                    + "run (e.g. \"no cloth until the caravan lands; the parka alert is expected "
                    + "for the next 3 days\").");

            // The live readout ONCE, before the loop: `AlertScanner.Snapshot`
            // is a verbatim read of `AlertsReadout.activeAlerts` and re-reading
            // it per id would pay for it N times for one answer.
            var live = new Dictionary<string, AlertScanner.AlertLine>(StringComparer.Ordinal);
            foreach (var line in AlertScanner.Snapshot())
                if (!live.ContainsKey(line.Id)) live[line.Id] = line;

            var applied = new List<object>();
            var refused = new List<object>();
            foreach (var raw in rawIds)
            {
                if (!(raw is string id) || string.IsNullOrWhiteSpace(id))
                {
                    refused.Add(new Dictionary<string, object>
                    {
                        ["id"] = raw,
                        ["reason"] = "not an Alert class name (string)",
                    });
                    continue;
                }
                id = id.Trim();
                if (release)
                {
                    if (store.Remove(id)) applied.Add(id);
                    else refused.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["reason"] = "was not muted",
                    });
                    continue;
                }
                // THE PRECONDITION: it must be an alert that can actually
                // exist. See the header — a mute that matches nothing is worse
                // than no mute, because the agent believes it is covered.
                if (!AlertMuteComponent.IsKnownAlert(id))
                {
                    var row = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["reason"] = "no Alert subclass with that class name is loaded — "
                            + "ids are `alert.GetType().Name`, e.g. \"Alert_LowFood\"; "
                            + "see `alert-mute {}` for what is live and `digest.alerts` "
                            + "for what the readout is showing",
                    };
                    var near = AlertMuteComponent.NearMisses(id);
                    if (near.Count > 0) row["did_you_mean"] = near;
                    refused.Add(row);
                    continue;
                }
                store.Add(id, reason.Trim());
                var done = new Dictionary<string, object>
                {
                    ["id"] = id,
                    // The header's "muting an alert that is already on" case,
                    // TOLD rather than left to be inferred from an absence: the
                    // mute takes effect on the NEXT on-cycle, because the
                    // current one already emitted its `alert_on`.
                    ["already_on"] = live.ContainsKey(id),
                };
                if (live.ContainsKey(id))
                    done["note"] = "this alert is ON right now, so it has already emitted its "
                        + "`alert_on`; the mute takes effect on its next on-cycle";
                applied.Add(done);
            }

            long seq = applied.Count > 0
                ? Act(V, release ? "unmute" : "mute", applied.Count + " alerts",
                    new Dictionary<string, object>
                    {
                        ["ids"] = AppliedIds(applied),
                        ["reason"] = release ? null : reason.Trim(),
                        ["refused_count"] = refused.Count,
                    })
                : 0;
            var data = AlertMuteListing(V, store, applied.Count > 0 ? Stamp(seq) : NoStamp(),
                (release ? "un-muted " : "muted ") + applied.Count);
            data["applied"] = applied;
            if (refused.Count > 0) data["refused"] = refused;
            return data;
        }

        // `applied` rows are dictionaries on a mute and bare ids on a release;
        // the journal row wants ids either way, so a post-mortem grepping
        // `action` lines does not have to know which branch wrote it.
        private static List<object> AppliedIds(List<object> applied)
        {
            var ids = new List<object>();
            foreach (var row in applied)
                ids.Add(row is Dictionary<string, object> d && d.TryGetValue("id", out var v) ? v : row);
            return ids;
        }

        // The set, plus what the readout is showing right now. Same disposition
        // as `threat-pardon`'s listing: the mutes and the candidates they could
        // be drawn from, in one answer, because ids the agent cannot obtain
        // make the verb unusable and it has no other route to them.
        private static Dictionary<string, object> AlertMuteListing(string verb,
            AlertMuteComponent store, Dictionary<string, object> action, string did)
        {
            var live = AlertScanner.Snapshot();
            var liveIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in live) liveIds.Add(line.Id);

            var muted = new List<object>();
            var ids = new List<string>(store.All.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
                muted.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["reason"] = store.Reason(id),
                    // Whether this mute is doing anything at the moment. A mute
                    // set on day 2 for a condition that has since been fixed is
                    // `live:false`, and that is the row an agent should be
                    // releasing.
                    ["live"] = liveIds.Contains(id),
                });

            var candidates = new List<object>();
            live.Sort((x, y) =>
            {
                int c = ((int)y.Priority).CompareTo((int)x.Priority);
                return c != 0 ? c : x.Order.CompareTo(y.Order);
            });
            foreach (var line in live)
                candidates.Add(new Dictionary<string, object>
                {
                    ["id"] = line.Id,
                    ["label"] = line.Label,
                    ["priority"] = line.Priority.ToString(),
                    ["muted"] = store.Has(line.Id),
                });

            var data = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = true,
                ["muted"] = muted,
                ["mutes_held"] = muted.Count,
                ["live_alerts"] = candidates.Count,
                ["candidates"] = candidates,
                ["action"] = action,
                ["note"] = "a mute is a recorded decision not to be WOKEN, not a filter: "
                    + "`digest.alerts` still lists every active alert and `journal` still "
                    + "carries every `alert_on`. It only stops `advance` halting on that "
                    + "alert's next on-cycle. It is permanent until released — see the "
                    + "verb's header for why nothing lapses it — and `digest.alerts.muted` "
                    + "carries the whole list so a mute set on day 2 is still visible on "
                    + "day 8. `until:{alert:\"<id>\"}` OVERRIDES a mute: asking to wait FOR "
                    + "an alert is a different question from asking to be woken by one.",
            };
            if (did != null) data["did"] = did;
            return data;
        }
    }
}
