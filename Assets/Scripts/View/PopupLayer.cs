using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// One Canvas for every popup, so a popup can never dirty the game canvas: a Canvas is the unit of
    /// rebuild, and a Graphic dirtied under this one rebuilds this one alone. Ordering is sibling order —
    /// no nested canvas per popup, because overrideSorting terminates Graphic.Raycast's upward walk and
    /// would silently disable the input gate below.
    ///
    /// The layer owns three things the popups themselves cannot: the counted input gate, the backdrop's
    /// position and alpha, and the list of live views the resume path reads.
    /// </summary>
    public sealed class PopupLayer : MonoBehaviour
    {
        [SerializeField] private Canvas _canvas;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private RectTransform _content;
        [SerializeField] private RectTransform _poolRoot;
        [SerializeField] private Image _backdrop;
        [SerializeField] private CanvasGroup _backdropGroup;
        [SerializeField] private Button _backdropButton;
        [SerializeField] private float _backdropAlpha = 0.72f;
        [SerializeField] private float _backdropFadeDuration = 0.2f;

        private readonly List<PopupView> _views = new List<PopupView>(8);

        private IPopupService _service;
        private Action<PopupCompletion> _onRequestCompleted;
        private CancellationTokenSource _backdropCts;
        private int _transitionDepth;
        private bool _initialized;

        /// <summary>True while some modal wants the backdrop. Its own fade finishing is not the same thing.</summary>
        private bool _backdropWanted;

        internal RectTransform Content => _content;

        internal RectTransform PoolRoot => _poolRoot;

        internal CanvasGroup Group => _group;

        internal Image Backdrop => _backdrop;

        internal CanvasGroup BackdropGroup => _backdropGroup;

        internal int TransitionDepth => _transitionDepth;

        internal IReadOnlyList<PopupView> LiveViews => _views;

        private void Awake()
        {
            EnsureInitialized();
        }

        /// <remarks>
        /// Idempotent and called from every entry point, not only from Awake: Unity does not run Awake on
        /// a component created outside play mode, and the EditMode suite drives this object directly. The
        /// alternative — an [ExecuteAlways] attribute — would also run it in the scene view, which is not
        /// what this needs.
        /// </remarks>
        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _onRequestCompleted = OnRequestCompleted;

            // The input gate below switches blocksRaycasts on THIS object's CanvasGroup, and the backdrop
            // is a descendant — so without this the gate would switch the backdrop off too, and every tap
            // during an open or close transition would fall straight through a modal onto the game canvas
            // behind it. Graphic.Raycast's upward walk stops collecting CanvasGroups at the first one
            // flagged ignoreParentGroups, while still honouring that group's own blocksRaycasts, so the
            // backdrop keeps blocking on its own authority for the whole transition.
            //
            // Resolved and forced here rather than trusted from the Inspector: the failure is invisible
            // (input silently passes through) and a prefab authored without the flag would reintroduce it.
            if (_backdropGroup == null)
            {
                _backdropGroup = _backdrop.GetComponent<CanvasGroup>();

                if (_backdropGroup == null)
                {
                    _backdropGroup = _backdrop.gameObject.AddComponent<CanvasGroup>();
                }
            }

            // Permanent: this is the C1 flag, and nothing may switch it off.
            _backdropGroup.ignoreParentGroups = true;

            // Whether it blocks is decided by ApplyBackdropState, which owns both this and raycastTarget.
            // The backdrop starts down.
            _backdropGroup.blocksRaycasts = false;

            if (_backdropButton != null)
            {
                // Wired once, here, for the same reason a popup's close buttons are: this object is never
                // destroyed between popups, so a per-show subscription would fire N times on the Nth show.
                _backdropButton.onClick.AddListener(OnBackdropTapped);
            }
        }

        // ---------------------------------------------------------------- the input gate

        /// <summary>
        /// Counted, not a boolean: the close transition of one popup and the open transition of the next
        /// overlap across two requests, and a boolean would let the first EndTransition reopen input while
        /// the second transition is still running.
        /// </summary>
        internal void BeginTransition()
        {
            EnsureInitialized();
            _transitionDepth++;
            if (_transitionDepth == 1)
            {
                _group.blocksRaycasts = false;
            }
        }

        internal void EndTransition()
        {
            if (_transitionDepth == 0)
            {
                Debug.LogWarning($"{nameof(PopupLayer)}.{nameof(EndTransition)} called with no transition running");
                return;
            }

            _transitionDepth--;
            if (_transitionDepth == 0)
            {
                _group.blocksRaycasts = true;

                // The backdrop is torn down HERE, not when its own fade ended. See ApplyBackdropState.
                ApplyBackdropState();
            }
        }

        // ---------------------------------------------------------------- the live-view list

        internal void Register(PopupView view)
        {
            EnsureInitialized();

            if (!_views.Contains(view))
            {
                _views.Add(view);
            }
        }

        internal void Unregister(PopupView view)
        {
            _views.Remove(view);
        }

        // ---------------------------------------------------------------- the backdrop

        /// <remarks>
        /// No caller token by design. This is layer-scoped work that outlives any one request, so a
        /// cancelled request must not leave the backdrop stranded at a partial alpha — the layer keeps its
        /// own source and cancels it when the next backdrop call starts.
        /// </remarks>
        internal async UniTask ShowBackdropBelowAsync(PopupView modal)
        {
            CancelBackdropWork();
            _backdropWanted = true;
            ApplyBackdropState();
            PlaceBackdropBelow(modal);

            _backdropCts = new CancellationTokenSource();
            CancellationToken token = _backdropCts.Token;

            try
            {
                await FadeBackdropAsync(_backdropAlpha, token);
            }
            catch (OperationCanceledException)
            {
                SetBackdropAlpha(_backdropAlpha);
            }
        }

        /// <remarks>
        /// This fades the backdrop out; it does NOT stop it blocking input. Those are two different
        /// lifetimes and conflating them reopens the game canvas for the rest of every close animation —
        /// the same window C1 closed on the opening half, reached by a different route. The fade is short
        /// (and outside play mode it settles synchronously), while the popup's own out-transition can run
        /// for as long as it likes; input belongs to the layer until the whole transition ends, which is
        /// what <see cref="EndTransition"/> decides.
        /// </remarks>
        internal async UniTask HideBackdropAsync()
        {
            CancelBackdropWork();

            _backdropWanted = false;
            _backdropCts = new CancellationTokenSource();
            CancellationToken token = _backdropCts.Token;

            try
            {
                await FadeBackdropAsync(0f, token);
            }
            catch (OperationCanceledException)
            {
                // Somebody else already owns the backdrop; it will set its own state.
                return;
            }

            ApplyBackdropState();
        }

        /// <summary>
        /// Synchronous, because the popup it must sit below is already on screen: an animated re-drop
        /// would show the resumed popup above its own dimming for the duration of the fade.
        /// </summary>
        internal void MoveBackdropBelow(PopupView modal)
        {
            CancelBackdropWork();
            _backdropWanted = true;
            ApplyBackdropState();
            PlaceBackdropBelow(modal);
            SetBackdropAlpha(_backdropAlpha);
        }

        internal void HideBackdropImmediate()
        {
            CancelBackdropWork();
            _backdropWanted = false;
            SetBackdropAlpha(0f);
            ApplyBackdropState();
        }

        /// <summary>
        /// The single owner of "is the backdrop up and swallowing input".
        ///
        /// Two conditions, not one: a modal wants it, OR a transition is still running and it was already
        /// up. The second is what keeps a stray tap off the game canvas for the whole close animation —
        /// rapid-fire input is a stated requirement, and a modal that stops being modal the moment it
        /// starts closing does not meet it. A popup that never raised the backdrop (a modeless one) leaves
        /// it down throughout, because the second condition is gated on it already being active.
        /// </summary>
        private void ApplyBackdropState()
        {
            EnsureInitialized();

            bool heldByTransition = _backdrop.gameObject.activeSelf && _transitionDepth > 0;
            bool active = _backdropWanted || heldByTransition;

            _backdrop.gameObject.SetActive(active);
            _backdrop.raycastTarget = active;
            _backdropGroup.blocksRaycasts = active;
        }

        // ---------------------------------------------------------------- service binding

        internal void Bind(IPopupService service)
        {
            EnsureInitialized();

            if (_service != null)
            {
                _service.RequestCompleted -= _onRequestCompleted;
            }

            _service = service;

            if (_service != null)
            {
                _service.RequestCompleted += _onRequestCompleted;
            }
        }

        /// <summary>
        /// When an interrupter terminates and a suspended modal becomes topmost again, the backdrop must
        /// drop below it — otherwise the resumed popup sits above its own dimming. The view is already
        /// unregistered by the time this runs: the core releases at terminal step 7 and raises this event
        /// at step 11.
        /// </summary>
        private void OnRequestCompleted(PopupCompletion completion)
        {
            PopupView top = FindTopmostModal();

            if (top == null)
            {
                HideBackdropImmediate();
                return;
            }

            MoveBackdropBelow(top);
        }

        private PopupView FindTopmostModal()
        {
            PopupView top = null;
            int topIndex = int.MinValue;

            for (int i = 0; i < _views.Count; i++)
            {
                PopupView view = _views[i];
                if (view == null || view.Modality != PopupModality.Modal)
                {
                    continue;
                }

                int index = view.transform.GetSiblingIndex();
                if (index > topIndex)
                {
                    topIndex = index;
                    top = view;
                }
            }

            return top;
        }

        private void OnBackdropTapped()
        {
            PopupView top = FindTopmostModal();
            if (top == null || !top.DismissOnBackdropTap)
            {
                // Swallowed on purpose: refusing the tap is what a non-dismissible modal is for.
                return;
            }

            top.RequestClose("backdrop");
        }

        // ---------------------------------------------------------------- internals

        private void PlaceBackdropBelow(PopupView modal)
        {
            int index = modal.transform.GetSiblingIndex();
            _backdrop.transform.SetSiblingIndex(index == 0 ? 0 : index - 1);
        }

        private UniTask FadeBackdropAsync(float targetAlpha, CancellationToken ct)
        {
            _backdrop.DOKill();

            if (_backdropFadeDuration <= 0f)
            {
                SetBackdropAlpha(targetAlpha);
                return UniTask.CompletedTask;
            }

            Tweener tween = _backdrop.DOFade(targetAlpha, _backdropFadeDuration).SetEase(Ease.Linear);
            return PopupTween.PlayAsync(tween, _backdrop, targetAlpha, ct);
        }

        private void SetBackdropAlpha(float alpha)
        {
            Color color = _backdrop.color;
            color.a = alpha;
            _backdrop.color = color;
        }

        private void CancelBackdropWork()
        {
            if (_backdropCts == null)
            {
                return;
            }

            _backdropCts.Cancel();
            _backdropCts.Dispose();
            _backdropCts = null;
        }

        private void OnDestroy()
        {
            if (_service != null)
            {
                _service.RequestCompleted -= _onRequestCompleted;
                _service = null;
            }

            if (_backdropButton != null)
            {
                _backdropButton.onClick.RemoveListener(OnBackdropTapped);
            }

            if (_backdrop != null)
            {
                _backdrop.DOKill();
            }

            CancelBackdropWork();
            _views.Clear();
        }

        // ---------------------------------------------------------------- runtime construction

        /// <summary>
        /// Builds the hierarchy of §3.1 in code. The demo scene may author the same thing by hand; the
        /// composition root and the suite use this so neither depends on a scene asset existing.
        /// </summary>
        internal static PopupLayer CreateRuntime(string name = "PopupLayer")
        {
            GameObject root = new GameObject(name, typeof(RectTransform), typeof(Canvas),
                                             typeof(CanvasScaler), typeof(GraphicRaycaster),
                                             typeof(CanvasGroup), typeof(PopupLayer));

            Canvas canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0f;

            RectTransform content = NewStretchedChild(root.transform, "SafeArea");
            content.gameObject.AddComponent<SafeAreaFitter>();

            RectTransform backdropRect = NewStretchedChild(content, "Backdrop");
            Image backdrop = backdropRect.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0f, 0f, 0f, 0f);
            backdrop.raycastTarget = true;
            CanvasGroup backdropGroup = backdropRect.gameObject.AddComponent<CanvasGroup>();
            backdropGroup.ignoreParentGroups = true;
            backdropGroup.blocksRaycasts = true;
            Button backdropButton = backdropRect.gameObject.AddComponent<Button>();
            backdropButton.transition = Selectable.Transition.None;
            backdropRect.gameObject.SetActive(false);

            RectTransform poolRoot = NewStretchedChild(root.transform, "PoolRoot");
            poolRoot.gameObject.SetActive(false);

            PopupLayer layer = root.GetComponent<PopupLayer>();
            layer._canvas = canvas;
            layer._group = root.GetComponent<CanvasGroup>();
            layer._content = content;
            layer._poolRoot = poolRoot;
            layer._backdrop = backdrop;
            layer._backdropGroup = backdropGroup;
            layer._backdropButton = backdropButton;
            return layer;
        }

        private static RectTransform NewStretchedChild(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
            return rect;
        }
    }
}
