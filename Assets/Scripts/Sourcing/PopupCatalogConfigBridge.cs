using System;
using System.Collections.Generic;
using PeerPlay.Popups.View;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The one path a remote config takes into the view layer: it subscribes to every adoption — the one
    /// at boot included — translates each rule into a view DTO and pushes the whole set into the catalog.
    ///
    /// It lives here rather than inline in the composition root for one reason: a subscription written in
    /// a MonoBehaviour's Awake cannot be tested, and this is a mechanism two review rounds found dead. The
    /// reference graph is unaffected — the sourcing assembly already references the view assembly, and the
    /// view assembly still knows nothing about configuration.
    /// </summary>
    public sealed class PopupCatalogConfigBridge : IDisposable
    {
        private readonly RemotePopupConfigService _config;
        private readonly IPopupCatalogWriter _catalog;

        // Reused, so an adoption allocates nothing per entry.
        private readonly List<PopupCatalogOverride> _overrides = new List<PopupCatalogOverride>(16);

        private bool _disposed;

        public PopupCatalogConfigBridge(RemotePopupConfigService config, IPopupCatalogWriter catalog)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

            _config.Adopted += OnConfigAdopted;

            // A config adopted before this was constructed would otherwise never reach the catalog.
            if (_config.Current != null && _config.Current.Count > 0)
            {
                OnConfigAdopted(_config.Current);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _config.Adopted -= OnConfigAdopted;
        }

        private void OnConfigAdopted(PopupConfigSnapshot snapshot)
        {
            _overrides.Clear();

            for (int i = 0; i < snapshot.Rules.Count; i++)
            {
                PopupRule rule = snapshot.Rules[i];
                _overrides.Add(new PopupCatalogOverride(rule.KeyId, rule.AssetId, rule.TransitionId,
                                                        rule.Modality, rule.Suspend, rule.ImageUrl,
                                                        rule.TitleKey, rule.BodyKey));
            }

            _catalog.ApplyOverrides(_overrides);
        }
    }
}
