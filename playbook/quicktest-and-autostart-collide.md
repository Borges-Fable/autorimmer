# `--quicktest` and an `autostart.rws` cannot both exist

- **severity**: Critical
- **confidence**: verified-in-source (and reproduced 2/2, then fixed 1/1, on 2026-08-31)
- **bites when**: launching the agent bench with `run-agent.sh --quicktest` on any
  profile where a previous session left `autostart.rws` in `Saves/`

## The symptom

Map generation fails. `status.json` heartbeats normally but stays at
`tick: 0, gameLoaded: false`; `rwa status` then reports `menu`, correctly, on a
fresh heartbeat. The launcher's own stdout carries no stack. It looks like bad
luck with a seed, and session 11 recorded it as a one-off to relaunch past.

It is not luck. It is deterministic, and it recurs every launch until the save
is moved.

## The mechanism, from the decompiled 1.6 source

`Verse/Root_Entry.Start` looks for an autostart save before anything else:

    FileInfo fileInfo = (Root.checkedAutostartSaveFile ? null : SaveGameFilesUtility.GetAutostartSaveFile());
    Root.checkedAutostartSaveFile = true;
    if (fileInfo != null) GameDataSaveLoader.LoadGame(fileInfo);

`GameDataSaveLoader.LoadGame(string)` does **not** load anything itself. It
queues a long event whose target scene is `"Play"`:

    LongEventHandler.QueueLongEvent(PreLoadAct, "Play", "LoadingLongEvent", doAsynchronously: true, null);

The scene switch therefore happens BEFORE `PreLoadAct` runs. So
`Verse/Root_Play.Start` executes while `Current.Game` is still null, sees
`Root.checkedAutostartSaveFile == true` (Root_Entry just set it), falls through
to its third branch and queues the quicktest lambda:

    if (Current.Game == null) { SetupForQuickTestPlay(); … }
    Current.Game.InitNewGame();

Now the queue holds `PreLoadAct` first, the quicktest lambda second.
`PreLoadAct` runs and assigns `Current.Game = new Game()` with an `InitData` and
**no `World`**. The quicktest lambda then runs, finds `Current.Game != null`,
**skips `SetupForQuickTestPlay()` — which is the only thing that would have
built the world** — and calls `InitNewGame()` on the half-built Game.
`InitNewGame` gets past its `initData == null` guard and dereferences
`Find.WorldObjects`, whose getter is `World.worldObjects`, and `Find.World`
returns null because `Current.Game.World` is null and `Current.CreatingWorld` is
null too. NRE, handled by `GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap`,
which sends the game to the main menu.

The stack, which lives only in Unity's `Player.log` and is overwritten by the
next launch:

    Exception from asynchronous event: System.NullReferenceException
      at Verse.Find.get_WorldObjects ()
      at Verse.Game.InitNewGame ()
      at Verse.Root_Play+<>c.<Start>b__1_2 ()
      at Verse.LongEventHandler.RunEventFromAnotherThread (System.Action action)

The quicktest branch's guard is `Current.Game == null`, and that is simply the
wrong question once an autostart load is in flight.

## What to do

Before any `--quicktest` launch, move `autostart.rws` out of the bench's
`Saves/` directory. Nothing else is needed and nothing else works — the two
entry paths are mutually exclusive by construction.

    S="$BENCH/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Saves"
    mkdir -p "$S/parked" && mv "$S/autostart.rws" "$S/parked/" 2>/dev/null

Two failed launches and one clean one on 2026-08-31 established the causal link;
the source above explains it rather than merely correlating it.

## The reporting trap around it

`Player.log` is truncated at process start, so the stack is destroyed by the
relaunch that is the obvious response. Capture it BEFORE relaunching or the
evidence for this diagnosis does not exist. Related: [[read-every-return-or-lose-a-colonist]].
