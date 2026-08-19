using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Where a prefab comes from. Declared by the view layer and implemented by the sourcing layer, so
    /// the view assembly never names Addressables and the reference graph stays acyclic.
    /// </summary>
    public interface IPopupViewSource
    {
        /// <summary>The prefab, not an instance: instance lifetime belongs to the pool.</summary>
        UniTask<GameObject> AcquirePrefabAsync(string assetId, CancellationToken ct);

        /// <summary>Called once the pool's last instance of that asset is gone.</summary>
        void ReleasePrefab(string assetId);
    }
}
