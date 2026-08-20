using System;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PeerPlay.Popups.Tests
{
    /// <summary>
    /// The example tests around the floor: the remaining edge cases from the design, and one named
    /// regression per defect that was found and fixed.
    /// </summary>
    internal sealed class PopupCoreTests
    {
        private static PopupResult Result(UniTask<PopupResult> task)
        {
            Assert.AreEqual(UniTaskStatus.Succeeded, task.Status, "the request has not produced a terminal yet");
            return task.GetAwaiter().GetResult();
        }

        private static PopupRequest FindLive(PopupTestHarness harness, string keyId)
        {
            foreach (PopupRequest request in harness.Service.LiveRequests)
            {
                if (request.KeyId == keyId)
                {
                    return request;
                }
            }

            return null;
        }

        // 3 — priority and FIFO together.
        [Test]
        public void Priority_And_Fifo_Combined()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High));
                harness.Show(TestKeys.D);
                harness.Show(TestKeys.C, new ShowOptions(PopupPriority.High));
                harness.Show(TestKeys.E);

                harness.CloseCurrent();
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.d", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.e", harness.Service.CurrentKeyId);
            }
        }

        // 5b — a suspended popup outranks the pending queue, whatever the queue's priority.
        [Test]
        public void Resumption_Outranks_A_Critical_Pending_Request()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume));
                harness.Show(TestKeys.C, new ShowOptions(PopupPriority.Critical));

                harness.CloseCurrent();
                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId, "the suspended popup was buried");

                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId, "the Critical request was starved");
            }
        }

        // 5c — the head dies while the occupant is closing for it.
        [Test]
        public void Head_That_Dies_During_The_Close_Strands_Nothing()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                UniTask<PopupResult> occupant = harness.ShowAsync(TestKeys.A);
                FakeView occupantView = harness.Factory.LastCreatedFor("popup.a");
                occupantView.HoldClose = true;

                PopupHandle replacer = harness.Show(TestKeys.B,
                    new ShowOptions(PopupPriority.High, PopupSequencing.Replace));

                Assert.IsTrue(occupantView.IsHoldingClose, "the close transition is not in flight");

                Assert.IsTrue(replacer.Cancel());
                harness.Show(TestKeys.C);

                occupantView.ResolveClose();

                Assert.AreEqual(PopupOutcome.Superseded, Result(occupant).Outcome);
                Assert.AreEqual(PopupOutcome.Cancelled, harness.CompletionFor("popup.b").Result.Outcome);
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId, "the next pending request was stranded");
            }
        }

        // 5d — a higher-priority arrival during the close wins the freed slot, and nothing is opened
        // only to be destroyed on the next lap.
        [Test]
        public void Higher_Priority_Arrival_During_The_Close_Takes_The_Freed_Slot()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                FakeView occupantView = harness.Factory.LastCreatedFor("popup.a");
                occupantView.HoldClose = true;

                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.Replace));
                Assert.IsTrue(occupantView.IsHoldingClose);

                harness.Show(TestKeys.C, new ShowOptions(PopupPriority.Critical));

                occupantView.ResolveClose();

                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId, "the newcomer did not win the freed slot");
                Assert.AreEqual(2, harness.Factory.CreateCount, "a view was opened only to be destroyed again");

                harness.CloseCurrent();
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the original replacer was lost");
            }
        }

        // 6 — a lower-priority interrupter waits, and its sequencing survives the wait untouched.
        [Test]
        public void Interrupter_Below_The_Occupants_Priority_Waits_With_Its_Mode_Intact()
        {
            LogAssert.Expect(LogType.Warning, new Regex(".*cannot preempt.*"));

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A, new ShowOptions(PopupPriority.Critical));
                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.Low, PopupSequencing.InterruptAndResume));

                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId, "a Low request preempted a Critical one");

                harness.CloseCurrent();

                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);
                Assert.AreEqual(PopupSequencing.InterruptAndResume, harness.Service.CurrentRequest.Sequencing,
                    "the request's sequencing was mutated while it waited");
            }
        }

        // 8 — Replace with nothing to replace is just an open.
        [Test]
        public void Replace_With_An_Empty_Slot_Behaves_As_Queue()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A, new ShowOptions(PopupPriority.Normal, PopupSequencing.Replace));

                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId);
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);
                Assert.AreEqual(0, harness.Factory.ReleaseCount);
            }
        }

        // 9b — an illegal transition inside the pump terminates that request and leaves the queue usable.
        [Test]
        public void Illegal_Transition_Inside_The_Pump_Does_Not_Wedge_The_Queue()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B);

                // Drive the core into an edge the table forbids: Opening -> Initializing.
                PopupRequest queued = FindLive(harness, "popup.b");
                Assert.IsNotNull(queued);
                queued.State = PopupState.Opening;

                harness.CloseCurrent();

                Assert.AreEqual(1, harness.Service.ForceTerminateCount);
                Assert.AreEqual(PopupOutcome.Faulted, harness.CompletionFor("popup.b").Result.Outcome);
                Assert.IsNull(harness.Service.CurrentKeyId);

                harness.Show(TestKeys.C);
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId, "the pump abandoned the queue");
            }

            LogAssert.ignoreFailingMessages = false;
        }

        // 9c — an ordinary cancellation during loading must not trip the programmer-error signal.
        // The test asserts it by NOT ignoring failing messages: a logged exception fails it.
        [Test]
        public void Cancelling_While_The_View_Is_Loading_Logs_No_Exception()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Factory.HoldNextCreate = true;
                PopupHandle loading = harness.Show(TestKeys.A);

                Assert.AreEqual(PopupState.Initializing, loading.State);

                Assert.IsTrue(loading.Cancel());

                Assert.AreEqual(PopupOutcome.Cancelled, harness.CompletionFor("popup.a").Result.Outcome);
                Assert.AreEqual(0, harness.Factory.LiveViewCount, "a view leaked");
                Assert.IsNull(harness.Service.CurrentKeyId);
            }
        }

        // 10 — closing twice is a no-op the second time.
        [Test]
        public void Closing_Twice_Reports_False_And_Releases_Once()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                PopupHandle handle = harness.Show(TestKeys.A);

                Assert.IsTrue(handle.Close("ok"));
                Assert.IsFalse(handle.Close("ok"), "a stale handle closed something");

                Assert.AreEqual(1, harness.Factory.ReleaseCount);
                Assert.AreEqual(1, harness.Completions.Count);
                Assert.AreEqual("ok", harness.CompletionFor("popup.a").Result.Action);
                Assert.AreEqual(CloseSource.Code, harness.CompletionFor("popup.a").Result.Source);
            }
        }

        // 11b — the awaiter resumes after the unregister, so a caller can immediately re-show its key.
        [Test]
        public void ReShowing_From_Inside_The_Awaiter_Is_Not_A_Duplicate()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                UniTask<PopupResult> task = harness.ShowAsync(TestKeys.A);
                UniTask<PopupResult>.Awaiter awaiter = task.GetAwaiter();

                PopupHandle reshown = default;
                awaiter.OnCompleted(() =>
                {
                    awaiter.GetResult();
                    reshown = harness.Show(TestKeys.A);
                });

                harness.CloseCurrent();

                Assert.IsTrue(reshown.IsLive, "the re-show was rejected as a duplicate of the request that just closed");
                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId);
                Assert.AreEqual(0, harness.CountOutcome(PopupOutcome.Duplicate));
            }
        }

        // 12 — duplicates are allowed when the caller opts in.
        [Test]
        public void Duplicates_Allowed_Opens_Every_Submission_In_Turn()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                ShowOptions allow = new ShowOptions(PopupPriority.Normal, PopupSequencing.Queue,
                    PopupDuplicatePolicy.Allow);

                for (int i = 0; i < 10; i++)
                {
                    harness.Show(TestKeys.A, allow);
                }

                Assert.AreEqual(0, harness.CountOutcome(PopupOutcome.Duplicate));

                for (int i = 0; i < 10; i++)
                {
                    Assert.AreEqual("popup.a", harness.Service.CurrentKeyId, $"popup {i} did not open");
                    harness.CloseCurrent();
                }

                Assert.AreEqual(10, harness.Factory.CreateCount);
                Assert.IsNull(harness.Service.CurrentKeyId);
            }
        }

        // 13b — a cancelled pending request cannot come back through the object pool.
        [Test]
        public void A_Cancelled_Pending_Request_Cannot_Be_Resurrected_By_The_Pool()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                PopupHandle cancelled = harness.Show(TestKeys.B);

                Assert.IsTrue(cancelled.Cancel());

                // Rents the very object that was just recycled — with a fresh id.
                harness.Show(TestKeys.C);

                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.IsNull(harness.Service.CurrentKeyId, "a dead id opened a popup");
                Assert.AreEqual(2, harness.Factory.CreateCount);
                Assert.AreEqual(3, harness.Completions.Count);
                Assert.IsFalse(cancelled.IsLive);
            }
        }

        // 16c — admission runs before anything is vacated, so a refusal cannot destroy a live popup.
        [Test]
        public void A_Refused_Replacer_Leaves_The_Occupant_Untouched()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                FakeView occupantView = harness.Factory.LastCreatedFor("popup.a");

                harness.Policy.RefusedKeys.Add("popup.b");
                UniTask<PopupResult> replacer = harness.ShowAsync(TestKeys.B,
                    new ShowOptions(PopupPriority.High, PopupSequencing.Replace));

                Assert.AreEqual(PopupOutcome.Refused, Result(replacer).Outcome);
                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId, "the occupant was destroyed for a refused request");
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);
                Assert.AreEqual(0, occupantView.SuspendCalls, "the occupant flickered for a refused request");
                Assert.AreEqual(0, harness.Factory.ReleaseCount);
            }
        }

        // 17 — a view that throws while opening: the request faults, the view is released, the queue moves.
        [Test]
        public void View_That_Throws_While_Opening_Faults_And_Releases()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Factory.ThrowOnOpenNextView = true;
                UniTask<PopupResult> faulting = harness.ShowAsync(TestKeys.A);
                harness.Show(TestKeys.B);

                Assert.AreEqual(PopupOutcome.Faulted, Result(faulting).Outcome);
                Assert.AreEqual(1, harness.Factory.ReleaseCount, "the faulted view was not released");
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the queue stalled on a fault");
            }

            LogAssert.ignoreFailingMessages = false;
        }

        // 17b — the same three assertions at the close-watcher boundary.
        [Test]
        public void View_That_Throws_While_Waiting_Or_Closing_Faults_And_Releases()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Factory.ThrowOnWaitNextView = true;
                UniTask<PopupResult> faulting = harness.ShowAsync(TestKeys.A);
                harness.Show(TestKeys.B);

                Assert.AreEqual(PopupOutcome.Faulted, Result(faulting).Outcome);
                Assert.AreEqual(1, harness.Factory.ReleaseCount);
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);

                // And a throw from the close transition still reaches the terminal.
                harness.Factory.LastCreatedFor("popup.b").ThrowOnClose = true;
                harness.CloseCurrent();

                Assert.AreEqual(PopupOutcome.Completed, harness.CompletionFor("popup.b").Result.Outcome);
                Assert.AreEqual(2, harness.Factory.ReleaseCount, "a view whose close threw was not released");
                Assert.IsNull(harness.Service.CurrentKeyId);
            }

            LogAssert.ignoreFailingMessages = false;
        }

        // 19 — disposing mid-flight cancels everything, leaks nothing, and stays polite afterwards.
        [Test]
        public void Dispose_Mid_Flight_Cancels_And_Leaks_Nothing()
        {
            LogAssert.Expect(LogType.Warning, new Regex(".*the service is disposed.*"));

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Factory.HoldNextCreate = true;
                UniTask<PopupResult> loading = harness.ShowAsync(TestKeys.A);
                UniTask<PopupResult> queued = harness.ShowAsync(TestKeys.B);

                harness.Service.Dispose();

                Assert.AreEqual(PopupOutcome.Cancelled, Result(loading).Outcome);
                Assert.AreEqual(PopupOutcome.Cancelled, Result(queued).Outcome);

                // The factory answers after the drain: that instance is ours to release, and nobody else's.
                harness.Factory.ResolveHeldCreate();
                Assert.AreEqual(0, harness.Factory.LiveViewCount, "a view created after the drain leaked");

                UniTask<PopupResult> afterDispose = harness.ShowAsync(TestKeys.C);
                Assert.AreEqual(PopupOutcome.Cancelled, Result(afterDispose).Outcome);
                Assert.AreEqual(0, harness.Service.PendingCount);
            }
        }

        // 20 — a handle to a recycled request cannot act on whichever popup inherited the object.
        [Test]
        public void A_Stale_Handle_Is_Inert()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                PopupHandle stale = harness.Show(TestKeys.A);
                harness.CloseCurrent();

                Assert.IsFalse(stale.IsLive);
                Assert.AreEqual(PopupState.None, stale.State);

                PopupHandle fresh = harness.Show(TestKeys.B);

                Assert.IsFalse(stale.Close());
                Assert.IsFalse(stale.Cancel());
                Assert.IsTrue(fresh.IsLive, "a stale handle acted on the popup that inherited its object");
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);
            }
        }

        // 20b — a hundred popups on one long-lived caller token accumulate nothing.
        [Test]
        public void Repeated_Cycles_Leave_No_Token_Source_And_No_Registration_Behind()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            using (CancellationTokenSource caller = new CancellationTokenSource())
            {
                for (int i = 0; i < 100; i++)
                {
                    harness.ShowAsync(TestKeys.A, default, caller.Token).Forget();
                    harness.CloseCurrent();

                    Assert.IsTrue(PopupRequestPool<TestData>.AllPooledAreClean(),
                        $"cycle {i}: a recycled request still holds a token source or a view");
                }

                Assert.AreEqual(100, harness.Completions.Count);
                Assert.AreEqual(0, harness.Service.LiveCount);

                int completionsBefore = harness.Completions.Count;
                caller.Cancel();

                Assert.AreEqual(completionsBefore, harness.Completions.Count,
                    "a registration from a finished popup was still attached to the caller's token");
            }
        }

        // 21 — the analytics and the policy see exactly the events they are supposed to.
        [Test]
        public void Analytics_And_Policy_See_Shown_And_Dismissed_Only()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Policy.RefusedKeys.Add("popup.b");
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B);
                harness.Show(TestKeys.A);

                Assert.AreEqual(1, harness.Analytics.ShownKeys.Count);
                Assert.AreEqual("popup.a", harness.Analytics.ShownKeys[0]);
                Assert.AreEqual(1, harness.Policy.ShownNotifications.Count);
                Assert.AreEqual("popup.a", harness.Policy.ShownNotifications[0]);

                harness.CloseCurrent("confirm");

                Assert.AreEqual(1, harness.Analytics.DismissedKeys.Count);
                Assert.AreEqual("popup.a", harness.Analytics.DismissedKeys[0]);
                Assert.AreEqual("confirm", harness.Analytics.DismissActions[0]);
                Assert.AreEqual(0, harness.Analytics.ConvertedKeys.Count);
            }
        }

        // 22 — a fire-and-forget Show takes no awaiter at all.
        [Test]
        public void Fire_And_Forget_Show_Allocates_No_Awaiter()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                Assert.IsFalse(harness.Service.CurrentRequest.HasAwaiter, "Show created a completion source");

                harness.CloseCurrent();

                harness.ShowAsync(TestKeys.B).Forget();
                Assert.IsTrue(harness.Service.CurrentRequest.HasAwaiter, "ShowAsync did not create one");
            }
        }

        // A suspended popup can be cancelled where it stands, and its close plays out behind whatever
        // interrupted it. Two popups are alive on screen for the length of that animation — one
        // interactive, one on its way out — and that is the design, not a slot violation.
        [Test]
        public void A_Suspended_Popup_Cancelled_Closes_Behind_The_Interrupter()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                PopupHandle suspended = harness.Show(TestKeys.A);
                FakeView suspendedView = harness.Factory.LastCreatedFor("popup.a");
                suspendedView.HoldClose = true;

                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume));
                Assert.IsTrue(suspendedView.IsSuspended);

                Assert.IsTrue(suspended.Cancel());

                // The interrupter keeps the slot while the suspended one animates out behind it.
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);
                Assert.AreEqual(PopupState.Closing, suspended.State);
                Assert.AreEqual(0, harness.Service.SuspendedCount, "the cancelled popup stayed on the stack");
                Assert.IsTrue(suspendedView.IsHoldingClose);

                suspendedView.ResolveClose();

                Assert.AreEqual(PopupOutcome.Cancelled, harness.CompletionFor("popup.a").Result.Outcome);
                Assert.AreEqual(1, harness.Factory.ReleaseCount);
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the interrupter was disturbed");

                harness.CloseCurrent();
                Assert.IsNull(harness.Service.CurrentKeyId, "the drained popup was resumed from the stack");
            }
        }

        // Foreign code outside the terminal — a third-party SDK that throws must cost its own call and
        // nothing else. Each of these four sites could take down something it has no business touching.

        [Test]
        public void Analytics_That_Throws_On_Dismiss_Still_Delivers_The_Terminal()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B);
                harness.Analytics.ThrowOnDismissed = true;

                harness.CloseCurrent("ok");

                Assert.AreEqual(PopupOutcome.Completed, harness.CompletionFor("popup.a").Result.Outcome,
                    "the terminal never arrived");
                Assert.AreEqual(1, harness.Factory.ReleaseCount, "the view was never released");
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the slot stayed pinned forever");
            }

            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void Policy_That_Throws_At_Admission_Does_Not_Destroy_The_Occupant()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                FakeView occupantView = harness.Factory.LastCreatedFor("popup.a");

                harness.Policy.ThrowingKeys.Add("popup.b");
                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume));

                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId,
                    "the occupant was destroyed for a third party's policy failure");
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);
                Assert.AreEqual(0, occupantView.SuspendCalls);
                Assert.AreEqual(PopupOutcome.Faulted, harness.CompletionFor("popup.b").Result.Outcome);
                Assert.AreEqual(0, harness.Service.ForceTerminateCount, "the failure was blamed on the occupant");

                // The culprit left the queue, so the pump is not going to fault on it again.
                harness.Show(TestKeys.C);
                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId);
            }

            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void Policy_That_Throws_At_Submit_Faults_Only_That_Request()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                int pendingBefore = harness.Service.PendingCount;

                harness.Policy.ThrowingKeys.Add("popup.b");
                UniTask<PopupResult> faulted = harness.ShowAsync(TestKeys.B);

                Assert.AreEqual(PopupOutcome.Faulted, Result(faulted).Outcome);
                Assert.AreEqual(pendingBefore, harness.Service.PendingCount, "a faulted request was registered");
                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId);
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);
            }

            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void Telemetry_That_Throws_On_Show_Does_Not_Destroy_The_Popup_It_Reported()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Analytics.ThrowOnShown = true;
                harness.Policy.ThrowOnNotifyShown = true;

                harness.Show(TestKeys.A);

                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId, "the popup was destroyed by its own telemetry");
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);
                Assert.AreEqual(0, harness.Service.ForceTerminateCount);

                // The close watcher started despite both throws — without it the popup could never close.
                harness.CloseCurrent("ok");
                Assert.AreEqual(PopupOutcome.Completed, harness.CompletionFor("popup.a").Result.Outcome);
                Assert.IsNull(harness.Service.CurrentKeyId);
            }

            LogAssert.ignoreFailingMessages = false;
        }

        // 22b — every terminal is observable through the event, including the ones nobody awaits.
        [Test]
        public void RequestCompleted_Fires_For_Every_Terminal()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Factory.FailNextCreate = true;
                harness.Show(TestKeys.A);

                harness.Policy.RefusedKeys.Add("popup.b");
                harness.Show(TestKeys.B);

                harness.Show(TestKeys.C);
                harness.CloseCurrent();

                Assert.AreEqual(3, harness.Completions.Count);
                Assert.AreEqual(PopupOutcome.LoadFailed, harness.CompletionFor("popup.a").Result.Outcome);
                Assert.AreEqual(PopupOutcome.Refused, harness.CompletionFor("popup.b").Result.Outcome);
                Assert.AreEqual(PopupOutcome.Completed, harness.CompletionFor("popup.c").Result.Outcome);

                for (int i = 0; i < harness.Completions.Count; i++)
                {
                    Assert.AreNotEqual(0UL, harness.Completions[i].Id);
                }

                // A handler that shows a popup gets it opened on the same pass.
                Action<PopupCompletion> reentrant = null;
                reentrant = completion =>
                {
                    harness.Service.RequestCompleted -= reentrant;
                    harness.Show(TestKeys.D);
                };

                harness.Service.RequestCompleted += reentrant;
                harness.Show(TestKeys.E);
                harness.CloseCurrent();

                Assert.AreEqual("popup.d", harness.Service.CurrentKeyId,
                    "a popup submitted from a completion handler was not opened");
            }

            LogAssert.ignoreFailingMessages = false;
        }
    }
}
