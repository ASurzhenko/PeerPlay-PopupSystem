using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    public readonly struct ConfigFetchResult
    {
        public readonly bool Adopted;
        public readonly string Reason;

        private ConfigFetchResult(bool adopted, string reason)
        {
            Adopted = adopted;
            Reason = reason;
        }

        public static ConfigFetchResult Ok()
        {
            return new ConfigFetchResult(true, null);
        }

        public static ConfigFetchResult Fail(string reason)
        {
            return new ConfigFetchResult(false, reason);
        }
    }

    /// <summary>
    /// Where "a bad remote config cannot take the popups away" is either true or false.
    ///
    /// Three sources, one order, and every one of them goes through the same structural pass: the
    /// last-known-good cache, then the built-in default, then whatever the network eventually says.
    ///
    /// The cache wins at boot, and that is only defensible because of what is allowed into it: a payload is
    /// cached only after BOTH passes ran and passed. A config adopted with rule 14 skipped serves that
    /// session and is deliberately not persisted — otherwise the boot path, which can only afford the
    /// structural pass, would go on re-adopting something no catalog ever confirmed.
    /// </summary>
    public sealed class RemotePopupConfigService
    {
        internal const int HttpTimeoutSeconds = 5;

        private readonly TextAsset _builtInDefault;
        private readonly PopupConfigCache _cache;
        private readonly PopupConfigValidator _validator;
        private readonly IHttpClient _http;
        private readonly ICatalogProbe _probe;
        private readonly string _configUrl;
        private readonly List<string> _assetIdScratch = new List<string>(16);

        public RemotePopupConfigService(TextAsset builtInDefault, PopupConfigCache cache,
                                        PopupConfigValidator validator, IHttpClient http,
                                        ICatalogProbe probe, string configUrl)
        {
            _builtInDefault = builtInDefault;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _probe = probe;
            _configUrl = configUrl;

            Current = PopupConfigSnapshot.Empty;
        }

        public PopupConfigSnapshot Current { get; private set; }

        /// <summary>Raised on EVERY adoption, the one at boot included — the view overrides depend on it.</summary>
        public event Action<PopupConfigSnapshot> Adopted;

        /// <summary>
        /// Called once, synchronously, from Awake. Structural pass only: Addressables is not initialised
        /// yet, so rule 14 cannot run and deliberately does not.
        /// </summary>
        public void AdoptBestAvailable()
        {
            if (_cache.TryRead(out string cached)
                && _validator.TryValidateStructure(cached, out PopupConfigSnapshot fromCache, out _))
            {
                Adopt(fromCache, "cache");
                return;
            }

            if (_builtInDefault == null)
            {
                Debug.LogError($"{nameof(RemotePopupConfigService)}.{nameof(AdoptBestAvailable)} " +
                               "[Config] no built-in default assigned");
                return;
            }

            if (_validator.TryValidateStructure(_builtInDefault.text, out PopupConfigSnapshot fallback,
                                                out string reason))
            {
                Adopt(fallback, "built-in");
                return;
            }

            // A build error, not a runtime condition: the default ships inside the package.
            Debug.LogError($"{nameof(RemotePopupConfigService)}.{nameof(AdoptBestAvailable)} " +
                           $"[Config] built-in default failed validation: {reason}");
        }

        public UniTask InitializeAddressablesAsync(CancellationToken ct)
        {
            return _probe == null ? UniTask.CompletedTask : _probe.InitializeAsync(ct);
        }

        public async UniTask<ConfigFetchResult> RefreshAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_configUrl))
            {
                return ConfigFetchResult.Fail("no config url");
            }

            HttpResult response = await _http.GetAsync(_configUrl, HttpTimeoutSeconds, ct);

            if (!response.Ok || response.Data == null)
            {
                string why = $"fetch failed ({response.Failure}: {response.Error})";
                Debug.LogError($"{nameof(RemotePopupConfigService)}.{nameof(RefreshAsync)} [Config] {why}");
                return ConfigFetchResult.Fail(why);
            }

            string rawJson = Decode(response.Data);

            if (!_validator.TryValidateStructure(rawJson, out PopupConfigSnapshot snapshot, out string reason))
            {
                // Loudly, and the current snapshot and the cache are both left exactly as they were.
                return ConfigFetchResult.Fail(reason);
            }

            bool resolutionRan = ShouldRunAssetResolution(CatalogsReady);

            if (resolutionRan && !await TryResolveAssetsAsync(snapshot, ct))
            {
                return ConfigFetchResult.Fail("asset resolution failed");
            }

            Adopt(snapshot, "remote");

            // The cache is written ONLY when both passes actually ran. Skipping rule 14 is the right call
            // for THIS session — refusing a valid config exactly when the network is already degraded is
            // the wrong direction — but persisting that config would promote something never checked
            // against the catalogs into the last-known-good, and the boot path adopts the cache on the
            // structural pass alone. It would stay there, unchecked, across every future cold start.
            if (resolutionRan)
            {
                _cache.Write(rawJson);
            }
            else
            {
                Debug.LogWarning($"{nameof(RemotePopupConfigService)}.{nameof(RefreshAsync)} [Config] adopted " +
                                 "for this session but NOT cached — asset resolution was skipped, and the " +
                                 "last-known-good must only ever hold a fully validated config");
            }

            return ConfigFetchResult.Ok();
        }

        /// <summary>
        /// Bytes to string, at the one place the wire becomes text.
        ///
        /// The UTF-8 BOM is stripped because JsonUtility throws on a leading U+FEFF and a CDN is entitled
        /// to add one: the served bytes are whatever the origin was given, and re-uploading through a
        /// different tool is all it takes. Measured on the live distribution (2026-08-19) the config comes
        /// back with no BOM and no content-encoding — this exists so the day that changes is a no-op
        /// instead of a config that stops parsing in the field.
        /// </summary>
        internal static string Decode(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }

            int offset = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(data, offset, data.Length - offset);
        }

        internal bool CatalogsReady => _probe != null && _probe.CatalogsReady;

        /// <summary>
        /// Applying rule 14 unconditionally would reject a VALID config precisely when the network is
        /// already degraded. The skip is logged rather than silent, because "config adopted" looks
        /// identical either way.
        /// </summary>
        internal static bool ShouldRunAssetResolution(bool catalogsReady)
        {
            if (catalogsReady)
            {
                return true;
            }

            Debug.Log($"{nameof(RemotePopupConfigService)}.{nameof(ShouldRunAssetResolution)} " +
                      "[Config] adopted without asset-resolution check — remote catalog unavailable");
            return false;
        }

        private async UniTask<bool> TryResolveAssetsAsync(PopupConfigSnapshot snapshot, CancellationToken ct)
        {
            _assetIdScratch.Clear();

            for (int i = 0; i < snapshot.Rules.Count; i++)
            {
                _assetIdScratch.Add(snapshot.Rules[i].AssetId);
            }

            bool resolved = await _probe.TryResolveAllAsync(_assetIdScratch, ct);

            if (!resolved)
            {
                Debug.LogError($"{nameof(RemotePopupConfigService)}.{nameof(TryResolveAssetsAsync)} " +
                               "[Config] rejected: an assetId resolves to no location");
            }

            return resolved;
        }

        private void Adopt(PopupConfigSnapshot snapshot, string source)
        {
            Current = snapshot;
            Debug.Log($"{nameof(RemotePopupConfigService)}.{nameof(Adopt)} [Config] source={source} " +
                      $"popups={snapshot.Count}");
            Adopted?.Invoke(snapshot);
        }
    }
}
