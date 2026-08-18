using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace PeerPlay.Popups.Tests
{
    /// <summary>
    /// The property test. The invariants are the value; the interleavings (held create / open / close)
    /// are what makes them fire — a purely sequential operation set cannot construct the races the
    /// example tests were written for.
    /// </summary>
    internal sealed class PopupFuzzTests
    {
        private const int OperationCount = 10000;
        private const int PumpLapWatchdog = 1000;

        private readonly List<PopupHandle> _handles = new List<PopupHandle>();
        private readonly List<CancellationTokenSource> _tokens = new List<CancellationTokenSource>();
        private readonly HashSet<ulong> _completedIds = new HashSet<ulong>();

        private int _submitted;
        private bool _faultsInjected;

        [Test]
        public void Fuzz_Operations_Preserve_Every_Invariant([Values(1, 2, 3, 4, 5)] int seed)
        {
            LogAssert.ignoreFailingMessages = true;

            System.Random random = new System.Random(seed);
            _handles.Clear();
            _tokens.Clear();
            _completedIds.Clear();
            _submitted = 0;
            _faultsInjected = false;

            PopupStateMachine.RecordTransitions = true;
            PopupStateMachine.Transitions.Clear();

            try
            {
                using (PopupTestHarness harness = new PopupTestHarness())
                {
                    for (int operation = 0; operation < OperationCount; operation++)
                    {
                        int lapsBefore = harness.Service.PumpLaps;

                        Execute(harness, random);

                        int laps = harness.Service.PumpLaps - lapsBefore;
                        Assert.LessOrEqual(laps, PumpLapWatchdog,
                            $"seed {seed}, operation {operation}: the pump ran {laps} laps — a livelock");

                        AssertInvariants(harness, seed, operation);
                    }

                    RunToQuiescence(harness, seed);
                    AssertDrained(harness, seed);
                }
            }
            finally
            {
                for (int i = 0; i < _tokens.Count; i++)
                {
                    _tokens[i].Dispose();
                }

                _tokens.Clear();
                PopupStateMachine.RecordTransitions = false;
                PopupStateMachine.Transitions.Clear();
                LogAssert.ignoreFailingMessages = false;
            }
        }

        private void Execute(PopupTestHarness harness, System.Random random)
        {
            switch (random.Next(0, 15))
            {
                case 0:
                case 1:
                case 2:
                    Submit(harness, random, TestKeys.All[random.Next(TestKeys.All.Length)]);
                    break;

                case 3:
                    if (harness.Service.CurrentState == PopupState.Active)
                    {
                        harness.CurrentView?.SimulateUserClose("user");
                    }

                    break;

                case 4:
                    RandomHandle(random).Close("code");
                    break;

                case 5:
                    RandomHandle(random).Cancel();
                    break;

                case 6:
                    if (_tokens.Count > 0)
                    {
                        _tokens[random.Next(_tokens.Count)].Cancel();
                    }

                    break;

                case 7:
                    harness.Service.CloseTop("back");
                    break;

                case 8:
                    if (random.Next(0, 40) == 0)
                    {
                        harness.Service.ForceCloseAll();
                    }

                    break;

                case 9:
                    // A deliberate duplicate of whatever is on screen.
                    if (harness.Service.CurrentKeyId != null)
                    {
                        Submit(harness, random, KeyFor(harness.Service.CurrentKeyId));
                    }

                    break;

                case 10:
                    HoldNext(harness, random);
                    break;

                case 11:
                    ResolveHeld(harness, random);
                    break;

                case 12:
                    harness.Factory.FailNextCreate = true;
                    break;

                case 13:
                    _faultsInjected = true;
                    if (random.Next(0, 2) == 0)
                    {
                        harness.Factory.ThrowOnOpenNextView = true;
                    }
                    else
                    {
                        harness.Factory.ThrowOnCloseNextView = true;
                    }

                    break;

                case 14:
                    switch (random.Next(0, 4))
                    {
                        case 0:
                            _faultsInjected = true;
                            harness.Factory.ThrowOnNextRelease = true;
                            break;
                        case 1:
                            // Foreign code outside the terminal: the policy and the analytics.
                            _faultsInjected = true;
                            harness.Policy.ThrowNextEvaluate = true;
                            break;
                        case 2:
                            _faultsInjected = true;
                            harness.Analytics.ThrowOnShown = random.Next(0, 2) == 0;
                            harness.Analytics.ThrowOnDismissed = random.Next(0, 2) == 0;
                            break;
                        default:
                            harness.Policy.RefuseAll = random.Next(0, 4) == 0;
                            break;
                    }

                    break;
            }
        }

        private void Submit(PopupTestHarness harness, System.Random random, in PopupKey<TestData> key)
        {
            ShowOptions options = new ShowOptions(
                (PopupPriority)random.Next(0, 4),
                (PopupSequencing)random.Next(0, 3),
                random.Next(0, 4) == 0 ? PopupDuplicatePolicy.Allow : PopupDuplicatePolicy.Reject);

            _submitted++;

            if (random.Next(0, 3) == 0)
            {
                CancellationTokenSource caller = new CancellationTokenSource();
                _tokens.Add(caller);
                harness.ShowAsync(key, options, caller.Token).Forget();
                return;
            }

            _handles.Add(harness.Show(key, options));
        }

        private static PopupKey<TestData> KeyFor(string keyId)
        {
            for (int i = 0; i < TestKeys.All.Length; i++)
            {
                if (TestKeys.All[i].Id == keyId)
                {
                    return TestKeys.All[i];
                }
            }

            return TestKeys.A;
        }

        private PopupHandle RandomHandle(System.Random random)
        {
            return _handles.Count == 0 ? default : _handles[random.Next(_handles.Count)];
        }

        private static void HoldNext(PopupTestHarness harness, System.Random random)
        {
            switch (random.Next(0, 3))
            {
                case 0:
                    harness.Factory.HoldNextCreate = true;
                    break;
                case 1:
                    harness.Factory.HoldOpenOnCreatedViews = true;
                    break;
                default:
                    harness.Factory.HoldCloseOnCreatedViews = true;
                    break;
            }
        }

        private static void ResolveHeld(PopupTestHarness harness, System.Random random)
        {
            harness.Factory.HoldOpenOnCreatedViews = false;
            harness.Factory.HoldCloseOnCreatedViews = false;

            if (harness.Factory.IsHoldingCreate && random.Next(0, 3) != 0)
            {
                harness.Factory.ResolveHeldCreate();
                return;
            }

            for (int i = harness.Factory.Created.Count - 1; i >= 0; i--)
            {
                FakeView view = harness.Factory.Created[i];
                if (view.IsHoldingOpen)
                {
                    view.ResolveOpen();
                    return;
                }

                if (view.IsHoldingClose)
                {
                    view.ResolveClose();
                    return;
                }
            }
        }

        private void AssertInvariants(PopupTestHarness harness, int seed, int operation)
        {
            string where = $"seed {seed}, operation {operation}";

            // 1 — at most one popup is interactive, and it is the one holding the slot.
            //
            // Closing is deliberately not counted: a suspended popup cancelled where it stands plays its
            // close behind the interrupter, so more than one popup can legitimately be animating out at
            // once. What must never happen is two popups a player could touch — and the identity check
            // against _current is stronger than counting states, which is what the count alone missed.
            PopupRequest occupant = harness.Service.CurrentRequest;
            int interactive = 0;
            int viewsHeldByLiveRequests = 0;

            foreach (PopupRequest request in harness.Service.LiveRequests)
            {
                if (request.State == PopupState.Initializing || request.State == PopupState.Opening
                    || request.State == PopupState.Active)
                {
                    interactive++;
                    Assert.AreSame(occupant, request,
                        $"{where}: an interactive popup that is not the occupant of the slot");
                }

                if (request.View != null)
                {
                    viewsHeldByLiveRequests++;
                }

                // 8 (first half) — no live request outlives its CTS.
                Assert.IsNotNull(request.Cts, $"{where}: a live request has no CancellationTokenSource");
            }

            Assert.LessOrEqual(interactive, 1, $"{where}: more than one popup occupies the slot");

            if (occupant != null)
            {
                Assert.IsTrue(
                    occupant.State == PopupState.Initializing || occupant.State == PopupState.Opening
                    || occupant.State == PopupState.Active || occupant.State == PopupState.Closing,
                    $"{where}: the slot holds a request in state {occupant.State}");
            }

            // 2 — every recorded transition was legal, and ForceTerminate did not run unprovoked.
            List<PopupTransition> transitions = PopupStateMachine.Transitions;
            for (int i = 0; i < transitions.Count; i++)
            {
                PopupTransition transition = transitions[i];
                Assert.IsTrue(PopupStateMachine.IsLegal(transition.From, transition.To),
                    $"{where}: illegal transition {transition.From} -> {transition.To} (id {transition.Id})");
            }

            transitions.Clear();

            if (!_faultsInjected)
            {
                Assert.AreEqual(0, harness.Service.ForceTerminateCount,
                    $"{where}: ForceTerminate ran without an injected fault");
            }

            // 3 — view accounting closes.
            Assert.AreEqual(viewsHeldByLiveRequests, harness.Factory.LiveViewCount,
                $"{where}: view accounting does not close");

            // 4 / 5 — one terminal per request, none of them None, and none of them lost.
            for (int i = _completedIds.Count; i < harness.Completions.Count; i++)
            {
                PopupCompletion completion = harness.Completions[i];
                Assert.IsTrue(_completedIds.Add(completion.Id),
                    $"{where}: request {completion.Id} completed twice");
                Assert.AreNotEqual(PopupOutcome.None, completion.Result.Outcome,
                    $"{where}: request {completion.Id} delivered PopupOutcome.None");
            }

            Assert.AreEqual(_submitted - harness.Service.LiveCount, harness.Completions.Count,
                $"{where}: a terminal was lost");

            // 7 — the suspend stack is consistent.
            IReadOnlyList<PopupRequest> stack = harness.Service.SuspendStack;
            for (int i = 0; i < stack.Count; i++)
            {
                Assert.AreEqual(PopupState.Suspended, stack[i].State,
                    $"{where}: a request on the suspend stack is not Suspended");

                for (int j = i + 1; j < stack.Count; j++)
                {
                    Assert.AreNotSame(stack[i], stack[j], $"{where}: a request is on the suspend stack twice");
                }
            }

            foreach (PopupRequest request in harness.Service.LiveRequests)
            {
                if (request.State != PopupState.Suspended)
                {
                    continue;
                }

                bool found = false;
                for (int i = 0; i < stack.Count; i++)
                {
                    found |= ReferenceEquals(stack[i], request);
                }

                Assert.IsTrue(found, $"{where}: a Suspended request is missing from the suspend stack");
            }
        }

        private static void RunToQuiescence(PopupTestHarness harness, int seed)
        {
            harness.Policy.RefuseAll = false;
            harness.Policy.ThrowNextEvaluate = false;
            harness.Policy.ThrowingKeys.Clear();
            harness.Policy.ThrowOnNotifyShown = false;
            harness.Analytics.ThrowOnShown = false;
            harness.Analytics.ThrowOnDismissed = false;
            harness.Factory.FailAllCreates = false;
            harness.Factory.FailNextCreate = false;
            harness.Factory.ThrowOnRelease = false;
            harness.Factory.ThrowOnNextRelease = false;
            harness.Factory.ThrowOnOpenNextView = false;
            harness.Factory.ThrowOnCloseNextView = false;
            harness.Factory.HoldOpenOnCreatedViews = false;
            harness.Factory.HoldCloseOnCreatedViews = false;
            harness.Factory.HoldNextCreate = false;

            for (int guard = 0; guard < 5000 && harness.Service.LiveCount > 0; guard++)
            {
                if (harness.Factory.IsHoldingCreate)
                {
                    harness.Factory.ResolveHeldCreate();
                    continue;
                }

                bool resolvedHold = false;
                for (int i = 0; i < harness.Factory.Created.Count; i++)
                {
                    FakeView view = harness.Factory.Created[i];
                    if (view.IsHoldingOpen)
                    {
                        view.ResolveOpen();
                        resolvedHold = true;
                        break;
                    }

                    if (view.IsHoldingClose)
                    {
                        view.ResolveClose();
                        resolvedHold = true;
                        break;
                    }
                }

                if (resolvedHold)
                {
                    continue;
                }

                if (harness.Service.CurrentState == PopupState.Active)
                {
                    harness.CurrentView?.SimulateUserClose(null);
                    continue;
                }

                // Nothing left that the pump can move on its own.
                harness.Service.ForceCloseAll();
            }

            Assert.AreEqual(0, harness.Service.LiveCount, $"seed {seed}: the queue did not drain");
        }

        private static void AssertDrained(PopupTestHarness harness, int seed)
        {
            Assert.AreEqual(0, harness.Service.PendingCount, $"seed {seed}: PendingCount");
            Assert.IsNull(harness.Service.CurrentKeyId, $"seed {seed}: an occupant survived");
            Assert.AreEqual(0, harness.Service.SuspendedCount, $"seed {seed}: the suspend stack survived");
            Assert.AreEqual(0, harness.Service.LiveCount, $"seed {seed}: _live is not empty");
            Assert.AreEqual(0, harness.Service.LiveKeyCount, $"seed {seed}: _liveByKey is not empty");
            Assert.IsTrue(harness.Service.AllBandsEmpty, $"seed {seed}: a band still holds ids");
            Assert.AreEqual(0, harness.Factory.LiveViewCount, $"seed {seed}: a view was never released");

            // 8 (second half) — every recycled request left its CTS and its registration behind.
            Assert.IsTrue(PopupRequestPool<TestData>.AllPooledAreClean(),
                $"seed {seed}: a pooled request still holds a CancellationTokenSource or a view");
        }
    }
}
