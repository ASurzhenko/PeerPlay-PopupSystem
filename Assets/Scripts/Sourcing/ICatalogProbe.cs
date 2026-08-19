using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Everything the config service needs from Addressables, and nothing else — the same split
    /// <see cref="IPrefabLoader"/> makes, for the same reason: the suite drives the config service in an
    /// assembly that does not reference Addressables, and a private field of an Addressables type would
    /// drag the reference back in.
    /// </summary>
    public interface ICatalogProbe
    {
        /// <summary>
        /// Initialises Addressables and answers the one question rule 14 needs answered — "did the REMOTE
        /// catalog load" — by probing a sentinel entry that only exists in the remote group. Init succeeds
        /// even when the remote catalog did not, so inferring it from init is not an answer.
        /// </summary>
        UniTask InitializeAsync(CancellationToken ct);

        bool CatalogsReady { get; }

        /// <summary>True when every id resolves to at least one location.</summary>
        UniTask<bool> TryResolveAllAsync(IReadOnlyList<string> assetIds, CancellationToken ct);
    }
}
