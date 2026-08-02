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

            // SmartASS: delay Engage; track committed mode each frame; capture attitude for
            // sanctioned-pilot hold during signal blackout.
            Patch(harmony, "MuMech.MechJebModuleSmartASS:Engage",
                  prefix:  H(typeof(SmartAssBridge),  nameof(SmartAssBridge.EngagePrefix)));
            Patch(harmony, "MuMech.ComputerModule:OnFixedUpdate",
                  postfix: H(typeof(SmartAssBridge),  nameof(SmartAssBridge.TrackPostfix)));
            Patch(harmony, "MuMech.MechJebModuleAttitudeController:Drive",
                  postfix: H(typeof(SmartAssBridge),  nameof(SmartAssBridge.CaptureAttitudePostfix)));

            // Node executor: delay activation only. Abort() is intentionally not patched —
            // MechJeb calls it internally when nodes are consumed or deleted, and those
            // internal calls must not be queued. The instant local abort is the §8 cutoff.
            Patch(harmony, "MuMech.MechJebModuleNodeExecutor:ExecuteOneNode",
                  prefix: H(typeof(NodeExecutorBridge), nameof(NodeExecutorBridge.ExecuteOneNodePrefix)));
            Patch(harmony, "MuMech.MechJebModuleNodeExecutor:ExecuteAllNodes",
                  prefix: H(typeof(NodeExecutorBridge), nameof(NodeExecutorBridge.ExecuteAllNodesPrefix)));

            // Landing autopilot: delay activation only. StopLanding() is intentionally not
            // patched — MechJeb calls it internally at touchdown, same issue as Abort above.
            Patch(harmony, "MuMech.MechJebModuleLandingAutopilot:LandAtPositionTarget",
                  prefix: H(typeof(LandingBridge), nameof(LandingBridge.LandAtPositionTargetPrefix)));
            Patch(harmony, "MuMech.MechJebModuleLandingAutopilot:LandUntargeted",
                  prefix: H(typeof(LandingBridge), nameof(LandingBridge.LandUntargetedPrefix)));

            Debug.Log("[MJ-RT Bridge] Initialised.");
        }

        private static HarmonyMethod H(Type t, string methodName) =>
            new HarmonyMethod(t, methodName);

        private static void Patch(Harmony harmony, string target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                MethodBase method = AccessTools.Method(target);
                if (method == null)
                {
                    Debug.LogWarning($"[MJ-RT Bridge] patch target not found (skipped): {target}");
                    return;
                }
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                Debug.Log($"[MJ-RT Bridge] patched {target}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MJ-RT Bridge] failed to patch {target} (skipped): {e.Message}");
            }
        }
    }
}
