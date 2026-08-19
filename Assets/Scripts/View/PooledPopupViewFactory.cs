using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Seams;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The core's single view seam, implemented. It answers two questions the core deliberately does not
    /// ask separately: how the instance is made and reset (here) and where the prefab came from (the
    /// injected <see cref="IPopupViewSource"/>, which is the sourcing package's problem).
    /// </summary>
    public sealed class PooledPopupViewFactory : IPopupViewFactory
    {
        private readonly PopupLayer _layer;
        private readonly PopupCatalog _catalog;
        private readonly IPopupViewSource _source;
        private readonly PopupTransitionRegistry _transitions;
        private readonly IPopupTextProvider _text;
        private readonly IRemoteImageSource _images;
        private readonly PopupPool _pool;

        public PooledPopupViewFactory(PopupLayer layer, PopupCatalog catalog, IPopupViewSource source,
                                      PopupTransitionRegistry transitions, IPopupTextProvider text,
                                      IRemoteImageSource images)
        {
            _layer = layer;
            _catalog = catalog;
            _source = source;
            _transitions = transitions;
            _text = text;
            _images = images;
            _pool = new PopupPool(source);
        }

        internal PopupPool Pool => _pool;

        /// <remarks>
        /// Not itself async: the core's seam passes the key and the payload by `in`, and an async method
        /// cannot have `in` parameters (CS1988). So this hop copies them out and the real work happens
        /// below — the seam keeps its no-copy call site, and the payload is copied exactly once.
        /// </remarks>
        public UniTask<IPopupView> CreateAsync<TData>(in PopupKey<TData> key, in TData data, CancellationToken ct)
        {
            return CreateInternalAsync(key.Id, data, ct);
        }

        private async UniTask<IPopupView> CreateInternalAsync<TData>(string keyId, TData data, CancellationToken ct)
        {
            // A request cancelled while it waited in a band rents nothing and instantiates nothing.
            ct.ThrowIfCancellationRequested();

            PopupCatalogEntry entry = _catalog.Resolve(keyId);

            PopupView rented = _pool.TryRent(keyId);

            // Explicit != null, never ??: PopupView derives from UnityEngine.Object, whose overloaded ==
            // reports a destroyed instance as null while ?? bypasses it and hands back a dead object.
            PopupView view = rented != null ? rented : await InstantiateAsync(keyId, entry, ct);

            // Before the type check below, so the mismatch path can return the instance under its key
            // rather than lose it.
            view.AssignKey(keyId);

            if (view is not IPopupDataReceiver<TData> receiver)
            {
                _pool.Return(view);
                throw new PopupBindException(
                    $"'{keyId}' resolves to {view.GetType().Name}, which does not accept {typeof(TData).Name}");
            }

            // The override if the config named one, otherwise the prefab's authored id — resolved here
            // rather than from view.TransitionId, which still holds the previous rent's answer.
            string transitionId = entry.TransitionId ?? view.AuthoredTransitionId;

            view.PrepareForUse(new PopupViewSetup(keyId, _transitions.Resolve(transitionId), _text,
                                                  _layer, _images, ct, entry));
            receiver.Bind(in data);
            return view;
        }

        public void Release(IPopupView view)
        {
            if (view is not PopupView typed)
            {
                return;
            }

            typed.OnReleased();
            _pool.Return(typed);
        }

        internal void ClearPool()
        {
            _pool.Clear();
        }

        private async UniTask<PopupView> InstantiateAsync(string keyId, PopupCatalogEntry entry, CancellationToken ct)
        {
            if (entry.IsEmpty || string.IsNullOrEmpty(entry.AssetId))
            {
                throw new PopupBindException($"'{keyId}' has no catalog entry, so there is no asset to load");
            }

            GameObject prefab = await _source.AcquirePrefabAsync(entry.AssetId, ct);

            // From here the acquire is OURS to unwind. Every exit below except the successful one used to
            // leave the refcount raised with no instance to ever lower it, and a refcount that never
            // reaches zero means Addressables.Release for that asset is unreachable for the session. The
            // missing-component branch is deterministic: one leaked count per attempt.
            try
            {
                if (prefab == null)
                {
                    throw new PopupBindException($"'{keyId}' asset '{entry.AssetId}' produced no prefab");
                }

                // Cancelled between the load and here: the core never learns this instance exists, so
                // releasing it is ours to do.
                ct.ThrowIfCancellationRequested();

                GameObject instance = Object.Instantiate(prefab, _layer.PoolRoot, false);
                instance.SetActive(false);

                PopupView view = instance.GetComponent<PopupView>();

                if (view == null)
                {
                    UnityObjects.Destroy(instance);
                    throw new PopupBindException(
                        $"'{keyId}' asset '{entry.AssetId}' has no {nameof(PopupView)} component");
                }

                // The acquire is now owned by this instance, and the pool releases it when the instance is
                // destroyed. One acquire, one instance, one release.
                _pool.NotifyInstantiated(keyId, entry.AssetId);
                return view;
            }
            catch
            {
                _source.ReleasePrefab(entry.AssetId);
                throw;
            }
        }
    }
}
