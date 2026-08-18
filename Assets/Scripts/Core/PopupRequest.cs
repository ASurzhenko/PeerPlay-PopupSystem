using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Seams;

namespace PeerPlay.Popups
{
    /// <summary>
    /// One submitted request. The queue is heterogeneous, so everything the queue needs lives on this
    /// untyped base; the payload and the awaiter live on the generic subclass, which is what keeps the
    /// whole pipeline free of casts and boxing.
    /// </summary>
    internal abstract class PopupRequest
    {
        internal ulong Id;
        internal string KeyId;
        internal string Family;
        internal PopupPriority Priority;
        internal PopupSequencing Sequencing;
        internal PopupState State;

        /// <summary>True once the request entered the live map. Gates every unregister action, so a
        /// request rejected before registration cannot decrement counters it never incremented.</summary>
        internal bool IsRegistered;

        /// <summary>Set the first time the pump refuses this request a preemption, so the misuse
        /// warning is logged once per request rather than once per lap.</summary>
        internal bool PreemptionWarned;

        internal CancellationTokenSource Cts;

        /// <summary>Cached so it stays readable after the source is disposed at the terminal.</summary>
        internal CancellationToken Token;

        /// <summary>The bridge that gives a request with no hop in flight — a pending one — a cancel path.</summary>
        internal CancellationTokenRegistration CancelRegistration;

        internal IPopupView View;

        private bool _completed;
        private bool _viewReleased;
        private PopupOutcome _outcome;
        private CloseSource _closeSource;
        private string _action;
        private string _reason;

        internal bool IsCompleted => _completed;

        internal PopupResult Result => new PopupResult(_outcome, _closeSource, _action, _reason);

        internal PopupRequestInfo Info => new PopupRequestInfo(KeyId, Family, Priority);

        /// <summary>
        /// The terminal latch. A token stops future work; it cannot un-write work already in flight, so
        /// the first caller here decides the outcome and every later one is a no-op. That is what makes
        /// double completion and double release unrepresentable rather than merely unlikely.
        /// </summary>
        internal bool TryComplete(PopupOutcome outcome, in PopupCloseInfo closeInfo, string reason)
        {
            if (_completed)
            {
                PopupLog.Trace($"{nameof(PopupRequest)}.{nameof(TryComplete)} id={Id} key={KeyId} already terminal ({_outcome}); {outcome} ignored");
                return false;
            }

            _completed = true;
            _outcome = outcome;
            _closeSource = closeInfo.Source;
            _action = closeInfo.Action;
            _reason = reason;
            return true;
        }

        internal bool TryMarkViewReleased()
        {
            if (_viewReleased)
            {
                return false;
            }

            _viewReleased = true;
            return true;
        }

        internal abstract bool HasAwaiter { get; }

        internal abstract UniTask<IPopupView> CreateViewAsync(IPopupViewFactory factory, CancellationToken ct);

        /// <summary>Completes the awaiter, if the caller took one. Resumes that caller inline.</summary>
        internal abstract void Complete(in PopupResult result);

        /// <summary>Clears the payload and returns the object to its per-payload free list.</summary>
        internal abstract void Recycle();

        protected void ResetBase()
        {
            Id = 0;
            KeyId = null;
            Family = null;
            Priority = PopupPriority.Normal;
            Sequencing = PopupSequencing.Queue;
            State = PopupState.None;
            IsRegistered = false;
            PreemptionWarned = false;
            Cts = null;
            Token = default;
            CancelRegistration = default;
            View = null;
            _completed = false;
            _viewReleased = false;
            _outcome = PopupOutcome.None;
            _closeSource = CloseSource.None;
            _action = null;
            _reason = null;
        }
    }

    internal sealed class PopupRequest<TData> : PopupRequest
    {
        private PopupKey<TData> _key;
        private TData _data;
        private AutoResetUniTaskCompletionSource<PopupResult> _awaiter;

        internal void Initialize(ulong id, in PopupKey<TData> key, in TData data, in ShowOptions options)
        {
            Id = id;
            _key = key;
            _data = data;
            KeyId = key.Id;
            Family = string.IsNullOrEmpty(options.Family) ? key.Id : options.Family;
            Priority = options.Priority;
            Sequencing = options.Sequencing;
            State = PopupState.None;
        }

        internal UniTask<PopupResult> CreateAwaiter()
        {
            _awaiter = AutoResetUniTaskCompletionSource<PopupResult>.Create();
            return _awaiter.Task;
        }

        internal override bool HasAwaiter => _awaiter != null;

        internal override UniTask<IPopupView> CreateViewAsync(IPopupViewFactory factory, CancellationToken ct)
        {
            return factory.CreateAsync(_key, _data, ct);
        }

        internal override void Complete(in PopupResult result)
        {
            AutoResetUniTaskCompletionSource<PopupResult> awaiter = _awaiter;
            if (awaiter == null)
            {
                return;
            }

            _awaiter = null;
            awaiter.TrySetResult(result);
        }

        internal override void Recycle()
        {
            _key = default;
            _data = default;
            _awaiter = null;
            ResetBase();
            PopupRequestPool<TData>.Return(this);
        }
    }
}
