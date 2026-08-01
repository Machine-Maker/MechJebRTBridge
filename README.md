# MechJeb ⇄ RemoteTech Bridge

A standalone KSP compatibility mod that routes **MechJeb autopilot commands** through
**RemoteTech's signal-delay queue**, so issuing a command to a distant vessel takes the
correct light-speed time to arrive — while the resulting control loop still runs locally
and instantly on the vessel.

Neither MechJeb2 nor RemoteTech is modified. This is a separate assembly that **hard-
depends on both** (a compatibility shim is pointless without them):

- **RemoteTech** is used through its public `RemoteTech.API` class
  (`IsRemoteTechEnabled`, `HasLocalControl`, `QueueCommandToFlightComputer`).
- **MechJeb** is intercepted with **Harmony**, because it exposes no event to hook — we
  prefix-patch the internal `MechJebModuleSmartASS.Engage`.

References use `SpecificVersion=False`, so a MechJeb/RemoteTech *rebuild* is fine; only an
actual change to a signature we touch (e.g. `Engage`) forces a bridge recompile.

## The model

MechJeb is treated as a computer *on board* the vessel. Every action taken in the MechJeb
UI is a **command** that must travel the light-speed link (delayed). The program it starts
(attitude hold, landing descent, node burn, …) then has **local, real-time** control of
the vessel — its per-frame `Drive(FlightCtrlState)` loop is never delayed.

So we delay only the *command boundary* — the public activation / parameter-change call —
not the control loop. This mirrors how RemoteTech's own `AttitudeCommand` works.

## How it works (proof of concept: SmartASS)

1. A Harmony **prefix** on `MechJebModuleSmartASS.Engage(bool)` intercepts the button press.
2. If RemoteTech is active and the vessel lacks local control, the bridge snapshots the
   intent, packages it as a RemoteTech `ExternalAPICommand` via
   `RemoteTech.API.API.QueueCommandToFlightComputer(ConfigNode)`, and **suppresses** the
   immediate Engage.
3. RemoteTech holds the command for the light-speed delay (it appears in the flight-computer
   queue UI with the label `SmartASS → PROGRADE`, etc.).
4. When the delay elapses, RemoteTech reflects back into `SmartAssBridge.RtPop`, which
   re-invokes the real `Engage` locally (guarded so the prefix lets that call through).

Additional command seams — `LandingAutopilot.LandAtPositionTarget/StopLanding`,
`NodeExecutor.ExecuteAllNodes/Abort`, the Ascent AP toggle, Translatron, etc. — follow the
exact same pattern and would be added in `BridgeAddon.Start()`.

## Building

Requires a local KSP install with **MechJeb2, RemoteTech, and Harmony (`000_Harmony`)
installed in `GameData`** — these provide the reference assemblies. Paths are auto-detected
in `Directory.Build.props` (mirroring MechJeb2's convention); override in a
`Directory.Build.props.user` if your layout differs. You also need a .NET / Mono build
toolchain (`dotnet` or `msbuild`).

```
dotnet build MechJebRTBridge.csproj -c Release
```

Override paths with a gitignored `Directory.Build.props.user`:

```xml
<Project>
  <PropertyGroup>
    <KspDir>/path/to/Kerbal Space Program</KspDir>
    <HarmonyDll>/path/to/0Harmony.dll</HarmonyDll>
  </PropertyGroup>
</Project>
```

## Install

Copy the built `MechJebRTBridge.dll` to:

```
GameData/MechJebRTBridge/Plugins/MechJebRTBridge.dll
```

Requires MechJeb2, RemoteTech, and Harmony (000_Harmony) installed.

## Known limitations (POC scope)

- **SmartASS only.** Other MechJeb programs are not yet patched (the framework is in place).
- **Pending intents live in memory**, keyed by a token in the command node. A quicksave/load
  during the in-flight delay drops the pending map, so a command mid-delay is skipped rather
  than resumed. A production version should serialize enough parameters into the command node
  to reconstruct the action in `RtPop` without the live reference.
- **Harmony patches MechJeb internals**, whose signatures can change between MechJeb versions.
  With the hard reference this surfaces as a load/JIT failure (logged) rather than a silent
  no-op, and requires a bridge recompile against the new MechJeb.
- **Signal-loss policy** (should a running local program keep flying, or cut out?) is not yet
  wired to `MechJebCore.DeactivateControl`. Currently only *new* commands are delayed.
- **Telemetry is not delayed** — the MechJeb UI still shows live vessel state. Out of scope.
- Commands are only accepted for the **active vessel** (a RemoteTech API constraint).
