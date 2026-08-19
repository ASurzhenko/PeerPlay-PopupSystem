using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using PeerPlay.Popups.View;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace PeerPlay.Popups.ViewSourcing.Tests
{
    /// <summary>
    /// The view layer's floor. V6, V7 and V8 exist because two independent review rounds found the input
    /// gate, the backdrop and the resume reposition described but unreachable — each of these fails if its
    /// one call site is deleted, which is the only thing that keeps a mechanism alive.
    /// </summary>
    internal sealed class ViewTests
    {
        // ------------------------------------------------------------------ V1: the pooling invariant

        [Test]
        public void V1_RentedInstanceIsIndistinguishableFromAFreshOne()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                TestPopupView first = harness.Rent();

                int generationOnFirstRent = first.Generation;
                UniTask<PopupCloseInfo> firstChannel = first.WaitForCloseAsync(CancellationToken.None);

                // Dirty every row of the invariant table that a real popup can dirty.
                first.CanvasGroup.alpha = 0.37f;
                first.CanvasGroup.interactable = false;
                first.CanvasGroup.blocksRaycasts = false;
                first.transform.localScale = Vector3.one * 2f;
                ((RectTransform)first.transform).anchoredPosition = new Vector2(13f, -21f);
                first.SetSuspended(true);
                first.RequestClose("first");

                Assert.AreEqual(UniTaskStatus.Succeeded, firstChannel.Status, "the first channel must resolve");
                Assert.AreEqual(1, harness.Layer.LiveViews.Count, "a rented view is registered on the layer");

                harness.Release(first);

                Assert.AreEqual(0, harness.Layer.LiveViews.Count, "a released view is unregistered");
                Assert.IsFalse(first.gameObject.activeSelf, "a released view is inactive");
                Assert.AreSame(harness.Layer.PoolRoot, first.transform.parent, "a released view lives under the pool root");

                TestPopupView second = harness.Rent();

                Assert.AreSame(first, second, "the second rent must reuse the pooled instance");
                Assert.AreEqual(generationOnFirstRent + 1, second.Generation, "the generation counter advances per rent");
                Assert.AreEqual(ViewHarness.KeyId, second.KeyId, "the key is assigned at rent");
                Assert.AreEqual(0f, second.CanvasGroup.alpha, "alpha is reset");
                Assert.IsTrue(second.CanvasGroup.interactable, "interactivity is reset");
                Assert.IsTrue(second.CanvasGroup.blocksRaycasts, "raycast blocking is reset");
                Assert.AreEqual(Vector3.one, second.transform.localScale, "scale is reset");
                Assert.AreEqual(Vector2.zero, ((RectTransform)second.transform).anchoredPosition, "position is reset");
                Assert.IsFalse(second.IsSuspended, "suspension does not survive a rent");
                Assert.IsTrue(second.gameObject.activeSelf, "a rented view is active");
                Assert.AreSame(harness.Layer.Content, second.transform.parent, "a rented view lives under the layer content");
                Assert.AreEqual(1, harness.Layer.LiveViews.Count, "the rent re-registers it");
                Assert.AreEqual(2, second.BindCount, "the payload is bound on every rent");

                UniTask<PopupCloseInfo> secondChannel = second.WaitForCloseAsync(CancellationToken.None);
                Assert.AreEqual(UniTaskStatus.Pending, secondChannel.Status,
                                "a fresh close channel per rent — reusing the previous one resolves the next popup instantly");
            }
        }

        [Test]
        public void V1b_CloseButtonListenersDoNotAccumulateAcrossRents()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                harness.Source.Prefab = harness.BuildPrefab<TestPopupView>(
                    "WithButton", PopupModality.Modal, PopupSuspendBehaviour.Hide, "instant", withCloseButton: true);

                TestPopupView view = harness.Rent();
                harness.Release(view);
                view = harness.Rent();
                harness.Release(view);
                view = harness.Rent();

                UnityEngine.UI.Button button = view.GetComponentInChildren<UnityEngine.UI.Button>(true);
                button.onClick.Invoke();

                Assert.AreEqual(1, view.CloseButtonInvocations,
                                "one click must run the handler once, however many times the view was rented");
            }
        }

        [Test]
        public void V1c_ConfigOverridesDoNotStickWhenALaterConfigDropsThem()
        {
            using (ViewHarness harness = new ViewHarness(suspend: PopupSuspendBehaviour.Hide))
            {
                TestPopupView authored = harness.Rent();
                Assert.AreEqual(PopupSuspendBehaviour.Hide, authored.SuspendBehaviour, "the prefab's value to begin with");
                harness.Release(authored);

                harness.Catalog.ApplyOverrides(new List<PopupCatalogOverride>
                {
                    new PopupCatalogOverride(ViewHarness.KeyId, ViewHarness.AssetId, "instant",
                                             PopupModality.Modeless, PopupSuspendBehaviour.StayVisible,
                                             "", "title.key", "body.key")
                });

                TestPopupView overridden = harness.Rent();
                Assert.AreEqual(PopupSuspendBehaviour.StayVisible, overridden.SuspendBehaviour, "the config wins while it names the key");
                Assert.AreEqual(PopupModality.Modeless, overridden.Modality, "and it owns the modality too");
                harness.Release(overridden);

                harness.Catalog.ApplyOverrides(new List<PopupCatalogOverride>());

                TestPopupView restored = harness.Rent();
                Assert.AreEqual(PopupSuspendBehaviour.Hide, restored.SuspendBehaviour,
                                "a config that drops the override must leave the AUTHORED value, not the previous config's");
                Assert.AreEqual(PopupModality.Modal, restored.Modality);
            }
        }

        // ------------------------------------------------------------------ V2: the pool cap

        /// <summary>
        /// The count, not merely the presence. A pool that releases once per KEY balances only in a demo
        /// where every asset is taken exactly once; with three acquisitions and one release the refcount
        /// never reaches zero and the handle is pinned for the session. So this asserts acquires ==
        /// releases across a whole cycle, which is the assertion "Releases contains the id" cannot make.
        /// </summary>
        [Test]
        public void V2_EveryPrefabAcquisitionIsBalancedByExactlyOneRelease()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                TestPopupView a = harness.Rent();
                TestPopupView b = harness.Rent();
                TestPopupView c = harness.Rent();

                Assert.AreEqual(3, harness.Source.Acquires.Count, "three instances means three acquisitions");
                CollectionAssert.IsEmpty(harness.Source.Releases, "all three are live");

                harness.Release(a);
                harness.Release(b);
                harness.Release(c);

                Assert.AreEqual(PopupPool.PerKeyCap, harness.Factory.Pool.IdleCount(ViewHarness.KeyId),
                                "the pool keeps at most the cap");
                Assert.IsTrue(a == null || b == null || c == null,
                              "the instance over the cap is destroyed through the seam");
                Assert.AreEqual(1, harness.Source.Releases.Count,
                                "destroying one instance gives back exactly one refcount, not the key's whole count");

                harness.Factory.ClearPool();

                Assert.AreEqual(harness.Source.Acquires.Count, harness.Source.Releases.Count,
                                "acquires and releases balance one-for-one over the full cycle");

                foreach (string released in harness.Source.Releases)
                {
                    Assert.AreEqual(ViewHarness.AssetId, released, "and against the asset they were taken on");
                }
            }
        }

        /// <summary>
        /// Every exit from the instantiation path after the acquire has to give the refcount back. The
        /// missing-component branch is the deterministic one: without the unwind it leaks one count per
        /// attempt, and Addressables.Release for that asset becomes unreachable for the session.
        /// </summary>
        [Test]
        public void V2b_AThrowAfterTheAcquireStillReleasesThePrefab()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                // A prefab with no PopupView on it at all.
                GameObject bare = new GameObject("NoView", typeof(RectTransform));
                bare.SetActive(false);
                harness.Own(bare);
                harness.Source.Prefab = bare;

                Assert.Throws<PopupBindException>(() =>
                    harness.Factory.CreateAsync(new PopupKey<TestPayload>(ViewHarness.KeyId),
                                                new TestPayload("x"), CancellationToken.None)
                           .GetAwaiter().GetResult());

                Assert.AreEqual(1, harness.Source.Acquires.Count);
                Assert.AreEqual(1, harness.Source.Releases.Count,
                                "the acquire is unwound on the way out, not left standing");
            }
        }

        [Test]
        public void V2c_ACancelBetweenTheLoadAndTheInstantiateReleasesThePrefab()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                // Cancelled the moment the prefab arrives — the window between the acquire and the
                // instantiate, which is the other post-acquire exit.
                using (CancellationTokenSource cts = new CancellationTokenSource())
                {
                    harness.Source.OnAcquired = () => cts.Cancel();

                    Assert.Throws<OperationCanceledException>(() =>
                        harness.Factory.CreateAsync(new PopupKey<TestPayload>(ViewHarness.KeyId),
                                                    new TestPayload("x"), cts.Token)
                               .GetAwaiter().GetResult());
                }

                Assert.AreEqual(1, harness.Source.Acquires.Count);
                Assert.AreEqual(1, harness.Source.Releases.Count, "a cancelled create pins nothing");
            }
        }

        /// <summary>
        /// The pool holds one asset slot per popup key, so a config that re-points a live key mid-session
        /// cannot be represented. That is a real limitation, not a prevented one — so it has to be loud and
        /// it has to fail safe: releasing an asset a live popup is still built from is the worse outcome
        /// than leaking one count, so the original mapping stands.
        /// </summary>
        [Test]
        public void V2d_ARepointedAssetIdWithLiveInstancesIsLoggedAndIgnored()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                TestPopupView live = harness.Rent();
                Assert.AreEqual(ViewHarness.AssetId, harness.Source.Acquires[0]);

                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                    "was acquired as '" + ViewHarness.AssetId + "' and is now 'asset_swapped'"));

                // What a mid-session config adoption would do.
                harness.Catalog.ApplyOverrides(new List<PopupCatalogOverride>
                {
                    new PopupCatalogOverride(ViewHarness.KeyId, "asset_swapped", null,
                                             PopupModality.Modal, PopupSuspendBehaviour.Hide,
                                             "", "title.key", "body.key")
                });

                TestPopupView second = harness.Rent();
                Assert.AreEqual("asset_swapped", harness.Source.Acquires[1], "the new instance loads the new asset");

                harness.Release(second);
                harness.Release(live);
                harness.Factory.ClearPool();

                foreach (string released in harness.Source.Releases)
                {
                    Assert.AreEqual(ViewHarness.AssetId, released,
                                    "every release still goes to the originally recorded asset — the live " +
                                    "one is never released out from under its instance");
                }
            }
        }

        // ------------------------------------------------------------------ V3: the bind type check

        [Test]
        public void V3_BindMismatchThrowsNamingBothTypesAndReturnsTheViewUnderItsKey()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                harness.Source.Prefab = harness.BuildPrefab<WrongPayloadPopupView>(
                    "Wrong", PopupModality.Modal, PopupSuspendBehaviour.Hide, "instant");

                PopupBindException error = Assert.Throws<PopupBindException>(() =>
                    harness.Factory.CreateAsync(new PopupKey<TestPayload>(ViewHarness.KeyId),
                                                new TestPayload("x"), CancellationToken.None)
                           .GetAwaiter().GetResult());

                StringAssert.Contains(nameof(WrongPayloadPopupView), error.Message, "the message names the view type");
                StringAssert.Contains(nameof(TestPayload), error.Message, "and the payload type");

                Assert.AreEqual(1, harness.Factory.Pool.IdleCount(ViewHarness.KeyId),
                                "the instance goes back under its key rather than being lost");
            }
        }

        // ------------------------------------------------------------------ V4: a cancelled request rents nothing

        [Test]
        public void V4_ACancelledRequestRentsNothingAndNeverBinds()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                TestPopupView warm = harness.Rent();
                int bindsBefore = warm.BindCount;
                harness.Release(warm);

                using (CancellationTokenSource cts = new CancellationTokenSource())
                {
                    cts.Cancel();

                    Assert.Throws<OperationCanceledException>(() =>
                        harness.Factory.CreateAsync(new PopupKey<TestPayload>(ViewHarness.KeyId),
                                                    new TestPayload("x"), cts.Token)
                               .GetAwaiter().GetResult());
                }

                Assert.AreEqual(1, harness.Factory.Pool.IdleCount(ViewHarness.KeyId), "the pool is untouched");
                Assert.AreEqual(0, harness.Factory.Pool.LiveCount(ViewHarness.KeyId), "nothing is live");
                Assert.AreEqual(bindsBefore, warm.BindCount, "Bind never ran");
            }
        }

        // ------------------------------------------------------------------ V5: suspend behaviour

        [Test]
        public void V5_HideDropsAlphaAndStayVisibleDoesNot()
        {
            using (ViewHarness harness = new ViewHarness(suspend: PopupSuspendBehaviour.Hide))
            {
                TestPopupView view = harness.Rent();
                view.CanvasGroup.alpha = 1f;

                view.SetSuspended(true);

                Assert.AreEqual(0f, view.CanvasGroup.alpha, "Hide takes the popup off the screen");
                Assert.IsFalse(view.CanvasGroup.blocksRaycasts, "on the VIEW's own group, not the layer's");
                Assert.IsFalse(view.CanvasGroup.interactable);
                Assert.IsTrue(view.gameObject.activeSelf,
                              "SetActive(false) is not used: the close channel and any in-flight image must survive");

                view.SetSuspended(false);
                Assert.AreEqual(1f, view.CanvasGroup.alpha);
                Assert.IsTrue(view.CanvasGroup.blocksRaycasts);
            }

            using (ViewHarness harness = new ViewHarness(suspend: PopupSuspendBehaviour.StayVisible))
            {
                TestPopupView view = harness.Rent();
                view.CanvasGroup.alpha = 1f;

                view.SetSuspended(true);

                Assert.AreEqual(1f, view.CanvasGroup.alpha, "StayVisible leaves it on screen behind the interrupter");
                Assert.IsFalse(view.CanvasGroup.blocksRaycasts, "but input belongs to the interrupter regardless");
                Assert.IsFalse(view.CanvasGroup.interactable);
            }
        }

        // ------------------------------------------------------------------ V6: the input gate

        [Test]
        public void V6_TheLayerGateIsShutForTheDurationOfEveryTransition()
        {
            using (ViewHarness harness = new ViewHarness(transitionId: "hold"))
            {
                TestPopupView view = harness.Rent();

                Assert.IsTrue(harness.Layer.Group.blocksRaycasts, "input is open before anything animates");

                UniTask open = view.PlayOpenAsync(CancellationToken.None);

                Assert.AreEqual(UniTaskStatus.Pending, open.Status, "the transition is being held");
                Assert.IsFalse(harness.Layer.Group.blocksRaycasts, "taps during the open transition do nothing");
                Assert.AreEqual(1, harness.Layer.TransitionDepth);

                harness.Holding.ReleaseIn();
                open.GetAwaiter().GetResult();

                Assert.IsTrue(harness.Layer.Group.blocksRaycasts, "and input reopens when it finishes");
                Assert.AreEqual(0, harness.Layer.TransitionDepth);
            }
        }

        /// <summary>
        /// The gate must stop taps reaching POPUPS, and must not stop them reaching the BACKDROP — a modal
        /// that lets input through to the game canvas for the length of every transition is not modal.
        ///
        /// Asserted through Graphic.Raycast itself, because the two behaviours are indistinguishable from
        /// the layer's CanvasGroup: blocksRaycasts reads false either way, which is exactly why the
        /// original V6 passed while input was falling through. Raycast is the method that performs the
        /// upward CanvasGroup walk, so this exercises the real uGUI rule rather than restating it.
        /// </summary>
        [Test]
        public void V6b_TheGateBlocksThePopupButNeverTheBackdrop()
        {
            using (ViewHarness harness = new ViewHarness(transitionId: "hold"))
            {
                TestPopupView view = harness.Rent();
                Image popupGraphic = view.gameObject.AddComponent<Image>();

                UniTask open = view.PlayOpenAsync(CancellationToken.None);
                Assert.AreEqual(UniTaskStatus.Pending, open.Status, "held mid-transition");
                Assert.IsFalse(harness.Layer.Group.blocksRaycasts, "the gate is shut");

                Vector2 backdropPoint = ScreenPointOf(harness.Layer.Backdrop.rectTransform);
                Vector2 popupPoint = ScreenPointOf((RectTransform)view.transform);

                Assert.IsFalse(popupGraphic.Raycast(popupPoint, null),
                               "taps during the transition must not reach the popup");
                Assert.IsTrue(harness.Layer.Backdrop.Raycast(backdropPoint, null),
                              "but the modal backdrop must keep swallowing them, or they land on the game canvas behind");

                harness.Holding.ReleaseIn();
                open.GetAwaiter().GetResult();

                Assert.IsTrue(popupGraphic.Raycast(popupPoint, null), "and the popup takes input once it is open");
                Assert.IsTrue(harness.Layer.Backdrop.Raycast(backdropPoint, null));

                // The closing half. The backdrop's own fade is over long before the popup's out-transition
                // is — outside play mode it settles synchronously — so dropping the block when the fade
                // ends would reopen the game canvas for the whole rest of the animation.
                UniTask close = view.PlayCloseAsync(CancellationToken.None);

                Assert.AreEqual(UniTaskStatus.Pending, close.Status, "held mid-close");
                Assert.AreEqual(0f, harness.Layer.Backdrop.color.a, 0.001f, "already faded out");
                Assert.IsTrue(harness.Layer.Backdrop.Raycast(backdropPoint, null),
                              "but still swallowing input until the close transition actually ends");
                Assert.IsFalse(popupGraphic.Raycast(popupPoint, null), "and the popup is gated throughout");

                harness.Holding.ReleaseOut();
                close.GetAwaiter().GetResult();

                Assert.IsFalse(harness.Layer.Backdrop.gameObject.activeSelf,
                               "and only then does it go down");
            }
        }

        private static Vector2 ScreenPointOf(RectTransform rect)
        {
            return RectTransformUtility.WorldToScreenPoint(null, rect.position);
        }

        [Test]
        public void V6_ACancelledCloseStillReopensInput()
        {
            using (ViewHarness harness = new ViewHarness(transitionId: "hold"))
            {
                TestPopupView view = harness.Rent();

                UniTask close = view.PlayCloseAsync(CancellationToken.None);
                Assert.IsFalse(harness.Layer.Group.blocksRaycasts);

                // A cancelled close is a skipped animation, never a skipped release — and never a gate left
                // shut, which is what the finally is for.
                harness.Holding.CancelOut();

                Assert.Throws<OperationCanceledException>(() => close.GetAwaiter().GetResult());
                Assert.IsTrue(harness.Layer.Group.blocksRaycasts, "the finally reopened input anyway");
                Assert.AreEqual(0, harness.Layer.TransitionDepth);
            }
        }

        [Test]
        public void V6_TwoOverlappingTransitionsReopenInputOnlyAfterBothEnd()
        {
            using (ViewHarness harness = new ViewHarness(transitionId: "hold"))
            {
                TestPopupView first = harness.Rent(ViewHarness.KeyId);

                // A second transition object, so the two can be released independently.
                HoldingTransition second = new HoldingTransition();
                harness.Transitions.Register("hold2", second);
                harness.Source.Prefab = harness.BuildPrefab<TestPopupView>(
                    "Second", PopupModality.Modeless, PopupSuspendBehaviour.Hide, "hold2");
                TestPopupView other = harness.Rent(ViewHarness.OtherKeyId);

                UniTask closing = first.PlayCloseAsync(CancellationToken.None);
                UniTask opening = other.PlayOpenAsync(CancellationToken.None);

                Assert.AreEqual(2, harness.Layer.TransitionDepth, "counted, not a boolean");
                Assert.IsFalse(harness.Layer.Group.blocksRaycasts);

                harness.Holding.ReleaseOut();
                closing.GetAwaiter().GetResult();

                Assert.IsFalse(harness.Layer.Group.blocksRaycasts,
                               "a boolean gate would have reopened input while the second transition still runs");

                second.ReleaseIn();
                opening.GetAwaiter().GetResult();

                Assert.IsTrue(harness.Layer.Group.blocksRaycasts);
            }
        }

        // ------------------------------------------------------------------ V7: the backdrop runs alongside

        [Test]
        public void V7_TheBackdropRunsAlongsideTheOpenAndCloseTransitions()
        {
            using (ViewHarness harness = new ViewHarness(transitionId: "hold"))
            {
                TestPopupView view = harness.Rent();

                Assert.IsFalse(harness.Layer.Backdrop.gameObject.activeSelf, "no backdrop before the popup opens");

                UniTask open = view.PlayOpenAsync(CancellationToken.None);

                Assert.AreEqual(UniTaskStatus.Pending, open.Status, "the popup is still opening");
                Assert.IsTrue(harness.Layer.Backdrop.gameObject.activeSelf,
                              "the backdrop is already up — started alongside the transition, not after it");
                Assert.Greater(harness.Layer.Backdrop.color.a, 0f);
                Assert.Less(harness.Layer.Backdrop.transform.GetSiblingIndex(), view.transform.GetSiblingIndex(),
                            "and it sits beneath the modal it dims for");

                harness.Holding.ReleaseIn();
                open.GetAwaiter().GetResult();

                UniTask close = view.PlayCloseAsync(CancellationToken.None);

                Assert.AreEqual(UniTaskStatus.Pending, close.Status, "the popup is still closing");
                Assert.AreEqual(0f, harness.Layer.Backdrop.color.a, 0.001f,
                                "the backdrop's dimming went with it");

                // It stays ACTIVE until the transition ends — that is the input block, not the dimming.
                // V6b is what asserts the input half.
                Assert.IsTrue(harness.Layer.Backdrop.gameObject.activeSelf);

                harness.Holding.ReleaseOut();
                close.GetAwaiter().GetResult();

                Assert.IsFalse(harness.Layer.Backdrop.gameObject.activeSelf);
            }
        }

        [Test]
        public void V7_AModelessPopupNeverTouchesTheBackdrop()
        {
            using (ViewHarness harness = new ViewHarness(modality: PopupModality.Modeless, transitionId: "hold"))
            {
                TestPopupView view = harness.Rent();

                UniTask open = view.PlayOpenAsync(CancellationToken.None);
                Assert.IsFalse(harness.Layer.Backdrop.gameObject.activeSelf);

                harness.Holding.ReleaseIn();
                open.GetAwaiter().GetResult();

                Assert.IsFalse(harness.Layer.Backdrop.gameObject.activeSelf);
            }
        }

        // ------------------------------------------------------------------ V8: backdrop reposition on resume

        [Test]
        public void V8_WhenTheInterrupterTerminatesTheBackdropDropsBelowTheResumedModal()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                Assert.IsTrue(harness.Service.HasSubscriber, "the layer subscribes to RequestCompleted in Bind");

                TestPopupView suspended = harness.Rent(ViewHarness.KeyId);
                suspended.PlayOpenAsync(CancellationToken.None).GetAwaiter().GetResult();

                harness.Source.Prefab = harness.BuildPrefab<TestPopupView>(
                    "Interrupter", PopupModality.Modal, PopupSuspendBehaviour.Hide, "instant");
                TestPopupView interrupter = harness.Rent(ViewHarness.OtherKeyId);
                interrupter.PlayOpenAsync(CancellationToken.None).GetAwaiter().GetResult();

                Assert.Less(harness.Layer.Backdrop.transform.GetSiblingIndex(),
                            interrupter.transform.GetSiblingIndex());
                Assert.Greater(harness.Layer.Backdrop.transform.GetSiblingIndex(),
                               suspended.transform.GetSiblingIndex(),
                               "while the interrupter is up the backdrop dims the suspended popup too");

                // The core releases at terminal step 7 and raises the event at step 11, in that order.
                harness.Release(interrupter);
                harness.Service.RaiseCompleted(ViewHarness.OtherKeyId);

                Assert.IsTrue(harness.Layer.Backdrop.gameObject.activeSelf,
                              "the resumed modal still needs its backdrop");
                Assert.Less(harness.Layer.Backdrop.transform.GetSiblingIndex(),
                            suspended.transform.GetSiblingIndex(),
                            "or the resumed popup would sit above its own dimming");
            }
        }

        [Test]
        public void V8_WithNoModalLeftTheBackdropGoesAway()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                TestPopupView view = harness.Rent();
                view.PlayOpenAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(harness.Layer.Backdrop.gameObject.activeSelf);

                harness.Release(view);
                harness.Service.RaiseCompleted(ViewHarness.KeyId);

                Assert.IsFalse(harness.Layer.Backdrop.gameObject.activeSelf);
            }
        }
    }
}
