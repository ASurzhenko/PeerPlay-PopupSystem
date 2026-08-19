using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Defaults;
using PeerPlay.Popups.View;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.ViewSourcing.Tests
{
    /// <summary>
    /// Builds a real layer, a real catalog and a real factory over fakes. Nothing here is a scene asset:
    /// the whole point of the layer's runtime constructor is that this suite needs no prefab on disk.
    /// </summary>
    internal sealed class ViewHarness : IDisposable
    {
        internal const string KeyId = "test";
        internal const string AssetId = "asset_test";
        internal const string OtherKeyId = "other";
        internal const string OtherAssetId = "asset_other";

        internal readonly PopupLayer Layer;
        internal readonly PopupCatalog Catalog;
        internal readonly FakeViewSource Source = new FakeViewSource();
        internal readonly FakeImages Images = new FakeImages();
        internal readonly HoldingTransition Holding = new HoldingTransition();
        internal readonly PopupTransitionRegistry Transitions = new PopupTransitionRegistry();
        internal readonly PooledPopupViewFactory Factory;
        internal readonly FakeService Service = new FakeService();

        private readonly List<GameObject> _owned = new List<GameObject>();

        internal ViewHarness(PopupModality modality = PopupModality.Modal,
                             PopupSuspendBehaviour suspend = PopupSuspendBehaviour.Hide,
                             string transitionId = "instant")
        {
            Layer = PopupLayer.CreateRuntime();
            _owned.Add(Layer.gameObject);

            Transitions.Register("instant", new InstantTransition());
            Transitions.Register("hold", Holding);

            Catalog = ScriptableObject.CreateInstance<PopupCatalog>();
            Catalog.Author(KeyId, AssetId, null, "title.key", "body.key");
            Catalog.Author(OtherKeyId, OtherAssetId, null, "title.key", "body.key");

            Source.Prefab = BuildPrefab<TestPopupView>("TestPopup", modality, suspend, transitionId);

            Factory = new PooledPopupViewFactory(Layer, Catalog, Source, Transitions,
                                                 new PassthroughTextProvider(), Images);

            Layer.Bind(Service);
        }

        internal GameObject BuildPrefab<TView>(string name, PopupModality modality,
                                               PopupSuspendBehaviour suspend, string transitionId,
                                               bool withCloseButton = false)
            where TView : PopupView
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup));
            go.SetActive(false);
            _owned.Add(go);

            TView view = go.AddComponent<TView>();

            Button[] buttons = null;

            if (withCloseButton)
            {
                GameObject buttonGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
                buttonGo.transform.SetParent(go.transform, false);
                buttons = new[] { buttonGo.GetComponent<Button>() };
            }

            view.AuthorForTests(modality, suspend, transitionId, closeButtons: buttons);
            return go;
        }

        /// <summary>Hands a GameObject to the harness so teardown destroys it.</summary>
        internal void Own(GameObject go)
        {
            _owned.Add(go);
        }

        internal TestPopupView Rent(string keyId = KeyId, CancellationToken ct = default)
        {
            return (TestPopupView)RentView(keyId, ct);
        }

        internal PopupView RentView(string keyId = KeyId, CancellationToken ct = default)
        {
            return (PopupView)Factory.CreateAsync(new PopupKey<TestPayload>(keyId), new TestPayload("x"), ct)
                                     .GetAwaiter().GetResult();
        }

        internal void Release(PopupView view)
        {
            Factory.Release(view);
        }

        public void Dispose()
        {
            Factory.ClearPool();

            for (int i = 0; i < _owned.Count; i++)
            {
                if (_owned[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_owned[i]);
                }
            }

            _owned.Clear();

            if (Catalog != null)
            {
                UnityEngine.Object.DestroyImmediate(Catalog);
            }
        }
    }

    /// <summary>
    /// Just enough IPopupService to own the RequestCompleted event the layer subscribes to. The backdrop's
    /// resume path is driven by that event, so the test has to be able to raise it.
    /// </summary>
    internal sealed class FakeService : IPopupService
    {
        public event Action<PopupCompletion> RequestCompleted;

        public int PendingCount => 0;

        public PopupState CurrentState => PopupState.None;

        public string CurrentKeyId => null;

        internal void RaiseCompleted(string keyId)
        {
            RequestCompleted?.Invoke(new PopupCompletion(1UL, keyId,
                new PopupResult(PopupOutcome.Completed, CloseSource.User, "close", null)));
        }

        internal bool HasSubscriber => RequestCompleted != null;

        public PopupHandle Show<TData>(in PopupKey<TData> key, in TData data, in ShowOptions options = default)
        {
            return default;
        }

        public UniTask<PopupResult> ShowAsync<TData>(in PopupKey<TData> key, in TData data,
                                                     in ShowOptions options = default,
                                                     CancellationToken cancellationToken = default)
        {
            return UniTask.FromResult(default(PopupResult));
        }

        public bool CloseTop(string action = null)
        {
            return false;
        }

        public void ForceCloseAll()
        {
        }
    }
}
