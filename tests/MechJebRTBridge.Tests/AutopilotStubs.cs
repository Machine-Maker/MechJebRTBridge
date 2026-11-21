using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MechJebRTBridge.Tests
{
    /// <summary>
    /// Test double for the autopilots the tests engage (and the controllers they drive). While <see cref="Active"/>, the per-frame
    /// work of these modules (Drive / OnFixedUpdate / OnUpdate) is skipped, so an engaged
    /// autopilot stays engaged without a target and never burns, warps, or disengages itself.
    /// The tests check the command boundary (when Enabled flips), not what the autopilot flies.
    /// Engage/disengage code (Users, OnModuleEnabled/Disabled, Init) still runs for real.
    /// </summary>
    internal static class AutopilotStubs
    {
        private const string HarmonyId = "com.mechjebrtbridge.tests";

        // By type name (a module matches if it or a base class has one of these names), because
        // the ascent classes differ between MechJeb versions. The staging and thrust controllers
        // get users when Ascent engages: stubbed so autostage can't fire a stage on the test probe.
        private static readonly HashSet<string> StubbedNames = new HashSet<string>
        {
            "MechJebModuleNodeExecutor",
            "MechJebModuleLandingAutopilot",
            "MechJebModuleRendezvousAutopilot",
            "MechJebModuleDockingAutopilot",
            "MechJebModuleAscentBaseAutopilot",    // 2.15.x
            "MechJebModuleAscentClassicAutopilot", // dev
            "MechJebModulePSGAutopilot",           // dev
            "MechJebModuleStagingController",
            "MechJebModuleThrustController",
        };

        private static readonly Dictionary<Type, bool> _isStubbed = new Dictionary<Type, bool>();

        private static bool IsStubbed(Type t)
        {
            if (_isStubbed.TryGetValue(t, out bool s)) return s;
            for (Type b = t; b != null && !s; b = b.BaseType) s = StubbedNames.Contains(b.Name);
            return _isStubbed[t] = s;
        }

        internal static bool Active;
        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            var harmony = new Harmony(HarmonyId);
            var prefix  = new HarmonyMethod(typeof(AutopilotStubs), nameof(SkipPrefix));
            var done    = new HashSet<MethodBase>();
            foreach (string typeName in StubbedNames)
            {
                Type t = AccessTools.TypeByName("MuMech." + typeName);
                if (t == null) continue; // not in this MechJeb version
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
        }

        private static bool SkipPrefix(object __instance) =>
            !(Active && __instance != null && IsStubbed(__instance.GetType()));
    }
}
