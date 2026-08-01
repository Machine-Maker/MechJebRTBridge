# MechJeb ⇄ RemoteTech — Command Delay Implementation Plan

Status of this doc: **design + inventory**. It captures everything learned while building
the SmartASS proof of concept, so the same treatment can be rolled out to every MechJeb
command. Line references are against the MechJeb2 `dev` source cloned alongside this repo
and may drift between MechJeb versions — re-verify method names before patching.

---

## 1. The principle

**MechJeb is a computer *on board* the vessel.** Every action taken in the MechJeb UI is a
*command*. Under RemoteTech, a command issued to a vessel that lacks local control must
travel the light-speed link before it takes effect. But the *program* a command starts
(attitude hold, landing descent, node burn…) runs **locally and instantly** on the vessel —
its per-frame control loop is never delayed.

Three consequences drive every decision below:

1. **Delay the command boundary, not the loop.** The thing that crosses the link is
   "activate / deactivate / change a setpoint." The `Drive(FlightCtrlState)` loop that
   follows is local.
2. **Navigating the UI is local.** Switching tabs/panels, opening windows, selecting which
   readout to show — none of that commands the vessel, so none of it is delayed. (Example:
   the SmartASS **mode** row — ORBIT/SURFACE/TARGET/ADVANCED — only picks which target
   buttons are shown; it never calls `Engage()`, so it must stay instant.)
3. **The UI must not lie.** Because a command is delayed, the UI should reflect the vessel's
   *actual* state until the command lands, not the just-clicked intent. See §4.

---

## 2. Architecture (the reference pattern)

Established by the SmartASS implementation (`src/SmartAssBridge.cs`). Every seam follows it.

```
Player clicks a MechJeb command button
        │
        ▼
Harmony PREFIX on the command method  ── if RT off OR vessel has local control ──▶ run normally
        │  (RT on AND no local control)
        ▼
Snapshot intent  →  API.QueueCommandToFlightComputer(ConfigNode)   [RemoteTech applies delay]
        │                                                            (command shows in FC queue)
        ▼  return false  (suppress the immediate action)
   ...light-speed delay elapses...
        │
        ▼
RemoteTech reflects back into our public static  RtPop(ConfigNode)
        │
        ▼
Re-invoke the real MechJeb method behind a bypass guard  →  program starts, runs locally
```

Key RemoteTech API (all public, present since the 1.9.x release — verified stable):
- `RemoteTech.API.API.IsRemoteTechEnabled()`
- `RemoteTech.API.API.HasLocalControl(Guid)`
- `RemoteTech.API.API.GetShortestSignalDelay(Guid)` — for UI cues
- `RemoteTech.API.API.QueueCommandToFlightComputer(ConfigNode)` — enqueues an
  `ExternalAPICommand` whose `ReflectionPopMethod`/`ReflectionExecuteMethod` call us back.

Command node contract (see `BuildCommandNode`): `GUIDString`, `Executor`, `ReflectionType`
(assembly-qualified), `ReflectionPopMethod`, labels, plus our own `Token`. One-shot commands
do their work in the Pop method and return `false` (no Execute method → not kept active).

Dependencies: **hard** references to MechJeb2 + RemoteTech (a shim is pointless without
both); **Harmony** for interception (MechJeb exposes no events). Target `net48`.

---

## 3. Classification of MechJeb actions

### A. Continuous control programs — delay activation + every setpoint change; loop is local
The activation method and any parameter mutation are commands. The `Drive` loop is local.

### B. One-shot discrete actions — delay the whole action as a single command
Instantaneous events (stage, deploy, abort). Delay the event itself (mirrors RemoteTech's
own `ActionGroupCommand`/`PartActionCommand`).

### C. Pure local — never delayed
Planning, information, and UI navigation that never write `FlightCtrlState` / never command
the vessel. Delaying these would just make the UI feel broken.

---

## 4. UI-truthfulness treatment ("hold highlight + queued cue")

For continuous programs whose UI shows a selected state, the delayed command must not flip
the UI immediately. Reference implementation (SmartASS):

- Track the **actual** state every frame via a postfix on the module's `OnFixedUpdate`,
  recording it only while no command is in flight (`_committed`).
- On interception: revert the UI field to the actual state, and post a screen message
  `"<action> queued · signal delay X.Xs"` (`ScreenMessages.PostScreenMessage`).
- On `RtPop`: set the UI field to the requested state, update `_committed`, run the command
  — so the highlight snaps over exactly when the vessel acts.
- Handle rapid re-clicks with an in-flight counter (`_inFlight`).

This is safe only because the control loop is **latched** at activation (e.g. SmartASS
`Engage()` pushes into `Core.Attitude`; the UI `target` field is display-only). **Verify per
seam** that the UI field is display-only before reverting it.

---

## 5. Master command inventory

Legend — Treatment: **A** continuous (delay activate + setpoints), **B** one-shot discrete,
**C** local/no-delay. UI-hold: whether §4 applies. Status: ✅ done · ⬜ todo · ⚠️ needs design.

| Program | Module (`MechJebModule*`) | Command entry point(s) — verified | Treatment | UI-hold | Status |
|---|---|---|---|---|---|
| Attitude / SmartASS | `SmartASS` + `AttitudeController` | `Engage(bool)` (`:622`); mode row is **C** | A | yes | ✅ |
| Landing autopilot | `LandingAutopilot` | `LandAtPositionTarget(obj)` (`:114`), `LandUntargeted(obj)` (`:136`), `StopLanding()` (`:150`) | A | target/enable | ⬜ |
| Node executor | `NodeExecutor` | `ExecuteOneNode(obj)` (`:83`), `ExecuteAllNodes(obj)` (`:90`), `Abort()` (`:106`) | A | enable | ⬜ |
| Ascent autopilot | `AscentBaseAutopilot` (+ `AscentMenu`) | AP toggle (`KSPAction #MechJeb_AscentAPtoggle`); `StartCountdown(double)` (`:92`); enable via `Users`/`Enabled` | A | enable | ⬜ |
| Rendezvous autopilot | `RendezvousAutopilot` | enable via `Enabled` setter / `Users`; `Users.Clear()` (`:35/:60`) | A | enable | ⬜ |
| Docking autopilot | `DockingAutopilot` | enable via `Enabled` setter (`OnModuleEnabled` `:~90`); `Users.Clear()` (`:378`) | A | enable | ⬜ |
| Hoverslam / suicide burn | `HoverslamAutopilot` | `ToggleEnabled()` (`:17`) → `Enabled` setter | A | enable | ⬜ |
| Translatron | `Translatron` | `SetMode(TMode)` (`:144`), `PanicSwitch()` (`:163`) | A (B for PANIC) | mode | ⬜ |
| Spinup | `SpinupController` | `AssertStart()` (`:81`) / `Enabled` | A | enable | ⬜ |
| Thrust management | `ThrustController` | limiter toggles (`Limit*InfoItem()` `:41+`) + limit value setpoints | A | toggles | ⚠️ |
| RCS controller/balancer | `RCSController`, `RCSBalancer` | `Enabled` setter | A | enable | ⬜ |
| Rover autopilot | `RoverController` | `Enabled` / control-mode setters; waypoint/speed setpoints | A | enable | ⚠️ |
| Auto-staging | `StagingController` | `AutostageOnce(obj)` (`:172`); `Users.Clear()` (`:428`) | A | enable | ⬜ |
| Immediate stage | `StagingController` | `ImmediateStage()` (`:391`), `Stage()` (`:431`) | **B** | — | ⬜ |
| PANIC / abort | `Translatron.PanicSwitch`, core `#MechJeb_PANIC` | one-shot | **B** | — | ⬜ |
| Deploy/retract gear, chutes, panels, antennas | `SolarPanelController`, `DeployableAntennaController`, `DeployableController` | discrete toggles | **B** | — | ⬜ |
| SmartASS mode row | `SmartASS` | `ModeButton` sets `mode` only (`:135`) — no `Engage` | **C** | — | ✅ (leave instant) |
| Maneuver planning / node editing | `ManeuverPlanner`, `NodeEditor` | node create/edit | **C** | — | n/a |
| Predictions / sims / readouts | `LandingPredictions`, `HoverslamSimulation`, `StageStats`, `FlightRecorder*`, `InfoItems`, `CustomInfoWindow` | — | **C** | — | n/a |
| Time warp | `WarpController`, `WarpHelper` | client/game-time op | **C** | — | n/a |
| Target selection | `TargetController` | UI selection (arguable) | **C** | — | n/a |

> Activation-shape note: many programs don't expose a distinct "Start" method — they enable
> via the `ComputerModule.Enabled` property setter (which fires `OnModuleEnabled` →
> `Users.Add`). For those, the interception seam is the **`Enabled` setter** (or the specific
> menu method that sets it), not a bespoke method. Prefer patching the highest-level public
> entry a UI button actually calls.

---

## 6. Generalized pattern to add a seam

1. **Find the command boundary** — the public method a UI button/`KSPAction` invokes that
   changes vessel state (`Users.Add/Clear`, `Enabled=`, or a setpoint write). Confirm it
   *latches* (so the loop is local) and identify the display-only UI field, if any.
2. **Harmony-prefix it.** Guard: `_bypass` → run; `!IsRemoteTechEnabled()` → run;
   `HasLocalControl` → run. Otherwise snapshot params, queue via RT, revert UI field, post
   cue, return `false`.
3. **Serialize the intent** into the command node (§8 persistence) or stash it by token.
4. **`RtPop`**: rebuild params, set `_bypass`, re-invoke the real method, update UI/committed
   state, clear in-flight.
5. **Cancellation** (`StopLanding`, `Abort`, disable) is *also* a command — delay it too
   (except the emergency local cutoff, §8).
6. Add the seam to `BridgeAddon` / annotate the patch class.

---

## 7. Command vs. setpoint granularity

A running program's parameter changes are **new delayed commands**, not free local tweaks:
- SmartASS PROGRADE → RETROGRADE is a new command; the hold in between is local.
- Landing target coordinates, ascent target apoapsis/inclination/heading, rover
  waypoint/speed, thrust limit value — each mutation must go through the delay layer.
- This means the interception must sit on **every setpoint mutation**, not just the initial
  enable. Some of these are field writes from UI code, not tidy methods → those seams are the
  harder ones (marked ⚠️) and may need patching the specific menu handler.

---

## 8. Cross-cutting concerns / open design decisions

- **Persistence across save/load (⚠️ current gap).** Pending intents live in memory keyed by
  a token; a quicksave/load mid-delay drops them. Production fix: serialize enough parameters
  into the command `ConfigNode` so `RtPop` reconstructs the action without the live object.
  RemoteTech already persists `ExternalAPICommand` nodes and reloads them, so this "just"
  needs each seam's params to be node-serializable.
- **Signal-loss policy (⚠️ not wired).** If the link drops while a program runs, should the
  onboard program keep flying (it's local) or cut out? The "computer on board" model says it
  keeps running — which differs from stock RT neutralizing controls. MechJeb exposes
  `MechJebCore.DeactivateControl` (`MechJebCore.cs:73`) for an emergency local cutoff; decide
  and wire a policy. This is the one place an *instant* (undelayed) action is correct.
- **Cancellation delay.** Deactivations should take the same light-speed time to arrive
  (an instant remote abort breaks the fiction) — except the §8 emergency cutoff.
- **Parameter marshalling.** Rich params (Vector3d target coords, quaternions) → prefer
  stashing the live object by token for the in-memory path; serialize to strings only for the
  persistence path.
- **Telemetry is not delayed (out of scope).** The MechJeb UI shows live vessel state; a
  strict model would delay displayed readouts by one-way light time. Large change; deferred.
- **Active vessel only.** `QueueCommandToFlightComputer` accepts commands only for the active
  vessel (a RemoteTech API constraint) — fine for player-driven UI.
- **Version coupling.** Harmony patches MechJeb internals; a signature change surfaces as a
  load/JIT failure and needs a bridge recompile. Keep a guard/log per patch.

---

## 9. Suggested rollout order

1. **Landing autopilot** — clean public methods (`LandAtPositionTarget`/`StopLanding`), high
   player value, great demo of delayed activation + delayed cancellation.
2. **NodeExecutor** — `ExecuteAllNodes`/`Abort`, also clean.
3. **Ascent autopilot** — the AP toggle; touches staging/throttle too.
4. **Persistence (§8)** — make commands survive save/load before breadth grows (the failure
   mode gets more likely the more seams exist).
5. **Signal-loss policy (§8)** — wire `DeactivateControl`.
6. Rendezvous, Docking, Hoverslam, Translatron, Spinup, auto-staging.
7. One-shot **B** actions (stage, PANIC, deploys).
8. Setpoint-granularity seams (⚠️ thrust limits, rover waypoints, ascent/landing params).

---

## 10. Reference implementation gotchas (learned from SmartASS)

- The UI sets the state field (`target`) **before** calling the command (`Engage`), so the
  prefix already sees the new value — track the *previous* value out-of-band (per-frame
  `OnFixedUpdate` postfix) to keep the highlight truthful (§4).
- `RtPop` must be `public static bool RtPop(ConfigNode)`; RemoteTech invokes it by reflection
  via `Type.GetType(AssemblyQualifiedName)` — keep the type public.
- RemoteTech wraps the queued node under an `ExternalData`/`MJRT` subnode before handing it
  back; read values with a shallow recursive lookup (`GetValueDeep`).
- One-shot = provide only `ReflectionPopMethod`, do the work there, return `false`.
- The bridge target framework must be ≤ RemoteTech's (released RT = old .NET 4.5 → build
  `net48`; the unreleased Burst rewrite targeted `net481` and forced a bump — watch for this).
```
