using System.Threading;
using PeerPlay.Popups.Seams;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Every per-rent dependency, in one value. Having a single entry point is what lets each mechanism
    /// name exactly one call site: the layer arrives here (so the input gate and the backdrop have a
    /// receiver), and so does the request's token (so the image fetch is cancellable at all).
    /// </summary>
    public readonly struct PopupViewSetup
    {
        public readonly string KeyId;
        public readonly IPopupTransition Transition;
        public readonly IPopupTextProvider Text;
        public readonly PopupLayer Layer;
        public readonly IRemoteImageSource Images;
        public readonly CancellationToken Token;
        public readonly PopupCatalogEntry Entry;

        public PopupViewSetup(string keyId, IPopupTransition transition, IPopupTextProvider text,
                              PopupLayer layer, IRemoteImageSource images, CancellationToken token,
                              in PopupCatalogEntry entry)
        {
            KeyId = keyId;
            Transition = transition;
            Text = text;
            Layer = layer;
            Images = images;
            Token = token;
            Entry = entry;
        }
    }
}
