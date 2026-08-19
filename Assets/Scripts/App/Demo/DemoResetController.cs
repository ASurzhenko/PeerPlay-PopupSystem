using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Sourcing;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// The incident beats change state that outlives the Play session, and without a way back the demo is a
    /// mine: press the kill switch, restart Play, and 'info' is disabled from frame one.
    ///
    /// Two controls, because there are two different states worth showing and one button cannot mean both:
    ///
    /// * <b>Reset demo state</b> — WRITES the baseline into the last-known-good cache, clears the policy's
    ///   session counters and the request ledger, and republishes the baseline. The next start then boots
    ///   through <c>AdoptBestAvailable</c>'s <i>cache</i> arm, which is the mechanic the whole incident block
    ///   is about: a good config survives the restart. Deleting the file instead would leave that arm dead in
    ///   the one scene built to demonstrate it.
    /// * <b>Wipe cache</b> — DELETES the file. That is the genuine cold start before any publish has ever
    ///   succeeded, where the built-in default carries the session, and it is worth seeing on purpose.
    ///
    /// The boot pass runs the Reset semantics, and its cache write happens in Awake: the cache is read
    /// inside PopupSystemInstaller.Awake, and a Script Execution Order entry puts this component ahead of it.
    /// Everything that needs the installer's objects waits for the boot refresh first (see Start) so the
    /// demo never moves the endpoint out from under the live fetch.
    /// </summary>
    public sealed class DemoResetController : MonoBehaviour
    {
        /// <summary>
        /// Survives one Play restart so "Wipe cache" means what it says. Without it the boot pass re-seeds
        /// the cache before the installer reads it, the cache arm runs anyway, and the button's caption —
        /// "the next start boots on the built-in default" — would be false the moment it is tested.
        /// </summary>
        private const string SkipSeedKey = "peerplay.demo.skip-cache-seed";

        [SerializeField] private PopupSystemInstaller _installer;
        [SerializeField] private DemoConfigPublisher _publisher;
        [SerializeField] private DemoDirector _director;
        [SerializeField] private Button _resetButton;
        [SerializeField] private Button _wipeCacheButton;

        private UnityEngine.Events.UnityAction _onReset;
        private UnityEngine.Events.UnityAction _onWipe;

        private void Awake()
        {
            if (PlayerPrefs.GetInt(SkipSeedKey, 0) == 1)
            {
                // Exactly one start, and only after the reviewer asked for it: this is the cold start with
                // no last known good, so the built-in default carries the session.
                PlayerPrefs.DeleteKey(SkipSeedKey);
                PlayerPrefs.Save();
                return;
            }

            // Before the installer reads it. Writing rather than deleting is the whole point: the boot that
            // follows this line is the one the reviewer watches take its config from the cache. A failure
            // here is logged by the helper and leaves whatever the cache already held — the installer's
            // three-way fallback covers it, and nothing on screen claims otherwise.
            TryWriteBaselineToCache();
        }

        private void Start()
        {
            _onReset = ResetDemoState;
            _onWipe = WipeCache;

            if (_resetButton != null)
            {
                _resetButton.onClick.AddListener(_onReset);
            }

            if (_wipeCacheButton != null)
            {
                _wipeCacheButton.onClick.AddListener(_onWipe);
            }

            AfterBootAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        private void OnDestroy()
        {
            if (_resetButton != null && _onReset != null)
            {
                _resetButton.onClick.RemoveListener(_onReset);
            }

            if (_wipeCacheButton != null && _onWipe != null)
            {
                _wipeCacheButton.onClick.RemoveListener(_onWipe);
            }
        }

        /// <summary>
        /// The session is made deterministic only AFTER the installer's own fetch against the production
        /// endpoint has finished. Republishing the baseline before that would point the service at a fixture
        /// while the live request was still in flight — the live path would never run, and the fixture's own
        /// publish would come back superseded and open the demo on a failure line.
        /// </summary>
        private async UniTaskVoid AfterBootAsync(CancellationToken ct)
        {
            try
            {
                await _installer.WaitForBootRefreshAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ResetDemoState();
        }

        private void ResetDemoState()
        {
            PlayerPrefs.DeleteKey(SkipSeedKey);
            PlayerPrefs.Save();

            bool seeded = TryWriteBaselineToCache();

            _installer.Policy.ResetSessionState();
            _director.Ledger.Clear();

            // The line says what happened, not what was attempted. Printing "baseline written" without
            // having checked would be worse than printing nothing: the next boot would quietly adopt the
            // old cache, and after the kill-switch beat that is a disabled 'info' in front of the reviewer.
            if (seeded)
            {
                _director.Ledger.Note("demo state reset — baseline is the last known good");
                _publisher.ReportResetAction(
                    "reset: baseline written to the last-known-good cache, counters cleared");
            }
            else
            {
                _director.Ledger.Note("demo state reset — counters cleared, cache NOT written");
                _publisher.ReportResetAction(
                    "reset: counters cleared, but the cache could NOT be written — the next start boots on " +
                    "whatever it already held");
            }

            _publisher.Publish(DemoConfigPublisher.BaselineFixture);
        }

        private void WipeCache()
        {
            new PopupConfigCache(PopupSystemInstaller.CacheFilePath).Delete();

            PlayerPrefs.SetInt(SkipSeedKey, 1);
            PlayerPrefs.Save();

            _director.Ledger.Note("cache wiped");
            _publisher.ReportResetAction(
                "cache wiped: the NEXT start has no last known good and boots on the built-in default " +
                "(the one after it is seeded again)");
        }

        /// <summary>
        /// Seeds the last-known-good cache with the baseline fixture, and answers whether it landed.
        ///
        /// It is a plain file copy on purpose — going through the service would need a fetch, and this has
        /// to be finished before the installer's Awake reads the file. Two things can stop it, and both are
        /// reported rather than assumed:
        ///
        /// * the fixture may not be readable — the catch covers what File.ReadAllText actually throws, not
        ///   only IOException;
        /// * PopupConfigCache.Write swallows its own failure (it logs and returns void), so the only honest
        ///   confirmation that the bytes are there is reading them back.
        /// </summary>
        private static bool TryWriteBaselineToCache()
        {
            string fixturePath = Path.Combine(Application.streamingAssetsPath, "DemoConfigs",
                                              DemoConfigPublisher.BaselineFixture + ".json");

            string json;

            try
            {
                json = File.ReadAllText(fixturePath);
            }
            catch (Exception e) when (e is IOException
                                      || e is UnauthorizedAccessException
                                      || e is NotSupportedException
                                      || e is ArgumentException
                                      || e is System.Security.SecurityException)
            {
                // Nothing is written rather than something wrong being written: an unreadable fixture leaves
                // whatever the cache already held, and the installer's own three-way fallback handles it.
                Debug.LogWarning($"{nameof(DemoResetController)}.{nameof(TryWriteBaselineToCache)} " +
                                 $"could not read '{fixturePath}': {e.Message}");
                return false;
            }

            PopupConfigCache cache = new PopupConfigCache(PopupSystemInstaller.CacheFilePath);
            cache.Write(json);

            if (cache.TryRead(out string written) && string.Equals(written, json, StringComparison.Ordinal))
            {
                return true;
            }

            Debug.LogWarning($"{nameof(DemoResetController)}.{nameof(TryWriteBaselineToCache)} " +
                             $"the cache at '{cache.Path}' does not hold what was just written");
            return false;
        }
    }
}
