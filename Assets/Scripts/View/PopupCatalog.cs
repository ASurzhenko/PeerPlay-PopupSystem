using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The authored side of every popup — which asset backs a key, which art and copy it shows — plus
    /// whatever the remote config currently overrides on top of it. One serialized list, not a scriptable
    /// object per popup with an editor window over it: that answers nothing the spec asked for.
    ///
    /// The transition, the modality and the suspend mode are deliberately NOT authored here. They live on
    /// the prefab, and this type only carries the remote config's override of them.
    /// </summary>
    [CreateAssetMenu(menuName = "PeerPlay/Popup Catalog", fileName = "PopupCatalog")]
    public sealed class PopupCatalog : ScriptableObject, IPopupCatalogWriter
    {
        [Serializable]
        private sealed class AuthoredEntry
        {
            public string KeyId;
            public string AssetId;
            public string ImageUrl;
            public string TitleKey;
            public string BodyKey;
        }

        [SerializeField] private List<AuthoredEntry> _entries = new List<AuthoredEntry>();

        // Runtime only, and rebuilt wholesale on every adoption — an override the next config drops has
        // to disappear, not linger.
        private readonly Dictionary<string, PopupCatalogOverride> _overrides =
            new Dictionary<string, PopupCatalogOverride>(16);

        private Dictionary<string, AuthoredEntry> _authoredById;

        public void ApplyOverrides(IReadOnlyList<PopupCatalogOverride> overrides)
        {
            _overrides.Clear();

            if (overrides == null)
            {
                return;
            }

            for (int i = 0; i < overrides.Count; i++)
            {
                PopupCatalogOverride entry = overrides[i];
                if (string.IsNullOrEmpty(entry.KeyId))
                {
                    continue;
                }

                _overrides[entry.KeyId] = entry;
            }
        }

        /// <summary>
        /// The authored entry with any config override applied on top. A key the config never named
        /// resolves to the authored entry with all three view overrides null; a key only the config knows
        /// resolves from the override alone.
        /// </summary>
        public PopupCatalogEntry Resolve(string keyId)
        {
            EnsureIndex();

            bool hasOverride = _overrides.TryGetValue(keyId, out PopupCatalogOverride ovr);
            bool hasAuthored = _authoredById.TryGetValue(keyId, out AuthoredEntry authored);

            if (!hasAuthored && !hasOverride)
            {
                Debug.LogWarning($"{nameof(PopupCatalog)}.{nameof(Resolve)} no entry for key '{keyId}'");
                return default;
            }

            if (!hasOverride)
            {
                return new PopupCatalogEntry(authored.KeyId, authored.AssetId, authored.ImageUrl,
                                             authored.TitleKey, authored.BodyKey);
            }

            return new PopupCatalogEntry(
                keyId,
                string.IsNullOrEmpty(ovr.AssetId) && hasAuthored ? authored.AssetId : ovr.AssetId,
                ovr.ImageUrl,
                string.IsNullOrEmpty(ovr.TitleKey) && hasAuthored ? authored.TitleKey : ovr.TitleKey,
                string.IsNullOrEmpty(ovr.BodyKey) && hasAuthored ? authored.BodyKey : ovr.BodyKey,
                ovr.TransitionId,
                ovr.Modality,
                ovr.Suspend);
        }

        public bool Contains(string keyId)
        {
            EnsureIndex();
            return _authoredById.ContainsKey(keyId) || _overrides.ContainsKey(keyId);
        }

        /// <summary>
        /// Authoring hook for the composition root's scene setup and for the suite. The Inspector list is
        /// the normal route; this exists so a test does not need an asset on disk.
        /// </summary>
        internal void Author(string keyId, string assetId, string imageUrl = null, string titleKey = null,
                             string bodyKey = null)
        {
            EnsureIndex();

            if (!_authoredById.TryGetValue(keyId, out AuthoredEntry entry))
            {
                entry = new AuthoredEntry { KeyId = keyId };
                _entries.Add(entry);
                _authoredById[keyId] = entry;
            }

            entry.AssetId = assetId;
            entry.ImageUrl = imageUrl;
            entry.TitleKey = titleKey;
            entry.BodyKey = bodyKey;
        }

        private void EnsureIndex()
        {
            if (_authoredById != null)
            {
                return;
            }

            _authoredById = new Dictionary<string, AuthoredEntry>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                AuthoredEntry entry = _entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.KeyId))
                {
                    continue;
                }

                _authoredById[entry.KeyId] = entry;
            }
        }
    }
}
