using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups
{
    /// <summary>
    /// What a game developer sees. Two entry points, because the common case wants one line and no
    /// awaiter, and the rarer case wants the outcome.
    /// </summary>
    public interface IPopupService
    {
        /// <summary>
        /// Requests a popup and returns immediately. Allocates no awaiter: use this whenever the result
        /// is not needed. Subscribe to <see cref="RequestCompleted"/> to observe terminals of these.
        /// </summary>
        PopupHandle Show<TData>(in PopupKey<TData> key, in TData data, in ShowOptions options = default);

        /// <summary>
        /// Requests a popup and resolves with its outcome. Cancellation is reported as
        /// <see cref="PopupOutcome.Cancelled"/>, never as an exception.
        /// </summary>
        /// <remarks>
        /// Await the returned task <b>exactly once</b> — awaiting a UniTask twice throws. Use
        /// <see cref="Show{TData}"/> when the result is not needed.
        /// </remarks>
        UniTask<PopupResult> ShowAsync<TData>(in PopupKey<TData> key, in TData data,
                                              in ShowOptions options = default,
                                              CancellationToken cancellationToken = default);

        /// <summary>
        /// Asks the popup the player is looking at to close. Acts on the occupant only, never on a
        /// suspended popup behind it, and refuses while a transition is running. False if there was
        /// nothing to close.
        /// </summary>
        bool CloseTop(string action = null);

        /// <summary>Cancels everything — occupant, suspended stack and pending queue — without opening anything.</summary>
        void ForceCloseAll();

        /// <summary>
        /// Every terminal, for every request, including the fire-and-forget ones nobody awaits. This is
        /// how a Show() caller learns about a LoadFailed or a Refused without taking an awaiter.
        /// </summary>
        event Action<PopupCompletion> RequestCompleted;

        /// <summary>Accepted requests that have not yet reached a terminal.</summary>
        int PendingCount { get; }

        /// <summary>The occupant's state, or <see cref="PopupState.None"/> when the slot is empty.</summary>
        PopupState CurrentState { get; }

        /// <summary>The occupant's key id, or null when the slot is empty.</summary>
        string CurrentKeyId { get; }
    }
}
