using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Seams;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using UnityEngine;

namespace PeerPlay.Popups.ViewSourcing.Tests
{
    /// <summary>
    /// Every fake completes synchronously by default and can be told to hold. That is what keeps this
    /// suite off the PlayerLoop: nothing here yields to a frame that never comes in EditMode.
    /// </summary>
    internal sealed class FakePrefabLoader : IPrefabLoader
    {
        internal readonly List<string> Loads = new List<string>();
        internal readonly List<string> Releases = new List<string>();

        internal bool Hold;
        internal bool FailNext;
        internal GameObject Prefab;
        internal int Aborts;

        // A queue, not a field: more than one call can be in flight, and a test that completes "the held
        // one" means the oldest, not whichever overwrote the field last.
        private readonly Queue<UniTaskCompletionSource<GameObject>> _held =
            new Queue<UniTaskCompletionSource<GameObject>>();

        internal void CompleteHeld()
        {
            if (_held.Count > 0)
            {
                _held.Dequeue().TrySetResult(Prefab);
            }
        }

        public async UniTask<GameObject> LoadAsync(string assetId, CancellationToken ct)
        {
            Loads.Add(assetId);

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException($"scripted failure for '{assetId}'");
            }

            if (!Hold)
            {
                return Prefab;
            }

            UniTaskCompletionSource<GameObject> source = new UniTaskCompletionSource<GameObject>();
            _held.Enqueue(source);

            using (ct.Register(() =>
            {
                Aborts++;
                source.TrySetCanceled();
            }))
            {
                return await source.Task;
            }
        }

        public void Release(string assetId)
        {
            Releases.Add(assetId);
        }
    }

    internal sealed class FakeViewSource : IPopupViewSource
    {
        internal readonly List<string> Acquires = new List<string>();
        internal readonly List<string> Releases = new List<string>();

        internal GameObject Prefab;

        /// <summary>Fires after the acquire is recorded — the hook that opens the post-load window.</summary>
        internal Action OnAcquired;

        public UniTask<GameObject> AcquirePrefabAsync(string assetId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Acquires.Add(assetId);
            OnAcquired?.Invoke();
            return UniTask.FromResult(Prefab);
        }

        public void ReleasePrefab(string assetId)
        {
            Releases.Add(assetId);
        }
    }

    internal sealed class FakeHttp : IHttpClient
    {
        internal readonly Queue<HttpResult> Scripted = new Queue<HttpResult>();
        internal int RequestCount;
        internal int Aborts;
        internal bool Hold;

        private readonly Queue<UniTaskCompletionSource<HttpResult>> _held =
            new Queue<UniTaskCompletionSource<HttpResult>>();

        internal void Script(HttpResult result)
        {
            Scripted.Enqueue(result);
        }

        internal void ScriptMany(HttpResult result, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Scripted.Enqueue(result);
            }
        }

        internal void CompleteHeld(HttpResult result)
        {
            if (_held.Count > 0)
            {
                _held.Dequeue().TrySetResult(result);
            }
        }

        public UniTask<HttpResult> GetAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            return RespondAsync(ct);
        }

        public UniTask<HttpResult> GetTextureAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            return RespondAsync(ct);
        }

        private async UniTask<HttpResult> RespondAsync(CancellationToken ct)
        {
            RequestCount++;
            ct.ThrowIfCancellationRequested();

            if (!Hold)
            {
                return Scripted.Count > 0
                    ? Scripted.Dequeue()
                    : HttpResult.Fail(HttpFailure.Transport, "nothing scripted");
            }

            UniTaskCompletionSource<HttpResult> source = new UniTaskCompletionSource<HttpResult>();
            _held.Enqueue(source);

            using (ct.Register(() =>
            {
                // The abort the real UnityWebRequest would perform. S2d asserts this counter, which is what
                // makes "the fetch is cancellable" a claim about the request and not about the awaiter.
                Aborts++;
                source.TrySetCanceled();
            }))
            {
                return await source.Task;
            }
        }
    }

    internal sealed class FakeClock : IPopupClock
    {
        internal DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow => Now;

        public float RealtimeSeconds => 0f;

        internal void Advance(TimeSpan by)
        {
            Now += by;
        }
    }

    internal sealed class FakeDelayProvider : IDelayProvider
    {
        internal readonly List<TimeSpan> Delays = new List<TimeSpan>();
        internal TimeSpan? DeadlineRequested;
        internal bool HoldDelays;
        internal int DisarmCount;

        private CancellationTokenSource _deadlineCts;
        private UniTaskCompletionSource _held;

        public UniTask DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            Delays.Add(duration);

            if (!HoldDelays)
            {
                return UniTask.CompletedTask;
            }

            UniTaskCompletionSource source = new UniTaskCompletionSource();
            _held = source;
            ct.Register(() => source.TrySetCanceled());
            return source.Task;
        }

        public IDisposable CancelAfter(CancellationTokenSource cts, TimeSpan duration)
        {
            DeadlineRequested = duration;
            _deadlineCts = cts;
            return new Disarm(this);
        }

        internal void FireDeadline()
        {
            _deadlineCts?.Cancel();
        }

        internal void CompleteDelay()
        {
            UniTaskCompletionSource held = _held;
            _held = null;
            held?.TrySetResult();
        }

        private sealed class Disarm : IDisposable
        {
            private readonly FakeDelayProvider _owner;

            internal Disarm(FakeDelayProvider owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                _owner.DisarmCount++;
                _owner._deadlineCts = null;
            }
        }
    }

    internal sealed class FakeCatalogProbe : ICatalogProbe
    {
        internal bool Ready;
        internal bool ResolveAll = true;
        internal int InitCalls;
        internal int ResolveCalls;

        public bool CatalogsReady => Ready;

        public UniTask InitializeAsync(CancellationToken ct)
        {
            InitCalls++;
            return UniTask.CompletedTask;
        }

        public UniTask<bool> TryResolveAllAsync(IReadOnlyList<string> assetIds, CancellationToken ct)
        {
            ResolveCalls++;
            return UniTask.FromResult(ResolveAll);
        }
    }

    /// <summary>
    /// Records leases so a test can assert that a view returned what it took. Holds on demand, which is
    /// what puts a fetch in flight across a pool return.
    /// </summary>
    internal sealed class FakeImages : IRemoteImageSource
    {
        internal readonly List<string> Requests = new List<string>();
        internal int Returned;
        internal int Aborts;
        internal bool Hold;
        internal Sprite Result;

        private readonly Queue<UniTaskCompletionSource<Sprite>> _held =
            new Queue<UniTaskCompletionSource<Sprite>>();

        internal void CompleteHeld(Sprite sprite)
        {
            if (_held.Count > 0)
            {
                _held.Dequeue().TrySetResult(sprite);
            }
        }

        public async UniTask<Sprite> LoadAsync(string url, CancellationToken ct)
        {
            Requests.Add(url);
            ct.ThrowIfCancellationRequested();

            if (!Hold)
            {
                return Result;
            }

            UniTaskCompletionSource<Sprite> source = new UniTaskCompletionSource<Sprite>();
            _held.Enqueue(source);

            using (ct.Register(() =>
            {
                Aborts++;
                source.TrySetCanceled();
            }))
            {
                return await source.Task;
            }
        }

        public void ReturnLease(Sprite sprite)
        {
            Returned++;
        }
    }

    /// <summary>
    /// A transition that never finishes until told to. It is how the suite observes what happens DURING a
    /// transition — the input gate being shut, the backdrop already animating — rather than only after.
    /// </summary>
    internal sealed class HoldingTransition : IPopupTransition
    {
        internal int InCalls;
        internal int OutCalls;

        private UniTaskCompletionSource _in;
        private UniTaskCompletionSource _out;

        internal void ReleaseIn()
        {
            UniTaskCompletionSource source = _in;
            _in = null;
            source?.TrySetResult();
        }

        internal void ReleaseOut()
        {
            UniTaskCompletionSource source = _out;
            _out = null;
            source?.TrySetResult();
        }

        internal void CancelOut()
        {
            UniTaskCompletionSource source = _out;
            _out = null;
            source?.TrySetCanceled();
        }

        public UniTask PlayInAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            InCalls++;
            _in = new UniTaskCompletionSource();
            return _in.Task;
        }

        public UniTask PlayOutAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            OutCalls++;
            _out = new UniTaskCompletionSource();
            return _out.Task;
        }
    }

    internal readonly struct TestPayload
    {
        internal readonly string Text;

        internal TestPayload(string text)
        {
            Text = text;
        }
    }

    internal sealed class TestPopupView : PopupView<TestPayload>
    {
        internal int BindCount;
        internal int PreparedCount;
        internal int ReleasingCount;
        internal string LastText;

        public override void Bind(in TestPayload data)
        {
            BindCount++;
            LastText = data.Text;
        }

        protected override void OnPrepared()
        {
            PreparedCount++;
        }

        protected override void OnReleasing()
        {
            ReleasingCount++;
        }
    }

    /// <summary>The remote-content shape: it starts a fetch from Bind and never waits for it.</summary>
    internal sealed class RemoteContentPopupView : PopupView<TestPayload>
    {
        internal int Applied;
        internal int Unavailable;

        public override void Bind(in TestPayload data)
        {
            LoadContentAsync(Entry.ImageUrl).Forget();
        }

        protected override void ApplyContentSprite(Sprite sprite)
        {
            Applied++;
        }

        protected override void ShowContentUnavailable()
        {
            Unavailable++;
        }
    }

    /// <summary>Registered under a key whose payload it does not accept — the mis-registration V3 asserts.</summary>
    internal sealed class WrongPayloadPopupView : PopupView<int>
    {
        public override void Bind(in int data)
        {
        }
    }

    internal sealed class FakePopupView : IPopupView
    {
        internal readonly string KeyId;
        internal bool OpenPlayed;
        internal bool ClosePlayed;
        internal bool IsSuspended;

        private readonly UniTaskCompletionSource<PopupCloseInfo> _close =
            new UniTaskCompletionSource<PopupCloseInfo>();

        internal FakePopupView(string keyId)
        {
            KeyId = keyId;
        }

        public UniTask PlayOpenAsync(CancellationToken ct)
        {
            OpenPlayed = true;
            return UniTask.CompletedTask;
        }

        public UniTask<PopupCloseInfo> WaitForCloseAsync(CancellationToken ct)
        {
            return _close.Task.AttachExternalCancellation(ct);
        }

        public void RequestClose(string action)
        {
            _close.TrySetResult(new PopupCloseInfo(CloseSource.Code, action));
        }

        public UniTask PlayCloseAsync(CancellationToken ct)
        {
            ClosePlayed = true;
            return UniTask.CompletedTask;
        }

        public void SetSuspended(bool suspended)
        {
            IsSuspended = suspended;
        }
    }

    /// <summary>
    /// Counts what the core asks and forwards to the real policy. It exists to pin a contract the core's
    /// own doc got wrong: Evaluate is called an unfixed number of times, so a policy that counted there
    /// would consume its own cap before the popup opened.
    /// </summary>
    internal sealed class CountingPolicy : IPopupPolicy
    {
        internal readonly Dictionary<string, int> Evaluations = new Dictionary<string, int>();
        internal readonly Dictionary<string, int> Shows = new Dictionary<string, int>();

        private readonly IPopupPolicy _inner;

        internal CountingPolicy(IPopupPolicy inner)
        {
            _inner = inner;
        }

        internal int EvaluationsOf(string keyId)
        {
            return Evaluations.TryGetValue(keyId, out int count) ? count : 0;
        }

        internal int ShowsOf(string keyId)
        {
            return Shows.TryGetValue(keyId, out int count) ? count : 0;
        }

        public PopupDecision Evaluate(in PopupRequestInfo request)
        {
            Evaluations.TryGetValue(request.KeyId, out int count);
            Evaluations[request.KeyId] = count + 1;
            return _inner.Evaluate(request);
        }

        public void NotifyShown(in PopupRequestInfo request)
        {
            Shows.TryGetValue(request.KeyId, out int count);
            Shows[request.KeyId] = count + 1;
            _inner.NotifyShown(request);
        }
    }

    internal sealed class FakeViewFactory : IPopupViewFactory
    {
        internal readonly List<FakePopupView> Created = new List<FakePopupView>();
        internal int Released;

        public UniTask<IPopupView> CreateAsync<TData>(in PopupKey<TData> key, in TData data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FakePopupView view = new FakePopupView(key.Id);
            Created.Add(view);
            return UniTask.FromResult<IPopupView>(view);
        }

        public void Release(IPopupView view)
        {
            Released++;
        }
    }
}
