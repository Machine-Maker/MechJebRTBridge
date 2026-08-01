using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// One-time bootstrap. Patches each MechJeb seam independently so that one failing
    /// patch (e.g. a MechJeb version whose method signature changed) can't abort the others.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, once: true)]
    public class BridgeAddon : MonoBehaviour
    {
        private const string HarmonyId = "com.mechjebrtbridge";

        public void Start()
        {
            var harmony = new Harmony(HarmonyId);

            // Core: intercept the SmartASS attitude command and delay it. Must work.
            Patch(harmony, "MuMech.MechJebModuleSmartASS:Engage",
                  prefix: nameof(SmartAssBridge.EngagePrefix));

            // Optional: track the vessel's actual mode each frame so the held highlight
            // never flickers. SmartASS inherits OnFixedUpdate from ComputerModule, so patch
            // it on the base type (the postfix filters by instance).
            Patch(harmony, "MuMech.ComputerModule:OnFixedUpdate",
                  postfix: nameof(SmartAssBridge.TrackPostfix));

            // Keep the onboard attitude hold alive through a signal blackout: capture what
            // MechJeb's attitude controller computes each frame so we can re-apply it as a
            // RemoteTech sanctioned pilot (which runs after RT neutralizes on lost signal).
            Patch(harmony, "MuMech.MechJebModuleAttitudeController:Drive",
                  postfix: nameof(SmartAssBridge.CaptureAttitudePostfix));

            Debug.Log("[MJ-RT Bridge] Initialised.");
        }

        private static void Patch(Harmony harmony, string target, string prefix = null, string postfix = null)
        {
            try
            {
                MethodBase method = AccessTools.Method(target);
                if (method == null)
                {
                    Debug.LogWarning($"[MJ-RT Bridge] patch target not found (skipped): {target}");
                    return;
                }
                harmony.Patch(method,
                    prefix:  prefix  != null ? new HarmonyMethod(typeof(SmartAssBridge), prefix)  : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(SmartAssBridge), postfix) : null);
                Debug.Log($"[MJ-RT Bridge] patched {target}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MJ-RT Bridge] failed to patch {target} (skipped): {e.Message}");
            }
        }
    }
}
