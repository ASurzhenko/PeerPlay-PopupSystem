using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    public sealed class ScalePopTransition : IPopupTransition
    {
        private const float StartScale = 0.85f;

        private readonly float _inDuration;
        private readonly float _outDuration;

        public ScalePopTransition(float inDuration = 0.25f, float outDuration = 0.18f)
        {
            _inDuration = inDuration;
            _outDuration = outDuration;
        }

        public UniTask PlayInAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            group.DOKill();
            root.DOKill();
            group.alpha = 0f;
            root.localScale = Vector3.one * StartScale;

            Tweener fade = group.DOFade(1f, _inDuration).SetEase(Ease.OutQuad);
            Tweener scale = root.DOScale(1f, _inDuration).SetEase(Ease.OutBack);

            return UniTask.WhenAll(PopupTween.PlayAsync(fade, group, 1f, ct),
                                   PopupTween.PlayAsync(scale, root, Vector3.one, ct));
        }

        public UniTask PlayOutAsync(RectTransform root, CanvasGroup group, CancellationToken ct)
        {
            group.DOKill();
            root.DOKill();

            Tweener fade = group.DOFade(0f, _outDuration).SetEase(Ease.InQuad);
            Tweener scale = root.DOScale(StartScale, _outDuration).SetEase(Ease.InBack);

            return UniTask.WhenAll(PopupTween.PlayAsync(fade, group, 0f, ct),
                                   PopupTween.PlayAsync(scale, root, Vector3.one * StartScale, ct));
        }
    }
}
