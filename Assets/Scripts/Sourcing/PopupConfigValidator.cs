using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PeerPlay.Popups.View;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Rules 1–13: pure C#, no Unity async, no Addressables — which is what makes thirteen of the config
    /// tests runnable in EditMode with no scene, no catalog and no PlayerLoop. Rule 14 probes Addressables
    /// and lives on the service, because an AsyncOperationHandle cannot run inside a bool.
    ///
    /// All-or-nothing at the config level: a half-adopted config is the same failure as an empty one.
    /// </summary>
    public sealed class PopupConfigValidator
    {
        /// <summary>Bytes, not chars — a multi-byte payload is under the cap in chars and over it on the wire.</summary>
        internal const int MaxPayloadBytes = 256 * 1024;

        private readonly PopupTransitionRegistry _transitions;

        public PopupConfigValidator(PopupTransitionRegistry transitions)
        {
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        }

        public bool TryValidateStructure(string rawJson, out PopupConfigSnapshot snapshot, out string reason)
        {
            snapshot = null;

            // 1
            if (string.IsNullOrEmpty(rawJson))
            {
                return Reject("payload empty", out reason);
            }

            int bytes = Encoding.UTF8.GetByteCount(rawJson);
            if (bytes > MaxPayloadBytes)
            {
                return Reject($"payload {bytes}B exceeds the {MaxPayloadBytes}B cap", out reason);
            }

            // 2 — JsonUtility throws ArgumentException on malformed or empty JSON, so this is a real branch
            //     with a reason rather than a swallow.
            PopupConfigDto dto;
            try
            {
                dto = JsonUtility.FromJson<PopupConfigDto>(rawJson);
            }
            catch (Exception e)
            {
                return Reject($"malformed json — {e.Message}", out reason);
            }

            // 3
            if (dto == null || dto.version < 1)
            {
                return Reject($"version={(dto == null ? "<null>" : dto.version.ToString(CultureInfo.InvariantCulture))}",
                              out reason);
            }

            // 4 — a missing field of a [Serializable] CLASS type can come back non-null, so `popups == null`
            //     alone is not a reliable test. Both branches are checked.
            if (dto.popups == null || dto.popups.Length == 0)
            {
                return Reject("zero popups — this is the empty-publish case", out reason);
            }

            List<PopupRule> rules = new List<PopupRule>(dto.popups.Length);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < dto.popups.Length; i++)
            {
                PopupEntryDto entry = dto.popups[i];

                if (entry == null)
                {
                    return Reject($"entry[{i}] is null", out reason);
                }

                // 5
                if (string.IsNullOrEmpty(entry.id))
                {
                    return Reject($"entry[{i}] id missing", out reason);
                }

                if (!seen.Add(entry.id))
                {
                    return Reject($"duplicate id '{entry.id}'", out reason);
                }

                // 6
                if (string.IsNullOrEmpty(entry.assetId))
                {
                    return Reject($"'{entry.id}' assetId missing", out reason);
                }

                // 7
                bool disabled;
                if (string.Equals(entry.state, "enabled", StringComparison.Ordinal))
                {
                    disabled = false;
                }
                else if (string.Equals(entry.state, "disabled", StringComparison.Ordinal))
                {
                    disabled = true;
                }
                else
                {
                    return Reject($"'{entry.id}' state='{entry.state}' (expected enabled|disabled)", out reason);
                }

                // 8 — TryParse alone accepts any numeric string, including values outside the declared set,
                //     so "7" would yield a priority with no band and the queue scans exactly the four bands.
                if (!TryParseEnum(entry.priority, out PopupPriority priority))
                {
                    return Reject($"'{entry.id}' priority='{entry.priority}' is not a defined {nameof(PopupPriority)}",
                                  out reason);
                }

                if (!TryParseEnum(entry.sequencing, out PopupSequencing sequencing))
                {
                    return Reject($"'{entry.id}' sequencing='{entry.sequencing}' is not a defined {nameof(PopupSequencing)}",
                                  out reason);
                }

                if (!TryParseEnum(entry.modality, out PopupModality modality))
                {
                    return Reject($"'{entry.id}' modality='{entry.modality}' (expected Modal|Modeless)", out reason);
                }

                if (!TryParseEnum(entry.suspend, out PopupSuspendBehaviour suspend))
                {
                    return Reject($"'{entry.id}' suspend='{entry.suspend}' (expected Hide|StayVisible)", out reason);
                }

                // 9
                if (!_transitions.Contains(entry.transition))
                {
                    return Reject($"'{entry.id}' transition='{entry.transition}' not registered", out reason);
                }

                // 10
                if (!TryParseNonNegative(entry.cooldownSeconds, out int cooldown))
                {
                    return Reject($"'{entry.id}' cooldownSeconds='{entry.cooldownSeconds}'", out reason);
                }

                // 11
                if (!TryParseNonNegative(entry.maxPerSession, out int maxPerSession))
                {
                    return Reject($"'{entry.id}' maxPerSession='{entry.maxPerSession}' is not a number", out reason);
                }

                // 12
                if (entry.imageUrl == null
                    || (entry.imageUrl.Length > 0 && !entry.imageUrl.StartsWith("https://", StringComparison.Ordinal)))
                {
                    return Reject($"'{entry.id}' imageUrl not https", out reason);
                }

                // 13 — an absent titleKey reaches IPopupTextProvider.Get(null) and from there SetText(label, null).
                if (string.IsNullOrEmpty(entry.titleKey))
                {
                    return Reject($"'{entry.id}' titleKey missing", out reason);
                }

                if (string.IsNullOrEmpty(entry.bodyKey))
                {
                    return Reject($"'{entry.id}' bodyKey missing", out reason);
                }

                rules.Add(new PopupRule(entry.id, entry.assetId, disabled, priority, sequencing, modality,
                                        suspend, entry.transition, entry.imageUrl, entry.titleKey,
                                        entry.bodyKey, cooldown, maxPerSession));
            }

            snapshot = new PopupConfigSnapshot(dto.version, rules);
            reason = null;
            return true;
        }

        private static bool TryParseEnum<TEnum>(string value, out TEnum parsed) where TEnum : struct, Enum
        {
            parsed = default;

            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return Enum.TryParse(value, false, out parsed) && Enum.IsDefined(typeof(TEnum), parsed);
        }

        private static bool TryParseNonNegative(string value, out int parsed)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
        }

        private static bool Reject(string why, out string reason)
        {
            reason = why;
            Debug.LogError($"{nameof(PopupConfigValidator)}.{nameof(TryValidateStructure)} [Config] rejected: {why}");
            return false;
        }
    }
}
