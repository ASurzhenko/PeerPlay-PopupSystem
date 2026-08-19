using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The Addressables half of the config service. Which catalogs load is the profile's remote-catalog
    /// setting resolved by InitializeAsync — there is no explicit LoadContentCatalogAsync anywhere, on
    /// purpose: two ways to load the same catalog is two ways to disagree about which one is live.
    /// </summary>
    public sealed class AddressablesCatalogProbe : ICatalogProbe
    {
        /// <summary>One entry in the remote group, so "is the remote catalog loaded" has a direct answer.</summary>
        public const string RemoteProbeKey = "popup_remote_probe";

        private AsyncOperationHandle<IResourceLocator> _initHandle;
        private bool _remoteProbe;

        public bool CatalogsReady =>
            _initHandle.IsValid()
            && _initHandle.Status == AsyncOperationStatus.Succeeded
            && _remoteProbe;

        public async UniTask InitializeAsync(CancellationToken ct)
        {
            _initHandle = Addressables.InitializeAsync(false);
            await _initHandle.ToUniTask(cancellationToken: ct);
            _remoteProbe = await ExistsAsync(RemoteProbeKey, ct);
        }

        public async UniTask<bool> TryResolveAllAsync(IReadOnlyList<string> assetIds, CancellationToken ct)
        {
            for (int i = 0; i < assetIds.Count; i++)
            {
                if (!await ExistsAsync(assetIds[i], ct))
                {
                    return false;
                }
            }

            return true;
        }

        /// <remarks>
        /// An unknown key completes with an EMPTY list rather than throwing, which is what makes this a
        /// question and not an exception handler. The handle is released immediately either way.
        /// </remarks>
        private static async UniTask<bool> ExistsAsync(string key, CancellationToken ct)
        {
            AsyncOperationHandle<IList<IResourceLocation>> handle =
                Addressables.LoadResourceLocationsAsync(key, null);

            try
            {
                IList<IResourceLocation> locations = await handle.ToUniTask(cancellationToken: ct);
                return locations != null && locations.Count > 0;
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"{nameof(AddressablesCatalogProbe)}.{nameof(ExistsAsync)} '{key}': {e.Message}");
                return false;
            }
            finally
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            }
        }
    }
}
