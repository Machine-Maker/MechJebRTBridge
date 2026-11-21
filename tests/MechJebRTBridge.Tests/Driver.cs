using System;
using System.Collections;
using System.Collections.Generic;

namespace MechJebRTBridge.Tests
{
    /// <summary>Thrown by <see cref="Ctx.Check"/>: the test failed.</summary>
    internal sealed class TestFailure : Exception
    {
        internal TestFailure(string message) : base(message) { }
    }

    /// <summary>Thrown by <see cref="Ctx.Skip"/>: the test can't run on this vessel / version.</summary>
    internal sealed class TestSkipped : Exception
    {
        internal TestSkipped(string message) : base(message) { }
    }

    /// <summary>
    /// Yield this to run <see cref="Action"/> inside the next physics step (from the runner's
    /// FixedUpdate, so <c>Time.inFixedTimeStep</c> is true). That's where MechJeb's autopilots
    /// and kOS call into MechJeb, so the bridge treats it as an on-board, undelayed call.
    /// An exception thrown by the action is rethrown into the yielding coroutine.
    /// </summary>
    internal sealed class PhysicsStep
    {
        internal readonly Action Action;
        internal bool Done;
        internal Exception Error;
        internal PhysicsStep(Action action) { Action = action; }
    }

    /// <summary>
    /// Yield this to run <see cref="Body"/> as a nested coroutine that can fail without taking
    /// the caller down: an exception anywhere inside it unwinds to here and lands in
    /// <see cref="Error"/>, and the caller resumes.
    /// </summary>
    internal sealed class Guarded
    {
        internal readonly IEnumerator Body;
        internal Exception Error;
        internal Guarded(IEnumerator body) { Body = body; }
    }

    /// <summary>
    /// Minimal coroutine driver. Unity coroutines can't catch exceptions across a yield, and
    /// tests need to (a failed check must not abort the run). Call <see cref="Update"/> once per
    /// frame and <see cref="FixedUpdate"/> once per physics step.
    ///
    /// Yield values: <c>null</c> (next frame), an <see cref="IEnumerator"/> (run it to the end,
    /// then continue), a <see cref="PhysicsStep"/>, or a <see cref="Guarded"/>.
    /// </summary>
    internal sealed class Driver
    {
        private sealed class Frame
        {
            internal IEnumerator It;
            internal Guarded Guard; // non-null if this frame is a Guarded body
        }

        private readonly Stack<Frame> _stack = new Stack<Frame>();
        private PhysicsStep _waiting;

        internal bool Running => _stack.Count > 0;

        internal void Start(IEnumerator root)
        {
            _stack.Clear();
            _waiting = null;
            _stack.Push(new Frame { It = root });
        }

        internal void Stop()
        {
            _stack.Clear();
            _waiting = null;
        }

        internal void FixedUpdate()
        {
            PhysicsStep p = _waiting;
            if (p == null || p.Done) return;
            try { p.Action(); }
            catch (Exception e) { p.Error = e; }
            p.Done = true;
        }

        internal void Update()
        {
            Exception pending = null;
            if (_waiting != null)
            {
                if (!_waiting.Done) return;
                pending  = _waiting.Error;
                _waiting = null;
            }

            for (int guard = 0; guard < 10000 && _stack.Count > 0; guard++)
            {
                if (pending != null)
                {
                    if (!Unwind(pending)) return; // no guard caught it: run is over
                    pending = null;
                    continue;
                }

                Frame top = _stack.Peek();
                bool moved;
                try { moved = top.It.MoveNext(); }
                catch (Exception e) { pending = e; continue; }

                if (!moved) { _stack.Pop(); continue; }

                switch (top.It.Current)
                {
                    case Guarded g:     _stack.Push(new Frame { It = g.Body, Guard = g }); continue;
                    case IEnumerator n: _stack.Push(new Frame { It = n });                continue;
                    case PhysicsStep p: _waiting = p; return;
                    default:            return; // null or anything else: next frame
                }
            }
        }

        // Pops frames up to and including the innermost Guarded body, recording the error there.
        private bool Unwind(Exception e)
        {
            while (_stack.Count > 0)
            {
                Frame f = _stack.Pop();
                if (f.Guard != null)
                {
                    f.Guard.Error = e;
                    return true;
                }
            }
            UnityEngine.Debug.LogError("[MJ-RT Test] unguarded exception, run aborted: " + e);
            return false;
        }
    }
}
