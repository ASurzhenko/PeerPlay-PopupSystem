using System.Collections.Generic;
using PeerPlay.Popups.View;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Bounded, and it actually destroys what it evicts. An LRU that only drops references is a leak with
    /// a cap on nothing: the Texture2D stays alive in the native heap until the domain reloads.
    ///
    /// An entry still leased by a live view is never evicted, even over cap. Destroying a texture a popup
    /// is currently drawing is the destructive direction, so ambiguity resolves the other way and the
    /// overshoot is logged instead.
    /// </summary>
    public sealed class SpriteLruCache
    {
        private sealed class Node
        {
            internal string Url;
            internal Sprite Sprite;
            internal Texture2D Texture;
            internal int Leases;
            internal long Stamp;
        }

        private readonly Dictionary<string, Node> _byUrl;
        private readonly int _capacity;
        private long _clock;

        public SpriteLruCache(int capacity)
        {
            _capacity = Mathf.Max(1, capacity);
            _byUrl = new Dictionary<string, Node>(_capacity);
        }

        internal int Count => _byUrl.Count;

        /// <summary>A hit takes a lease, exactly like a fresh fetch does. The caller returns it either way.</summary>
        internal bool TryTake(string url, out Sprite sprite)
        {
            if (!_byUrl.TryGetValue(url, out Node node) || node.Sprite == null)
            {
                sprite = null;
                return false;
            }

            node.Leases++;
            node.Stamp = ++_clock;
            sprite = node.Sprite;
            return true;
        }

        /// <param name="leases">
        /// How many callers are about to hold this sprite. A coalesced fetch resolves several awaiters at
        /// once and each of them will return its own lease, so the count has to be taken here rather than
        /// one at a time — an entry that reached zero leases in between would be evictable while its
        /// awaiters were still resuming.
        /// </param>
        internal void Add(string url, Sprite sprite, Texture2D texture, int leases = 1)
        {
            if (_byUrl.TryGetValue(url, out Node existing))
            {
                existing.Leases += leases;
                existing.Stamp = ++_clock;
                return;
            }

            _byUrl[url] = new Node
            {
                Url = url,
                Sprite = sprite,
                Texture = texture,
                Leases = leases,
                Stamp = ++_clock
            };

            Trim();
        }

        internal void Release(Sprite sprite)
        {
            if (sprite == null)
            {
                return;
            }

            foreach (KeyValuePair<string, Node> pair in _byUrl)
            {
                if (!ReferenceEquals(pair.Value.Sprite, sprite))
                {
                    continue;
                }

                if (pair.Value.Leases > 0)
                {
                    pair.Value.Leases--;
                }

                Trim();
                return;
            }
        }

        internal void Clear()
        {
            foreach (KeyValuePair<string, Node> pair in _byUrl)
            {
                Destroy(pair.Value);
            }

            _byUrl.Clear();
        }

        private void Trim()
        {
            while (_byUrl.Count > _capacity)
            {
                Node victim = null;

                foreach (KeyValuePair<string, Node> pair in _byUrl)
                {
                    if (pair.Value.Leases > 0)
                    {
                        continue;
                    }

                    if (victim == null || pair.Value.Stamp < victim.Stamp)
                    {
                        victim = pair.Value;
                    }
                }

                if (victim == null)
                {
                    Debug.LogWarning($"{nameof(SpriteLruCache)}.{nameof(Trim)} over cap at {_byUrl.Count}/" +
                                     $"{_capacity}: every entry is still leased");
                    return;
                }

                _byUrl.Remove(victim.Url);
                Destroy(victim);
            }
        }

        private static void Destroy(Node node)
        {
            // Through the seam, because Object.Destroy throws in edit mode and the whole suite is EditMode.
            UnityObjects.Destroy(node.Sprite);
            UnityObjects.Destroy(node.Texture);
            node.Sprite = null;
            node.Texture = null;
        }
    }
}
