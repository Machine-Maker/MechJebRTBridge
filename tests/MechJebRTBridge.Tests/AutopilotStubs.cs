using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MuMech;
using UnityEngine;

namespace MechJebRTBridge.Tests
{
    /// <summary>
    /// Test double for the autopilots the tests engage. While <see cref="Active"/>, the per-frame
    /// work of these modules (Drive / OnFixedUpdate / OnUpdate) is skipped, so an engaged
    /// autopilot stays engaged without a target and never burns, warps, or disengages itself.
    /// The tests check the command boundary (when Enabled flips), not what the autopilot flies.
    /// Engage/disengage code (Users, OnModuleEnabled/Disabled, Init) still runs for real.
    /// </summary>
    internal static class AutopilotStubs
    {
        private const string HarmonyId = "com.mechjebrtbridge.tests";

        internal static readonly HashSet<Type> Stubbed = new HashSet<Type>
        {
            typeof(MechJebModuleNodeExecutor),
            typeof(MechJebModuleLandingAutopilot),
            typeof(MechJebModuleRendezvousAutopilot),
            typeof(MechJebModuleDockingAutopilot),
        };

        internal static bool Active;
        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            var harmony = new Harmony(HarmonyId);
            var prefix  = new HarmonyMethod(typeof(AutopilotStubs), nameof(SkipPrefix));
            var done    = new HashSet<MethodBase>();
            foreach (Type t in Stubbed)
            foreach (string name in new[] { "Drive", "OnFixedUpdate", "OnUpdate" })
            {
                // May resolve to a base-class method (AutopilotModule, ComputerModule) shared
                // with other modules; SkipPrefix filters by the instance's type.
                MethodBase m = AccessTools.Method(t, name);
                if (m == null) continue;
                // Harmony only patches a method looked up on the type that declares it.
                Type[] args = Array.ConvertAll(m.GetParameters(), p => p.ParameterType);
                m = AccessTools.DeclaredMethod(m.DeclaringType, name, args) ?? m;
                if (!done.Add(m)) continue;
                try { harmony.Patch(m, prefix: prefix); }
                catch (Exception e) { Debug.LogError($"[MJ-RT Test] stub patch failed for {m.DeclaringType?.Name}.{name}: {e.Message}"); }
            }
        }

        private static bool SkipPrefix(object __instance) =>
            !(Active && __instance != null && Stubbed.Contains(__instance.GetType()));
    }
}
