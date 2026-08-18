using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups.Seams
{
    /// <summary>
    /// The core's whole view of a popup. No UnityEngine.UI type appears here, and none may: this
    /// interface is where "the queue logic is independent of the UI rendering" is either true or false.
    /// </summary>
    public interface IPopupView
    {
        UniTask PlayOpenAsync(CancellationToken ct);

        /// <summary>
        /// Resolves when the popup closes, whichever producer got there first — the user, or
        /// <see cref="RequestClose"/>. One channel, so the core awaits one thing and there is no race
        /// between two completions to arbitrate.
        /// </summary>
        UniTask<PopupCloseInfo> WaitForCloseAsync(CancellationToken ct);

        /// <summary>Code-initiated close. Resolves the same channel as a user close.</summary>
        void RequestClose(string action);

        UniTask PlayCloseAsync(CancellationToken ct);

        /// <summary>
        /// Preempted or resumed. The view decides what that looks like — hidden, or left visible behind
        /// the interrupter — because that decision is rendering, and rendering is not the queue's.
        /// </summary>
        void SetSuspended(bool suspended);
    }
}
