using System.Threading;
using Cysharp.Threading.Tasks;

namespace PeerPlay.Popups.Seams
{
    /// <summary>
    /// Creation, pooling and destruction of everything the player can see. The core never touches a
    /// GameObject; it asks this seam and releases through it.
    /// </summary>
    /// <remarks>
    /// Two obligations the core cannot check and the implementation must honour:
    /// <list type="number">
    /// <item>A pooled view must set up a <b>fresh close channel</b> on each CreateAsync. The core
    /// cancels the request's token before releasing, so an unresolved WaitForCloseAsync is always
    /// unwound — but reusing the previous channel would resolve the next popup instantly.</item>
    /// <item>If CreateAsync is cancelled after instantiating and before returning, it must release what
    /// it built. The core never learns that instance exists and cannot release it for you.</item>
    /// </list>
    /// </remarks>
    public interface IPopupViewFactory
    {
        UniTask<IPopupView> CreateAsync<TData>(in PopupKey<TData> key, in TData data, CancellationToken ct);

        void Release(IPopupView view);
    }
}
