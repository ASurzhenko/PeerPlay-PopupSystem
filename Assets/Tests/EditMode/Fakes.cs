using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups;
using PeerPlay.Popups.Seams;

namespace PeerPlay.Popups.Tests
{
    /// <summary>
    /// Payload used by every test popup. A struct, so the typed pipeline is exercised without boxing.
    /// </summary>
    internal readonly struct TestData
    {
        internal readonly string Text;

        internal TestData(string text)
        {
            Text = text;
        }
    }

    internal static class TestKeys
    {
        internal static readonly PopupKey<TestData> A = new PopupKey<TestData>("popup.a");
        internal static readonly PopupKey<TestData> B = new PopupKey<TestData>("popup.b");
        internal static readonly PopupKey<TestData> C = new PopupKey<TestData>("popup.c");
        internal static readonly PopupKey<TestData> D = new PopupKey<TestData>("popup.d");
        internal static readonly PopupKey<TestData> E = new PopupKey<TestData>("popup.e");
        internal static readonly PopupKey<TestData> F = new PopupKey<TestData>("popup.f");

        internal static readonly PopupKey<TestData>[] All = { A, B, C, D, E, F };
    }

    /// <summary>
    /// Records every call the core makes and can hold any of the three awaited members open, which is
    /// what puts two requests in flight at the same time.
    /// </summary>
    internal sealed class FakeView : IPopupView
    {
        internal readonly string KeyId;
        internal readonly List<string> Calls = new List<string>();

        internal bool IsSuspended;
        internal int SuspendCalls;
        internal int ResumeCalls;

        internal bool ThrowOnOpen;
        internal bool ThrowOnClose;
        internal bool ThrowOnWait;

        internal bool HoldOpen;
        internal bool HoldClose;

        internal bool OpenPlayed;
        internal bool ClosePlayed;

        private UniTaskCompletionSource _openSource;
        private UniTaskCompletionSource _closeSource;
        private UniTaskCompletionSource<PopupCloseInfo> _waitSource;
        private PopupCloseInfo _pendingClose;
        private bool _hasPendingClose;

        internal FakeView(string keyId)
        {
            KeyId = keyId;
        }

        internal bool IsHoldingOpen => _openSource != null;
        internal bool IsHoldingClose => _closeSource != null;
        internal bool IsWaitingForClose => _waitSource != null;

        public UniTask PlayOpenAsync(CancellationToken ct)
        {
            Calls.Add(nameof(PlayOpenAsync));
            if (ThrowOnOpen)
            {
                throw new InvalidOperationException("fake view: PlayOpenAsync failure");
            }

            if (!HoldOpen)
            {
                OpenPlayed = true;
                return UniTask.CompletedTask;
            }

            _openSource = new UniTaskCompletionSource();
            ct.Register(CancelSource, _openSource);
            return _openSource.Task;
        }

        public UniTask<PopupCloseInfo> WaitForCloseAsync(CancellationToken ct)
        {
            Calls.Add(nameof(WaitForCloseAsync));
            if (ThrowOnWait)
            {
                throw new InvalidOperationException("fake view: WaitForCloseAsync failure");
            }

            _waitSource = new UniTaskCompletionSource<PopupCloseInfo>();
            ct.Register(CancelCloseSource, _waitSource);

            if (_hasPendingClose)
            {
                _hasPendingClose = false;
                _waitSource.TrySetResult(_pendingClose);
            }

            return _waitSource.Task;
        }

        public void RequestClose(string action)
        {
            Calls.Add(nameof(RequestClose));
            Resolve(new PopupCloseInfo(CloseSource.Code, action));
        }

        public UniTask PlayCloseAsync(CancellationToken ct)
        {
            Calls.Add(nameof(PlayCloseAsync));
            if (ThrowOnClose)
            {
                throw new InvalidOperationException("fake view: PlayCloseAsync failure");
            }

            if (!HoldClose)
            {
                ClosePlayed = true;
                return UniTask.CompletedTask;
            }

            _closeSource = new UniTaskCompletionSource();
            ct.Register(CancelSource, _closeSource);
            return _closeSource.Task;
        }

        public void SetSuspended(bool suspended)
        {
            Calls.Add(suspended ? "SetSuspended(true)" : "SetSuspended(false)");
            IsSuspended = suspended;
            if (suspended)
            {
                SuspendCalls++;
            }
            else
            {
                ResumeCalls++;
            }
        }

        internal void SimulateUserClose(string action = null)
        {
            Resolve(new PopupCloseInfo(CloseSource.User, action));
        }

        internal void ResolveOpen()
        {
            UniTaskCompletionSource source = _openSource;
            _openSource = null;
            OpenPlayed = true;
            source?.TrySetResult();
        }

        internal void ResolveClose()
        {
            UniTaskCompletionSource source = _closeSource;
            _closeSource = null;
            ClosePlayed = true;
            source?.TrySetResult();
        }

        private void Resolve(in PopupCloseInfo info)
        {
            if (_waitSource == null)
            {
                _pendingClose = info;
                _hasPendingClose = true;
                return;
            }

            UniTaskCompletionSource<PopupCloseInfo> source = _waitSource;
            _waitSource = null;
            source.TrySetResult(info);
        }

        private static readonly Action<object> CancelSource =
            static state => ((UniTaskCompletionSource)state).TrySetCanceled();

        private static readonly Action<object> CancelCloseSource =
            static state => ((UniTaskCompletionSource<PopupCloseInfo>)state).TrySetCanceled();
    }

    internal sealed class FakeViewFactory : IPopupViewFactory
    {
        internal int CreateCount;
        internal int ReleaseCount;

        internal readonly List<FakeView> Created = new List<FakeView>();
        internal readonly List<FakeView> Released = new List<FakeView>();

        internal bool FailNextCreate;
        internal bool FailAllCreates;
        internal bool ReturnNullNextCreate;
        internal bool HoldNextCreate;
        internal bool ThrowOnRelease;
        internal bool ThrowOnNextRelease;

        // Applied to every view this factory creates from now on — the fuzz uses them to hold a
        // transition open so two requests are in flight at the same time.
        internal bool HoldOpenOnCreatedViews;
        internal bool HoldCloseOnCreatedViews;
        internal bool ThrowOnOpenNextView;
        internal bool ThrowOnCloseNextView;
        internal bool ThrowOnWaitNextView;

        private UniTaskCompletionSource<IPopupView> _heldCreate;
        private FakeView _heldView;

        internal bool IsHoldingCreate => _heldCreate != null;

        /// <summary>Views the core currently owns (created and handed over, not yet released).</summary>
        internal int LiveViewCount => CreateCount - ReleaseCount;

        public UniTask<IPopupView> CreateAsync<TData>(in PopupKey<TData> key, in TData data, CancellationToken ct)
        {
            if (FailAllCreates || FailNextCreate)
            {
                FailNextCreate = false;
                throw new InvalidOperationException("fake factory: CreateAsync failure");
            }

            if (ReturnNullNextCreate)
            {
                ReturnNullNextCreate = false;
                return UniTask.FromResult<IPopupView>(null);
            }

            FakeView view = new FakeView(key.Id)
            {
                HoldOpen = HoldOpenOnCreatedViews,
                HoldClose = HoldCloseOnCreatedViews
            };

            if (ThrowOnOpenNextView)
            {
                ThrowOnOpenNextView = false;
                view.ThrowOnOpen = true;
            }

            if (ThrowOnCloseNextView)
            {
                ThrowOnCloseNextView = false;
                view.ThrowOnClose = true;
            }

            if (ThrowOnWaitNextView)
            {
                ThrowOnWaitNextView = false;
                view.ThrowOnWait = true;
            }

            if (HoldNextCreate)
            {
                HoldNextCreate = false;
                _heldView = view;
                _heldCreate = new UniTaskCompletionSource<IPopupView>();
                ct.Register(CancelHeldCreate);
                return _heldCreate.Task;
            }

            CountCreated(view);
            return UniTask.FromResult<IPopupView>(view);
        }

        public void Release(IPopupView view)
        {
            ReleaseCount++;
            Released.Add(view as FakeView);

            if (ThrowOnRelease || ThrowOnNextRelease)
            {
                ThrowOnNextRelease = false;
                throw new InvalidOperationException("fake factory: Release failure");
            }
        }

        /// <summary>Completes a held CreateAsync with the view it was going to return.</summary>
        internal FakeView ResolveHeldCreate()
        {
            UniTaskCompletionSource<IPopupView> source = _heldCreate;
            FakeView view = _heldView;
            _heldCreate = null;
            _heldView = null;

            if (source == null)
            {
                return null;
            }

            CountCreated(view);
            source.TrySetResult(view);
            return view;
        }

        /// <summary>
        /// A real Addressables load aborts when its token fires, so the held one does too — and it drops
        /// the pending view, which is what keeps the create/release accounting honest.
        /// </summary>
        private void CancelHeldCreate()
        {
            UniTaskCompletionSource<IPopupView> source = _heldCreate;
            _heldCreate = null;
            _heldView = null;
            source?.TrySetCanceled();
        }

        /// <summary>Fails a held CreateAsync, as a remote fetch that came back empty would.</summary>
        internal void FailHeldCreate()
        {
            UniTaskCompletionSource<IPopupView> source = _heldCreate;
            _heldCreate = null;
            _heldView = null;
            source?.TrySetException(new InvalidOperationException("fake factory: held CreateAsync failure"));
        }

        internal FakeView LastCreatedFor(string keyId)
        {
            for (int i = Created.Count - 1; i >= 0; i--)
            {
                if (Created[i].KeyId == keyId)
                {
                    return Created[i];
                }
            }

            return null;
        }

        private void CountCreated(FakeView view)
        {
            CreateCount++;
            Created.Add(view);
        }
    }

    internal sealed class FakePolicy : IPopupPolicy
    {
        internal bool RefuseAll;
        internal string RefuseReason = "fake policy refusal";
        internal readonly HashSet<string> RefusedKeys = new HashSet<string>();
        internal readonly HashSet<string> ThrowingKeys = new HashSet<string>();
        internal readonly List<string> ShownNotifications = new List<string>();
        internal int EvaluateCount;
        internal bool ThrowNextEvaluate;
        internal bool ThrowOnNotifyShown;

        public PopupDecision Evaluate(in PopupRequestInfo request)
        {
            EvaluateCount++;

            if (ThrowNextEvaluate || ThrowingKeys.Contains(request.KeyId))
            {
                ThrowNextEvaluate = false;
                throw new InvalidOperationException("fake policy: Evaluate failure");
            }

            if (RefuseAll || RefusedKeys.Contains(request.KeyId))
            {
                return PopupDecision.Refuse(RefuseReason);
            }

            return PopupDecision.Allow;
        }

        public void NotifyShown(in PopupRequestInfo request)
        {
            ShownNotifications.Add(request.KeyId);

            if (ThrowOnNotifyShown)
            {
                throw new InvalidOperationException("fake policy: NotifyShown failure");
            }
        }
    }

    internal sealed class RecordingAnalytics : IPopupAnalytics
    {
        internal readonly List<string> ShownKeys = new List<string>();
        internal readonly List<string> DismissedKeys = new List<string>();
        internal readonly List<string> DismissActions = new List<string>();
        internal readonly List<string> ConvertedKeys = new List<string>();

        internal bool ThrowOnShown;
        internal bool ThrowOnDismissed;

        public void Shown(string keyId)
        {
            ShownKeys.Add(keyId);

            if (ThrowOnShown)
            {
                throw new InvalidOperationException("fake analytics: Shown failure");
            }
        }

        public void Dismissed(string keyId, string action)
        {
            DismissedKeys.Add(keyId);
            DismissActions.Add(action);

            if (ThrowOnDismissed)
            {
                throw new InvalidOperationException("fake analytics: Dismissed failure");
            }
        }

        public void Converted(string keyId, string action)
        {
            ConvertedKeys.Add(keyId);
        }
    }

    /// <summary>
    /// Builds the whole graph by hand — the same construction the composition root performs — and keeps
    /// the fakes reachable for assertions.
    /// </summary>
    internal sealed class PopupTestHarness : IDisposable
    {
        internal readonly FakeViewFactory Factory = new FakeViewFactory();
        internal readonly FakePolicy Policy = new FakePolicy();
        internal readonly RecordingAnalytics Analytics = new RecordingAnalytics();
        internal readonly PopupService Service;
        internal readonly List<PopupCompletion> Completions = new List<PopupCompletion>();

        internal PopupTestHarness()
        {
            Service = new PopupService(Factory, Policy, Analytics);
            Service.RequestCompleted += OnRequestCompleted;
        }

        internal PopupHandle Show(in PopupKey<TestData> key, in ShowOptions options = default)
        {
            return Service.Show(key, new TestData(key.Id), options);
        }

        internal UniTask<PopupResult> ShowAsync(in PopupKey<TestData> key, in ShowOptions options = default,
                                                CancellationToken cancellationToken = default)
        {
            return Service.ShowAsync(key, new TestData(key.Id), options, cancellationToken);
        }

        /// <summary>The view of whatever currently occupies the slot.</summary>
        internal FakeView CurrentView => Factory.LastCreatedFor(Service.CurrentKeyId);

        internal void CloseCurrent(string action = null)
        {
            FakeView view = CurrentView;
            if (view == null)
            {
                throw new InvalidOperationException("no occupant to close");
            }

            view.SimulateUserClose(action);
        }

        internal PopupCompletion CompletionFor(string keyId)
        {
            for (int i = 0; i < Completions.Count; i++)
            {
                if (Completions[i].KeyId == keyId)
                {
                    return Completions[i];
                }
            }

            throw new InvalidOperationException($"no completion recorded for {keyId}");
        }

        internal int CountOutcome(PopupOutcome outcome)
        {
            int count = 0;
            for (int i = 0; i < Completions.Count; i++)
            {
                if (Completions[i].Result.Outcome == outcome)
                {
                    count++;
                }
            }

            return count;
        }

        private void OnRequestCompleted(PopupCompletion completion)
        {
            Completions.Add(completion);
        }

        public void Dispose()
        {
            Service.RequestCompleted -= OnRequestCompleted;
            Service.Dispose();
        }
    }
}
