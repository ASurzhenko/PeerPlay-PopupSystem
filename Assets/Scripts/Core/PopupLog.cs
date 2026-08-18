using System.Diagnostics;
using Debug = UnityEngine.Debug;

namespace PeerPlay.Popups
{
    /// <summary>
    /// Debug-level logging for the paths that are neither misuse nor failure — a stale handle, a latch
    /// that absorbed a second completion. The attribute removes the call *and the evaluation of its
    /// argument*, so the interpolated string these callers build costs nothing unless POPUP_DIAGNOSTICS
    /// is defined. Warnings and errors are logged directly and always: they mark misuse or failure, not
    /// the measured path.
    /// </summary>
    internal static class PopupLog
    {
        [Conditional("POPUP_DIAGNOSTICS")]
        internal static void Trace(string message)
        {
            Debug.Log(message);
        }
    }
}
