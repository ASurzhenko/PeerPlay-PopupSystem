using System;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using UnityEngine;
using UnityEngine.TestTools;

namespace PeerPlay.Popups.ViewSourcing.Tests
{
    /// <summary>
    /// The config spine: validation, the last-known-good cache and the boot order. It is the part
    /// "a bad remote config cannot take the popups away" rests on, so it is also the part with the most
    /// rejection cases.
    /// </summary>
    internal sealed class ConfigTests
    {
        private PopupTransitionRegistry _transitions;
        private PopupConfigValidator _validator;
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _transitions = new PopupTransitionRegistry()
                .Register("instant", new InstantTransition())
                .Register("fade", new FadeTransition())
                .Register("scale_pop", new ScalePopTransition());

            _validator = new PopupConfigValidator(_transitions);

            _tempDir = Path.Combine(Path.GetTempPath(), "peerplay-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        // These tests do NOT suppress logs. A refusal is a warning, which the framework lets through, so an
        // ERROR reaching this class fails the test that produced it — which is the point: the one thing
        // worth being told about here is a refusal turning back into a lost config.

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }

        private string CachePath => Path.Combine(_tempDir, "popup-config.json");

        private bool Validate(string json, out string reason)
        {
            return _validator.TryValidateStructure(json, out _, out reason);
        }

        private static TextAsset Text(string json)
        {
            return new TextAsset(json);
        }

        // ------------------------------------------------------------------ C1

        [Test]
        public void C1_TheGoldenConfigAdoptsAndIsCached()
        {
            FakeHttp http = new FakeHttp();
            string golden = ConfigJson.Golden();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(golden)));

            PopupConfigCache cache = new PopupConfigCache(CachePath);

            // Catalogs ready, so BOTH passes run — which is what earns a payload its place in the cache.
            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(golden), cache, _validator, http, new FakeCatalogProbe { Ready = true },
                "https://cdn/config.json");

            int adoptions = 0;
            service.Adopted += _ => adoptions++;

            ConfigFetchResult result = service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(result.Adopted, result.Reason);
            Assert.AreEqual(2, service.Current.Count, "the golden fixture carries two rules");
            Assert.AreEqual(1, adoptions, "Adopted is raised once per adoption");
            Assert.IsTrue(File.Exists(CachePath), "a config that passed both passes becomes the last known good");
            StringAssert.Contains("offer_weekend", File.ReadAllText(CachePath));
        }

        // ------------------------------------------------------------------ C2 - C11: the structural rules

        [Test]
        public void C2_AnEmptyPayloadIsRejected()
        {
            Assert.IsFalse(Validate(string.Empty, out string reason));
            StringAssert.Contains("payload empty", reason);
        }

        [Test]
        public void C3_ZeroPopupsIsRejected()
        {
            Assert.IsFalse(Validate("{\"version\":1,\"popups\":[]}", out string reason));
            StringAssert.Contains("zero popups", reason, "this is the incident the kill switch exists for");
        }

        [Test]
        public void C4_MalformedJsonIsRejectedWithTheParserMessage()
        {
            Assert.IsFalse(Validate("{\"version\":1,\"popups\":[", out string reason));
            StringAssert.Contains("malformed json", reason);
        }

        [Test]
        public void C5_AnEmptyObjectIsRejected()
        {
            Assert.IsFalse(Validate("{}", out string reason));
            Assert.IsNotNull(reason);

            // Both branches of rule 4 — a null array and an empty one.
            Assert.IsFalse(Validate("{\"version\":1}", out string nullPopups));
            StringAssert.Contains("zero popups", nullPopups);
        }

        [Test]
        public void C6_AMissingStateIsRejectedNamingTheEntry()
        {
            Assert.IsFalse(Validate(ConfigJson.One(state: null), out string reason));
            StringAssert.Contains("info", reason);
            StringAssert.Contains("state", reason);
        }

        [Test]
        public void C7_UnknownEnumsAndUnknownTransitionsAreRejected()
        {
            Assert.IsFalse(Validate(ConfigJson.One(priority: "Urgent"), out string priority));
            StringAssert.Contains("priority", priority);

            Assert.IsFalse(Validate(ConfigJson.One(sequencing: "Shove"), out string sequencing));
            StringAssert.Contains("sequencing", sequencing);

            Assert.IsFalse(Validate(ConfigJson.One(transition: "wobble"), out string transition));
            StringAssert.Contains("not registered", transition);
        }

        [Test]
        public void C7b_ANumericPriorityOutsideTheDeclaredSetIsRejected()
        {
            Assert.IsFalse(Validate(ConfigJson.One(priority: "7"), out string reason),
                           "Enum.TryParse accepts any numeric string, so Enum.IsDefined is the half that matters");
            StringAssert.Contains("is not a defined", reason);
        }

        [Test]
        public void C8_ADuplicateIdIsRejected()
        {
            Assert.IsFalse(Validate(ConfigJson.Two("same", "same"), out string reason));
            StringAssert.Contains("duplicate id", reason);
        }

        [Test]
        public void C9_NonNumericAndNegativeCountsAreRejectedAndZeroIsAccepted()
        {
            Assert.IsFalse(Validate(ConfigJson.One(cooldownSeconds: "-1"), out _));
            Assert.IsFalse(Validate(ConfigJson.One(cooldownSeconds: "soon"), out _));
            Assert.IsFalse(Validate(ConfigJson.One(maxPerSession: "-3"), out _));
            Assert.IsFalse(Validate(ConfigJson.One(maxPerSession: "lots"), out _));

            Assert.IsTrue(Validate(ConfigJson.One(cooldownSeconds: "0", maxPerSession: "0"), out _),
                          "zero is the unlimited sentinel, not an error");
        }

        [Test]
        public void C9b_AMissingCooldownIsRejected()
        {
            Assert.IsFalse(Validate(ConfigJson.One(cooldownSeconds: null), out string reason),
                           "an absent int would have defaulted to 0 silently; an absent string is null and is caught");
            StringAssert.Contains("cooldownSeconds", reason);
        }

        [Test]
        public void C10_ImageUrlMustBeEmptyOrHttps()
        {
            Assert.IsFalse(Validate(ConfigJson.One(imageUrl: "http://cdn/x.png"), out string reason));
            StringAssert.Contains("not https", reason);

            Assert.IsTrue(Validate(ConfigJson.One(imageUrl: ""), out _), "empty means 'no remote content'");
            Assert.IsTrue(Validate(ConfigJson.One(imageUrl: "https://cdn/x.png"), out _));
        }

        [Test]
        public void C10b_MissingTextKeysAreRejected()
        {
            Assert.IsFalse(Validate(ConfigJson.One(titleKey: null), out string title));
            StringAssert.Contains("titleKey", title);

            Assert.IsFalse(Validate(ConfigJson.One(bodyKey: null), out string body));
            StringAssert.Contains("bodyKey", body);
        }

        [Test]
        public void C11_TheSizeCapIsMeasuredInBytesNotCharacters()
        {
            string payload = ConfigJson.OversizedByBytes();

            Assert.Less(payload.Length, PopupConfigValidator.MaxPayloadBytes,
                        "under the cap when counted as characters");
            Assert.Greater(Encoding.UTF8.GetByteCount(payload), PopupConfigValidator.MaxPayloadBytes,
                           "and over it on the wire");

            Assert.IsFalse(Validate(payload, out string reason));
            StringAssert.Contains("exceeds", reason);
        }

        // ------------------------------------------------------------------ C12 - C16: adoption and boot

        [Test]
        public void C12_ABrokenPublishLeavesTheLastKnownGoodInPlace()
        {
            FakeHttp http = new FakeHttp();
            string golden = ConfigJson.Golden();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(golden)));
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes("{\"version\":1,\"popups\":[]}")));

            PopupConfigCache cache = new PopupConfigCache(CachePath);
            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(golden), cache, _validator, http, new FakeCatalogProbe { Ready = true },
                "https://cdn/config.json");

            service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            PopupConfigSnapshot good = service.Current;

            ConfigFetchResult broken = service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsFalse(broken.Adopted);
            Assert.AreSame(good, service.Current, "the snapshot is not swapped for a rejected payload");
            StringAssert.Contains("offer_weekend", File.ReadAllText(CachePath), "and the cache still holds the good copy");
        }

        /// <summary>
        /// A publish that never arrives leaves the live config serving, so it is a warning — and the level
        /// is asserted rather than assumed, because nothing else in the suite would notice it being raised
        /// back to an error. `LogAssert` fails the test twice over if that happens: once for the error
        /// nobody expected, once for the warning that never came.
        /// </summary>
        [Test]
        public void C12b_AFetchThatNeverArrivesWarnsAndLeavesTheLiveConfigServing()
        {
            FakeHttp http = new FakeHttp();
            string golden = ConfigJson.Golden();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(golden)));
            http.Script(HttpResult.Fail(HttpFailure.Transport, "no route"));

            PopupConfigCache cache = new PopupConfigCache(CachePath);
            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(golden), cache, _validator, http, new FakeCatalogProbe { Ready = true },
                "https://cdn/config.json");

            service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            PopupConfigSnapshot live = service.Current;

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("fetch failed"));

            ConfigFetchResult failed = service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsFalse(failed.Adopted);
            Assert.AreEqual(ConfigFetchOutcome.Rejected, failed.Outcome);
            Assert.AreSame(live, service.Current, "a failed fetch must not disturb what is already serving");
        }

        [Test]
        public void C13_ColdStartWithNoCacheAndNoNetworkAdoptsTheBuiltInDefault()
        {
            FakeCatalogProbe probe = new FakeCatalogProbe();
            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(ConfigJson.Golden()), new PopupConfigCache(CachePath), _validator,
                new FakeHttp(), probe, "https://cdn/config.json");

            service.AdoptBestAvailable();

            Assert.AreEqual(2, service.Current.Count, "the built-in default is adopted");
            Assert.AreEqual(0, probe.InitCalls, "and nothing touched Addressables to do it");
        }

        [Test]
        public void C13b_AValidCacheIsPreferredOverTheBuiltInDefault()
        {
            File.WriteAllText(CachePath, ConfigJson.One(id: "from_cache"));

            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(ConfigJson.Golden()), new PopupConfigCache(CachePath), _validator,
                new FakeHttp(), new FakeCatalogProbe(), "https://cdn/config.json");

            service.AdoptBestAvailable();

            Assert.IsTrue(service.Current.TryGet("from_cache", out _),
                          "the cache is a config that was fetched, validated and adopted on a previous run");
        }

        [Test]
        public void C13c_AdoptedFiresFromAdoptBestAvailableBeforeAnyNetworkCall()
        {
            FakeHttp http = new FakeHttp();
            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(ConfigJson.Golden()), new PopupConfigCache(CachePath), _validator,
                http, new FakeCatalogProbe(), "https://cdn/config.json");

            int adoptions = 0;
            service.Adopted += _ => adoptions++;

            service.AdoptBestAvailable();

            Assert.AreEqual(1, adoptions,
                            "the view overrides are live from the first frame, not only once the network answers");
            Assert.AreEqual(0, http.RequestCount);
        }

        [Test]
        public void C14_ACorruptCacheFallsBackToTheBuiltInAndTheNextGoodFetchRewritesIt()
        {
            File.WriteAllText(CachePath, "{ not json");

            FakeHttp http = new FakeHttp();
            string golden = ConfigJson.Golden();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(golden)));

            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(golden), new PopupConfigCache(CachePath), _validator, http,
                new FakeCatalogProbe { Ready = true }, "https://cdn/config.json");

            service.AdoptBestAvailable();
            Assert.AreEqual(2, service.Current.Count, "the built-in default carried the boot");

            service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            StringAssert.Contains("offer_weekend", File.ReadAllText(CachePath), "and the corrupt file is replaced");
        }

        [Test]
        public void C14b_TheFirstCacheWriteOnAFreshInstallDoesNotThrow()
        {
            PopupConfigCache cache = new PopupConfigCache(CachePath);

            Assert.IsFalse(File.Exists(CachePath), "a fresh install has no destination for File.Replace");
            Assert.DoesNotThrow(() => cache.Write("{\"hello\":\"world\"}"));

            Assert.IsTrue(cache.TryRead(out string round));
            Assert.AreEqual("{\"hello\":\"world\"}", round);

            // And the replace branch, now that the destination exists.
            Assert.DoesNotThrow(() => cache.Write("{\"hello\":\"again\"}"));
            Assert.IsTrue(cache.TryRead(out string second));
            Assert.AreEqual("{\"hello\":\"again\"}", second);
        }

        [Test]
        public void C15_TheBuiltInDefaultIsStructurallyValid()
        {
            Assert.IsTrue(_validator.TryValidateStructure(ConfigJson.Golden(), out PopupConfigSnapshot snapshot,
                                                          out string reason),
                          $"the shipped default must validate, this is a build-time guard: {reason}");
            Assert.AreEqual(2, snapshot.Count);
        }

        /// <summary>
        /// The fixture is a real capture — curl'd from the live CloudFront distribution, not copied out of
        /// the design doc. That is the whole point: a copy cannot show a BOM, a re-encode or a gzip body,
        /// because it was never on the wire. This pins what the wire actually returned, so a CDN
        /// reconfiguration shows up as a failing test rather than as a config that stops parsing.
        /// </summary>
        [Test]
        public void C15b_TheGoldenFixtureIsRawUtf8WithNoByteOrderMark()
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, ConfigJson.GoldenRelativePath));

            Assert.Greater(bytes.Length, 0);
            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                           "the live distribution served no BOM on 2026-08-19; re-capture if this changes");
            Assert.AreEqual((byte)'{', bytes[0], "and no gzip envelope — the body starts as JSON");

            Assert.IsTrue(_validator.TryValidateStructure(RemotePopupConfigService.Decode(bytes),
                                                          out PopupConfigSnapshot snapshot, out string reason),
                          $"the captured bytes must parse as shipped: {reason}");
            Assert.AreEqual(2, snapshot.Count);
        }

        [Test]
        public void C15c_ABomIsStrippedAtTheDecodeStep()
        {
            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] body = Encoding.UTF8.GetBytes(ConfigJson.Golden());
            byte[] combined = new byte[withBom.Length + body.Length];
            Buffer.BlockCopy(withBom, 0, combined, 0, withBom.Length);
            Buffer.BlockCopy(body, 0, combined, withBom.Length, body.Length);

            Assert.IsTrue(_validator.TryValidateStructure(RemotePopupConfigService.Decode(combined), out _, out _),
                          "JsonUtility throws on a leading U+FEFF, and a CDN is entitled to add one");
        }

        [Test]
        public void C16_AssetResolutionIsSkippedAndLoggedWhenTheCatalogsAreNotReady()
        {
            Assert.IsTrue(RemotePopupConfigService.ShouldRunAssetResolution(true));

            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(
                "adopted without asset-resolution check"));

            Assert.IsFalse(RemotePopupConfigService.ShouldRunAssetResolution(false),
                           "rejecting a VALID config precisely when the network is already degraded is the wrong direction");
        }

        [Test]
        public void C16b_WhenTheCatalogsAreReadyAnUnresolvableAssetRejectsTheConfig()
        {
            FakeHttp http = new FakeHttp();
            string golden = ConfigJson.Golden();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(golden)));

            FakeCatalogProbe probe = new FakeCatalogProbe { Ready = true, ResolveAll = false };
            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(golden), new PopupConfigCache(CachePath), _validator, http, probe, "https://cdn/config.json");

            ConfigFetchResult result = service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsFalse(result.Adopted);
            Assert.AreEqual(1, probe.ResolveCalls);
            Assert.IsFalse(File.Exists(CachePath), "a rejected config never becomes the last known good");
        }

        /// <summary>
        /// The hole this closes: a config adopted with rule 14 skipped used to be written to the cache, and
        /// the boot path adopts the cache on the structural pass alone — so something no catalog ever
        /// confirmed became the "last known good" permanently, on the one mechanism the whole feature is
        /// about.
        /// </summary>
        [Test]
        public void C17_AConfigAdoptedWithoutAssetResolutionIsNotPersistedAsLastKnownGood()
        {
            FakeHttp http = new FakeHttp();
            string golden = ConfigJson.Golden();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(golden)));

            FakeCatalogProbe probe = new FakeCatalogProbe { Ready = false };
            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(golden), new PopupConfigCache(CachePath), _validator, http, probe, "https://cdn/config.json");

            ConfigFetchResult skipped = service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(skipped.Adopted, "it still serves this session — refusing when the network is degraded is worse");
            Assert.AreEqual(2, service.Current.Count);
            Assert.AreEqual(0, probe.ResolveCalls, "rule 14 was skipped");
            Assert.IsFalse(File.Exists(CachePath),
                           "but it must not become the last-known-good the next cold start adopts unchecked");

            // And once the catalogs are there, the same payload does earn its place.
            probe.Ready = true;
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(golden)));
            service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(1, probe.ResolveCalls);
            Assert.IsTrue(File.Exists(CachePath), "fully validated, so it is cached");
        }

        /// <summary>
        /// The endpoint is mutable so a LIVE system can be pointed at another config — and mutability on its
        /// own is a race: two requests can be in flight and the OLDER one can answer last. Here the second
        /// answer lands first, so without the generation check the first endpoint's payload would be what
        /// ends up adopted and cached, and the system would serve a config nobody asked for last.
        /// </summary>
        [Test]
        public void C18_AnInFlightFetchCannotAdoptAfterTheEndpointMoved()
        {
            FakeHttp http = new FakeHttp { Hold = true };

            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(ConfigJson.Golden()), new PopupConfigCache(CachePath), _validator, http,
                new FakeCatalogProbe { Ready = true }, "https://cdn/old.json");

            UniTask<ConfigFetchResult> first = service.RefreshAsync(CancellationToken.None);

            service.ConfigUrl = "https://cdn/new.json";

            UniTask<ConfigFetchResult> second = service.RefreshAsync(CancellationToken.None);

            Assert.AreEqual(2, http.HeldCount, "both requests are genuinely in flight");
            CollectionAssert.AreEqual(new[] { "https://cdn/old.json", "https://cdn/new.json" }, http.Urls,
                                      "each went to the endpoint that was current when it started");

            // The newer answer first, the older one after it — the ordering a second button press produces.
            http.CompleteHeldAt(1, HttpResult.Success(200, Encoding.UTF8.GetBytes(ConfigJson.One(id: "from_new"))));
            http.CompleteHeldAt(0, HttpResult.Success(200, Encoding.UTF8.GetBytes(ConfigJson.One(id: "from_old"))));

            ConfigFetchResult newResult = second.GetAwaiter().GetResult();
            ConfigFetchResult oldResult = first.GetAwaiter().GetResult();

            Assert.IsTrue(newResult.Adopted, newResult.Reason);
            Assert.AreEqual(ConfigFetchOutcome.Superseded, oldResult.Outcome,
                            "the superseded request must not adopt, and must not read as a rejected config");
            Assert.IsNotNull(oldResult.Reason, "and it says why rather than returning silently");

            Assert.IsTrue(service.Current.TryGet("from_new", out _), "the new endpoint's payload is what serves");
            Assert.IsFalse(service.Current.TryGet("from_old", out _));

            string cached = File.ReadAllText(CachePath);
            StringAssert.Contains("from_new", cached, "and what became the last known good");
            StringAssert.DoesNotContain("from_old", cached, "the superseded payload landed nowhere");
        }

        /// <summary>
        /// C18's supersede happens across the HTTP hop, so the FIRST generation check catches it and the
        /// second one — the check sited immediately before Adopt and the cache write — is never reached.
        /// Here the endpoint moves while the ASSET RESOLUTION is in flight, which is the only way into it.
        ///
        /// Delete the check before Adopt and this goes red: the older request completes last and would
        /// otherwise adopt its payload over the newer one and cache it.
        /// </summary>
        [Test]
        public void C19_AFetchSupersededDuringAssetResolutionAdoptsNothing()
        {
            FakeHttp http = new FakeHttp();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(ConfigJson.One(id: "from_old"))));
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(ConfigJson.One(id: "from_new"))));

            FakeCatalogProbe probe = new FakeCatalogProbe { Ready = true, Hold = true };

            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(ConfigJson.Golden()), new PopupConfigCache(CachePath), _validator, http, probe,
                "https://cdn/old.json");

            // Its http answer already landed, so it is past the first check and parked on the resolution.
            UniTask<ConfigFetchResult> first = service.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(1, probe.HeldCount, "the first request is held at the resolution hop");

            service.ConfigUrl = "https://cdn/new.json";

            UniTask<ConfigFetchResult> second = service.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(2, probe.HeldCount);

            // Newer first, older after it: the ordering that lets a stale answer overwrite a fresh one.
            probe.CompleteHeldAt(1, true);
            probe.CompleteHeldAt(0, true);

            ConfigFetchResult newResult = second.GetAwaiter().GetResult();
            ConfigFetchResult oldResult = first.GetAwaiter().GetResult();

            Assert.IsTrue(newResult.Adopted, newResult.Reason);
            Assert.AreEqual(ConfigFetchOutcome.Superseded, oldResult.Outcome,
                            "and it is reported as ignored, not as a config that was refused");

            Assert.IsTrue(service.Current.TryGet("from_new", out _));
            Assert.IsFalse(service.Current.TryGet("from_old", out _), "the stale payload was not adopted");

            string cached = File.ReadAllText(CachePath);
            StringAssert.Contains("from_new", cached);
            StringAssert.DoesNotContain("from_old", cached, "and it was not written to the last known good");
        }

        /// <summary>
        /// A superseded request must not be reported as a rejected config: the incident block's whole job is
        /// to teach the difference, and a caller that can only read a bool has to print one word for both.
        /// </summary>
        [Test]
        public void C20_TheThreeOutcomesAreDistinguishable()
        {
            FakeHttp http = new FakeHttp();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(ConfigJson.Golden())));
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes("{\"version\":1,\"popups\":[]}")));

            RemotePopupConfigService service = new RemotePopupConfigService(
                Text(ConfigJson.Golden()), new PopupConfigCache(CachePath), _validator, http,
                new FakeCatalogProbe { Ready = true }, "https://cdn/config.json");

            Assert.AreEqual(ConfigFetchOutcome.Adopted,
                            service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult().Outcome);

            ConfigFetchResult rejected = service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(ConfigFetchOutcome.Rejected, rejected.Outcome);
            StringAssert.Contains("zero popups", rejected.Reason);
        }

        // ------------------------------------------------------------------ the config to view path

        [Test]
        public void V1c_TheBridgePushesEveryAdoptionIntoTheCatalog()
        {
            PopupCatalog catalog = ScriptableObject.CreateInstance<PopupCatalog>();

            try
            {
                RemotePopupConfigService service = new RemotePopupConfigService(
                    Text(ConfigJson.One(id: "info", assetId: "popup_info", transition: "scale_pop",
                                        modality: "Modeless", suspend: "StayVisible")),
                    new PopupConfigCache(CachePath), _validator, new FakeHttp(),
                    new FakeCatalogProbe(), "https://cdn/config.json");

                using (new PopupCatalogConfigBridge(service, catalog))
                {
                    service.AdoptBestAvailable();

                    PopupCatalogEntry entry = catalog.Resolve("info");

                    Assert.AreEqual("popup_info", entry.AssetId);
                    Assert.AreEqual("scale_pop", entry.TransitionId);
                    Assert.AreEqual(PopupModality.Modeless, entry.Modality);
                    Assert.AreEqual(PopupSuspendBehaviour.StayVisible, entry.Suspend);
                    Assert.AreEqual("info.title", entry.TitleKey);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }
    }
}
