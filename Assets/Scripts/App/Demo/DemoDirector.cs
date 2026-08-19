using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.View;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// One handler per button, and the request ledger the panel draws. No beat registry and no descriptor
    /// assets: a demo of a UI framework that needs its own UI framework has answered the wrong question.
    /// </summary>
    public sealed class DemoDirector : MonoBehaviour
    {
        private const float WaitForActiveSeconds = 5f;

        [SerializeField] private PopupSystemInstaller _installer;
        [SerializeField] private DemoConfigPublisher _publisher;

        [Header("Sequencing")]
        [SerializeField] private Button _queueThreeButton;
        [SerializeField] private Button _interruptHideButton;
        [SerializeField] private Button _interruptStayVisibleButton;
        [SerializeField] private Button _replaceButton;
        [SerializeField] private Button _rapidFireButton;

        [Header("Sourcing")]
        [SerializeField] private Button _infoButton;
        [SerializeField] private Button _confirmButton;
        [SerializeField] private Button _rewardButton;
        [SerializeField] private Button _offerButton;
        [SerializeField] private Button _modelessButton;
        [SerializeField] private Button _blockingButton;

        [Header("Teardown and caps")]
        [SerializeField] private Button _killWhileWaitingButton;
        [SerializeField] private Button _cappedButton;
        [SerializeField] private Button _forceCloseButton;
        [SerializeField] private Button _releasePoolButton;

        private readonly DemoRequestLedger _ledger = new DemoRequestLedger();

        private Action<PopupCompletion> _onRequestCompleted;
        private bool _beatRunning;

        internal DemoRequestLedger Ledger => _ledger;

        private IPopupService Service => _installer.Service;

        /// <remarks>
        /// Start, not OnEnable: the service is constructed in PopupSystemInstaller.Awake and Unity gives no
        /// cross-object ordering guarantee between one component's Awake and another's OnEnable. Start runs
        /// after every Awake, and it still runs before any button can be pressed — which is what makes a
        /// terminal raised INSIDE Show observable.
        /// </remarks>
        private void Start()
        {
            _onRequestCompleted = OnRequestCompleted;
            Service.RequestCompleted += _onRequestCompleted;

            Wire(_queueThreeButton, BeatQueueThreeMixedPriorities);
            Wire(_interruptHideButton, BeatInterruptAndResumeHide);
            Wire(_interruptStayVisibleButton, BeatInterruptAndResumeStayVisible);
            Wire(_replaceButton, BeatReplace);
            Wire(_rapidFireButton, BeatRapidFire);

            Wire(_infoButton, BeatLocalInfo);
            Wire(_confirmButton, BeatLocalConfirm);
            Wire(_rewardButton, BeatLocalReward);
            Wire(_offerButton, BeatRemoteOffer);
            Wire(_modelessButton, BeatModeless);
            Wire(_blockingButton, BeatBlocking);

            Wire(_killWhileWaitingButton, BeatKillWhileItWaits);
            Wire(_cappedButton, BeatCapped);
            Wire(_forceCloseButton, BeatForceCloseAll);
            Wire(_releasePoolButton, BeatReleasePool);
        }

        private void OnDestroy()
        {
            if (_installer != null && Service != null && _onRequestCompleted != null)
            {
                Service.RequestCompleted -= _onRequestCompleted;
            }

            Unwire(_queueThreeButton, BeatQueueThreeMixedPriorities);
            Unwire(_interruptHideButton, BeatInterruptAndResumeHide);
            Unwire(_interruptStayVisibleButton, BeatInterruptAndResumeStayVisible);
            Unwire(_replaceButton, BeatReplace);
            Unwire(_rapidFireButton, BeatRapidFire);

            Unwire(_infoButton, BeatLocalInfo);
            Unwire(_confirmButton, BeatLocalConfirm);
            Unwire(_rewardButton, BeatLocalReward);
            Unwire(_offerButton, BeatRemoteOffer);
            Unwire(_modelessButton, BeatModeless);
            Unwire(_blockingButton, BeatBlocking);

            Unwire(_killWhileWaitingButton, BeatKillWhileItWaits);
            Unwire(_cappedButton, BeatCapped);
            Unwire(_forceCloseButton, BeatForceCloseAll);
            Unwire(_releasePoolButton, BeatReleasePool);
        }

        // ---------------------------------------------------------------- B1 - B5, sequencing

        /// <summary>
        /// The gate is the beat, not scenery. Submit ends with a synchronous TryPump and OpenAsync assigns
        /// the slot before its first await, so a submit into an EMPTY slot opens immediately — the
        /// first-submitted popup would win and the button would disprove its own caption. With the slot
        /// occupied the pump breaks on a Queue head, so all three wait and the bands decide the order.
        /// </summary>
        private void BeatQueueThreeMixedPriorities()
        {
            RunBeatAsync(QueueThreeAsync).Forget();
        }

        private async UniTask QueueThreeAsync(CancellationToken ct)
        {
            PopupHandle gate = Submit(PopupKeys.Info,
                                      new InfoData("Close me — three popups are waiting behind this one."),
                                      default, "gate");

            if (!await WaitForActiveAsync(gate, ct))
            {
                _ledger.Note("queue beat aborted: the gate never opened");
                return;
            }

            // Submission order Low, Normal, Critical. The panel lists them in the order they will PLAY.
            Submit(DemoKeys.QueueLow, new InfoData("submitted 1st"),
                   new ShowOptions(PopupPriority.Low, PopupSequencing.Queue), "submitted 1st");
            Submit(DemoKeys.QueueNormal, new InfoData("submitted 2nd"),
                   new ShowOptions(PopupPriority.Normal, PopupSequencing.Queue), "submitted 2nd");
            Submit(DemoKeys.QueueCritical, new InfoData("submitted 3rd"),
                   new ShowOptions(PopupPriority.Critical, PopupSequencing.Queue), "submitted 3rd");
        }

        private void BeatInterruptAndResumeHide()
        {
            RunBeatAsync(ct => InterruptAsync(PopupKeys.Confirm,
                                              new ConfirmData("I get hidden, then resumed — same instance."),
                                              ct)).Forget();
        }

        /// <summary>
        /// Same queue path as the Hide beat; only the interrupted popup's suspend mode differs, and that is
        /// a view-level flag. The offer's is StayVisible in the demo's config.
        /// </summary>
        private void BeatInterruptAndResumeStayVisible()
        {
            RunBeatAsync(ct => InterruptAsync(PopupKeys.Offer,
                                              new OfferData("weekend", "$4.99", 40),
                                              ct)).Forget();
        }

        private async UniTask InterruptAsync<TData>(PopupKey<TData> key, TData data, CancellationToken ct)
        {
            PopupHandle occupant = Submit(key, data, default, "gets interrupted");

            if (!await WaitForActiveAsync(occupant, ct))
            {
                _ledger.Note($"interrupt beat aborted: '{key.Id}' never opened");
                return;
            }

            Submit(DemoKeys.QueueCritical, new InfoData("the interrupter — close me to resume the popup behind"),
                   new ShowOptions(PopupPriority.Critical, PopupSequencing.InterruptAndResume), "interrupter");
        }

        private void BeatReplace()
        {
            RunBeatAsync(ReplaceAsync).Forget();
        }

        private async UniTask ReplaceAsync(CancellationToken ct)
        {
            PopupHandle occupant = Submit(PopupKeys.Info, new InfoData("I am about to be superseded."),
                                          default, "gets replaced");

            if (!await WaitForActiveAsync(occupant, ct))
            {
                _ledger.Note("replace beat aborted: nothing opened to replace");
                return;
            }

            Submit(DemoKeys.QueueCritical, new InfoData("Replace — nothing resumes behind me."),
                   new ShowOptions(PopupPriority.Critical, PopupSequencing.Replace), "replaces");
        }

        /// <summary>
        /// Ten requests in one frame: four keys and six deliberate duplicates. The duplicates are rejected
        /// before registration, so they never become rows — they land in the terminal log inside this very
        /// frame, each naming its reason. ShowOptions.Duplicates defaults to Reject, so this is the default
        /// behaviour rather than something configured for the demo.
        /// </summary>
        private void BeatRapidFire()
        {
            Submit(PopupKeys.Info, new InfoData("rapid-fire 1"), default, "rapid-fire");
            Submit(PopupKeys.Info, new InfoData("rapid-fire dup"), default, "duplicate");
            Submit(PopupKeys.Confirm, new ConfirmData("rapid-fire 2"),
                   new ShowOptions(PopupPriority.Low), "rapid-fire");
            Submit(PopupKeys.Confirm, new ConfirmData("rapid-fire dup"), default, "duplicate");
            Submit(PopupKeys.Reward, new RewardData(250, "coins"),
                   new ShowOptions(PopupPriority.High), "rapid-fire");
            Submit(PopupKeys.Reward, new RewardData(250, "coins"), default, "duplicate");
            Submit(DemoKeys.QueueCritical, new InfoData("rapid-fire 4"),
                   new ShowOptions(PopupPriority.Critical), "rapid-fire");
            Submit(DemoKeys.QueueCritical, new InfoData("rapid-fire dup"), default, "duplicate");
            Submit(PopupKeys.Info, new InfoData("rapid-fire dup"), default, "duplicate");
            Submit(PopupKeys.Confirm, new ConfirmData("rapid-fire dup"), default, "duplicate");
        }

        // ---------------------------------------------------------------- B6, B7, B20, B16

        private void BeatLocalInfo()
        {
            Submit(PopupKeys.Info, new InfoData("Local prefab, no network on this path."), default, "local");
        }

        private void BeatLocalConfirm()
        {
            Submit(PopupKeys.Confirm, new ConfirmData("ConfirmData carried this line."), default, "local");
        }

        private void BeatLocalReward()
        {
            Submit(PopupKeys.Reward, new RewardData(1200, "coins"), default, "local");
        }

        /// <summary>
        /// The popup opens on the local prefab immediately and the CloudFront image lands into it later:
        /// the request is never blocked on the network, which is the claim.
        /// </summary>
        private void BeatRemoteOffer()
        {
            Submit(PopupKeys.Offer, new OfferData("weekend", "$4.99", 40), default, "remote content");
        }

        private void BeatModeless()
        {
            Submit(DemoKeys.Modeless, new InfoData("Modeless — the buttons behind me still work."),
                   default, "modeless");
        }

        private void BeatBlocking()
        {
            Submit(DemoKeys.Blocking, new InfoData("Non-dismissible: Escape and the backdrop both refuse."),
                   default, "non-dismissible");
        }

        /// <summary>
        /// The config is consulted at submit AND again at admission, and this is where the difference is
        /// visible. Step 3 publishes the kill switch and NOTHING happens on screen: nothing pumps on an
        /// adoption, and while the slot is occupied the pump breaks on a Queue head before it ever asks the
        /// policy. The queued row terminates Refused — without ever opening — at step 4, when the reviewer
        /// closes the blocker and the queue advances.
        /// </summary>
        private void BeatKillWhileItWaits()
        {
            RunBeatAsync(KillWhileItWaitsAsync).Forget();
        }

        private async UniTask KillWhileItWaitsAsync(CancellationToken ct)
        {
            PopupHandle blocker = Submit(PopupKeys.Confirm,
                                         new ConfirmData("Blocker. Close me and watch the queued row."),
                                         default, "blocker");

            if (!await WaitForActiveAsync(blocker, ct))
            {
                _ledger.Note("kill-while-waiting aborted: the blocker never opened");
                return;
            }

            Submit(PopupKeys.Info, new InfoData("Queued behind the blocker."), default,
                   "queued behind the blocker");

            _publisher.Publish("killswitch");
            _ledger.Note("kill switch published — nothing changes until the blocker closes");
        }

        private void BeatCapped()
        {
            Submit(DemoKeys.Capped, new InfoData("A real maxPerSession of 1. Press again."), default, "capped");
        }

        private void BeatForceCloseAll()
        {
            Service.ForceCloseAll();
        }

        /// <summary>
        /// An idle pooled instance keeps holding its prefab refcount by design, so the counters only reach
        /// zero when the pool is dropped. That is what makes the pooling claim checkable instead of
        /// decorative.
        ///
        /// It refuses while anything is still live, and says so. Dropping the pool under a live popup loses
        /// the key-to-asset mapping that instance's refcount is released through, so the count would be
        /// stranded for the session — the composition root avoids it by closing everything before it clears
        /// (see PopupSystemInstaller.OnDestroy), and a button the reviewer can press at any moment has to do
        /// the same.
        /// </summary>
        private void BeatReleasePool()
        {
            if (Service.PendingCount > 0)
            {
                _ledger.Note($"release refused: {Service.PendingCount} request(s) still live — force close first");
                return;
            }

            _installer.Factory.ClearPool();
            _ledger.Note("pool released — prefab refcounts drop to 0");
        }

        // ---------------------------------------------------------------- plumbing

        internal PopupHandle Submit<TData>(in PopupKey<TData> key, in TData data,
                                           in ShowOptions options = default, string note = null)
        {
            PopupHandle handle = Service.Show(key, data, options);
            _ledger.Track(handle, key.Id, options.Priority, options.Sequencing, note);
            return handle;
        }

        /// <summary>Bounded: a beat that waits forever on a popup that was refused is a frozen demo.</summary>
        private async UniTask<bool> WaitForActiveAsync(PopupHandle handle, CancellationToken ct)
        {
            float deadline = Time.realtimeSinceStartup + WaitForActiveSeconds;

            while (handle.State != PopupState.Active)
            {
                if (!handle.IsLive || Time.realtimeSinceStartup > deadline)
                {
                    return false;
                }

                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            return true;
        }

        /// <summary>
        /// One multi-step beat at a time, and cancelled with the object: every one of them can be sitting in
        /// a wait when Play mode exits.
        /// </summary>
        private async UniTaskVoid RunBeatAsync(Func<CancellationToken, UniTask> beat)
        {
            if (_beatRunning)
            {
                _ledger.Note("a scripted beat is already running");
                return;
            }

            _beatRunning = true;

            try
            {
                await beat(this.GetCancellationTokenOnDestroy());
            }
            catch (OperationCanceledException)
            {
                // Play mode ended under the beat. Nothing to report and nothing to clean up.
            }
            finally
            {
                _beatRunning = false;
            }
        }

        private void OnRequestCompleted(PopupCompletion completion)
        {
            _ledger.OnCompleted(completion);
        }

        private static void Wire(Button button, UnityEngine.Events.UnityAction handler)
        {
            if (button != null)
            {
                button.onClick.AddListener(handler);
            }
        }

        private static void Unwire(Button button, UnityEngine.Events.UnityAction handler)
        {
            if (button != null)
            {
                button.onClick.RemoveListener(handler);
            }
        }
    }
}
