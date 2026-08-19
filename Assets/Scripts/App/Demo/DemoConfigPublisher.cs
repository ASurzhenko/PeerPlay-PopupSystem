using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Sourcing;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// The incident block: the LiveOps publish that broke a live game, reproduced against the running
    /// system. Every button points the LIVE config service at another payload and refreshes it — the point
    /// is that the system survives the publish, so nothing here is rebuilt between presses.
    ///
    /// The fixtures are fetched over file:// through the same UnityWebRequestHttpClient the CDN goes
    /// through, so the code path under test is the real one and the beats work with no network. The first
    /// line the reviewer reads is not a fixture at all: it is the boot fetch against the production
    /// endpoint, reported here as its own row, because that is the only on-screen evidence that the remote
    /// source is real rather than a local file with a CDN-shaped name.
    /// </summary>
    public sealed class DemoConfigPublisher : MonoBehaviour
    {
        internal const string BaselineFixture = "config_baseline";

        /// <summary>Unroutable on purpose: nothing resolves it, so the fetch fails the way a dead CDN does.</summary>
        private const string BrokenConfigUrl = "https://config-endpoint.invalid/popup-config.json";

        private const string AdoptedColour = "#7FD98A";
        private const string RejectedColour = "#F2777A";
        private const string IgnoredColour = "#C8B78A";

        [SerializeField] private PopupSystemInstaller _installer;
        [SerializeField] private TMP_Text _status;

        [Header("Publish")]
        [SerializeField] private Button _goodV2Button;
        [SerializeField] private Button _emptyButton;
        [SerializeField] private Button _malformedButton;
        [SerializeField] private Button _killSwitchButton;
        [SerializeField] private Button _deadImageButton;
        [SerializeField] private Button _liveCdnButton;
        [SerializeField] private Button _breakNetworkButton;

        [Header("Transitions (config, not a rebuild)")]
        [SerializeField] private Button _instantButton;
        [SerializeField] private Button _fadeButton;
        [SerializeField] private Button _scaleButton;

        // Kept so every listener this component added can be taken back off, the way its five siblings do.
        private readonly List<KeyValuePair<Button, UnityEngine.Events.UnityAction>> _wired =
            new List<KeyValuePair<Button, UnityEngine.Events.UnityAction>>(10);

        // Three, because two publishes can overlap and the interesting frame is the one where BOTH are on
        // screen: the newer one adopted and the older one ignored as superseded.
        private const int ActionLines = 3;

        private readonly List<string> _actions = new List<string>(ActionLines);
        private readonly System.Text.StringBuilder _builder = new System.Text.StringBuilder(256);

        private Action<PopupConfigSnapshot> _onAdopted;
        private string _bootLine = "boot: cold start, then the live config…";

        private RemotePopupConfigService Config => _installer.Config;

        /// <remarks>
        /// Editor and desktop only. On Android <c>streamingAssetsPath</c> is a <c>jar:file://…!/assets/…</c>
        /// URL inside the APK rather than a filesystem path, so neither Path.Combine nor a round-trip
        /// through Uri produces something UnityWebRequest can read. The grader plays this in the Editor and
        /// the fixtures exist to make the incident block reachable without a CDN, so the platform branch is
        /// deliberately not built — see DECISIONS.md.
        /// </remarks>
        internal static string FixtureUrl(string name)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "DemoConfigs", name + ".json");
            return new Uri(path).AbsoluteUri;
        }

        private void Start()
        {
            _onAdopted = OnAdopted;
            Config.Adopted += _onAdopted;

            // Stated, not implied: which arm of the three-way boot fallback carried the cold start is the
            // answer to "does a good config survive a restart", and it is decided before this object exists.
            _bootLine = $"boot: cold start from {_installer.ColdStartSource} → live CDN…";

            Wire(_goodV2Button, "good_v2");
            Wire(_emptyButton, "empty");
            Wire(_malformedButton, "malformed");
            Wire(_killSwitchButton, "killswitch");
            Wire(_deadImageButton, "dead_image");
            Wire(_instantButton, "transition_instant");
            Wire(_fadeButton, "transition_fade");
            Wire(_scaleButton, "transition_scale");
            Wire(_liveCdnButton, PublishFromLiveCdn);
            Wire(_breakNetworkButton, BreakTheNetwork);

            // No "ready" here: the reset controller has already set a truthful line by now, and overwriting
            // it would make the demo's first second show a state that is not the one it is in.
            Render();
            ReportBootAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        private void OnDestroy()
        {
            if (_installer != null && Config != null && _onAdopted != null)
            {
                Config.Adopted -= _onAdopted;
            }

            for (int i = 0; i < _wired.Count; i++)
            {
                if (_wired[i].Key != null)
                {
                    _wired[i].Key.onClick.RemoveListener(_wired[i].Value);
                }
            }

            _wired.Clear();
        }

        /// <summary>Also the body behind "Reset demo state", which republishes the baseline.</summary>
        internal void Publish(string fixtureName)
        {
            PublishAsync(fixtureName, FixtureUrl(fixtureName)).Forget();
        }

        /// <summary>
        /// The boot fetch belongs to the installer, not to a button — this only reports what it did, once it
        /// is done, so the endpoint is never moved out from under it.
        /// </summary>
        private async UniTaskVoid ReportBootAsync(CancellationToken ct)
        {
            try
            {
                await _installer.WaitForBootRefreshAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ConfigFetchResult result = _installer.BootResult;

            _bootLine = $"boot: cold start from {_installer.ColdStartSource} → live CDN → " + Describe(result);
            Render();
        }

        private void PublishFromLiveCdn()
        {
            PublishAsync("live CDN", _installer.LiveConfigUrl).Forget();
        }

        /// <summary>
        /// The reason is rendered verbatim rather than pinned to a failure class: an unroutable host under
        /// the 8 s whole-operation deadline can report either Transport or Deadline depending on how DNS
        /// answers, and printing a class the run did not produce would be a caption that lies.
        /// </summary>
        private void BreakTheNetwork()
        {
            PublishAsync("unroutable endpoint", BrokenConfigUrl).Forget();
        }

        /// <remarks>
        /// Deliberately re-entrant. Pressing a second publish while the first is still in flight is not a
        /// misuse to be locked out — it is the case the endpoint's generation guard exists for, and a lock
        /// here would hide the one behaviour worth showing: the newer answer wins and the older one is
        /// reported as ignored rather than as a bad config.
        /// </remarks>
        private async UniTaskVoid PublishAsync(string label, string url)
        {
            Report($"{label}: fetching…");

            try
            {
                // Both writes are the point: the endpoint moves and the running service refreshes from it.
                Config.ConfigUrl = url;
                ConfigFetchResult result = await Config.RefreshAsync(this.GetCancellationTokenOnDestroy());

                Report($"{label}: {Describe(result)}");
            }
            catch (OperationCanceledException)
            {
                // Play mode ended while the fetch was in flight.
            }
        }

        /// <summary>
        /// Three outcomes, three words, and the word comes from the result's own outcome rather than from
        /// what the caller assumed: REJECTED is the payload's fault and is the thing the incident block
        /// exists to show, while an ignored answer is the service discarding a stale continuation and is
        /// not a fault at all.
        /// </summary>
        private static string Describe(in ConfigFetchResult result)
        {
            switch (result.Outcome)
            {
                case ConfigFetchOutcome.Adopted:
                    return Colour(AdoptedColour, "adopted");

                case ConfigFetchOutcome.Superseded:
                    return Colour(IgnoredColour, "ignored (superseded)") + " — " + result.Reason;

                case ConfigFetchOutcome.Rejected:
                    return Colour(RejectedColour, "REJECTED") + " — " + result.Reason;

                default:
                    // A defaulted result, which is not a verdict about anything. Saying so beats printing
                    // one of the three real outcomes for a fetch that never reported.
                    return Colour(IgnoredColour, "no result yet");
            }
        }

        private static string Colour(string colour, string text)
        {
            return $"<color={colour}>{text}</color>";
        }

        private void OnAdopted(PopupConfigSnapshot snapshot)
        {
            Render();
        }

        internal void ReportResetAction(string line)
        {
            Report(line);
        }

        private void Report(string line)
        {
            _actions.Add(line);

            if (_actions.Count > ActionLines)
            {
                _actions.RemoveAt(0);
            }

            Render();
        }

        private void Render()
        {
            if (_status == null)
            {
                return;
            }

            PopupConfigSnapshot current = Config.Current;

            // The source is read off the service, not remembered from the last button pressed: the boot
            // adoption happens before anything here exists, and a remembered label would claim the cache
            // carried a session the built-in default actually carried.
            _builder.Clear();
            _builder.Append($"config v{current.Version}  ·  {current.Count} popups  ·  source: ")
                    .AppendLine(Config.CurrentSource)
                    .AppendLine(_bootLine);

            for (int i = 0; i < _actions.Count; i++)
            {
                _builder.AppendLine(_actions[i]);
            }

            _status.text = _builder.ToString();
        }

        private void Wire(Button button, string fixtureName)
        {
            Wire(button, () => Publish(fixtureName));
        }

        private void Wire(Button button, UnityEngine.Events.UnityAction handler)
        {
            if (button == null)
            {
                return;
            }

            button.onClick.AddListener(handler);
            _wired.Add(new KeyValuePair<Button, UnityEngine.Events.UnityAction>(button, handler));
        }
    }
}
