using System;
using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace MechJebRTBridge.Tests
{
    /// <summary>
    /// Flight-scene test runner: a toolbar button opens a window that runs <see cref="Tests.All"/>
    /// against the active vessel and shows pass/fail/skip per test. Every result also goes to
    /// KSP.log as <c>[MJ-RT Test] PASS|FAIL|SKIP name — detail</c>, ending with a
    /// <c>[MJ-RT Test] SUMMARY</c> line.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TestRunner : MonoBehaviour
    {
        private enum Status { NotRun, Running, Pass, Fail, Skip }

        private sealed class Result
        {
            internal TestCase Test;
            internal Status Status;
            internal string Detail;
        }

        private readonly Driver _driver = new Driver();
        private readonly List<Result> _results = new List<Result>();
        private Ctx _ctx;
        private string _header = "Not run. Read tests/README.md for the test vessel.";
        private bool _stopRequested;

        private ApplicationLauncherButton _button;
        private bool _show;
        private Rect _rect = new Rect(200, 100, 560, 520);
        private Vector2 _scroll;

        // ---- lifecycle -----------------------------------------------------

        public void Start()
        {
            foreach (TestCase t in Tests.All) _results.Add(new Result { Test = t });
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            if (ApplicationLauncher.Ready) AddButton();
        }

        public void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (_button != null) ApplicationLauncher.Instance?.RemoveModApplication(_button);
            if (_driver.Running)
            {
                Debug.LogWarning("[MJ-RT Test] scene changed mid-run; restoring RT delay and autopilot stubs");
                _driver.Stop();
            }
            _ctx?.RestoreSync();
            _ctx?.StopLogCapture();
        }

        private void AddButton()
        {
            if (_button != null) return;
            var tex = new Texture2D(38, 38, TextureFormat.ARGB32, false);
            var px  = new Color32[38 * 38];
            for (int i = 0; i < px.Length; i++)
            {
                int x = i % 38, y = i / 38;
                bool border = x < 3 || y < 3 || x > 34 || y > 34;
                px[i] = border ? new Color32(255, 255, 255, 255) : new Color32(40, 140, 60, 255);
            }
            tex.SetPixels32(px);
            tex.Apply();
            _button = ApplicationLauncher.Instance.AddModApplication(
                () => _show = true, () => _show = false, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT, tex);
        }

        public void Update()        => _driver.Update();
        public void FixedUpdate()   => _driver.FixedUpdate();

        // ---- running -------------------------------------------------------

        private void StartRun(IList<Result> selection)
        {
            if (_driver.Running) return;
            foreach (Result r in _results) if (selection.Contains(r)) { r.Status = Status.NotRun; r.Detail = null; }
            _stopRequested = false;
            _ctx = new Ctx();
            _ctx.StartLogCapture();
            _driver.Start(Run(selection));
        }

        private IEnumerator Run(IList<Result> selection)
        {
            Debug.Log($"[MJ-RT Test] RUN {selection.Count} test(s)");
            _header = "Setting up…";
            var setup = new Guarded(_ctx.Setup());
            yield return setup;
            if (setup.Error != null)
            {
                _header = "Setup failed: " + setup.Error.Message;
                Debug.LogError("[MJ-RT Test] SETUP FAILED — " + setup.Error.Message);
                yield return new Guarded(_ctx.Teardown());
                _ctx.StopLogCapture();
                yield break;
            }

            int pass = 0, fail = 0, skip = 0;
            foreach (Result r in selection)
            {
                if (_stopRequested) break;
                r.Status = Status.Running;
                _header  = "Running: " + r.Test.Name;

                var body = new Guarded(r.Test.Body(_ctx));
                yield return body;
                switch (body.Error)
                {
                    case null:
                        r.Status = Status.Pass; r.Detail = _ctx.Note; pass++; break;
                    case TestSkipped s:
                        r.Status = Status.Skip; r.Detail = s.Message; skip++; break;
                    case TestFailure f:
                        r.Status = Status.Fail; r.Detail = f.Message; fail++; break;
                    default:
                        r.Status = Status.Fail; r.Detail = "exception: " + body.Error; fail++; break;
                }
                Debug.Log($"[MJ-RT Test] {r.Status.ToString().ToUpperInvariant()} {r.Test.Name}" +
                          (string.IsNullOrEmpty(r.Detail) ? "" : " — " + r.Detail));

                var cleanup = new Guarded(_ctx.Cleanup());
                yield return cleanup;
                if (cleanup.Error != null)
                {
                    Debug.LogError("[MJ-RT Test] cleanup failed, stopping the run: " + cleanup.Error);
                    _header = "Cleanup failed: " + cleanup.Error.Message;
                    break;
                }
            }

            yield return new Guarded(_ctx.Teardown());
            _ctx.StopLogCapture();
            string summary = $"{pass} passed, {fail} failed, {skip} skipped" + (_stopRequested ? " (stopped)" : "");
            if (!_header.StartsWith("Cleanup failed")) _header = summary;
            Debug.Log("[MJ-RT Test] SUMMARY " + summary);
        }

        // ---- UI ------------------------------------------------------------

        public void OnGUI()
        {
            if (!_show) return;
            GUI.skin = HighLogic.Skin;
            _rect = GUILayout.Window(GetInstanceID(), _rect, DrawWindow, "MechJebRTBridge tests");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label(_header);
            GUILayout.BeginHorizontal();
            GUI.enabled = !_driver.Running;
            if (GUILayout.Button("Run all")) StartRun(_results);
            GUI.enabled = _driver.Running;
            if (GUILayout.Button("Stop after this test")) _stopRequested = true;
            GUI.enabled = true;
            if (GUILayout.Button("Close")) { _show = false; _button?.SetFalse(false); }
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (Result r in _results)
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = !_driver.Running;
                if (GUILayout.Button("▶", GUILayout.Width(28))) StartRun(new[] { r });
                GUI.enabled = true;
                var old = GUI.contentColor;
                GUI.contentColor = Colour(r.Status);
                GUILayout.Label(Label(r.Status), GUILayout.Width(48));
                GUI.contentColor = old;
                GUILayout.Label(r.Test.Name);
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(r.Detail))
                    GUILayout.Label("    " + r.Detail);
            }
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private static string Label(Status s)
        {
            switch (s)
            {
                case Status.Running: return "…";
                case Status.Pass:    return "PASS";
                case Status.Fail:    return "FAIL";
                case Status.Skip:    return "SKIP";
                default:             return "—";
            }
        }

        private static Color Colour(Status s)
        {
            switch (s)
            {
                case Status.Pass: return Color.green;
                case Status.Fail: return Color.red;
                case Status.Skip: return Color.yellow;
                default:          return Color.white;
            }
        }
    }
}
