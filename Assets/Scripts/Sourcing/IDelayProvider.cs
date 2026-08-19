using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Both time sources behind one seam, because both are PlayerLoop-driven and the suite is EditMode.
    /// Extending the core's IPopupClock instead was rejected on purpose: it is a core seam, and this
    /// package promises not to edit those.
    /// </summary>
    public interface IDelayProvider
    {
        UniTask DelayAsync(TimeSpan duration, CancellationToken ct);

        /// <summary>Arms a deadline on the source. Disposing it disarms the deadline.</summary>
        IDisposable CancelAfter(CancellationTokenSource cts, TimeSpan duration);
    }
}
