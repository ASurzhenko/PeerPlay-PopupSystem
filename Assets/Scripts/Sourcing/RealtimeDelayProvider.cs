using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups.Sourcing
{
    public sealed class RealtimeDelayProvider : IDelayProvider
    {
        public UniTask DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            // Realtime, so a paused game (timeScale 0) does not freeze a retry backoff.
            return UniTask.Delay(duration, DelayType.Realtime, PlayerLoopTiming.Update, ct);
        }

        public IDisposable CancelAfter(CancellationTokenSource cts, TimeSpan duration)
        {
            cts.CancelAfterSlim(duration, DelayType.Realtime);
            return Disarmed.Instance;
        }

        /// <remarks>
        /// CancelAfterSlim has no cancel-the-timer handle; the token source being disposed by the caller is
        /// what ends it. The IDisposable exists so the fake can disarm, and so the call site reads as a
        /// scoped budget.
        /// </remarks>
        private sealed class Disarmed : IDisposable
        {
            internal static readonly Disarmed Instance = new Disarmed();

            public void Dispose()
            {
            }
        }
    }
}
