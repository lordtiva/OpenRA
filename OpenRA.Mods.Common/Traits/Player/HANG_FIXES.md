# C# root hang fixes (FastAdvance → World.Tick)

## Root cause
FastAdvance → `World.Tick` hangs → poisoned .NET session → destroy may fail →
zombie sessions → Python collect idle with Docker healthy and markers=0.

## Fixes (1–4)
1. **Cancelable FastAdvance + hard server deadline** — `OPENRA_RL_FAST_ADVANCE_DEADLINE_S` (default **90**).
   `TickSession` checks cancel + wall clock between ticks; gRPC `WaitAsync` uses the same deadline.
2. **Session isolation** — on deadline/TickLock timeout: `PoisonAndDestroy` + optional
   **replacement worker** so a wedged `World.Tick` does not starve the pool (concurrency 4).
3. **Deterministic session GC** — `PoisonAndDestroy` / `DestroySession(..., forcePurge: true)`:
   cancel work, purge `Sessions`/`PlayerSessions`/`SessionStates`, background dispose with short wait,
   log `active_sessions=N [...]`.
4. **Never reuse poisoned session** — `IsPoisoned` / `SessionDone` → Lookup returns null;
   FastAdvance returns `Aborted` / `DeadlineExceeded` with message that **CreateSession** is required.

## Files
- `RLSessionManager.cs` — deadline, WorkItem cancel, TickSession abort, PoisonAndDestroy, worker replace
- `ExternalBotBridge.cs` — FastAdvance deadline path, LookupSession, Deactivate, GetState `error`
- `RLBridgeService.cs` — DestroySession forcePurge, WaitForBridge poisoned skip
- `proto/rl_bridge.proto` (+ Protos copy) — thin comments only
- `docker-compose.yaml` — `OPENRA_RL_FAST_ADVANCE_DEADLINE_S=90`

## Rebuild (local OpenRA submodule via Dockerfile.local)
```bat
cd C:\Users\lordc\Desktop\OpenRA-RL
docker compose -f docker-compose.yaml -f docker-compose.scale.yaml build openra-rl
docker compose -f docker-compose.yaml -f docker-compose.scale.yaml up -d
```
Single daemon:
```bat
docker compose build openra-rl
docker compose up -d
```

Do **not** wipe ckpts. Train was stopped; restart train only after containers are healthy.

## Risks / limits
- A single hung `World.Tick()` **cannot** be preempted mid-call; the worker thread is abandoned and replaced.
- Abandoned worlds may retain memory until process recycle; forcePurge avoids blocking the host.
- Deadline must stay ≥ Python client min timeout (~90s) or benign slow advances will poison sessions.
