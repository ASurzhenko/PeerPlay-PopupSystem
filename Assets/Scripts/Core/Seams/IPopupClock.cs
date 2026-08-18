using System;

namespace PeerPlay.Popups.Seams
{
    /// <summary>Injected so that caps and cooldowns are testable without waiting for real time.</summary>
    public interface IPopupClock
    {
        DateTime UtcNow { get; }

        float RealtimeSeconds { get; }
    }
}
