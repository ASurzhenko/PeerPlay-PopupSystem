using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Remote popup content, fetched at display time. Declared here because the view assembly names it in
    /// three places — the factory's constructor, the view's own fetch, and the lease returned on release.
    /// </summary>
    public interface IRemoteImageSource
    {
        /// <summary>
        /// Null on transport, HTTP or deadline failure — all three are a normal result the view answers
        /// with its placeholder copy. Propagates <see cref="System.OperationCanceledException"/> ONLY when
        /// the caller's own token fired, which is a teardown and paints nothing.
        /// </summary>
        UniTask<Sprite> LoadAsync(string url, CancellationToken ct);

        /// <summary>Hands the sprite back so the cache may evict it. A leaked lease is an unbounded texture cache.</summary>
        void ReturnLease(Sprite sprite);
    }
}
