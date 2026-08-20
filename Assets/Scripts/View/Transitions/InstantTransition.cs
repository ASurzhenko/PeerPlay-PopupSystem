using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The zero-cost baseline, the fallback for an unknown id, and what the EditMode suite resolves —
    /// DOTween advances from a MonoBehaviour update that does not run outside play mode, so a test
    /// awaiting a real tween would hang rather than fail.
    /// </summary>
    public sealed class InstantTransition : IPopupTransition
    {
        public UniTask PlayInAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            group.alpha = 1f;
            root.localScale = Vector3.one;
            return UniTask.CompletedTask;
        }

        public UniTask PlayOutAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            group.alpha = 0f;
            return UniTask.CompletedTask;
        }
    }
}
