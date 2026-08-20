using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Seams;
using UnityEngine;

namespace PeerPlay.Popups
{
    /// <summary>
    /// The pump and the single terminal path. Everything it depends on arrives through the constructor
    /// as an interface — there is no singleton, no service locator and no FindObjectOfType anywhere
    /// below this line, which is what makes the whole engine runnable in EditMode with no scene.
    ///
    /// Single-threaded by construction: UniTask continuations are driven by the PlayerLoop and the game
    /// calls Show from Update or a UI callback, so there are no locks and no concurrent collections.
    /// "Simultaneously" in the spec means interleaved across frames, not concurrently.
    /// </summary>
    public sealed class PopupService : IPopupService, IDisposable
    {
        private const int PumpFaultBudget = 2;

        private readonly IPopupViewFactory _factory;
        private readonly IPopupPolicy _policy;
        private readonly IPopupAnalytics _analytics;

        private readonly PopupQueue _queue = new PopupQueue();
        private readonly CancellationTokenSource _serviceCts = new CancellationTokenSource();
        private readonly List<ulong> _drainScratch = new List<ulong>(16);
        private readonly Action<object> _onRequestCancelled;

        private PopupRequest _current;
        private ulong _nextId = 1;
        private bool _pumping;
        private bool _draining;
        private bool _disposed;

        /// <remarks>
        /// No clock: the queue has no time-dependent behaviour. Caps and cooldowns are the policy's, and
        /// <see cref="IPopupClock"/> is injected into the policy that actually reads it — a dependency
        /// every composition root and every test would otherwise have to supply for nothing.
        /// </remarks>
        public PopupService(IPopupViewFactory factory, IPopupPolicy policy, IPopupAnalytics analytics)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));

            // Cached once: the cancellation bridge must not allocate a closure per popup.
            _onRequestCancelled = OnRequestCancelled;
        }

        public event Action<PopupCompletion> RequestCompleted;

        public int PendingCount => _queue.PendingCount;

        public PopupState CurrentState => _current == null ? PopupState.None : _current.State;

        public string CurrentKeyId => _current?.KeyId;

        // Test hooks. The suite is the evidence the queue works, and it cannot assert on state the
        // public surface deliberately does not expose.
        internal int LiveCount => _queue.LiveCount;
        internal int LiveKeyCount => _queue.LiveKeyCount;
        internal int SuspendedCount => _queue.SuspendedCount;
        internal bool AllBandsEmpty => _queue.AllBandsEmpty;
        internal IReadOnlyList<PopupRequest> SuspendStack => _queue.SuspendStack;
        internal Dictionary<ulong, PopupRequest>.ValueCollection LiveRequests => _queue.LiveRequests;
        internal PopupRequest CurrentRequest => _current;
        internal int ForceTerminateCount { get; private set; }
        internal int PumpLaps { get; private set; }

        public PopupHandle Show<TData>(in PopupKey<TData> key, in TData data, in ShowOptions options = default)
        {
            ulong id = Submit(key, data, options, CancellationToken.None, false, out _);
            return id == 0UL ? default : new PopupHandle(this, id);
        }

        public UniTask<PopupResult> ShowAsync<TData>(in PopupKey<TData> key, in TData data,
                                                     in ShowOptions options = default,
                                                     CancellationToken cancellationToken = default)
        {
            Submit(key, data, options, cancellationToken, true, out UniTask<PopupResult> task);
            return task;
        }

        public bool CloseTop(string action = null)
        {
            if (_current == null)
            {
                return false;
            }

            if (_current.State != PopupState.Active || _current.View == null)
            {
                PopupLog.Trace($"{nameof(PopupService)}.{nameof(CloseTop)} refused: key={_current.KeyId} state={_current.State}");
                return false;
            }

            _current.View.RequestClose(action);
            return true;
        }

        public void ForceCloseAll()
        {
            if (_disposed)
            {
                return;
            }

            Drain();
            TryPump();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Drain();

            _serviceCts.Cancel();
            _serviceCts.Dispose();
        }

        // ---------------------------------------------------------------- submit

        private ulong Submit<TData>(in PopupKey<TData> key, in TData data, in ShowOptions options,
                                    CancellationToken cancellationToken, bool wantsResult,
                                    out UniTask<PopupResult> task)
        {
            task = default;

            PopupRequest<TData> request = PopupRequestPool<TData>.Rent();
            ulong id = _nextId++;
            request.Initialize(id, key, data, options);

            if (wantsResult)
            {
                task = request.CreateAwaiter();
            }

            if (_disposed)
            {
                Debug.LogWarning($"{nameof(PopupService)}.{nameof(Submit)} key={key.Id} rejected: the service is disposed");
                RejectBeforeRegistration(request, PopupOutcome.Cancelled, "the popup service is disposed");
                return 0UL;
            }

            if (options.Duplicates == PopupDuplicatePolicy.Reject && _queue.IsKeyLive(key.Id))
            {
                RejectBeforeRegistration(request, PopupOutcome.Duplicate, "a request with the same key is already live");
                return 0UL;
            }

            PopupDecision decision;

            try
            {
                decision = _policy.Evaluate(request.Info);
            }
            catch (Exception e)
            {
                // Show() is called from game code and must not throw back into it, and the request object
                // is already rented — an escape here would lose it instead of recycling it.
                Debug.LogException(e);
                RejectBeforeRegistration(request, PopupOutcome.Faulted, e.Message);
                return 0UL;
            }

            if (!decision.Allowed)
            {
                RejectBeforeRegistration(request, PopupOutcome.Refused, decision.Reason);
                return 0UL;
            }

            // Registration happens BEFORE the cancellation bridge is armed: a caller token that is
            // already cancelled fires the callback synchronously, and it must find a request the
            // terminal path can unregister rather than a half-built one.
            _queue.Register(request);
            PopupStateMachine.Transition(request, PopupState.Pending);
            _queue.Enqueue(request);

            request.Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _serviceCts.Token);
            request.Token = request.Cts.Token;
            request.CancelRegistration =
                request.Token.RegisterWithoutCaptureExecutionContext(_onRequestCancelled, request);

            TryPump();
            return id;
        }

        /// <summary>
        /// A rejection that happened before the request entered any registry: it delivers the terminal
        /// and recycles, and it must not run the teardown steps that unwind things it never acquired.
        /// </summary>
        private void RejectBeforeRegistration(PopupRequest request, PopupOutcome outcome, string reason)
        {
            request.TryComplete(outcome, default, reason);
            PopupStateMachine.Transition(request, PopupState.Disposed);

            TerminalContext context = new TerminalContext(this, request, request.Result,
                new PopupCompletion(request.Id, request.KeyId, request.Result));

            Guard(StepComplete, context);
            Guard(StepRecycle, context);
            Guard(StepRaiseCompleted, context);
        }

        private void OnRequestCancelled(object state)
        {
            PopupRequest request = (PopupRequest)state;
            FinishAsync(request, PopupOutcome.Cancelled, default, null).Forget();
        }

        // ---------------------------------------------------------------- handle operations

        internal bool IsLive(ulong id)
        {
            return _queue.TryGet(id, out _);
        }

        internal PopupState GetState(ulong id)
        {
            return _queue.TryGet(id, out PopupRequest request) ? request.State : PopupState.None;
        }

        internal bool TryCloseFromHandle(ulong id, string action)
        {
            if (!_queue.TryGet(id, out PopupRequest request))
            {
                PopupLog.Trace($"{nameof(PopupService)}.{nameof(TryCloseFromHandle)} stale handle id={id}");
                return false;
            }

            if (request.State != PopupState.Active || request.View == null)
            {
                PopupLog.Trace($"{nameof(PopupService)}.{nameof(TryCloseFromHandle)} id={id} is {request.State}, not on screen");
                return false;
            }

            request.View.RequestClose(action);
            return true;
        }

        internal bool TryCancelFromHandle(ulong id)
        {
            if (!_queue.TryGet(id, out PopupRequest request))
            {
                PopupLog.Trace($"{nameof(PopupService)}.{nameof(TryCancelFromHandle)} stale handle id={id}");
                return false;
            }

            FinishAsync(request, PopupOutcome.Cancelled, default, null).Forget();
            return true;
        }

        // ---------------------------------------------------------------- the pump

        private void TryPump()
        {
            if (_pumping || _draining || _disposed)
            {
                return;
            }

            PumpAsync().Forget();
        }

        /// <summary>
        /// One lap either admits a request into the slot or stops. The two shapes that make it correct:
        /// admission (policy included) runs BEFORE anything is vacated, so nothing live is destroyed for
        /// a request that will then be refused; and once the slot has been vacated, which request fills
        /// it is decided again from the bands — never from the suspend stack, because resuming the popup
        /// just suspended is the one move that would spin forever.
        /// </summary>
        private async UniTaskVoid PumpAsync()
        {
            _pumping = true;
            int faults = 0;

            try
            {
                while (true)
                {
                    PumpLaps++;

                    try
                    {
                        if (_current != null)
                        {
                            PopupRequest head = _queue.PeekHead();
                            if (head == null)
                            {
                                break;
                            }

                            if (head.Sequencing == PopupSequencing.Queue)
                            {
                                break;
                            }

                            if (head.Priority < _current.Priority)
                            {
                                WarnPreemptionRefused(head, _current);
                                break;
                            }

                            if (_current.State != PopupState.Active)
                            {
                                break;
                            }

                            if (!TryAdmit(head))
                            {
                                continue;
                            }

                            if (head.Sequencing == PopupSequencing.Replace)
                            {
                                await FinishAsync(_current, PopupOutcome.Superseded, default, null);
                            }
                            else
                            {
                                SuspendCurrent();
                            }

                            PopupRequest next = _queue.DequeueHighest();
                            if (next != null && TryAdmit(next))
                            {
                                await OpenAsync(next);
                            }

                            continue;
                        }

                        if (_queue.SuspendedCount > 0)
                        {
                            ResumeTop();
                            continue;
                        }

                        PopupRequest pending = _queue.PeekHead();
                        if (pending == null)
                        {
                            break;
                        }

                        if (!TryAdmit(pending))
                        {
                            continue;
                        }

                        await OpenAsync(_queue.DequeueHighest());
                    }
                    catch (Exception e)
                    {
                        // The state machine throws by design, and a view is foreign code. Either way the
                        // lap must remove work from the system or give up loudly — never abandon a
                        // populated queue silently.
                        Debug.LogException(e);

                        if (_current != null && ForceTerminate(_current, PopupOutcome.Faulted, e.Message))
                        {
                            continue;
                        }

                        if (++faults <= PumpFaultBudget)
                        {
                            continue;
                        }

                        Debug.LogError($"{nameof(PopupService)}.{nameof(PumpAsync)} aborted after {faults} unattributable faults");
                        break;
                    }
                }
            }
            finally
            {
                _pumping = false;
            }
        }

        private bool TryAdmit(PopupRequest request)
        {
            PopupDecision decision;

            try
            {
                decision = _policy.Evaluate(request.Info);
            }
            catch (Exception e)
            {
                // A policy failure belongs to the request it was asked about. Letting it escape into the
                // pump's catch would blame it on whatever popup happens to hold the slot — destroying a
                // live popup over a third party's request, and leaving the real culprit in the queue to
                // fault the pump again on the next lap.
                Debug.LogException(e);
                FinishAsync(request, PopupOutcome.Faulted, default, e.Message).Forget();
                return false;
            }

            if (decision.Allowed)
            {
                return true;
            }

            // A refused request leaves the live map, so the next scan purges its id and the loop makes
            // progress even though nothing opened.
            FinishAsync(request, PopupOutcome.Refused, default, decision.Reason).Forget();
            return false;
        }

        private void SuspendCurrent()
        {
            PopupRequest request = _current;
            PopupStateMachine.Transition(request, PopupState.Suspended);
            _queue.PushSuspended(request);

            // _current is cleared only after the view has been told, so a throw here is still
            // attributable to this request and the pump's catch can terminate it.
            request.View.SetSuspended(true);
            _current = null;
        }

        private void ResumeTop()
        {
            PopupRequest request = _queue.PopSuspended();
            if (request == null)
            {
                return;
            }

            _current = request;
            PopupStateMachine.Transition(request, PopupState.Active);
            request.View.SetSuspended(false);

            // The close watcher was never cancelled — suspension does not end the wait — so it is still
            // in flight and must not be started twice.
        }

        private void WarnPreemptionRefused(PopupRequest head, PopupRequest occupant)
        {
            if (head.PreemptionWarned)
            {
                return;
            }

            head.PreemptionWarned = true;
            Debug.LogWarning(
                $"{nameof(PopupService)}.{nameof(PumpAsync)} [Preempt] key={head.KeyId} ({head.Priority}) cannot preempt " +
                $"key={occupant.KeyId} ({occupant.Priority}); it waits its turn with {head.Sequencing} intact");
        }

        // ---------------------------------------------------------------- opening

        private async UniTask OpenAsync(PopupRequest request)
        {
            if (request == null)
            {
                return;
            }

            ulong id = request.Id;
            _current = request;
            PopupStateMachine.Transition(request, PopupState.Initializing);

            IPopupView view;
            try
            {
                view = await request.CreateViewAsync(_factory, request.Token);
            }
            catch (OperationCanceledException)
            {
                if (!IsStale(request, id))
                {
                    await FinishAsync(request, PopupOutcome.Cancelled, default, null);
                }

                return;
            }
            catch (Exception e)
            {
                if (!IsStale(request, id))
                {
                    Debug.LogWarning($"{nameof(PopupService)}.{nameof(OpenAsync)} [Load] key={request.KeyId} failed: {e.Message}");
                    await FinishAsync(request, PopupOutcome.LoadFailed, default, e.Message);
                }

                return;
            }

            if (view == null)
            {
                if (!IsStale(request, id))
                {
                    Debug.LogWarning($"{nameof(PopupService)}.{nameof(OpenAsync)} [Load] key={request.KeyId} produced no view");
                    await FinishAsync(request, PopupOutcome.LoadFailed, default, "the view factory produced no view");
                }

                return;
            }

            if (!TryAdoptView(request, id, view))
            {
                return;
            }

            PopupStateMachine.Transition(request, PopupState.Opening);

            try
            {
                await request.View.PlayOpenAsync(request.Token);
            }
            catch (OperationCanceledException)
            {
                if (!IsStale(request, id))
                {
                    await FinishAsync(request, PopupOutcome.Cancelled, default, null);
                }

                return;
            }

            if (IsStale(request, id) || request.IsCompleted)
            {
                return;
            }

            PopupStateMachine.Transition(request, PopupState.Active);

            // The popup is on screen by now, so neither of these may cost it its life: a telemetry
            // failure that escaped here would reach the pump's catch, be blamed on the occupant, and
            // destroy the popup that had just opened correctly — before its close watcher even started,
            // which is the one thing that lets it ever be closed.
            try
            {
                _analytics.Shown(request.KeyId);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            try
            {
                _policy.NotifyShown(request.Info);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            WatchCloseAsync(request).Forget();
        }

        /// <summary>
        /// The factory answered, but the request may have died while it worked. If it did, this
        /// instance is one nobody else can release — so release it here rather than leak it.
        /// </summary>
        private bool TryAdoptView(PopupRequest request, ulong id, IPopupView view)
        {
            if (!IsStale(request, id) && !request.IsCompleted)
            {
                request.View = view;
                return true;
            }

            PopupLog.Trace($"{nameof(PopupService)}.{nameof(TryAdoptView)} id={id} died while loading; releasing the view");
            ReleaseView(view);
            return false;
        }

        /// <summary>
        /// The request object was recycled and rented out again, so it now belongs to somebody else and
        /// this continuation must not touch it.
        /// </summary>
        private static bool IsStale(PopupRequest request, ulong id)
        {
            return request.Id != id;
        }

        /// <summary>
        /// The pump does not hold the slot open waiting for a user: the wait lives here, and every way
        /// it can end routes into the one terminal path.
        /// </summary>
        private async UniTaskVoid WatchCloseAsync(PopupRequest request)
        {
            ulong id = request.Id;
            PopupCloseInfo info;

            try
            {
                info = await request.View.WaitForCloseAsync(request.Token);
            }
            catch (OperationCanceledException)
            {
                if (!IsStale(request, id))
                {
                    await FinishAsync(request, PopupOutcome.Cancelled, default, null);
                }

                return;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (!IsStale(request, id))
                {
                    await FinishAsync(request, PopupOutcome.Faulted, default, e.Message);
                }

                return;
            }

            if (IsStale(request, id))
            {
                return;
            }

            // Foreign code, and the only thing standing between this popup and its terminal. An
            // analytics SDK that throws here would leave the request without a terminal at all: the slot
            // never frees, the caller never resumes, and the exception escapes into an unobserved-task
            // handler where nothing can act on it. Every other failure has a way out; this one had none.
            try
            {
                _analytics.Dismissed(request.KeyId, info.Action);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            await FinishAsync(request, PopupOutcome.Completed, info, null);
        }

        // ---------------------------------------------------------------- the one terminal path

        /// <summary>
        /// Every terminal — user close, cancellation, refusal, load failure, supersede, drain — arrives
        /// here, and it is idempotent.
        ///
        /// Three of these steps call code this assembly does not own: cancelling the token runs the view
        /// layer's registrations, Release is the view layer's, and completing the awaiter resumes the
        /// calling game code <b>inline</b>. So each is guarded individually, the service is made
        /// consistent (steps 4 and 5) before any of them runs, and the re-pump in step 12 is
        /// unconditional — without that, one throw from foreign code pins the slot permanently.
        /// </summary>
        private async UniTask FinishAsync(PopupRequest request, PopupOutcome outcome, PopupCloseInfo closeInfo, string reason)
        {
            // 1. The latch: whoever gets here first decides the outcome.
            if (!request.TryComplete(outcome, closeInfo, reason))
            {
                return;
            }

            // A latched request is not resumable, so it leaves the suspend stack with the latch rather
            // than at step 4. A close transition takes time; leaving the entry there for its duration
            // lets the pump pop a request that is already terminating and drive it Closing -> Active,
            // which is not an edge the table allows.
            _queue.RemoveSuspended(request);

            try
            {
                // 2. The close transition, only for a request that actually opened. A view that was
                //    created but never opened has nothing to animate out, and asking it to would attempt
                //    a transition the state machine deliberately rejects.
                if (request.View != null && !_disposed
                    && (request.State == PopupState.Opening
                        || request.State == PopupState.Active
                        || request.State == PopupState.Suspended))
                {
                    PopupStateMachine.Transition(request, PopupState.Closing);

                    try
                    {
                        await request.View.PlayCloseAsync(request.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // A cancelled close is a skipped animation, never a skipped release.
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                PopupResult result = request.Result;
                TerminalContext context = new TerminalContext(this, request, result,
                    new PopupCompletion(request.Id, request.KeyId, result));

                Guard(StepDisposeRegistration, context);   // 3. ours first: it must not re-enter this teardown
                Unregister(request);                        // 4. live map, key index, slot, suspend stack
                MarkDisposed(request);                      // 5. the service is consistent from here on
                Guard(StepCancelCts, context);              // 6. foreign: the view layer's token callbacks
                Guard(StepRelease, context);                // 7. foreign: pool / asset handle
                Guard(StepDisposeCts, context);             // 8. after the release; nothing holds the token now
                Guard(StepComplete, context);               // 9. foreign: resumes the caller inline
                Guard(StepRecycle, context);                // 10.
                Guard(StepRaiseCompleted, context);         // 11. foreign: observers
            }
            finally
            {
                // 12. The queue advances even if every guarded step above threw.
                TryPump();
            }
        }

        /// <summary>
        /// The recovery path for a request whose state machine already threw: the terminal steps without
        /// the close transition (it must not be asked to animate) and without the re-pump (the pump's
        /// catch continues the loop itself). It is the one place that assigns Disposed directly, because
        /// routing it back through the validator would throw again inside the catch.
        /// </summary>
        private bool ForceTerminate(PopupRequest request, PopupOutcome outcome, string reason)
        {
            if (!request.TryComplete(outcome, default, reason))
            {
                return false;
            }

            ForceTerminateCount++;
            Debug.LogError($"{nameof(PopupService)}.{nameof(ForceTerminate)} id={request.Id} key={request.KeyId} outcome={outcome} reason={reason}");

            PopupResult result = request.Result;
            TerminalContext context = new TerminalContext(this, request, result,
                new PopupCompletion(request.Id, request.KeyId, result));

            Guard(StepDisposeRegistration, context);
            Guard(StepUnregister, context);
            request.State = PopupState.Disposed;
            Guard(StepCancelCts, context);
            Guard(StepRelease, context);
            Guard(StepDisposeCts, context);
            Guard(StepComplete, context);
            Guard(StepRecycle, context);
            Guard(StepRaiseCompleted, context);
            return true;
        }

        private void Unregister(PopupRequest request)
        {
            if (ReferenceEquals(_current, request))
            {
                _current = null;
            }

            _queue.RemoveSuspended(request);
            _queue.Unregister(request);
        }

        private static void MarkDisposed(PopupRequest request)
        {
            try
            {
                PopupStateMachine.Transition(request, PopupState.Disposed);
            }
            catch (Exception e)
            {
                // Every reachable state has a legal edge to Disposed, so this cannot fire on a correct
                // core — but the service must end up consistent whatever happened above it.
                Debug.LogException(e);
                request.State = PopupState.Disposed;
            }
        }

        private void ReleaseViewOnce(PopupRequest request)
        {
            if (request.View == null || !request.TryMarkViewReleased())
            {
                return;
            }

            IPopupView view = request.View;
            request.View = null;
            _factory.Release(view);
        }

        private void ReleaseView(IPopupView view)
        {
            if (view == null)
            {
                return;
            }

            try
            {
                _factory.Release(view);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void RaiseCompleted(in PopupCompletion completion)
        {
            RequestCompleted?.Invoke(completion);
        }

        /// <summary>
        /// Cancels every live request with the pump suppressed for the whole sweep, so no popup is
        /// created in the middle of a teardown. One re-pump at the end then genuinely finds nothing.
        /// </summary>
        private void Drain()
        {
            _draining = true;

            try
            {
                _drainScratch.Clear();
                _queue.CopyLiveIds(_drainScratch);

                for (int i = 0; i < _drainScratch.Count; i++)
                {
                    if (_queue.TryGet(_drainScratch[i], out PopupRequest request))
                    {
                        FinishAsync(request, PopupOutcome.Cancelled, default, null).Forget();
                    }
                }

                _queue.ClearSuspended();
                _queue.ClearBands();

                // _current is NOT cleared here. A close transition that is still running keeps the slot
                // until its own teardown reaches step 4; clearing it early lets the next Show open a
                // second popup while the drained one is still animating out.
            }
            finally
            {
                _draining = false;
            }
        }

        // ---------------------------------------------------------------- guarded terminal steps

        private readonly struct TerminalContext
        {
            internal readonly PopupService Service;
            internal readonly PopupRequest Request;
            internal readonly PopupResult Result;
            internal readonly PopupCompletion Completion;

            internal TerminalContext(PopupService service, PopupRequest request, in PopupResult result,
                                     in PopupCompletion completion)
            {
                Service = service;
                Request = request;
                Result = result;
                Completion = completion;
            }
        }

        // Static so that a terminal allocates no delegate and no closure.
        private static readonly Action<TerminalContext> StepDisposeRegistration =
            static context => context.Request.CancelRegistration.Dispose();

        private static readonly Action<TerminalContext> StepUnregister =
            static context => context.Service.Unregister(context.Request);

        private static readonly Action<TerminalContext> StepCancelCts =
            static context => context.Request.Cts?.Cancel();

        private static readonly Action<TerminalContext> StepRelease =
            static context => context.Service.ReleaseViewOnce(context.Request);

        private static readonly Action<TerminalContext> StepDisposeCts =
            static context =>
            {
                context.Request.Cts?.Dispose();
                context.Request.Cts = null;
                context.Request.CancelRegistration = default;
            };

        private static readonly Action<TerminalContext> StepComplete =
            static context => context.Request.Complete(context.Result);

        private static readonly Action<TerminalContext> StepRecycle =
            static context => context.Request.Recycle();

        private static readonly Action<TerminalContext> StepRaiseCompleted =
            static context => context.Service.RaiseCompleted(context.Completion);

        private static void Guard(Action<TerminalContext> step, in TerminalContext context)
        {
            try
            {
                step(context);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
