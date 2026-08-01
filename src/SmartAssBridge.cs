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
    /// Patched manually from <see cref="BridgeAddon"/>:
    ///   - EngagePrefix  → prefix on MechJebModuleSmartASS.Engage
    ///   - TrackPostfix  → postfix on MechJebModuleSmartASS.OnFixedUpdate (optional)
    /// </summary>
    public static class SmartAssBridge
    {
        private const string Executor = "MechJeb (SmartASS)";

        // Set only while we deliberately re-invoke Engage after the delay, so the prefix
        // lets that call through. Unity main thread only, so a plain flag is safe.
        [ThreadStatic] private static bool _bypass;

        private sealed class Pending
        {
            internal MechJebModuleSmartASS Instance;
            internal MechJebModuleSmartASS.Target Target;
            internal bool ResetPID;
        }
        private static readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();

        // The mode each SmartASS is actually holding (what the highlight should show).
        private static readonly Dictionary<MechJebModuleSmartASS, MechJebModuleSmartASS.Target> _committed =
            new Dictionary<MechJebModuleSmartASS, MechJebModuleSmartASS.Target>();

        // Commands in flight per instance (so we stop tracking the "actual" mode while the
        // highlight is being held, and support rapid re-clicks).
        private static readonly Dictionary<MechJebModuleSmartASS, int> _inFlight =
            new Dictionary<MechJebModuleSmartASS, int>();

        // ---- Harmony hooks (referenced by name from BridgeAddon) ------------------------

        internal static bool EngagePrefix(MechJebModuleSmartASS __instance, bool resetPID)
            => OnEngage(__instance, resetPID);

        internal static void TrackPostfix(object __instance)
        {
            if (__instance is MechJebModuleSmartASS sass && !HasInFlight(sass))
                _committed[sass] = sass.target;
        }

        // ---- keep the onboard attitude hold alive through a signal blackout -------------
        // RemoteTech neutralizes flight controls when the link is down, and MechJeb's output
        // runs before that hook, so it gets wiped. We capture the attitude MechJeb's
        // controller computes each frame and re-apply it as a RemoteTech "sanctioned pilot",
        // which runs in OnFlyByWirePost *after* the neutralize — so a command that has
        // already arrived keeps flying. Attitude only; throttle stays cut (no firing engines
        // without a link).
        private static Vector3 _capAttitude;
        private static int _capFrame = -1;
        private static readonly HashSet<Guid> _sanctioned = new HashSet<Guid>();

        internal static void CaptureAttitudePostfix(FlightCtrlState s)
        {
            _capAttitude = new Vector3(s.pitch, s.yaw, s.roll);
            _capFrame = Time.frameCount;

            // Register the sanctioned pilot the first time MechJeb drives a remote vessel.
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || _sanctioned.Contains(v.id)) return;
            try
            {
                if (!API.IsRemoteTechEnabled() || API.HasLocalControl(v.id)) return;
                API.AddSanctionedPilot(v.id, SanctionedAttitude);
                _sanctioned.Add(v.id);
                Debug.Log($"[MJ-RT Bridge] Sanctioned pilot registered for {v.vesselName}; " +
                          "attitude holds now survive signal loss.");
            }
            catch (Exception e) { Debug.LogError("[MJ-RT Bridge] AddSanctionedPilot failed: " + e); }
        }

        // Invoked by RemoteTech in OnFlyByWirePost, after it has neutralized controls on a
        // lost link. Re-apply MechJeb's attitude — but only if it actually drove this frame,
        // so we never replay a stale hold when the controller is idle.
        private static void SanctionedAttitude(FlightCtrlState s)
        {
            if (_capFrame != Time.frameCount) return;
            s.pitch = _capAttitude.x;
            s.yaw   = _capAttitude.y;
            s.roll  = _capAttitude.z;
        }

        // ---- command interception ------------------------------------------------------

        private static bool OnEngage(MechJebModuleSmartASS instance, bool resetPID)
        {
            try
            {
                if (_bypass) return true;                       // our own delayed re-invoke
                if (!API.IsRemoteTechEnabled()) return true;

                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null) return true;
                if (API.HasLocalControl(vessel.id)) return true; // crewed / launchpad / no delay

                MechJebModuleSmartASS.Target requested = instance.target;
                MechJebModuleSmartASS.Target committed =
                    _committed.TryGetValue(instance, out MechJebModuleSmartASS.Target c) ? c : requested;

                // Ask RemoteTech what the signal delay actually is. No finite delay ⇒ no
                // connection: RemoteTech's Enqueue would silently drop the command (its
                // !InputAllowed early-out) while still reporting success, so we must NOT
                // claim it was queued — and we must NOT run it locally (that would bypass
                // the very delay we exist to enforce). Just tell the player.
                double delay = SafeDelay(vessel.id);
                if (!IsConnected(delay))
                {
                    RevertHighlight(instance, committed);
                    Message($"SmartASS: {requested} — no connection, command not sent");
                    Debug.Log($"[MJ-RT Bridge] No connection (delay={delay}); SmartASS '{requested}' not sent.");
                    return false;
                }

                var pending = new Pending { Instance = instance, Target = requested, ResetPID = resetPID };
                string token = Guid.NewGuid().ToString("N");
                ConfigNode node = BuildCommandNode(vessel.id, token, requested);
                _pending[token] = pending;

                if (API.QueueCommandToFlightComputer(node))
                {
                    AddInFlight(instance);
                    RevertHighlight(instance, committed);          // hold highlight on actual mode
                    Message($"SmartASS: {requested} queued · signal delay {delay:F1}s");
                    Debug.Log($"[MJ-RT Bridge] SCHED {requested} delay={delay:F2}s token={Short(token)}" +
                              $" | pending={_pending.Count}: {StackString()}");
                    return false;
                }

                // RT refused it (e.g. not the active vessel): run it now rather than drop it.
                _pending.Remove(token);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] OnEngage error, running command locally: " + e);
                return true;
            }
        }

        // A finite, non-negative, sane delay means RemoteTech has a route (and Enqueue will
        // accept the command). PositiveInfinity / NaN / absurd values mean no connection.
        private static bool IsConnected(double delay) =>
            !double.IsNaN(delay) && !double.IsInfinity(delay) && delay >= 0.0 && delay < 1.0e6;

        private static double SafeDelay(Guid id)
        {
            try { return API.GetShortestSignalDelay(id); }
            catch { return double.PositiveInfinity; }
        }

        private static void RevertHighlight(MechJebModuleSmartASS instance, MechJebModuleSmartASS.Target committed)
        {
            if (_committed.ContainsKey(instance)) instance.target = committed;
        }

        private static void Message(string msg)
        {
            try { ScreenMessages.PostScreenMessage(msg, 4f, ScreenMessageStyle.UPPER_CENTER); }
            catch (Exception e) { Debug.LogError("[MJ-RT Bridge] Message failed: " + e); }
        }

        private static ConfigNode BuildCommandNode(Guid vesselId, string token, MechJebModuleSmartASS.Target target)
        {
            var node = new ConfigNode("MJRT");
            node.AddValue("GUIDString", vesselId.ToString());
            node.AddValue("Executor", Executor);
            node.AddValue("ReflectionType", typeof(SmartAssBridge).AssemblyQualifiedName);
            node.AddValue("ReflectionPopMethod", nameof(RtPop)); // one-shot: do work in Pop
            node.AddValue("QueueLabel", $"SmartASS → {target}");
            node.AddValue("ActiveLabel", $"SmartASS: {target}");
            node.AddValue("ShortLabel", "SmartASS");
            node.AddValue("Token", token);
            return node;
        }

        // ---- RemoteTech callback (after the delay) -------------------------------------

        /// <summary>
        /// Invoked by RemoteTech (via reflection) once the signal delay has elapsed.
        /// Must be public static, take the command's ConfigNode, return bool
        /// (false = one-shot, don't keep it active).
        /// </summary>
        public static bool RtPop(ConfigNode data)
        {
            try
            {
                string token = GetValueDeep(data, "Token");
                if (token == null || !_pending.TryGetValue(token, out Pending p))
                {
                    Debug.Log($"[MJ-RT Bridge] EXEC ?? token={Short(token)} not in pending; skipping" +
                              $" | pending={_pending.Count}: {StackString()}");
                    return false;
                }
                _pending.Remove(token);

                p.Instance.target = p.Target;      // highlight snaps to the requested mode
                _committed[p.Instance] = p.Target; // ...which is now what the vessel holds
                _bypass = true;
                try { p.Instance.Engage(p.ResetPID); } // latch the new attitude locally
                finally { _bypass = false; }
                RemoveInFlight(p.Instance);

                Debug.Log($"[MJ-RT Bridge] EXEC {p.Target} token={Short(token)}" +
                          $" | {StateAtExec()} | pending={_pending.Count}: {StackString()}");
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] RtPop error: " + e);
            }
            return false; // one-shot
        }

        // ---- in-flight bookkeeping -----------------------------------------------------

        private static bool HasInFlight(MechJebModuleSmartASS i) =>
            _inFlight.TryGetValue(i, out int n) && n > 0;

        private static void AddInFlight(MechJebModuleSmartASS i) =>
            _inFlight[i] = (_inFlight.TryGetValue(i, out int n) ? n : 0) + 1;

        private static void RemoveInFlight(MechJebModuleSmartASS i)
        {
            if (!_inFlight.TryGetValue(i, out int n)) return;
            if (n <= 1) _inFlight.Remove(i);
            else _inFlight[i] = n - 1;
        }

        // ---- debug: render the outstanding command stack -------------------------------

        /// <summary>
        /// Snapshot the vessel/control state at execution time, to distinguish the two
        /// reasons an executed command wouldn't move the craft:
        ///   - on-rails time warp (can't apply torque), or
        ///   - no live connection now (RemoteTech neutralizes control output).
        /// </summary>
        private static string StateAtExec()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                double delayNow = v != null ? SafeDelay(v.id) : double.NaN;
                bool connected = IsConnected(delayNow);
                bool local = v != null && API.HasLocalControl(v.id);
                bool onRails = TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1f;
                return $"warp={TimeWarp.CurrentRate:F0}x({TimeWarp.WarpMode}) onRails={onRails} " +
                       $"connectedNow={connected} localCtrl={local} delayNow={delayNow:F1}";
            }
            catch (Exception e) { return "state?(" + e.Message + ")"; }
        }

        private static string Short(string token) =>
            string.IsNullOrEmpty(token) ? "(null)" : token.Substring(0, Math.Min(6, token.Length));

        /// <summary>Comma-separated "TARGET#token" list of everything still queued, oldest first.</summary>
        private static string StackString()
        {
            if (_pending.Count == 0) return "(empty)";
            var sb = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, Pending> kv in _pending)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Value.Target).Append('#').Append(Short(kv.Key));
            }
            return sb.ToString();
        }

        /// <summary>
        /// RemoteTech wraps our node under an "ExternalData"/"MJRT" subnode before handing
        /// it back, so look at the top level and one level down.
        /// </summary>
        private static string GetValueDeep(ConfigNode node, string key)
        {
            if (node == null) return null;
            if (node.HasValue(key)) return node.GetValue(key);
            foreach (ConfigNode child in node.GetNodes())
            {
                string v = GetValueDeep(child, key);
                if (v != null) return v;
            }
            return null;
        }
    }
}
