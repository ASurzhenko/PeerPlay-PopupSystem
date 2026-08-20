using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The thin Addressables adapter, separated from the refcount and coalescing bookkeeping above it.
    /// It exists so that bookkeeping is testable with a fake in an assembly that references neither
    /// Addressables nor a PlayerLoop — not to abstract Addressables.
    /// </summary>
    public interface IPrefabLoader
    {
        UniTask<GameObject> LoadAsync(string assetId, CancellationToken ct);

        void Release(string assetId);
    }
}
