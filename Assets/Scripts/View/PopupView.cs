using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using PeerPlay.Popups.Seams;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The untyped plumbing every popup shares. Its whole job is the invariant a pooled instance has to
    /// keep: <b>a rented instance is indistinguishable from a freshly instantiated one</b>. Everything
    /// that can differ between the two is reset in <see cref="PrepareForUse"/> or
    /// <see cref="OnReleased"/>, and both are non-virtual so a subclass cannot quietly opt out of a row.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public abstract class PopupView : MonoBehaviour, IPopupView
    {
        [SerializeField] private CanvasGroup _canvasGroup;
        [SerializeField] private RectTransform _root;
        [SerializeField] private RectTransform _contentRoot;
        [SerializeField] private RectTransform _spinner;
        [SerializeField] private Image _contentImage;
        [SerializeField] private Sprite _placeholderSprite;
        [SerializeField] private TMP_Text _unavailableLabel;
        [SerializeField] private PopupModality _modality = PopupModality.Modal;
        [SerializeField] private PopupSuspendBehaviour _suspendBehaviour = PopupSuspendBehaviour.Hide;
        [SerializeField] private bool _dismissOnBackdropTap = true;
        [SerializeField] private bool _dismissible = true;
        [SerializeField] private string _transitionId = "fade";
        [SerializeField] private Button[] _closeButtons;

        // Captured once, in EnsureInitialized, so a config override can be applied on one rent and undone
        // on the next. Without them the first override would be permanent — the authored value is gone the
        // moment something writes over it.
        private string _authoredTransitionId;
        private PopupModality _authoredModality;
        private PopupSuspendBehaviour _authoredSuspend;

        private UniTaskCompletionSource<PopupCloseInfo> _closeSource;
        private IPopupTransition _transition;
        private PopupLayer _layer;
        private IRemoteImageSource _images;
        private CancellationToken _token;
        private Sprite _leasedSprite;
        private UnityEngine.Events.UnityAction _onCloseButton;
        private bool _initialized;
        private bool _inUse;
        private int _generation;

        internal int Generation => _generation;

        internal string KeyId { get; private set; }

        internal PopupModality Modality { get; private set; }

        internal PopupSuspendBehaviour SuspendBehaviour { get; private set; }

        internal bool DismissOnBackdropTap => _dismissOnBackdropTap;

        internal string TransitionId => _transitionId;

        /// <summary>
        /// What the prefab authored, captured before any config override could write over it. The factory
        /// reads it to resolve the transition for THIS rent, because <see cref="TransitionId"/> still holds
        /// the previous rent's answer until PrepareForUse runs.
        /// </summary>
        internal string AuthoredTransitionId
        {
            get
            {
                EnsureInitialized();
                return _authoredTransitionId;
            }
        }

        internal CanvasGroup CanvasGroup => _canvasGroup;

        internal bool IsSuspended { get; private set; }

        internal bool IsInUse => _inUse;

        /// <summary>
        /// Test hook. A close button wired in Bind instead of once per instance fires N times on the Nth
        /// reuse, and runtime AddListener is not enumerable — UnityEvent exposes only the persistent count.
        /// So the accumulation is asserted through this counter rather than by inspecting the event.
        /// </summary>
        internal int CloseButtonInvocations { get; private set; }

        /// <summary>The catalog's facts for this rent: the image URL and the two text keys a Bind reads.</summary>
        protected PopupCatalogEntry Entry { get; private set; }

        protected IPopupTextProvider Text { get; private set; }

        // ---------------------------------------------------------------- Unity lifecycle

        private void Awake()
        {
            EnsureInitialized();
        }

        /// <remarks>
        /// Idempotent and reachable from every entry point rather than only from Awake, because Unity does
        /// not run Awake on a component created outside play mode and the EditMode suite drives these
        /// objects directly. Everything here happens once per instance ever — in particular the close
        /// button listeners, which are the classic pooling bug when they are wired in Bind instead.
        /// </remarks>
        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            // Resolved rather than serialized, because both are always THIS object's. A [SerializeField]
            // pointing at another popup's CanvasGroup is non-null, passes every {fileID: 0} grep, and then
            // fades somebody else's dialog once per frame — removing the footgun beats remembering it.
            if (_root == null)
            {
                _root = (RectTransform)transform;
            }

            if (_canvasGroup == null)
            {
                _canvasGroup = GetComponent<CanvasGroup>();
            }

            if (_contentRoot == null)
            {
                _contentRoot = _root;
            }

            _authoredTransitionId = _transitionId;
            _authoredModality = _modality;
            _authoredSuspend = _suspendBehaviour;

            Modality = _modality;
            SuspendBehaviour = _suspendBehaviour;

            _onCloseButton = OnCloseButtonClicked;

            if (_closeButtons != null)
            {
                for (int i = 0; i < _closeButtons.Length; i++)
                {
                    if (_closeButtons[i] != null)
                    {
                        _closeButtons[i].onClick.AddListener(_onCloseButton);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            KillTweens();
        }

        /// <summary>
        /// Authoring hook for the suite. The prefab Inspector is the normal route; this exists so a test
        /// does not need a prefab asset on disk, and it re-captures the authored values so the
        /// override-then-drop path (V1c) is exercised against real ones.
        /// </summary>
        internal void AuthorForTests(PopupModality modality, PopupSuspendBehaviour suspend, string transitionId,
                                     bool dismissible = true, bool dismissOnBackdropTap = true,
                                     Button[] closeButtons = null)
        {
            EnsureInitialized();

            if (closeButtons != null)
            {
                _closeButtons = closeButtons;

                for (int i = 0; i < closeButtons.Length; i++)
                {
                    closeButtons[i].onClick.AddListener(_onCloseButton);
                }
            }

            _modality = modality;
            _suspendBehaviour = suspend;
            _transitionId = transitionId;
            _dismissible = dismissible;
            _dismissOnBackdropTap = dismissOnBackdropTap;

            _authoredModality = modality;
            _authoredSuspend = suspend;
            _authoredTransitionId = transitionId;

            Modality = modality;
            SuspendBehaviour = suspend;
        }

        // ---------------------------------------------------------------- rent / release

        /// <summary>
        /// Assigned at rent AND at instantiation, before the bind type check — the pool reads it to decide
        /// which stack an instance goes back to, and the mismatch path returns a freshly instantiated view
        /// that would otherwise carry a null key.
        /// </summary>
        internal void AssignKey(string keyId)
        {
            EnsureInitialized();
            KeyId = keyId;
        }

        /// <summary>
        /// The only way an instance leaves the pool. Non-virtual on purpose: an override is exactly how a
        /// subclass would skip a row of the invariant, and `sealed` is legal only on an override (CS0238).
        /// </summary>
        internal void PrepareForUse(in PopupViewSetup setup)
        {
            EnsureInitialized();

            KeyId = setup.KeyId;
            Entry = setup.Entry;
            Text = setup.Text;
            _transition = setup.Transition;
            _layer = setup.Layer;
            _images = setup.Images;
            _token = setup.Token;

            // A fresh channel per rent. Reusing the previous one resolves the next popup instantly, which
            // is the obligation the core's factory contract names and cannot check.
            _closeSource = new UniTaskCompletionSource<PopupCloseInfo>();

            // Bumped before anything can await: an image fetch started by the previous rent compares
            // against the value it captured and paints nothing once this moves.
            _generation++;
            _inUse = true;
            IsSuspended = false;

            // Either the config's override or the value the prefab authored — never the previous rent's.
            _transitionId = setup.Entry.TransitionId ?? _authoredTransitionId;
            Modality = setup.Entry.Modality ?? _authoredModality;
            SuspendBehaviour = setup.Entry.Suspend ?? _authoredSuspend;

            KillTweens();

            _canvasGroup.alpha = 0f;
            _canvasGroup.interactable = true;
            _canvasGroup.blocksRaycasts = true;
            _root.localScale = Vector3.one;
            _root.anchoredPosition = Vector2.zero;

            ShowSpinner(false);
            SetUnavailableVisible(false);
            RestorePlaceholder();

            transform.SetParent(_layer.Content, false);
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            // The writer for the list the backdrop's resume path reads. Register here and unregister in
            // OnReleased, so "live" means exactly "rented".
            _layer.Register(this);

            OnPrepared();
        }

        /// <summary>Called by the factory on release, before the pool decides to keep or destroy the instance.</summary>
        internal void OnReleased()
        {
            EnsureInitialized();

            if (!_inUse)
            {
                return;
            }

            _inUse = false;

            OnReleasing();

            if (_layer != null)
            {
                _layer.Unregister(this);
            }

            KillTweens();
            ReturnLease();

            // Not resolved with a result: a released view's channel must never hand the core a close it
            // did not observe. The core cancels the request token before releasing, so an outstanding
            // WaitForCloseAsync is already unwound by then.
            _closeSource = null;

            ShowSpinner(false);
            SetUnavailableVisible(false);
            RestorePlaceholder();
            ClearText();

            _canvasGroup.alpha = 0f;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            IsSuspended = false;

            gameObject.SetActive(false);

            if (_layer != null && _layer.PoolRoot != null)
            {
                transform.SetParent(_layer.PoolRoot, false);
            }

            _transition = null;
            _images = null;
            Text = null;
            _token = default;
            Entry = default;
        }

        protected virtual void OnPrepared()
        {
        }

        protected virtual void OnReleasing()
        {
        }

        /// <summary>Clears every label the base knows about. Override to clear the subclass's own.</summary>
        protected virtual void ClearText()
        {
        }

        // ---------------------------------------------------------------- IPopupView

        public async UniTask PlayOpenAsync(CancellationToken ct)
        {
            // The gate belongs here and not in the factory's hooks: CreateAsync completes before the core
            // transitions to Opening, and Release runs after PlayCloseAsync has already finished. These two
            // methods ARE the transition, and the finally is what reopens input on a throw or a cancel.
            _layer.BeginTransition();

            try
            {
                transform.SetAsLastSibling();

                if (Modality == PopupModality.Modal)
                {
                    // Driven from here rather than from the factory's hooks: CreateAsync completes before
                    // the core transitions to Opening, so a backdrop started there would dim the screen to
                    // nothing and only then show the dialog; Release runs after PlayCloseAsync, so one
                    // started there would linger past it.
                    //
                    // V7 pins the two ends that are observable — the backdrop is down when CreateAsync has
                    // returned, and up while this method is still awaiting. It does NOT prove the two run
                    // concurrently rather than backdrop-then-transition inside this WhenAll: the backdrop
                    // settles synchronously outside play mode, so there is no interleaving to observe.
                    await UniTask.WhenAll(_transition.PlayInAsync(_root, _canvasGroup, ct),
                                          _layer.ShowBackdropBelowAsync(this));
                }
                else
                {
                    await _transition.PlayInAsync(_root, _canvasGroup, ct);
                }
            }
            finally
            {
                _layer.EndTransition();
            }
        }

        public UniTask<PopupCloseInfo> WaitForCloseAsync(CancellationToken ct)
        {
            if (_closeSource == null)
            {
                throw new InvalidOperationException(
                    $"{nameof(PopupView)}.{nameof(WaitForCloseAsync)} on a view that is not rented (key={KeyId})");
            }

            return _closeSource.Task.AttachExternalCancellation(ct);
        }

        public void RequestClose(string action)
        {
            if (!_dismissible)
            {
                PopupViewLog.Trace($"{nameof(PopupView)}.{nameof(RequestClose)} ignored: key={KeyId} is not dismissible");
                return;
            }

            Resolve(CloseSource.Code, action);
        }

        public async UniTask PlayCloseAsync(CancellationToken ct)
        {
            _layer.BeginTransition();

            try
            {
                if (Modality == PopupModality.Modal)
                {
                    await UniTask.WhenAll(_transition.PlayOutAsync(_root, _canvasGroup, ct),
                                          _layer.HideBackdropAsync());
                }
                else
                {
                    await _transition.PlayOutAsync(_root, _canvasGroup, ct);
                }
            }
            finally
            {
                _layer.EndTransition();
            }
        }

        /// <summary>
        /// Synchronous, because the core calls it synchronously. SetActive(false) is deliberately not used
        /// for Hide: it stops coroutines, breaks in-flight tweens and — decisively — the close channel and
        /// any in-flight image must survive suspension, because the core keeps the close watcher running.
        /// </summary>
        public void SetSuspended(bool suspended)
        {
            IsSuspended = suspended;

            if (suspended)
            {
                if (SuspendBehaviour == PopupSuspendBehaviour.Hide)
                {
                    _canvasGroup.alpha = 0f;
                }

                // Both modes drop interactivity, always: input belongs to the interrupter regardless of
                // what is still visible.
                _canvasGroup.interactable = false;
                _canvasGroup.blocksRaycasts = false;
                return;
            }

            if (SuspendBehaviour == PopupSuspendBehaviour.Hide)
            {
                _canvasGroup.alpha = 1f;
            }

            _canvasGroup.interactable = true;
            _canvasGroup.blocksRaycasts = true;
        }

        // ---------------------------------------------------------------- content helpers

        /// <summary>Sets the label and settles its preferred size now; the one layout rebuild is RebuildContent's.</summary>
        protected void SetText(TMP_Text label, string value)
        {
            if (label == null)
            {
                return;
            }

            label.text = value;
            label.ForceMeshUpdate();
        }

        /// <summary>
        /// Called once, at the end of Bind. Assigning .text only marks layout dirty, so a ContentSizeFitter
        /// still reports the previous size for the rest of the frame and the open transition would animate
        /// a dialog laid out against stale widths.
        /// </summary>
        protected void RebuildContent()
        {
            if (_contentRoot != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRoot);
            }
        }

        /// <summary>
        /// The remote-content fetch, with the whole cancellation and staleness story in one place. Returns
        /// the sprite it applied, or null — and null is not one thing: a transport, HTTP or deadline
        /// failure shows the placeholder copy, while a teardown or a stale generation paints nothing at all.
        /// </summary>
        protected async UniTask<Sprite> LoadContentAsync(string url)
        {
            if (string.IsNullOrEmpty(url) || _images == null)
            {
                return null;
            }

            int captured = _generation;

            // Captured before the await, because OnReleased clears the field: a fetch that resolves after
            // this instance went back to the pool still has a lease to give back, and it must not go
            // looking for it on the NEXT rent's source.
            IRemoteImageSource images = _images;

            ShowSpinner(true);

            Sprite sprite;

            try
            {
                sprite = await images.LoadAsync(url, _token);
            }
            catch (OperationCanceledException)
            {
                // A teardown. Nothing to paint, and nothing to say.
                if (_generation == captured)
                {
                    ShowSpinner(false);
                }

                return null;
            }

            if (_generation != captured)
            {
                // This instance was returned to the pool and rented again while the fetch was in flight.
                // The lease is ours to give back; the pixels are not ours to paint.
                if (sprite != null)
                {
                    images.ReturnLease(sprite);
                }

                return null;
            }

            ShowSpinner(false);

            if (sprite == null)
            {
                // Includes the deadline path. A content failure is not a popup failure: the popup already
                // opened and the caller still gets a normal terminal when the player closes it.
                ShowContentUnavailable();
                return null;
            }

            _leasedSprite = sprite;
            ApplyContentSprite(sprite);
            return sprite;
        }

        /// <summary>The placeholder stays and one line of copy says so. A content failure is not a popup failure.</summary>
        protected virtual void ShowContentUnavailable()
        {
            SetUnavailableVisible(true);
        }

        protected virtual void ApplyContentSprite(Sprite sprite)
        {
            if (_contentImage != null)
            {
                _contentImage.sprite = sprite;
            }
        }

        private void SetUnavailableVisible(bool visible)
        {
            if (_unavailableLabel == null)
            {
                return;
            }

            if (visible && Text != null)
            {
                _unavailableLabel.text = Text.Get("popup.content.unavailable");
            }

            _unavailableLabel.gameObject.SetActive(visible);
        }

        private void RestorePlaceholder()
        {
            if (_contentImage != null)
            {
                _contentImage.sprite = _placeholderSprite;
            }
        }

        private void ShowSpinner(bool visible)
        {
            if (_spinner == null)
            {
                return;
            }

            _spinner.DOKill();
            _spinner.gameObject.SetActive(visible);

            if (!visible || !Application.isPlaying)
            {
                return;
            }

            _spinner.localRotation = Quaternion.identity;
            _spinner.DORotate(new Vector3(0f, 0f, -360f), 1f, RotateMode.FastBeyond360)
                    .SetEase(Ease.Linear)
                    .SetLoops(-1, LoopType.Restart)
                    .SetUpdate(true)
                    .SetLink(_spinner.gameObject);
        }

        // ---------------------------------------------------------------- internals

        private void OnCloseButtonClicked()
        {
            CloseButtonInvocations++;

            if (!_dismissible)
            {
                return;
            }

            Resolve(CloseSource.User, "close");
        }

        /// <summary>Resolves the one close channel. A second producer finds it already resolved and loses.</summary>
        protected void Resolve(CloseSource source, string action)
        {
            UniTaskCompletionSource<PopupCloseInfo> channel = _closeSource;
            if (channel == null)
            {
                return;
            }

            channel.TrySetResult(new PopupCloseInfo(source, action));
        }

        private void ReturnLease()
        {
            if (_leasedSprite == null || _images == null)
            {
                _leasedSprite = null;
                return;
            }

            _images.ReturnLease(_leasedSprite);
            _leasedSprite = null;
        }

        /// <summary>
        /// The spinner is on the list because SetLink fires on destroy, not on a pool return — so a rotate
        /// started by the previous rent survives into the next one and the popup opens showing a loading
        /// ring it never asked for.
        /// </summary>
        private void KillTweens()
        {
            if (_root != null)
            {
                _root.DOKill();
            }

            if (_canvasGroup != null)
            {
                _canvasGroup.DOKill();
            }

            if (_spinner != null)
            {
                _spinner.DOKill();
            }
        }
    }
}
