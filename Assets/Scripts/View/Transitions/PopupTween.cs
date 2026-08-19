using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The DOTween discipline in one place instead of copied into every transition: linked to the
    /// GameObject, independent of timeScale, killed rather than abandoned on cancellation.
    ///
    /// The edit-mode branch is load-bearing for the suite. DOTween advances from DOTweenComponent's
    /// Update, which does not run outside play mode, and ManualUpdate only drives tweens explicitly set
    /// to UpdateType.Manual — so a tween awaited in EditMode never completes and the test hangs instead
    /// of failing. Outside the Editor the branch is dead: Application.isPlaying is always true there.
    /// </summary>
    internal static class PopupTween
    {
        internal static UniTask PlayAsync(Tweener tween, CanvasGroup group, float finalAlpha, CancellationToken ct)
        {
            if (!Application.isPlaying)
            {
                tween.Kill();
                group.alpha = finalAlpha;
                return UniTask.CompletedTask;
            }

            return Await(tween, group.gameObject, ct);
        }

        internal static UniTask PlayAsync(Tweener tween, RectTransform root, Vector3 finalScale, CancellationToken ct)
        {
            if (!Application.isPlaying)
            {
                tween.Kill();
                root.localScale = finalScale;
                return UniTask.CompletedTask;
            }

            return Await(tween, root.gameObject, ct);
        }

        internal static UniTask PlayAsync(Tweener tween, Graphic graphic, float finalAlpha, CancellationToken ct)
        {
            if (!Application.isPlaying)
            {
                tween.Kill();
                Color color = graphic.color;
                color.a = finalAlpha;
                graphic.color = color;
                return UniTask.CompletedTask;
            }

            return Await(tween, graphic.gameObject, ct);
        }

        /// <remarks>
        /// A token that fires DURING the tween kills it and the await completes normally; a token already
        /// cancelled at call time makes the await throw OperationCanceledException, which is the normal
        /// state on the core's close path after a caller cancel. Both leave the caller's finally intact.
        /// </remarks>
        private static UniTask Await(Tweener tween, GameObject link, CancellationToken ct)
        {
            return tween.SetLink(link).SetUpdate(true).ToUniTask(TweenCancelBehaviour.Kill, ct);
        }
    }
}
