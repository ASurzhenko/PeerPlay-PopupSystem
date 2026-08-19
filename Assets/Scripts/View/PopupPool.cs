using System.Collections.Generic;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Idle instances per key, capped.
    ///
    /// It also owns half of the Addressables lifetime, and the rule is one-for-one: the factory raises the
    /// prefab's refcount once per instance it creates, and this releases it once per instance it destroys.
    /// Nothing here releases "per key" — a key acquired N times and released once leaves N-1 counts
    /// standing, so the handle is pinned for the session and the balance only appears to work in a demo
    /// where every asset happens to be taken exactly once.
    /// </summary>
    internal sealed class PopupPool
    {
        internal const int PerKeyCap = 2;

        private readonly Dictionary<string, Stack<PopupView>> _idle = new Dictionary<string, Stack<PopupView>>(8);
        private readonly Dictionary<string, int> _liveCount = new Dictionary<string, int>(8);

        /// <summary>keyId to the assetId its instances were acquired under, captured at instantiation.</summary>
        private readonly Dictionary<string, string> _assetIdByKey = new Dictionary<string, string>(8);

        private readonly IPopupViewSource _source;

        internal PopupPool(IPopupViewSource source)
        {
            _source = source;
        }

        internal int IdleCount(string keyId)
        {
            return _idle.TryGetValue(keyId, out Stack<PopupView> stack) ? stack.Count : 0;
        }

        internal int LiveCount(string keyId)
        {
            return _liveCount.TryGetValue(keyId, out int count) ? count : 0;
        }

        /// <summary>Null when nothing is idle for that key; the caller instantiates and calls <see cref="NotifyInstantiated"/>.</summary>
        internal PopupView TryRent(string keyId)
        {
            if (!_idle.TryGetValue(keyId, out Stack<PopupView> stack))
            {
                return null;
            }

            while (stack.Count > 0)
            {
                PopupView view = stack.Pop();

                // Unity's overloaded == reports a destroyed instance as null, and a scene unload can have
                // taken one while it sat here. `??` would bypass that operator and hand back a dead object.
                if (view != null)
                {
                    _liveCount[keyId] = LiveCount(keyId) + 1;
                    return view;
                }

                // Destroyed behind our back, so its refcount is ours to give back.
                ReleaseOne(keyId);
            }

            return null;
        }

        /// <summary>
        /// One instance was created, holding one prefab refcount on <paramref name="assetId"/>.
        ///
        /// Recording the id here rather than resolving it from the catalog at release time narrows the
        /// window but does not close it: this is ONE slot per popup key, so a config that re-points a key
        /// at a different asset mid-session cannot be represented. The pool is keyed by popup key, and
        /// re-keying it by asset is a structural change this does not make — so the case is detected,
        /// logged loudly, and resolved in the non-destructive direction instead of being papered over.
        /// The limitation is real and named: changing a live key's assetId requires a session restart.
        /// </summary>
        internal void NotifyInstantiated(string keyId, string assetId)
        {
            // Counted BEFORE this instance is added, so "outstanding" means instances that already hold a
            // refcount on the recorded asset.
            int outstanding = LiveCount(keyId) + IdleCount(keyId);

            if (_assetIdByKey.TryGetValue(keyId, out string recorded)
                && recorded != assetId
                && outstanding > 0)
            {
                // Re-pointing now would send every future release to the new asset: the old one would be
                // pinned for the session, and the new one's refcount would fall to zero while an instance
                // built from it is still on screen — an unloaded asset under a live popup. Keeping the old
                // mapping leaks at most the new instance's count, which is the survivable direction.
                Debug.LogError(
                    $"{nameof(PopupPool)}.{nameof(NotifyInstantiated)} key '{keyId}' was acquired as " +
                    $"'{recorded}' and is now '{assetId}' while {outstanding} instance(s) still hold the " +
                    "first. Keeping the original mapping: releasing an asset still in use is the worse " +
                    "failure. Restart the session to pick up the new asset for this key.");
            }
            else
            {
                _assetIdByKey[keyId] = assetId;
            }

            _liveCount[keyId] = LiveCount(keyId) + 1;
        }

        internal void Return(PopupView view)
        {
            if (view == null)
            {
                return;
            }

            string keyId = view.KeyId;

            if (string.IsNullOrEmpty(keyId))
            {
                // Unreachable on the normal path — AssignKey runs before the bind check for exactly this
                // reason — and with no key there is no asset id to release against, so it is logged rather
                // than silently absorbed.
                Debug.LogWarning($"{nameof(PopupPool)}.{nameof(Return)} a view came back with no key; " +
                                 "destroying it, and its prefab refcount cannot be attributed");
                UnityObjects.Destroy(view.gameObject);
                return;
            }

            int live = LiveCount(keyId);
            if (live > 0)
            {
                _liveCount[keyId] = live - 1;
            }

            if (!_idle.TryGetValue(keyId, out Stack<PopupView> stack))
            {
                stack = new Stack<PopupView>(PerKeyCap);
                _idle[keyId] = stack;
            }

            if (stack.Count < PerKeyCap)
            {
                // Kept, so it keeps holding its refcount.
                stack.Push(view);
                return;
            }

            UnityObjects.Destroy(view.gameObject);
            ReleaseOne(keyId);
        }

        internal void Clear()
        {
            foreach (KeyValuePair<string, Stack<PopupView>> pair in _idle)
            {
                Stack<PopupView> stack = pair.Value;

                while (stack.Count > 0)
                {
                    PopupView view = stack.Pop();

                    if (view != null)
                    {
                        UnityObjects.Destroy(view.gameObject);
                    }

                    ReleaseOne(pair.Key);
                }
            }

            _idle.Clear();
            _liveCount.Clear();
            _assetIdByKey.Clear();
        }

        /// <summary>Gives back exactly one of the refcounts this key's instances are holding.</summary>
        private void ReleaseOne(string keyId)
        {
            if (_source == null)
            {
                return;
            }

            if (!_assetIdByKey.TryGetValue(keyId, out string assetId) || string.IsNullOrEmpty(assetId))
            {
                // Nothing was ever instantiated under this key through us — a rented-only path, or a pool
                // cleared twice. Falling back to the catalog would risk releasing an asset we never took.
                return;
            }

            _source.ReleasePrefab(assetId);
        }
    }
}
