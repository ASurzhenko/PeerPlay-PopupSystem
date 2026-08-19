using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using UnityEngine;
using UnityEngine.TestTools;

namespace PeerPlay.Popups.ViewSourcing.Tests
{
    internal sealed class SourcingTests
    {
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _owned.Count; i++)
            {
                if (_owned[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_owned[i]);
                }
            }

            _owned.Clear();
        }

        private Texture2D NewTexture()
        {
            Texture2D texture = new Texture2D(2, 2);
            _owned.Add(texture);
            return texture;
        }

        private Sprite NewSprite(Texture2D texture)
        {
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f));
            _owned.Add(sprite);
            return sprite;
        }

        // ------------------------------------------------------------------ S1: the LRU actually destroys

        [Test]
        public void S1_TheNinthInsertDestroysTheOldestThroughTheSeam()
        {
            SpriteLruCache cache = new SpriteLruCache(8);

            Texture2D oldest = NewTexture();
            Sprite oldestSprite = NewSprite(oldest);

            cache.Add("url-0", oldestSprite, oldest);
            cache.Release(oldestSprite);

            for (int i = 1; i < 8; i++)
            {
                Texture2D texture = NewTexture();
                Sprite sprite = NewSprite(texture);
                cache.Add($"url-{i}", sprite, texture);
                cache.Release(sprite);
            }

            Assert.AreEqual(8, cache.Count, "at the cap and nothing evicted yet");
            Assert.IsTrue(oldest != null, "still alive at the cap");

            Texture2D ninth = NewTexture();
            cache.Add("url-8", NewSprite(ninth), ninth);

            Assert.AreEqual(8, cache.Count, "the cap holds");
            Assert.IsTrue(oldest == null,
                          "the evicted texture is DESTROYED — an LRU that only drops references is a leak with a cap on nothing");
        }

        [Test]
        public void S1b_AnEntryStillLeasedIsNotEvicted()
        {
            SpriteLruCache cache = new SpriteLruCache(1);

            Texture2D held = NewTexture();
            cache.Add("held", NewSprite(held), held);          // leased, never released

            Texture2D second = NewTexture();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("over cap"));
            cache.Add("second", NewSprite(second), second);

            Assert.IsTrue(held != null, "destroying a texture a live popup is drawing is the destructive direction");
        }

        // ------------------------------------------------------------------ S2: image failures

        [Test]
        public void S2_AnHttpFailureResolvesToNullRatherThanThrowing()
        {
            FakeHttp http = new FakeHttp();
            http.Script(HttpResult.Fail(HttpFailure.Http5xx, "500 boom", 500));

            LogAssert.ignoreFailingMessages = true;

            using (RemoteImageSource images = new RemoteImageSource(http, new SpriteLruCache(4)))
            {
                Sprite sprite = images.LoadAsync("https://x/y.png", CancellationToken.None)
                                      .GetAwaiter().GetResult();

                Assert.IsNull(sprite, "a content failure is a normal answer the view renders as its placeholder");
            }

            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void S2b_ACallerCancelPropagatesAsAnException()
        {
            FakeHttp http = new FakeHttp { Hold = true };

            using (RemoteImageSource images = new RemoteImageSource(http, new SpriteLruCache(4)))
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                UniTask<Sprite> pending = images.LoadAsync("https://x/y.png", cts.Token);
                Assert.AreEqual(UniTaskStatus.Pending, pending.Status);

                cts.Cancel();

                Assert.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult(),
                    "a teardown propagates; a deadline does not — that is the whole distinction");
            }
        }

        [Test]
        public void S2c_TwoConcurrentLoadsOfTheSameUrlShareOneFetch()
        {
            FakeHttp http = new FakeHttp { Hold = true };

            using (RemoteImageSource images = new RemoteImageSource(http, new SpriteLruCache(4)))
            {
                UniTask<Sprite> first = images.LoadAsync("https://x/y.png", CancellationToken.None);
                UniTask<Sprite> second = images.LoadAsync("https://x/y.png", CancellationToken.None);

                Assert.AreEqual(1, http.RequestCount, "coalesced into one request");

                Texture2D texture = NewTexture();
                http.CompleteHeld(HttpResult.Success(200, texture));

                Sprite a = first.GetAwaiter().GetResult();
                Sprite b = second.GetAwaiter().GetResult();

                Assert.IsNotNull(a);
                Assert.AreSame(a, b, "both awaiters resolve, and with the same sprite");
                _owned.Add(a);
            }
        }

        [Test]
        public void S2d_TheFetchItselfIsCancelledWhenTheRequestTokenFires()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                harness.Catalog.Author(ViewHarness.KeyId, ViewHarness.AssetId,
                                       "https://cdn.example/offer.png", "title.key", "body.key");
                harness.Source.Prefab = harness.BuildPrefab<RemoteContentPopupView>(
                    "Remote", PopupModality.Modal, PopupSuspendBehaviour.Hide, "instant");
                harness.Images.Hold = true;

                using (CancellationTokenSource cts = new CancellationTokenSource())
                {
                    RemoteContentPopupView view = (RemoteContentPopupView)harness.Factory
                        .CreateAsync(new PopupKey<TestPayload>(ViewHarness.KeyId), new TestPayload("x"), cts.Token)
                        .GetAwaiter().GetResult();

                    Assert.AreEqual(1, harness.Images.Requests.Count, "the fetch started from Bind");
                    Assert.AreEqual(0, harness.Images.Aborts);

                    // The request's token, carried into the view by PopupViewSetup. Without that wire the
                    // fetch would run to completion after the request was already gone.
                    cts.Cancel();

                    Assert.AreEqual(1, harness.Images.Aborts, "the request itself was aborted, not merely the awaiter");
                    Assert.AreEqual(0, view.Applied, "and nothing was painted");
                }
            }
        }

        // ------------------------------------------------------------------ S3: the generation guard

        [Test]
        public void S3_AFetchResolvingAfterAReRentPaintsNothingAndReturnsItsLease()
        {
            using (ViewHarness harness = new ViewHarness())
            {
                harness.Catalog.Author(ViewHarness.KeyId, ViewHarness.AssetId,
                                       "https://cdn.example/offer.png", "title.key", "body.key");
                harness.Source.Prefab = harness.BuildPrefab<RemoteContentPopupView>(
                    "Remote", PopupModality.Modal, PopupSuspendBehaviour.Hide, "instant");
                harness.Images.Hold = true;

                RemoteContentPopupView view = (RemoteContentPopupView)harness.Factory
                    .CreateAsync(new PopupKey<TestPayload>(ViewHarness.KeyId), new TestPayload("x"),
                                 CancellationToken.None)
                    .GetAwaiter().GetResult();

                harness.Release(view);
                harness.RentView();                   // same instance, next generation

                Texture2D texture = NewTexture();
                Sprite sprite = NewSprite(texture);
                harness.Images.CompleteHeld(sprite);

                Assert.AreEqual(0, view.Applied, "a stale continuation must not paint a re-rented view");
                Assert.AreEqual(1, harness.Images.Returned, "but the lease it took is still given back");
            }
        }

        // ------------------------------------------------------------------ S4 / S5 / S6: refcount + coalescing

        [Test]
        public void S4_TwoAcquiresLoadOnceAndTwoReleasesReleaseOnce()
        {
            FakePrefabLoader loader = new FakePrefabLoader { Prefab = NewGameObject("prefab") };
            RefCountedViewSource source = new RefCountedViewSource(loader);

            source.AcquirePrefabAsync("asset", CancellationToken.None).GetAwaiter().GetResult();
            source.AcquirePrefabAsync("asset", CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(1, loader.Loads.Count, "the second acquire reuses the first load");
            Assert.AreEqual(2, source.RefCountOf("asset"));

            source.ReleasePrefab("asset");
            CollectionAssert.IsEmpty(loader.Releases, "one user left, so the handle stays");

            source.ReleasePrefab("asset");
            Assert.AreEqual(1, loader.Releases.Count, "the last user leaving releases it exactly once");
            Assert.AreEqual(0, source.EntryCount);
        }

        [Test]
        public void S5_ACancelledAcquireLeavesNothingPinned()
        {
            FakePrefabLoader loader = new FakePrefabLoader { Prefab = NewGameObject("prefab"), Hold = true };
            RefCountedViewSource source = new RefCountedViewSource(loader);

            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                UniTask<GameObject> pending = source.AcquirePrefabAsync("asset", cts.Token);
                Assert.AreEqual(UniTaskStatus.Pending, pending.Status);

                cts.Cancel();

                Assert.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
            }

            Assert.AreEqual(0, source.RefCountOf("asset"));
            Assert.AreEqual(0, source.EntryCount, "the entry is gone");
            CollectionAssert.IsEmpty(loader.Releases, "nothing was ever loaded, so there is nothing to release");
            Assert.AreEqual(1, loader.Aborts, "and the in-flight load was told to stop");
        }

        [Test]
        public void S6_CoalescedAcquiresBothResolveAndAFailureFaultsBoth()
        {
            GameObject prefab = NewGameObject("prefab");
            FakePrefabLoader loader = new FakePrefabLoader { Prefab = prefab, Hold = true };
            RefCountedViewSource source = new RefCountedViewSource(loader);

            UniTask<GameObject> first = source.AcquirePrefabAsync("asset", CancellationToken.None);
            UniTask<GameObject> second = source.AcquirePrefabAsync("asset", CancellationToken.None);

            Assert.AreEqual(1, loader.Loads.Count, "one load for two callers");

            loader.CompleteHeld();

            Assert.AreSame(prefab, first.GetAwaiter().GetResult());
            Assert.AreSame(prefab, second.GetAwaiter().GetResult(),
                           "re-awaiting a UniTask would throw; the plain completion source is what carries many awaiters");

            // And the failure half.
            FakePrefabLoader failing = new FakePrefabLoader { FailNext = true };
            RefCountedViewSource failingSource = new RefCountedViewSource(failing);

            UniTask<GameObject> a = failingSource.AcquirePrefabAsync("bad", CancellationToken.None);
            Assert.Throws<PopupLoadException>(() => a.GetAwaiter().GetResult());
            Assert.AreEqual(0, failingSource.EntryCount, "a failed entry is retired so a later acquire retries");
        }

        // ------------------------------------------------------------------ S7: which bound fired

        [Test]
        public void S7_TransportFailuresRetryTwiceWithTheStatedBackoff()
        {
            FakeHttp http = new FakeHttp();
            http.ScriptMany(HttpResult.Fail(HttpFailure.Transport, "no route"), HttpWithRetry.MaxAttempts);
            FakeDelayProvider delays = new FakeDelayProvider();
            HttpWithRetry retry = new HttpWithRetry(http, delays);

            LogAssert.ignoreFailingMessages = true;
            HttpResult result = retry.GetAsync("https://x/c.json", 5, CancellationToken.None)
                                     .GetAwaiter().GetResult();
            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(HttpWithRetry.MaxAttempts, http.RequestCount, "three attempts");
            Assert.AreEqual(2, delays.Delays.Count);
            Assert.AreEqual(0.5, delays.Delays[0].TotalSeconds, 0.001);
            Assert.AreEqual(1.5, delays.Delays[1].TotalSeconds, 0.001);
            Assert.AreEqual(HttpFailure.Transport, result.Failure);
            Assert.AreEqual(8, delays.DeadlineRequested.Value.TotalSeconds, 0.001,
                            "the outer budget is armed once, around the whole retry loop");
        }

        [Test]
        public void S7b_TheDeadlineIsReportedAsADeadlineAndNotAsATeardown()
        {
            FakeHttp http = new FakeHttp();
            http.ScriptMany(HttpResult.Fail(HttpFailure.Transport, "no route"), HttpWithRetry.MaxAttempts);
            FakeDelayProvider delays = new FakeDelayProvider { HoldDelays = true };
            HttpWithRetry retry = new HttpWithRetry(http, delays);

            LogAssert.ignoreFailingMessages = true;
            UniTask<HttpResult> pending = retry.GetAsync("https://x/c.json", 5, CancellationToken.None);
            Assert.AreEqual(UniTaskStatus.Pending, pending.Status, "waiting out the first backoff");

            delays.FireDeadline();
            HttpResult result = pending.GetAwaiter().GetResult();
            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(HttpFailure.Deadline, result.Failure,
                            "an expired budget must not read as a teardown, or the view paints nothing at all");
            Assert.AreEqual("deadline", result.Error);
        }

        [Test]
        public void S7c_ATimeoutIsRetriedAndA4xxIsNot()
        {
            FakeHttp timingOut = new FakeHttp();
            timingOut.ScriptMany(HttpResult.Fail(HttpFailure.Timeout, "http-timeout"), HttpWithRetry.MaxAttempts);
            FakeDelayProvider delays = new FakeDelayProvider();

            LogAssert.ignoreFailingMessages = true;
            HttpResult timeout = new HttpWithRetry(timingOut, delays)
                .GetAsync("https://x/c.json", 5, CancellationToken.None).GetAwaiter().GetResult();
            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(HttpWithRetry.MaxAttempts, timingOut.RequestCount,
                            "a timeout is a transport-class failure, and retrying it is what makes the deadline reachable");
            Assert.AreEqual(HttpFailure.Timeout, timeout.Failure);
            Assert.AreEqual("http-timeout", timeout.Error);

            FakeHttp notFound = new FakeHttp();
            notFound.Script(HttpResult.Fail(HttpFailure.Http4xx, "404 Not Found", 404));

            HttpResult missing = new HttpWithRetry(notFound, new FakeDelayProvider())
                .GetAsync("https://x/c.json", 5, CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(1, notFound.RequestCount, "a publishing mistake is not retried");
            Assert.AreEqual(HttpFailure.Http4xx, missing.Failure);
        }

        private GameObject NewGameObject(string name)
        {
            GameObject go = new GameObject(name);
            go.SetActive(false);
            _owned.Add(go);
            return go;
        }
    }
}
