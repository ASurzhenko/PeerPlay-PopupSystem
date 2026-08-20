using System.Diagnostics;
using Debug = UnityEngine.Debug;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Debug-level logging for paths that are neither misuse nor failure — a close request a
    /// non-dismissible popup declined. The attribute removes the call and the evaluation of its argument,
    /// so the interpolated string costs nothing unless POPUP_DIAGNOSTICS is defined.
    /// </summary>
    internal static class PopupViewLog
    {
        [Conditional("POPUP_DIAGNOSTICS")]
        internal static void Trace(string message)
        {
            Debug.Log(message);
        }
    }
}
