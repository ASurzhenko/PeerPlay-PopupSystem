using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// HTTP behind a seam, which is what makes both the config path and the image path testable at all.
    ///
    /// Two methods rather than one because UnityWebRequestTexture decodes on a worker thread, while
    /// building a Texture2D from bytes with ImageConversion.LoadImage moves that decode onto the frame —
    /// a visible hitch on the spec's "the UI must remain responsive" line.
    /// </summary>
    public interface IHttpClient
    {
        UniTask<HttpResult> GetAsync(string url, int timeoutSeconds, CancellationToken ct);

        UniTask<HttpResult> GetTextureAsync(string url, int timeoutSeconds, CancellationToken ct);
    }
}
