using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// One path for local and remote. The difference is which Addressables group the entry sits in, which
    /// is decided in the Addressables window and not in code — a reviewer expecting two code paths finding
    /// one is the point.
    /// </summary>
    public sealed class AddressablesPrefabLoader : IPrefabLoader
    {
        private readonly Dictionary<string, AsyncOperationHandle<GameObject>> _handles =
            new Dictionary<string, AsyncOperationHandle<GameObject>>(8);

        /// <remarks>
        /// The prefab is loaded, never instantiated by Addressables: InstantiateAsync/ReleaseInstance take
        /// instance ownership, and instance ownership belongs to the pool.
        ///
        /// autoReleaseWhenCanceled stays at its default of false. The layer above keeps its own refcount
        /// table, so letting UniTask release the handle too would double-release; release happens in one
        /// place, keyed by that refcount reaching zero.
        /// </remarks>
        public async UniTask<GameObject> LoadAsync(string assetId, CancellationToken ct)
        {
            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(assetId);
            _handles[assetId] = handle;

            try
            {
                return await handle.ToUniTask(cancellationToken: ct, cancelImmediately: true);
            }
            catch
            {
                Release(assetId);
                throw;
            }
        }

        public void Release(string assetId)
        {
            if (!_handles.TryGetValue(assetId, out AsyncOperationHandle<GameObject> handle))
            {
                return;
            }

            _handles.Remove(assetId);

            if (handle.IsValid())
            {
                Addressables.Release(handle);
            }
        }
    }
}
