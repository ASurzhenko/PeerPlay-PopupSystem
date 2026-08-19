using System.Collections.Generic;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The one path a remote config takes into the view layer. The composition root is the only place
    /// that sees both assemblies, so it is the only caller: it translates a config snapshot into view
    /// DTOs and pushes them here on every adoption, including the one at boot.
    /// </summary>
    public interface IPopupCatalogWriter
    {
        /// <summary>
        /// Replaces the whole override set. A key the list does not name resolves to its authored entry
        /// unchanged, which is what makes an override that a later config drops actually disappear.
        /// </summary>
        void ApplyOverrides(IReadOnlyList<PopupCatalogOverride> overrides);
    }
}
