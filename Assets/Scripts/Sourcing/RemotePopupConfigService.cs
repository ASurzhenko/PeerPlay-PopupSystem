using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Three outcomes, not two. A config that was REFUSED and an answer that was merely IGNORED are
    /// different events — the first one is the payload's fault and is what the validator exists to catch,
    /// the second is this service correctly discarding a stale continuation. A caller that can only ask
    /// "was it adopted?" has to report them with the same word.
    /// </summary>
    public enum ConfigFetchOutcome : byte
    {
        /// <summary>
        /// No fetch has produced this result yet. Zero on purpose: a defaulted struct — a field nobody has
        /// assigned, a result read before the request finished — must not claim the one outcome that says
        /// something was written. The same reason ShowOptions offsets its priority so a defaulted struct
        /// does not silently mean Low.
        /// </summary>
        Unknown = 0,

        Adopted = 1,

        /// <summary>The payload was fetched and refused — invalid, or its assets do not resolve.</summary>
        Rejected = 2,

        /// <summary>A newer endpoint or refresh took over while this one was in flight. Nothing was written.</summary>
        Superseded = 3
    }

    public readonly struct ConfigFetchResult
    {
        public readonly ConfigFetchOutcome Outcome;
        public readonly string Reason;

        private ConfigFetchResult(ConfigFetchOutcome outcome, string reason)
        {
            Outcome = outcome;
            Reason = reason;
        }

        public bool Adopted => Outcome == ConfigFetchOutcome.Adopted;

        public static ConfigFetchResult Ok()
        {
            return new ConfigFetchResult(ConfigFetchOutcome.Adopted, null);
        }

        public static ConfigFetchResult Fail(string reason)
        {
            return new ConfigFetchResult(ConfigFetchOutcome.Rejected, reason);
        }

        public static ConfigFetchResult Stale(string reason)
        {
            return new ConfigFetchResult(ConfigFetchOutcome.Superseded, reason);
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
        private string _configUrl;

        /// <summary>
        /// Bumped by the endpoint setter AND by every RefreshAsync, so only the newest request may adopt or
        /// cache. See <see cref="ConfigUrl"/> for why a mutable endpoint on its own is not enough.
        /// </summary>
        private int _generation;

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

        /// <summary>
        /// Which of the three sources produced <see cref="Current"/> — "cache", "built-in" or "remote".
        /// Reported rather than inferred: the boot adoption happens inside Awake, before anything can
        /// subscribe to <see cref="Adopted"/>, so a UI that wants to say which branch ran has no other way
        /// to know, and guessing it from what was last requested is how a demo ends up claiming the cache
        /// carried a boot the built-in default actually carried.
        /// </summary>
        public string CurrentSource { get; private set; } = "none";

        /// <summary>
        /// The endpoint, changeable while the system runs: pointing a LIVE service at another config is the
        /// only way to show that a bad publish does not take the popups away — rebuilding the service per
        /// publish would prove the opposite claim.
        ///
        /// A mutable URL alone would be a race, so the write bumps a generation that every in-flight request
        /// re-checks before it adopts or writes the cache. Chosen over "capture the URL and compare it on
        /// completion" because two requests can share one URL (pressing the same button twice) and the older
        /// answer must still lose. An empty value keeps its existing meaning: no remote config.
        /// </summary>
        public string ConfigUrl
        {
            get => _configUrl;
            set
            {
                _configUrl = value;
                _generation++;
            }
        }

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
            // Claimed before the first await, and compared after every one of them: this request owns the
            // adoption only while nothing newer has been asked for.
            int generation = ++_generation;
            string url = _configUrl;

            if (string.IsNullOrEmpty(url))
            {
                return ConfigFetchResult.Fail("no config url");
            }

            HttpResult response = await _http.GetAsync(url, HttpTimeoutSeconds, ct);

            if (generation != _generation)
            {
                return Superseded(url);
            }

            if (!response.Ok || response.Data == null)
            {
                // Warning: the publish did not arrive, and whatever was adopted before is still serving.
                // The level tracks the consequence, not the event — see Reject in PopupConfigValidator.
                string why = $"fetch failed ({response.Failure}: {response.Error})";
                Debug.LogWarning($"{nameof(RemotePopupConfigService)}.{nameof(RefreshAsync)} [Config] {why}");
                return ConfigFetchResult.Fail(why);
            }

            string rawJson = Decode(response.Data);

            if (!_validator.TryValidateStructure(rawJson, out PopupConfigSnapshot snapshot, out string reason))
            {
                // The validator has already logged the refusal as a warning, and the current snapshot and
                // the cache are both left exactly as they were — which is why a warning is the right level.
                return ConfigFetchResult.Fail(reason);
            }

            bool resolutionRan = ShouldRunAssetResolution(CatalogsReady);
            bool resolved = !resolutionRan || await TryResolveAssetsAsync(snapshot, ct);

            // The second check, and the load-bearing one: Adopt and the cache write are the harmful writes,
            // and nothing awaits between here and them.
            //
            // It comes BEFORE the resolution verdict is interpreted on purpose. A superseded request has
            // been probing ids nobody asked for any more; blaming its config for "an assetId resolves to no
            // location" would put a red line in the console naming the wrong cause.
            if (generation != _generation)
            {
                return Superseded(url);
            }

            if (!resolved)
            {
                // Same rule as every other refusal on this path: the payload is dropped, the live config is
                // untouched, so this is a warning rather than an error.
                Debug.LogWarning($"{nameof(RemotePopupConfigService)}.{nameof(RefreshAsync)} " +
                                 "[Config] rejected: an assetId resolves to no location");
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
        /// A discarded answer is named, never silently dropped: "nothing happened" and "an older endpoint's
        /// answer arrived and was refused" look identical from the outside, and only one of them is a bug.
        /// </summary>
        private ConfigFetchResult Superseded(string url)
        {
            string why = $"a newer endpoint or refresh replaced '{url}' while it was in flight";
            Debug.LogWarning($"{nameof(RemotePopupConfigService)}.{nameof(RefreshAsync)} [Config] superseded — {why}");
            return ConfigFetchResult.Stale(why);
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

        /// <remarks>
        /// The id list is per call rather than a reused field. The probe iterates it across awaits, and
        /// overlapping refreshes are now a supported state — a shared buffer would let a newer request
        /// refill the list an older request's probe loop is still walking. It also does not log its own
        /// verdict: whether a failed resolution is the config's fault is only knowable after the caller has
        /// checked whether this request was superseded.
        /// </remarks>
        private async UniTask<bool> TryResolveAssetsAsync(PopupConfigSnapshot snapshot, CancellationToken ct)
        {
            List<string> assetIds = new List<string>(snapshot.Rules.Count);

            for (int i = 0; i < snapshot.Rules.Count; i++)
            {
                assetIds.Add(snapshot.Rules[i].AssetId);
            }

            return await _probe.TryResolveAllAsync(assetIds, ct);
        }

        private void Adopt(PopupConfigSnapshot snapshot, string source)
        {
            Current = snapshot;
            CurrentSource = source;
            Debug.Log($"{nameof(RemotePopupConfigService)}.{nameof(Adopt)} [Config] source={source} " +
                      $"popups={snapshot.Count}");
            Adopted?.Invoke(snapshot);
        }
    }
}
