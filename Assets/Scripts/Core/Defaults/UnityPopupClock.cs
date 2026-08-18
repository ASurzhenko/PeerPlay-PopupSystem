using System;
using PeerPlay.Popups.Seams;
using UnityEngine;

namespace PeerPlay.Popups.Defaults
{
    /// <summary>The real clock. Tests swap in one they control, which is the whole point of the seam.</summary>
    public sealed class UnityPopupClock : IPopupClock
    {
        public DateTime UtcNow => DateTime.UtcNow;

        public float RealtimeSeconds => Time.realtimeSinceStartup;
    }
}
