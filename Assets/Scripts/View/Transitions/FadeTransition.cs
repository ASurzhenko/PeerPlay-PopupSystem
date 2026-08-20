using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    public sealed class FadeTransition : IPopupTransition
    {
        private readonly float _inDuration;
        private readonly float _outDuration;

        public FadeTransition(float inDuration = 0.2f, float outDuration = 0.15f)
        {
            _inDuration = inDuration;
            _outDuration = outDuration;
        }

        public UniTask PlayInAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            group.DOKill();
            root.localScale = Vector3.one;
            group.alpha = 0f;

            Tweener tween = group.DOFade(1f, _inDuration).SetEase(Ease.OutQuad);
            return PopupTween.PlayAsync(tween, group, 1f, ct);
        }

        public UniTask PlayOutAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            group.DOKill();

            Tweener tween = group.DOFade(0f, _outDuration).SetEase(Ease.InQuad);
            return PopupTween.PlayAsync(tween, group, 0f, ct);
        }
    }
}
