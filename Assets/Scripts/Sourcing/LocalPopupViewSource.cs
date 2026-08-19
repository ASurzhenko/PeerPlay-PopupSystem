using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.View;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The offline sibling: a serialized assetId-to-prefab list, no Addressables and no network. It is
    /// what the demo's no-network switch points at, and what makes "the app is fully usable on first
    /// launch with no network" a thing you can demonstrate rather than assert.
    /// </summary>
    public sealed class LocalPopupViewSource : MonoBehaviour, IPopupViewSource
    {
        [Serializable]
        private sealed class Entry
        {
            public string AssetId;
            public GameObject Prefab;
        }

        [SerializeField] private List<Entry> _entries = new List<Entry>();

        private Dictionary<string, GameObject> _byId;

        public UniTask<GameObject> AcquirePrefabAsync(string assetId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            EnsureIndex();

            if (_byId.TryGetValue(assetId, out GameObject prefab) && prefab != null)
            {
                return UniTask.FromResult(prefab);
            }

            // A real branch with a reason, not a swallow: the core turns this into a LoadFailed the queue
            // advances past.
            throw new PopupLoadException($"'{assetId}' is not in the local source");
        }

        /// <remarks>Nothing to release: these prefabs are part of the build, not a handle.</remarks>
        public void ReleasePrefab(string assetId)
        {
        }

        internal void Author(string assetId, GameObject prefab)
        {
            EnsureIndex();
            _entries.Add(new Entry { AssetId = assetId, Prefab = prefab });
            _byId[assetId] = prefab;
        }

        private void EnsureIndex()
        {
            if (_byId != null)
            {
                return;
            }

            _byId = new Dictionary<string, GameObject>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.AssetId))
                {
                    continue;
                }

                _byId[entry.AssetId] = entry.Prefab;
            }
        }
    }
}
