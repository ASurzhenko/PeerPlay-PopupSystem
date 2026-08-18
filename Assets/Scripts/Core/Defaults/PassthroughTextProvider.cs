using System.Collections.Generic;
using PeerPlay.Popups.Seams;
using UnityEngine;

namespace PeerPlay.Popups.Defaults
{
    /// <summary>
    /// Returns the key as its own text. Enough to run the system with no localisation table, and it
    /// makes a missing key visible on screen rather than silently blank — logged once per key, because
    /// a missing string tends to be missing every frame.
    /// </summary>
    public sealed class PassthroughTextProvider : IPopupTextProvider
    {
        private readonly HashSet<string> _reported = new HashSet<string>();

        public bool TryGet(string key, out string value)
        {
            value = key;
            return false;
        }

        public string Get(string key)
        {
            if (_reported.Add(key))
            {
                Debug.LogWarning($"{nameof(PassthroughTextProvider)}.{nameof(Get)} no text for key={key}; showing the key");
            }

            return key;
        }
    }
}
