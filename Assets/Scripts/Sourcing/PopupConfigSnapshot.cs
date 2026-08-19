using System.Collections.Generic;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// An immutable config, swapped by reference. No reader ever sees a torn config, and a refresh landing
    /// between the core's submit-time and admission-time policy checks is the demoed kill switch rather
    /// than a race.
    /// </summary>
    public sealed class PopupConfigSnapshot
    {
        public static readonly PopupConfigSnapshot Empty =
            new PopupConfigSnapshot(0, new List<PopupRule>(0));

        private readonly Dictionary<string, int> _indexByKey;
        private readonly List<PopupRule> _rules;

        public PopupConfigSnapshot(int version, List<PopupRule> rules)
        {
            Version = version;
            _rules = rules;
            _indexByKey = new Dictionary<string, int>(rules.Count);

            for (int i = 0; i < rules.Count; i++)
            {
                _indexByKey[rules[i].KeyId] = i;
            }
        }

        public int Version { get; }

        public int Count => _rules.Count;

        public IReadOnlyList<PopupRule> Rules => _rules;

        public bool TryGet(string keyId, out PopupRule rule)
        {
            if (keyId != null && _indexByKey.TryGetValue(keyId, out int index))
            {
                rule = _rules[index];
                return true;
            }

            rule = default;
            return false;
        }
    }
}
