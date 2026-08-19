using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace PeerPlay.Popups.App.Demo.Editor
{
    /// <summary>
    /// Copies the active Play Mode Script's name into a runtime string, once per domain load.
    ///
    /// It exists because the overlay must be able to NAME the mode without a runtime assembly referencing
    /// UnityEditor, and because the answer is not guessable: the active index is stored in Library/, which
    /// no clone carries, so the only trustworthy source is the editor this project is open in right now.
    /// </summary>
    internal static class DemoAddressablesModeProbe
    {
        [InitializeOnLoadMethod]
        private static void Probe()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;

            if (settings == null)
            {
                DemoAddressablesMode.Name = "no Addressables settings in this project";
                return;
            }

            int index = settings.ActivePlayModeDataBuilderIndex;

            DemoAddressablesMode.Name = index >= 0 && index < settings.DataBuilders.Count
                ? settings.DataBuilders[index].name
                : $"unknown (index {index})";
        }
    }
}
