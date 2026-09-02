"""Page two: the agent's own instrument panel, not the colony's.

Page one answers "what happened". This answers "what could have happened" —
the verbs the mod exposes against the ones this run ever reached, the shelf of
standing documents the driver was meant to be reading, and the checklist it
actually recorded. A run driven off numbers alone looks fine on page one; the
64 verbs it never called and the playbook page it violated are only visible
here.

All of it is read off disk, like everything else in this tool.
"""

import json
import re
from collections import Counter

VERB_RE = re.compile(r'\[Verb\("([^"]+)"')
SHELF = [("playbook", "playbook", "*.md"),
         ("checklists", "checklists", "*.md"),
         ("colony memory", "RUNS", "*.colony.md")]


def verbs(repo):
    """Every verb the mod exposes, name -> declaring file.

    The registry is the `[Verb("…")]` attribute, so the source IS the list —
    asking the bench would mean sending a command, which this tool does not do.
    Returns {} when Source/ is not beside the transcripts, and the caller falls
    back to the ops the run actually used.
    """
    out = {}
    src = repo / "Source" / "AutoRimmer"
    if not src.is_dir():
        return out
    for f in sorted(src.glob("*.cs")):
        for name in VERB_RE.findall(f.read_text(encoding="utf-8", errors="replace")):
            out.setdefault(name, f.stem)
    return out


def usage(steps, upto=None):
    """op -> calls, over the whole run or up to a step."""
    return Counter(s.op for s in (steps if upto is None else steps[:upto + 1]))


def shelf(repo, run_dir):
    """The documents the driver had on the shelf, as (group, [(name, bytes)])."""
    out = []
    for label, sub, pat in SHELF:
        d = repo / sub
        if not d.is_dir():
            continue
        files = sorted(d.glob(pat), key=lambda p: p.name.lower())
        if files:
            out.append((label, [(p.name, p.stat().st_size) for p in files]))
    if run_dir and run_dir.is_dir():
        files = sorted(run_dir.glob("*.md"), key=lambda p: p.name.lower())
        if files:
            out.append((f"run · {run_dir.name}",
                        [(p.name, p.stat().st_size) for p in files]))
    return out


def checklist(run_dir):
    """`checklist.ndjson` — the driver's own record of what it decided and why.

    The one place in the run where the agent wrote down a READING and a NOTE
    rather than a verb call, which makes it the closest thing on disk to what
    it was thinking.
    """
    out = []
    p = (run_dir / "checklist.ndjson") if run_dir else None
    if not p or not p.is_file():
        return out
    for line in p.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            e = json.loads(line)
        except (json.JSONDecodeError, ValueError):
            continue
        if isinstance(e, dict):
            out.append(e)
    return out
