using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Defaults;
using PeerPlay.Popups.Seams;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using UnityEngine;

namespace PeerPlay.Popups.App
{
    /// <summary>
    /// The composition root, and the only place that sees both View and Sourcing — which is precisely why
    /// the config-to-view translation belongs here and nowhere else.
    ///
    /// Adopting Zenject later is deleting the `new` lines below and writing the same number of
    /// Container.Bind lines in an installer. No type changes, no [Inject], no attribute — that claim is
    /// checkable in thirty seconds against this file, which is the point of writing it this way.
    /// </summary>
    public sealed class PopupSystemInstaller : MonoBehaviour
    {
        [SerializeField] private PopupLayer _layer;

        /// <summary>
        /// NOT addressable, and referenced directly. It is needed in Awake, before Addressables exists —
        /// and an asset that is both an addressable entry and a direct scene reference ships twice.
        /// </summary>
        [SerializeField] private TextAsset _builtInDefault;

        [SerializeField] private PopupCatalog _catalog;
        [SerializeField] private LocalPopupViewSource _localSource;
        /// <summary>
        /// The live endpoint, as a default rather than an empty field a scene has to remember to fill.
        /// Empty means "no remote config" and the built-in default carries the session — a real state, not
        /// a misconfiguration, which is why it is not treated as an error.
        /// </summary>
        [SerializeField]
        private string _configUrl = "https://d2eupgfrfppc7x.cloudfront.net/peerplay/config/popup-config.json";
        [SerializeField] private bool _useRemote = true;

        // Concrete, not the interface: IPopupService declares no Dispose, and RemoteImageSource is an
        // IDisposable this object owns.
        private PopupService _service;
        private RemoteImageSource _images;
        private PooledPopupViewFactory _factory;
        private RemotePopupConfigService _config;
        private ConfigPopupPolicy _policy;
        private ConfiguredPopupTrigger _trigger;
        private PopupTransitionRegistry _transitions;
        private PopupCatalogConfigBridge _catalogBridge;

        public IPopupService Service => _service;

        public ConfiguredPopupTrigger Trigger => _trigger;

        public RemotePopupConfigService Config => _config;

        private void Awake()
        {
            IPopupClock clock = new UnityPopupClock();
            IPopupTextProvider text = new PassthroughTextProvider();
            IPopupAnalytics analytics = new LoggingPopupAnalytics();
            IDelayProvider delays = new RealtimeDelayProvider();
            IHttpClient http = new HttpWithRetry(new UnityWebRequestHttpClient(), delays);

            // Before the validator, because rule 9 asks it whether a transition id is registered.
            _transitions = new PopupTransitionRegistry()
                .Register("instant", new InstantTransition())
                .Register("fade", new FadeTransition())
                .Register("scale_pop", new ScalePopTransition());

            PopupConfigCache cache =
                new PopupConfigCache(Path.Combine(Application.persistentDataPath, "popup-config.json"));
            PopupConfigValidator validator = new PopupConfigValidator(_transitions);
            ICatalogProbe probe = _useRemote ? new AddressablesCatalogProbe() : null;

            _config = new RemotePopupConfigService(_builtInDefault, cache, validator, http, probe, _configUrl);
            _policy = new ConfigPopupPolicy(_config, clock);

            // The config-to-view path. Subscribing here rather than translating inline is what makes the
            // mechanism testable instead of buried in a MonoBehaviour's Awake.
            _catalogBridge = new PopupCatalogConfigBridge(_config, _catalog);

            _images = new RemoteImageSource(http, new SpriteLruCache(8));

            IPopupViewSource source = _useRemote
                ? new RefCountedViewSource(new AddressablesPrefabLoader())
                : (IPopupViewSource)_localSource;

            _factory = new PooledPopupViewFactory(_layer, _catalog, source, _transitions, text, _images);

            // Three arguments: the queue has no time-dependent behaviour, so the clock goes to the policy
            // that actually reads it.
            _service = new PopupService(_factory, _policy, analytics);
            _trigger = new ConfiguredPopupTrigger(_service, _config);

            _layer.Bind(_service);

            // Raises Adopted, so the overrides are live from the first frame and not only once the network
            // has answered.
            _config.AdoptBestAvailable();

            BootAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        private async UniTaskVoid BootAsync(CancellationToken ct)
        {
            if (_useRemote)
            {
                await _config.InitializeAddressablesAsync(ct);
            }

            await _config.RefreshAsync(ct);
        }

        private void OnDestroy()
        {
            _catalogBridge?.Dispose();

            if (_service != null)
            {
                // Closing first means every live view goes through the normal terminal path, so the pool
                // only has idle instances left to destroy.
                _service.ForceCloseAll();
                _service.Dispose();
            }

            _factory?.ClearPool();
            _images?.Dispose();
        }
    }
}
