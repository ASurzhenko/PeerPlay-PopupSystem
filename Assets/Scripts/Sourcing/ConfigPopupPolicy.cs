using System;
using System.Collections.Generic;
using PeerPlay.Popups.Seams;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The remote kill switch, per-family cooldowns and session caps, answered synchronously from the
    /// already-fetched snapshot. The core evaluates twice — at submit and again at admission — so a config
    /// flipped while a request waits in the queue still stops it opening.
    /// </summary>
    public sealed class ConfigPopupPolicy : IPopupPolicy
    {
        private readonly RemotePopupConfigService _config;
        private readonly IPopupClock _clock;
        private readonly Dictionary<string, DateTime> _cooldownUntil = new Dictionary<string, DateTime>(8);
        private readonly Dictionary<string, int> _shownThisSession = new Dictionary<string, int>(8);

        public ConfigPopupPolicy(RemotePopupConfigService config, IPopupClock clock)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public PopupDecision Evaluate(in PopupRequestInfo request)
        {
            PopupConfigSnapshot snapshot = _config.Current;

            // An unknown key is allowed, never refused: every local dialog on first launch is a key the
            // config has never heard of, and refusing would turn a legitimate cold-start state into a
            // loud failure.
            if (!snapshot.TryGet(request.KeyId, out PopupRule rule))
            {
                return PopupDecision.Allow;
            }

            if (rule.Disabled)
            {
                return PopupDecision.Refuse($"'{request.KeyId}' disabled by remote config");
            }

            string family = request.Family ?? request.KeyId;

            // Every lookup is TryGetValue. The indexer throws KeyNotFoundException before a popup of that
            // family has been shown — i.e. on the FIRST Show of every popup — and the core's guards would
            // turn that into a Faulted terminal, so the first popup of every family would silently never
            // open.
            if (_cooldownUntil.TryGetValue(family, out DateTime until) && _clock.UtcNow < until)
            {
                return PopupDecision.Refuse($"family '{family}' on cooldown until {until:HH:mm:ss}");
            }

            if (rule.MaxPerSession > 0
                && _shownThisSession.TryGetValue(request.KeyId, out int shown)
                && shown >= rule.MaxPerSession)
            {
                return PopupDecision.Refuse($"'{request.KeyId}' hit its session cap of {rule.MaxPerSession}");
            }

            return PopupDecision.Allow;
        }

        /// <summary>
        /// Counted at Active, which is where the core raises it — a refused, cancelled or failed popup must
        /// not burn the player's cap.
        /// </summary>
        public void NotifyShown(in PopupRequestInfo request)
        {
            string family = request.Family ?? request.KeyId;

            _shownThisSession.TryGetValue(request.KeyId, out int shown);
            _shownThisSession[request.KeyId] = shown + 1;

            if (_config.Current.TryGet(request.KeyId, out PopupRule rule) && rule.CooldownSeconds > 0)
            {
                _cooldownUntil[family] = _clock.UtcNow.AddSeconds(rule.CooldownSeconds);
            }
        }

        /// <summary>
        /// Forgets every cap and cooldown taken so far. Both dictionaries, because a cap and a cooldown are
        /// two halves of one rule and clearing one leaves the popup refused by the other.
        ///
        /// The product case is an account switch inside a running session: the counters are per-instance and
        /// a config republish does not touch them, so the incoming player would inherit the outgoing player's
        /// "already seen the weekend offer" for the rest of the session.
        /// </summary>
        public void ResetSessionState()
        {
            _shownThisSession.Clear();
            _cooldownUntil.Clear();
        }
    }
}
