using System.Collections.Generic;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// String id to transition. A string rather than [SerializeReference] because the remote config has to
    /// be able to name one, and a string is what survives a JSON round trip.
    /// </summary>
    public sealed class PopupTransitionRegistry
    {
        public const string FallbackId = "instant";

        private readonly Dictionary<string, IPopupTransition> _byId =
            new Dictionary<string, IPopupTransition>(8);

        public PopupTransitionRegistry Register(string id, IPopupTransition transition)
        {
            _byId[id] = transition;
            return this;
        }

        public bool Contains(string id)
        {
            return !string.IsNullOrEmpty(id) && _byId.ContainsKey(id);
        }

        /// <summary>
        /// An unknown id falls back to <see cref="FallbackId"/> and says so. It should be unreachable —
        /// the config validator rejects a payload naming a transition that is not registered — but a
        /// prefab authored with a typo would otherwise take a popup off the screen entirely.
        /// </summary>
        public IPopupTransition Resolve(string id)
        {
            if (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out IPopupTransition transition))
            {
                return transition;
            }

            Debug.LogWarning($"{nameof(PopupTransitionRegistry)}.{nameof(Resolve)} '{id}' is not registered; using '{FallbackId}'");
            return _byId.TryGetValue(FallbackId, out IPopupTransition fallback) ? fallback : null;
        }
    }
}
