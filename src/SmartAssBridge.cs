using System;
using System.Collections.Generic;
using MuMech;
using RemoteTech.API;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// Route MechJeb's SmartASS attitude command through RemoteTech's signal-delay queue.
    ///
    /// Model: MechJeb is a computer *on board* the vessel. Pressing a SmartASS button is a
    /// *command* that must travel at light speed; the attitude-hold loop it starts runs
    /// locally and instantly. Engage() latches the target attitude into Core.Attitude, so
    /// delaying Engage() correctly delays the actual manoeuvre. `target` is display-only.
    ///
    /// Patched from <see cref="BridgeAddon"/>:
    ///   - EngagePrefix  → prefix  on MechJebModuleSmartASS.Engage
    ///   - TrackPostfix  → postfix on ComputerModule.OnFixedUpdate (filters to SmartASS)
    ///   - CaptureAttitudePostfix → postfix on MechJebModuleAttitudeController.Drive
    /// </summary>
    public static class SmartAssBridge
    {
        private const string Executor = "MechJeb (SmartASS)";

        // ---- seam-specific state -------------------------------------------

        private sealed class Pending
        {
            internal MechJebModuleSmartASS Instance;
            internal MechJebModuleSmartASS.Target Target;
            internal bool ResetPID;
        }

        private static readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>();

        // The mode each SmartASS instance is actually holding (drives the button highlight).
        private static readonly Dictionary<MechJebModuleSmartASS, MechJebModuleSmartASS.Target> _committed =
            new Dictionary<MechJebModuleSmartASS, MechJebModuleSmartASS.Target>();

        private static readonly InFlightCounter<MechJebModuleSmartASS> _inFlight =
            new InFlightCounter<MechJebModuleSmartASS>();

        // ---- Harmony hooks (names referenced as strings from BridgeAddon) --

        internal static bool EngagePrefix(MechJebModuleSmartASS __instance, bool resetPID)
            => OnEngage(__instance, resetPID);

        // Tracks the actual committed mode each frame, but only while no command is in flight
        // (so the highlight stays frozen on the old mode while a command is travelling).
        internal static void TrackPostfix(object __instance)
        {
            if (__instance is MechJebModuleSmartASS sass && !_inFlight.Has(sass))
                _committed[sass] = sass.target;
        }

        // ---- sanctioned pilot (attitude hold through signal blackout) -------
        // MechJeb's Drive output is wiped by RT's neutralize-on-lost-link. We capture it
        // each frame and re-apply it as a sanctioned pilot (runs after RT's neutralize),
        // so an attitude hold that has already been commanded keeps flying without a link.
        // Attitude only — throttle stays cut (no engine burns without a connection).

        private static Vector3 _capAttitude;
        private static int     _capFrame = -1;

        internal static void CaptureAttitudePostfix(FlightCtrlState s)
        {
            _capAttitude = new Vector3(s.pitch, s.yaw, s.roll);
            _capFrame    = Time.frameCount;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            try
            {
                if (!API.IsRemoteTechEnabled() || API.HasLocalControl(v.id)) return;
                API.AddSanctionedPilot(v.id, SanctionedAttitude); // RT deduplicates
            }
            catch (Exception e) { Debug.LogError("[MJ-RT Bridge] AddSanctionedPilot failed: " + e); }
        }

        private static void SanctionedAttitude(FlightCtrlState s)
        {
            if (_capFrame != Time.frameCount) return;
            s.pitch = _capAttitude.x;
            s.yaw   = _capAttitude.y;
            s.roll  = _capAttitude.z;
        }

        // ---- command interception ------------------------------------------

        private static bool OnEngage(MechJebModuleSmartASS instance, bool resetPID)
        {
            try
            {
                if (BridgeCore.Bypass) return true;

                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null || !BridgeCore.NeedsDelay(vessel)) return true;

                MechJebModuleSmartASS.Target requested = instance.target;
                MechJebModuleSmartASS.Target committed =
                    _committed.TryGetValue(instance, out var c) ? c : requested;

                double delay = BridgeCore.SafeDelay(vessel.id);
                if (!BridgeCore.IsConnected(delay))
                {
                    RevertHighlight(instance, committed);
                    BridgeCore.Message($"SmartASS: {requested} — no connection, command not sent");
                    Debug.Log($"[MJ-RT Bridge] No connection (delay={delay}); SmartASS '{requested}' not sent.");
                    return false;
                }

                string token = Guid.NewGuid().ToString("N");
                _pending[token] = new Pending { Instance = instance, Target = requested, ResetPID = resetPID };

                if (API.QueueCommandToFlightComputer(BuildCommandNode(vessel.id, token, requested, resetPID)))
                {
                    _inFlight.Add(instance);
                    RevertHighlight(instance, committed);
                    if (delay >= 0.1)
                        BridgeCore.Message($"SmartASS: {requested} queued · signal delay {delay:F1}s");
                    Debug.Log($"[MJ-RT Bridge] SCHED {requested} delay={delay:F2}s token={BridgeCore.Short(token)}" +
                              $" | pending={_pending.Count}: {PendingStack()}");
                    return false;
                }

                // RT refused (e.g. not the active vessel) — run locally rather than drop.
                _pending.Remove(token);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] OnEngage error, running command locally: " + e);
                return true;
            }
        }

        // ---- RemoteTech callback (invoked by reflection after the delay) ---

        /// <summary>
        /// Called by RemoteTech once the signal delay has elapsed. Must be public static and
        /// match the signature RemoteTech expects: (ConfigNode) → bool (false = one-shot).
        /// </summary>
        public static bool RtPop(ConfigNode data)
        {
            try
            {
                string token = BridgeCore.GetValueDeep(data, "Token");

                MechJebModuleSmartASS sass;
                MechJebModuleSmartASS.Target target;
                bool resetPID;
                bool reconstructed;

                if (token != null && _pending.TryGetValue(token, out Pending p))
                {
                    _pending.Remove(token);
                    target    = p.Target;
                    resetPID  = p.ResetPID;
                    sass      = BridgeCore.ResolveModule(p.Instance, out string reason);
                    reconstructed = reason != null;
                    if (reconstructed && sass != null)
                        Debug.Log($"[MJ-RT Bridge] EXEC using fresh SmartASS ({reason}) for {target} token={BridgeCore.Short(token)}");
                }
                else
                {
                    // _pending lost (shouldn't normally happen) — reconstruct from persisted node.
                    string targetStr = BridgeCore.GetValueDeep(data, "SATarget");
                    string resetStr  = BridgeCore.GetValueDeep(data, "SAResetPID");
                    if (targetStr == null || resetStr == null)
                    {
                        Debug.LogWarning($"[MJ-RT Bridge] EXEC ?? token={BridgeCore.Short(token)} — not in pending and no fallback data; skipping");
                        return false;
                    }
                    sass          = BridgeCore.ResolveModule<MechJebModuleSmartASS>(null, out _);
                    target        = (MechJebModuleSmartASS.Target)int.Parse(targetStr);
                    resetPID      = bool.Parse(resetStr);
                    reconstructed = true;
                }

                if (sass == null)
                {
                    Debug.LogWarning($"[MJ-RT Bridge] EXEC ?? {target} token={BridgeCore.Short(token)} — no SmartASS on active vessel; skipping");
                    return false;
                }

                sass.target      = target;
                _committed[sass] = target;
                BridgeCore.Bypass = true;
                try { sass.Engage(resetPID); }
                finally { BridgeCore.Bypass = false; }
                _inFlight.Remove(sass);

                string src = reconstructed ? " (reconstructed)" : "";
                bool attEnabled = sass.Core?.Attitude?.Enabled ?? false;
                int  attUsers   = sass.Core?.Attitude?.Users?.Count ?? -1;
                bool sassIsUser = sass.Core?.Attitude?.Users?.Contains(sass) ?? false;
                Debug.Log($"[MJ-RT Bridge] EXEC{src} {target} token={BridgeCore.Short(token)}" +
                          $" | {BridgeCore.StateAtExec()} | pending={_pending.Count}: {PendingStack()}" +
                          $" | attCtrl enabled={attEnabled} users={attUsers} sassIsUser={sassIsUser}");
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] RtPop error: " + e);
            }
            return false;
        }

        // ---- helpers -------------------------------------------------------

        private static void RevertHighlight(MechJebModuleSmartASS instance, MechJebModuleSmartASS.Target committed)
        {
            if (_committed.ContainsKey(instance)) instance.target = committed;
        }

        private static ConfigNode BuildCommandNode(Guid vesselId, string token,
                                                    MechJebModuleSmartASS.Target target, bool resetPID)
        {
            ConfigNode node = BridgeCore.BuildBaseNode(
                vesselId, token, Executor,
                popType:     typeof(SmartAssBridge),
                popMethod:   nameof(RtPop),
                queueLabel:  $"SmartASS → {target}",
                activeLabel: $"SmartASS: {target}",
                shortLabel:  "SmartASS");
            // Persisted payload — enough to reconstruct the action after a reload.
            node.AddValue("SATarget",   (int)target);
            node.AddValue("SAResetPID", resetPID.ToString());
            return node;
        }

        private static string PendingStack()
        {
            if (_pending.Count == 0) return "(empty)";
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _pending)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Value.Target).Append('#').Append(BridgeCore.Short(kv.Key));
            }
            return sb.ToString();
        }
    }
}
