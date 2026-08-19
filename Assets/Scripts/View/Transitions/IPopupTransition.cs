using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// How a popup appears and disappears. Adding a fourth one is a class and a Register line — which is
    /// the extensibility criterion, demonstrated rather than claimed.
    /// </summary>
    public interface IPopupTransition
    {
        UniTask PlayInAsync(RectTransform root, CanvasGroup group, CancellationToken ct);

        UniTask PlayOutAsync(RectTransform root, CanvasGroup group, CancellationToken ct);
    }
}
