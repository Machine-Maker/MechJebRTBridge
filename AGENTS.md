# AGENTS.md

Instructions for AI coding agents (and humans) working in this repo.

## What this project is

**MechJebRTBridge** is a standalone KSP 1.12 compatibility mod. It makes **MechJeb2** obey
**RemoteTech** signal delay. Normally, clicking a MechJeb button on a probe that's light-minutes
away takes effect instantly, which defeats the point of RemoteTech. The bridge intercepts each
MechJeb *command* (engage SmartASS, execute node, land, …) and routes it through RemoteTech's
flight-computer queue, so it only takes effect after the signal delay. The program that command
starts (the attitude hold, the burn, the descent) then runs locally on the vessel in real time,
as if MechJeb were an on-board computer.

Neither MechJeb2 nor RemoteTech is modified. Interception is done with Harmony prefixes, and the
delay with RemoteTech's public `RemoteTech.API`.

Read these before changing anything:
- `README.md` — user-facing overview, build, install, limitations.
- `docs/COMMAND_DELAY_PLAN.md` — design principles, the reference pattern for adding a seam,
  the full MechJeb command inventory with status, open design questions, and gotchas.

## Rule #1: keep the docs in sync with the code

**Every change that alters behaviour, scope, build, or design must update the docs in the same
change.** Don't leave it for later. A change isn't finished until the docs describe the code as it
now stands. Before you report a task as done, check this list:

| If you… | Update… |
|---|---|
| Add, remove, or change a patched seam (any `Patch(...)` in `BridgeAddon.cs`) | The inventory table in `docs/COMMAND_DELAY_PLAN.md` §5 (status ✅/⬜/⚠️, entry points); the "Supported commands" list in `README.md`; the `Patched from BridgeAddon` header comment of that seam's class |
| Deliberately leave a method unpatched (e.g. `Abort`, `StopLanding`) | Say why in a comment in `BridgeAddon.cs` **and** in the plan doc. Never list it as patched in a header comment |
| Change what's persisted in a command `ConfigNode` | The persistence notes in the README "Known limitations" and plan doc §8 |
| Fix or introduce a limitation | README "Known limitations" |
| Resolve an open design question (signal-loss policy, cancellation delay, …) | Plan doc §8 (mark it decided, say what was chosen) and §9 rollout order |
| Learn a non-obvious gotcha (MechJeb/RT internals, reflection quirks, load order) | Plan doc §10 |
| Change build, dependencies, target framework, CI, or install layout | README "Building"/"Install" and the "Build & test" section below |
| Bump supported KSP / MechJeb / RemoteTech versions | `MechJebRTBridge.csproj` `KSPVersionFile` metadata and README |

Also:
- **Line references** in the plan doc (`:622` etc.) drift. When you touch a seam, re-verify its
  references against `../MechJeb2` and fix them.
- If you find docs that are already wrong, even unrelated to your task, fix them or flag them to
  the user. Don't leave them.
- Comments must describe the code as it is, not the history ("was", "used to", "now").

## Repo layout

```
src/BridgeAddon.cs          bootstrap: applies every Harmony patch, each isolated in try/catch
src/BridgeCore.cs           shared plumbing: Bypass guard, RT helpers, module resolution,
                            command-node builder, InFlightCounter<T>
src/SmartAssBridge.cs       seam: SmartASS Engage + UI highlight hold + sanctioned-pilot hold
src/NodeExecutorBridge.cs   seam: ExecuteOneNode / ExecuteAllNodes
src/LandingBridge.cs        seam: LandAtPositionTarget / LandUntargeted
docs/COMMAND_DELAY_PLAN.md  design + inventory + roadmap
GameData/MechJebRTBridge/   packaging root (Plugins/ and .version are build outputs, gitignored)
```

Sibling checkouts (reference only, not modified by this repo):
- `../MechJeb2` — MechJeb source (`dev`). Use it to find and verify the methods being patched.
- `../RemoteTech` — RemoteTech source (`develop`). Note that the game runs the **released**
  RemoteTech 1.9.x DLL, which may differ from `develop`.

## Adding a seam

Follow plan doc §6 and copy the shape of an existing seam:
1. Harmony prefix → `if (BridgeCore.Bypass) return true;` → `NeedsDelay` → `IsConnected`.
2. Stash the intent by token **and** serialize enough into the command node to rebuild it after
   a reload.
3. `public static bool RtPop(ConfigNode)` (it must be public: RemoteTech calls it by reflection).
   Re-invoke the real method inside `Bypass = true` / `finally Bypass = false`, then return `false`.
4. Register it in `BridgeAddon.Start()`.
5. **Update the docs (Rule #1).**

## Build & test

- Build: `dotnet build MechJebRTBridge.csproj -c Release` (Mono/dotnet on Linux).
- Machine paths live in the gitignored `Directory.Build.props.user` (`KSPBT_GameRoot`,
  `KSPBT_ManagedPath`). The post-build target in `Directory.Build.targets` copies the DLL into
  `$(KSPBT_GameRoot)/GameData/MechJebRTBridge/Plugins/`.
- There are no automated tests. Verification is in-game: launch the KSP instance and check
  `KSP.log` for `[MJ-RT Bridge]` lines. Expect a `patched …` line per seam, then `SCHED …` /
  `EXEC …` when commands are issued.
- Versioning comes from git tags via MinVer (`v0.1.0` → 0.1.0, with `MinVerTagPrefix=v`).
  Between tags the version is e.g. `0.1.1-alpha.0.N`. Releases are cut by pushing a `v*.*.*`
  tag (`.github/workflows/release.yml`).
- Don't set `<Version>` on the `KSPVersionFile` item from `$(MinVer*)` properties. They're
  empty at evaluation time and produce `"VERSION": ".."`. Leave it unset and KSPBuildTools
  fills it from `$(FileVersion)` after MinVer runs.
