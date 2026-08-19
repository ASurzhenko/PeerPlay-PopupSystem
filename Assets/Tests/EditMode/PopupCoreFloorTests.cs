using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace PeerPlay.Popups.Tests
{
    /// <summary>
    /// The floor: the tests the deliverable is not allowed to ship without. Each one either covers a
    /// spec sentence directly or pins a defect that was found and fixed.
    /// </summary>
    internal sealed class PopupCoreFloorTests
    {
        private static PopupResult Result(UniTask<PopupResult> task)
        {
            Assert.AreEqual(UniTaskStatus.Succeeded, task.Status, "the request has not produced a terminal yet");
            return task.GetAwaiter().GetResult();
        }

        // 1 [F] — FIFO within one priority band.
        [Test]
        public void Fifo_Within_One_Priority_Opens_In_Submit_Order()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B);
                harness.Show(TestKeys.C);

                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.IsNull(harness.Service.CurrentKeyId);
            }
        }

        // 2 [F] — Critical submitted last opens first; Low opens last.
        [Test]
        public void Priority_Ordering_Beats_Submit_Order()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.Low));
                harness.Show(TestKeys.C);
                harness.Show(TestKeys.D, new ShowOptions(PopupPriority.Critical));

                harness.CloseCurrent();
                Assert.AreEqual("popup.d", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);
            }
        }

        // 4 [F] — InterruptAndResume: the interrupter opens, the occupant comes back on the same view.
        [Test]
        public void InterruptAndResume_Suspends_Opens_Interrupter_Then_Resumes_Same_View()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                FakeView occupant = harness.Factory.LastCreatedFor("popup.a");

                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume));

                Assert.IsTrue(occupant.IsSuspended, "the occupant was not suspended");
                Assert.AreEqual(1, occupant.SuspendCalls);
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the interrupter did not open");
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);

                harness.CloseCurrent();

                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId, "the occupant did not resume");
                Assert.IsFalse(occupant.IsSuspended);
                Assert.AreEqual(1, occupant.ResumeCalls);
                Assert.AreSame(occupant, harness.Factory.LastCreatedFor("popup.a"), "the occupant was rebuilt");
                Assert.AreEqual(2, harness.Factory.CreateCount);
                Assert.AreEqual(PopupState.Active, harness.Service.CurrentState);
            }
        }

        // 5 [F] — the interrupter dies before it opens; the suspended occupant must still resume.
        [Test]
        public void Interrupter_Cancelled_Before_It_Opens_Still_Resumes_The_Occupant()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                FakeView occupant = harness.Factory.LastCreatedFor("popup.a");

                harness.Factory.HoldNextCreate = true;
                PopupHandle interrupter =
                    harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume));

                Assert.IsTrue(occupant.IsSuspended);
                Assert.AreEqual(PopupState.Initializing, interrupter.State);

                Assert.IsTrue(interrupter.Cancel());

                Assert.AreEqual(PopupOutcome.Cancelled, harness.CompletionFor("popup.b").Result.Outcome);
                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId, "the occupant did not resume");
                Assert.IsFalse(occupant.IsSuspended);
                Assert.AreEqual(1, harness.Factory.LiveViewCount, "a view leaked");

                // The pump must still be usable — it did not abandon the loop.
                harness.Show(TestKeys.C);
                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId);
            }
        }

        // 7 [F] — Replace supersedes the occupant and leaves the pending queue alone.
        [Test]
        public void Replace_Supersedes_Occupant_And_Leaves_The_Queue_Untouched()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                UniTask<PopupResult> occupant = harness.ShowAsync(TestKeys.A);
                harness.Show(TestKeys.C);

                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.Replace));

                Assert.AreEqual(PopupOutcome.Superseded, Result(occupant).Outcome);
                Assert.AreEqual(1, harness.Factory.ReleaseCount, "the superseded view was not released exactly once");
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);

                harness.CloseCurrent();
                Assert.AreEqual("popup.c", harness.Service.CurrentKeyId, "the pending request was lost");
            }
        }

        // 9 [F] — an illegal transition is a loud programmer error.
        [Test]
        public void Illegal_Transition_Throws_And_Names_Both_States()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => PopupStateMachine.Validate(7UL, PopupState.Pending, PopupState.Active));

            StringAssert.Contains("Pending", error.Message);
            StringAssert.Contains("Active", error.Message);
        }

        // 11 [F] — rapid-fire on one key: one popup, nine observable duplicates. The count must not drift.
        [Test]
        public void RapidFire_Duplicates_Produce_One_View_And_Nine_Duplicate_Terminals()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                for (int i = 0; i < 10; i++)
                {
                    harness.Show(TestKeys.A);
                }

                Assert.AreEqual(1, harness.Factory.CreateCount, "more than one view was created");
                Assert.AreEqual(9, harness.CountOutcome(PopupOutcome.Duplicate), "the duplicate count drifted");
                Assert.AreEqual(1, harness.Service.PendingCount);
            }
        }

        // 13 [F] — a request that is still waiting in a band can be cancelled.
        [Test]
        public void Cancelling_A_Pending_Request_Terminates_It_Immediately()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                UniTask<PopupResult> pending = harness.ShowAsync(TestKeys.B);
                PopupHandle handle = harness.Show(TestKeys.C);

                Assert.AreEqual(3, harness.Service.PendingCount);

                Assert.IsTrue(handle.Cancel());

                Assert.AreEqual(PopupOutcome.Cancelled, harness.CompletionFor("popup.c").Result.Outcome);
                Assert.AreEqual(2, harness.Service.PendingCount);
                Assert.AreEqual(1, harness.Factory.CreateCount, "a cancelled pending request was created");
                Assert.IsFalse(handle.IsLive);

                Assert.AreEqual(UniTaskStatus.Pending, pending.Status, "the wrong request was cancelled");

                // The key is dedupe-free again, and the dead id does not block the band.
                PopupHandle reshown = harness.Show(TestKeys.C);
                Assert.IsTrue(reshown.IsLive);

                harness.CloseCurrent();
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the dead id blocked the band");
            }
        }

        // 14 [F] — the same terminal, reached through the caller's own token (the registration, not a hop).
        [Test]
        public void Cancelling_A_Pending_Request_Through_The_Caller_Token_Terminates_It()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            using (CancellationTokenSource caller = new CancellationTokenSource())
            {
                harness.Show(TestKeys.A);
                UniTask<PopupResult> pending = harness.ShowAsync(TestKeys.B, default, caller.Token);

                Assert.AreEqual(2, harness.Service.PendingCount);

                caller.Cancel();

                Assert.AreEqual(PopupOutcome.Cancelled, Result(pending).Outcome);
                Assert.AreEqual(1, harness.Service.PendingCount);
                Assert.AreEqual(1, harness.Factory.CreateCount);
            }
        }

        // 15 [F] — a failed load is an outcome, and the queue keeps moving.
        [Test]
        public void Factory_Failure_Reports_LoadFailed_And_The_Next_Request_Opens()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Factory.HoldNextCreate = true;
                UniTask<PopupResult> failing = harness.ShowAsync(TestKeys.A);
                harness.Show(TestKeys.B);

                Assert.AreEqual("popup.a", harness.Service.CurrentKeyId);

                harness.Factory.FailHeldCreate();

                PopupResult result = Result(failing);
                Assert.AreEqual(PopupOutcome.LoadFailed, result.Outcome);
                Assert.IsNotNull(result.Reason);
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the queue stalled on a load failure");
                Assert.AreEqual(0, harness.Factory.ReleaseCount, "something was released that was never created");
            }
        }

        // 16 [F] — a submit-time refusal never enters the registries.
        [Test]
        public void Policy_Refusal_At_Submit_Never_Registers_The_Request()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                int pendingBefore = harness.Service.PendingCount;

                harness.Policy.RefusedKeys.Add("popup.b");
                UniTask<PopupResult> refused = harness.ShowAsync(TestKeys.B);

                PopupResult result = Result(refused);
                Assert.AreEqual(PopupOutcome.Refused, result.Outcome);
                Assert.AreEqual("fake policy refusal", result.Reason);
                Assert.AreEqual(pendingBefore, harness.Service.PendingCount, "a refused request was registered");
                Assert.IsFalse(harness.Policy.ShownNotifications.Contains("popup.b"));

                // The key is still dedupe-free: the refusal did not take the slot in _liveByKey.
                harness.Policy.RefusedKeys.Clear();
                harness.Show(TestKeys.B);
                harness.CloseCurrent();
                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId);
            }
        }

        // 16b [F] — the kill switch: a refusal that arrives while the request waits in the queue.
        [Test]
        public void Policy_Refusal_At_Admission_Terminates_Without_Opening()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                UniTask<PopupResult> queued = harness.ShowAsync(TestKeys.B);

                harness.Policy.RefuseAll = true;
                harness.CloseCurrent();

                Assert.AreEqual(PopupOutcome.Refused, Result(queued).Outcome);
                Assert.AreEqual(1, harness.Factory.CreateCount, "the refused request was created anyway");
                Assert.IsNull(harness.Service.CurrentKeyId);
                Assert.AreEqual(0, harness.Service.PendingCount, "the queue did not advance");
            }
        }

        // 17c [F] — a foreign throw in the teardown must not pin the slot.
        [Test]
        public void Throwing_Release_And_Throwing_Completion_Handler_Still_Advance_The_Queue()
        {
            LogAssert.ignoreFailingMessages = true;

            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Factory.ThrowOnRelease = true;
                harness.Service.RequestCompleted += ThrowingHandler;

                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B);

                harness.CloseCurrent();

                Assert.AreEqual("popup.b", harness.Service.CurrentKeyId, "the slot was pinned by a foreign throw");
                Assert.AreEqual(PopupOutcome.Completed, harness.CompletionFor("popup.a").Result.Outcome);

                harness.Service.RequestCompleted -= ThrowingHandler;
            }

            LogAssert.ignoreFailingMessages = false;
        }

        private static void ThrowingHandler(PopupCompletion completion)
        {
            throw new InvalidOperationException("observer failure");
        }

        // 18 [F] — ForceCloseAll drains everything and creates nothing while draining.
        [Test]
        public void ForceCloseAll_Cancels_Everything_And_Creates_Nothing_During_The_Drain()
        {
            using (PopupTestHarness harness = new PopupTestHarness())
            {
                harness.Show(TestKeys.A);
                harness.Show(TestKeys.B, new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume));
                harness.Show(TestKeys.C);

                int createdBefore = harness.Factory.CreateCount;
                Assert.AreEqual(2, createdBefore);

                harness.Service.ForceCloseAll();

                Assert.AreEqual(createdBefore, harness.Factory.CreateCount, "a popup was created inside the drain");
                Assert.AreEqual(2, harness.Factory.ReleaseCount, "a view was not released");
                Assert.AreEqual(0, harness.Service.PendingCount);
                Assert.AreEqual(0, harness.Service.SuspendedCount);
                Assert.IsTrue(harness.Service.AllBandsEmpty);
                Assert.IsNull(harness.Service.CurrentKeyId);
                Assert.AreEqual(3, harness.CountOutcome(PopupOutcome.Cancelled));

                // Still usable afterwards.
                harness.Show(TestKeys.D);
                Assert.AreEqual("popup.d", harness.Service.CurrentKeyId);
            }
        }
    }
}
