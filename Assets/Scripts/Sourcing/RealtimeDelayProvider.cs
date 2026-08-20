using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups.Sourcing
{
    public sealed class RealtimeDelayProvider : IDelayProvider
    {
        private static readonly Action<object> CancelCallback = state => ((CancellationTokenSource)state).Cancel();

        public UniTask DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            // Realtime, so a paused game (timeScale 0) does not freeze a retry backoff.
            return UniTask.Delay(duration, DelayType.Realtime, PlayerLoopTiming.Update, ct);
        }

        /// <summary>
        /// Arms a whole-operation budget on <paramref name="cts"/> and hands back the timer that carries it.
        /// </summary>
        /// <remarks>
        /// The handle is the point. Disposing a token source does NOT stop a timer already armed on it, so a
        /// request that finishes before its budget leaves the timer running: it fires one budget later, calls
        /// Cancel() on a disposed source, and an ObjectDisposedException lands on the player loop after a
        /// request that succeeded. Returning the timer lets the caller's using-scope disarm it first —
        /// which works because a using-scope disposes in reverse order, so the timer goes before the source.
        /// </remarks>
        public IDisposable CancelAfter(CancellationTokenSource cts, TimeSpan duration)
        {
            return PlayerLoopTimer.StartNew(duration, false, DelayType.Realtime, PlayerLoopTiming.Update,
                                            CancellationToken.None, CancelCallback, cts);
        }
    }
}
