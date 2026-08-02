using System;
using MuMech;
using RemoteTech.API;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// Shared plumbing for all command-delay seams. Each seam (SmartAssBridge, LandingBridge,
    /// etc.) is its own public static class (required by Harmony and RemoteTech reflection),
    /// but calls into here for everything that isn't seam-specific.
    /// </summary>
    internal static class BridgeCore
    {
        // ---- bypass guard ---------------------------------------------------
        // Set to true only while a seam is re-invoking the real MechJeb method after the
        // signal delay, so the Harmony prefix lets that call through without re-queuing it.
        // [ThreadStatic] because Unity can call flight-control callbacks off the main thread
        // in some versions; the flag must not bleed across threads.
        [ThreadStatic] internal static bool Bypass;

        // ---- RT connection helpers ------------------------------------------

        // True when delay is a real, finite, sane value (RemoteTech has a route and will
        // accept QueueCommandToFlightComputer). Infinity/NaN/absurd values mean no connection.
        internal static bool IsConnected(double delay) =>
            !double.IsNaN(delay) && !double.IsInfinity(delay) && delay >= 0.0 && delay < 1.0e6;

        internal static double SafeDelay(Guid id)
        {
            try { return API.GetShortestSignalDelay(id); }
            catch { return double.PositiveInfinity; }
        }

        // True when this command should be delayed: RT is active AND the vessel lacks local
        // control. Callers still need to check IsConnected(SafeDelay(...)) before queuing,
        // because no connection is a different condition (command refused, not just delayed).
        internal static bool NeedsDelay(Vessel vessel) =>
            API.IsRemoteTechEnabled() && !API.HasLocalControl(vessel.id);

        // ---- module resolution ---------------------------------------------
        // Each seam caches the MechJeb module instance in its Pending object. After a
        // quickload, KSP rebuilds the vessel, leaving the old instance alive but belonging
        // to the replaced vessel — Unity's null check doesn't catch that. This method
        // validates ownership and falls back to a fresh lookup if needed.
        //
        // Returns null if the module can't be found on the active vessel.
        // Sets fallbackReason to a short description when the fallback was needed, else null.
        internal static T ResolveModule<T>(T cached, out string fallbackReason)
            where T : ComputerModule
        {
            fallbackReason = null;
            if (cached != null && cached.Vessel == FlightGlobals.ActiveVessel)
                return cached;

            fallbackReason = cached == null ? "destroyed" : "wrong vessel";
            return FlightGlobals.ActiveVessel
                       ?.GetMasterMechJeb()
                       ?.GetComputerModule<T>();
        }

        // ---- ConfigNode builder --------------------------------------------
        // Constructs the standard MJRT command node header. Each seam calls this and then
        // appends its own payload values (the params needed to reconstruct after a reload).
        internal static ConfigNode BuildBaseNode(
            Guid vesselId, string token,
            string executor, Type popType, string popMethod,
            string queueLabel, string activeLabel, string shortLabel)
        {
            var node = new ConfigNode("MJRT");
            node.AddValue("GUIDString",            vesselId.ToString());
            node.AddValue("Executor",              executor);
            node.AddValue("ReflectionType",        popType.AssemblyQualifiedName);
            node.AddValue("ReflectionPopMethod",   popMethod);
            node.AddValue("QueueLabel",            queueLabel);
            node.AddValue("ActiveLabel",           activeLabel);
            node.AddValue("ShortLabel",            shortLabel);
            node.AddValue("Token",                 token);
            return node;
        }

        // ---- UI helpers ----------------------------------------------------

        internal static void Message(string msg)
        {
            try { ScreenMessages.PostScreenMessage(msg, 4f, ScreenMessageStyle.UPPER_CENTER); }
            catch (Exception e) { Debug.LogError("[MJ-RT Bridge] Message failed: " + e); }
        }

        // ---- ConfigNode utilities ------------------------------------------
        // RemoteTech wraps our node under an ExternalData/MJRT subnode before handing it
        // back, so search one level deep rather than assuming the key is at the root.
        internal static string GetValueDeep(ConfigNode node, string key)
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

        // ---- debug helpers -------------------------------------------------

        internal static string Short(string token) =>
            string.IsNullOrEmpty(token) ? "(null)" : token.Substring(0, Math.Min(6, token.Length));

        internal static string StateAtExec()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                double delayNow = v != null ? SafeDelay(v.id) : double.NaN;
                bool onRails = TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1f;
                return $"warp={TimeWarp.CurrentRate:F0}x({TimeWarp.WarpMode}) onRails={onRails} " +
                       $"connectedNow={IsConnected(delayNow)} localCtrl={v != null && API.HasLocalControl(v.id)} " +
                       $"delayNow={delayNow:F1}";
            }
            catch (Exception e) { return "state?(" + e.Message + ")"; }
        }
    }

    /// <summary>
    /// Per-seam counter tracking how many commands are currently in the RT queue for a given
    /// module instance. Used by Type-A seams to suppress UI-state tracking while a command
    /// is in flight, and to support rapid re-clicks without losing count.
    /// </summary>
    internal sealed class InFlightCounter<T> where T : class
    {
        private readonly System.Collections.Generic.Dictionary<T, int> _dict =
            new System.Collections.Generic.Dictionary<T, int>();

        internal bool Has(T instance) =>
            _dict.TryGetValue(instance, out int n) && n > 0;

        internal void Add(T instance) =>
            _dict[instance] = (_dict.TryGetValue(instance, out int n) ? n : 0) + 1;

        internal void Remove(T instance)
        {
            if (!_dict.TryGetValue(instance, out int n)) return;
            if (n <= 1) _dict.Remove(instance);
            else _dict[instance] = n - 1;
        }
    }
}
