using PeerPlay.Popups.Seams;
using UnityEngine;

namespace PeerPlay.Popups.Defaults
{
    /// <summary>Writes the three events to the console until a real analytics backend is wired in.</summary>
    public sealed class LoggingPopupAnalytics : IPopupAnalytics
    {
        public void Shown(string keyId)
        {
            Debug.Log($"{nameof(LoggingPopupAnalytics)}.{nameof(Shown)} key={keyId}");
        }

        public void Dismissed(string keyId, string action)
        {
            Debug.Log($"{nameof(LoggingPopupAnalytics)}.{nameof(Dismissed)} key={keyId} action={action ?? "-"}");
        }

        public void Converted(string keyId, string action)
        {
            Debug.Log($"{nameof(LoggingPopupAnalytics)}.{nameof(Converted)} key={keyId} action={action ?? "-"}");
        }
    }
}
