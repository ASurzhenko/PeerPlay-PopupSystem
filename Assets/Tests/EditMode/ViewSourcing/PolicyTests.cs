using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using PeerPlay.Popups.Seams;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using UnityEngine;
using UnityEngine.TestTools;

namespace PeerPlay.Popups.ViewSourcing.Tests
{
    /// <summary>
    /// The kill switch, the caps and the cooldowns — the part the tech lead's own production incident was
    /// about, so the part that gets the most direct tests.
    /// </summary>
    internal sealed class PolicyTests
    {
        private PopupConfigValidator _validator;
        private FakeClock _clock;
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _validator = new PopupConfigValidator(new PopupTransitionRegistry()
                .Register("instant", new InstantTransition())
                .Register("fade", new FadeTransition())
                .Register("scale_pop", new ScalePopTransition()));

            _clock = new FakeClock();
            _tempDir = Path.Combine(Path.GetTempPath(), "peerplay-policy-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;

            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch (IOException)
            {
            }
        }

        private RemotePopupConfigService Service(string builtInJson, FakeHttp http = null)
        {
            return new RemotePopupConfigService(
                new TextAsset(builtInJson),
                new PopupConfigCache(Path.Combine(_tempDir, "popup-config.json")),
                _validator,
                http ?? new FakeHttp(),
                new FakeCatalogProbe(),
                "https://cdn/config.json");
        }

        private static PopupRequestInfo Info(string keyId, string family = null)
        {
            return new PopupRequestInfo(keyId, family, PopupPriority.Normal);
        }

        // ------------------------------------------------------------------ POL1 / POL6

        [Test]
        public void POL1_ADisabledPopupIsRefusedWithAReasonThatNamesTheKey()
        {
            RemotePopupConfigService config = Service(ConfigJson.One(id: "offer", state: "disabled"));
            config.AdoptBestAvailable();

            ConfigPopupPolicy policy = new ConfigPopupPolicy(config, _clock);
            PopupDecision decision = policy.Evaluate(Info("offer"));

            Assert.IsFalse(decision.Allowed);
            StringAssert.Contains("offer", decision.Reason,
                                  "a bare 'refused' in a panel showing five refusals says nothing");
            StringAssert.Contains("disabled", decision.Reason);
        }

        [Test]
        public void POL6_AKeyTheConfigNeverNamedIsAllowed()
        {
            RemotePopupConfigService config = Service(ConfigJson.One(id: "offer"));
            config.AdoptBestAvailable();

            ConfigPopupPolicy policy = new ConfigPopupPolicy(config, _clock);

            Assert.IsTrue(policy.Evaluate(Info("some_local_dialog")).Allowed,
                          "the config governs only the popups it names; refusing would break every cold start");
        }

        // ------------------------------------------------------------------ POL2

        [Test]
        public void POL2_AKillSwitchFlippedWhileTheRequestIsQueuedStopsItOpening()
        {
            FakeHttp http = new FakeHttp();
            http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(
                ConfigJson.Two("blocker", "victim"))));

            RemotePopupConfigService config = Service(ConfigJson.Two("blocker", "victim"), http);
            config.AdoptBestAvailable();

            ConfigPopupPolicy policy = new ConfigPopupPolicy(config, _clock);
            FakeViewFactory factory = new FakeViewFactory();

            List<PopupCompletion> completions = new List<PopupCompletion>();

            using (PopupService service = new PopupService(factory, policy, new FakeAnalytics()))
            {
                service.RequestCompleted += c => completions.Add(c);

                service.Show(new PopupKey<TestPayload>("blocker"), new TestPayload("a"));
                service.Show(new PopupKey<TestPayload>("victim"), new TestPayload("b"));

                Assert.AreEqual("blocker", service.CurrentKeyId, "the first one owns the slot");

                // PendingCount is every accepted request that has not reached a terminal, occupant
                // included — so two means the victim is still waiting rather than already resolved.
                Assert.AreEqual(2, service.PendingCount, "and the second is queued behind it");
                Assert.IsEmpty(completions, "nothing has terminated yet");

                // The publish that flips the switch while the victim is still waiting.
                http.Scripted.Clear();
                http.Script(HttpResult.Success(200, Encoding.UTF8.GetBytes(ConfigJson.Wrap(2,
                    ConfigJson.Entry("blocker", "popup_a", "enabled", "Normal", "Queue", "Modal", "fade",
                                     "Hide", "", "a.title", "a.body", "0", "0") + "," +
                    ConfigJson.Entry("victim", "popup_b", "disabled", "Normal", "Queue", "Modal", "fade",
                                     "Hide", "", "b.title", "b.body", "0", "0")))));

                config.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

                Assert.IsTrue(service.CloseTop("done"), "close the occupant so the queue advances");
            }

            PopupCompletion victim = completions.Find(c => c.KeyId == "victim");

            Assert.AreEqual(PopupOutcome.Refused, victim.Result.Outcome,
                            "the core evaluates again at admission, which is what makes the demo beat work");
            StringAssert.Contains("victim", victim.Result.Reason);
        }

        // ------------------------------------------------------------------ POL3

        [Test]
        public void POL3_TheFirstRequestOfAFamilyDoesNotFault()
        {
            RemotePopupConfigService config = Service(ConfigJson.One(id: "offer", cooldownSeconds: "60"));
            config.AdoptBestAvailable();

            ConfigPopupPolicy policy = new ConfigPopupPolicy(config, _clock);

            PopupDecision decision = PopupDecision.Refuse("not evaluated");
            Assert.DoesNotThrow(() => decision = policy.Evaluate(Info("offer")),
                                "an indexer here would throw KeyNotFoundException on the FIRST show of every popup");
            Assert.IsTrue(decision.Allowed);
        }

        // ------------------------------------------------------------------ POL4 / POL5

        [Test]
        public void POL4_CooldownRefusesUntilTheClockPassesIt()
        {
            RemotePopupConfigService config = Service(ConfigJson.One(id: "offer", cooldownSeconds: "60"));
            config.AdoptBestAvailable();

            ConfigPopupPolicy policy = new ConfigPopupPolicy(config, _clock);

            policy.NotifyShown(Info("offer"));

            PopupDecision blocked = policy.Evaluate(Info("offer"));
            Assert.IsFalse(blocked.Allowed);
            StringAssert.Contains("cooldown", blocked.Reason);

            _clock.Advance(TimeSpan.FromSeconds(61));
            Assert.IsTrue(policy.Evaluate(Info("offer")).Allowed);
        }

        [Test]
        public void POL4b_CooldownIsPerFamilyNotPerKey()
        {
            RemotePopupConfigService config = Service(ConfigJson.Two("offer_a", "offer_b"));
            config.AdoptBestAvailable();

            // Two keys, one family. Re-adopt with a cooldown on the first.
            RemotePopupConfigService withCooldown = Service(ConfigJson.Wrap(1,
                ConfigJson.Entry("offer_a", "popup_a", "enabled", "Normal", "Queue", "Modal", "fade", "Hide",
                                 "", "a.title", "a.body", "60", "0") + "," +
                ConfigJson.Entry("offer_b", "popup_b", "enabled", "Normal", "Queue", "Modal", "fade", "Hide",
                                 "", "b.title", "b.body", "60", "0")));
            withCooldown.AdoptBestAvailable();

            ConfigPopupPolicy policy = new ConfigPopupPolicy(withCooldown, _clock);

            policy.NotifyShown(Info("offer_a", "offers"));

            Assert.IsFalse(policy.Evaluate(Info("offer_b", "offers")).Allowed,
                           "the family is what a cooldown groups; that is what the Family field is for");
        }

        [Test]
        public void POL5_SessionCapRefusesPastTheLimitAndZeroNeverDoes()
        {
            RemotePopupConfigService capped = Service(ConfigJson.One(id: "offer", maxPerSession: "1"));
            capped.AdoptBestAvailable();

            ConfigPopupPolicy policy = new ConfigPopupPolicy(capped, _clock);

            Assert.IsTrue(policy.Evaluate(Info("offer")).Allowed);
            policy.NotifyShown(Info("offer"));

            PopupDecision second = policy.Evaluate(Info("offer"));
            Assert.IsFalse(second.Allowed);
            StringAssert.Contains("session cap", second.Reason);

            RemotePopupConfigService unlimited = Service(ConfigJson.One(id: "offer", maxPerSession: "0"));
            unlimited.AdoptBestAvailable();

            ConfigPopupPolicy uncapped = new ConfigPopupPolicy(unlimited, _clock);
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(uncapped.Evaluate(Info("offer")).Allowed, "zero is the unlimited sentinel");
                uncapped.NotifyShown(Info("offer"));
            }
        }

        // ------------------------------------------------------------------ POL7

        /// <summary>
        /// The core evaluates a preempting request THREE times — submit, the admission that decides the
        /// occupant may be displaced, and the admission that fills the vacated slot. Its own interface doc
        /// said "twice". The count is not the contract; purity is. This pins both: the real count, and
        /// that a cap of one still lets the popup open and refuses only the NEXT request.
        /// </summary>
        [Test]
        public void POL7_EvaluateIsConsultedThreeTimesOnThePreemptPathAndTheCapIsUnaffected()
        {
            RemotePopupConfigService config = Service(ConfigJson.Wrap(1,
                ConfigJson.Entry("occupant", "popup_a", "enabled", "Normal", "Queue", "Modal", "fade",
                                 "Hide", "", "a.title", "a.body", "0", "0") + "," +
                ConfigJson.Entry("interrupter", "popup_b", "enabled", "High", "InterruptAndResume", "Modal",
                                 "fade", "Hide", "", "b.title", "b.body", "0", "1")));
            config.AdoptBestAvailable();

            CountingPolicy policy = new CountingPolicy(new ConfigPopupPolicy(config, _clock));
            FakeViewFactory factory = new FakeViewFactory();

            List<PopupCompletion> completions = new List<PopupCompletion>();

            using (PopupService service = new PopupService(factory, policy, new FakeAnalytics()))
            {
                service.RequestCompleted += c => completions.Add(c);

                service.Show(new PopupKey<TestPayload>("occupant"), new TestPayload("a"));
                Assert.AreEqual("occupant", service.CurrentKeyId);

                service.Show(new PopupKey<TestPayload>("interrupter"), new TestPayload("b"),
                             new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume));

                Assert.AreEqual("interrupter", service.CurrentKeyId,
                                "a cap of one must not be consumed by the admission checks themselves");
                Assert.AreEqual(3, policy.EvaluationsOf("interrupter"),
                                "submit, the displace decision, and the fill of the vacated slot");
                Assert.AreEqual(1, policy.ShowsOf("interrupter"),
                                "NotifyShown is the once-per-request hook a cap may count on");

                // And the cap is real: the next request for the same key is refused, not the current one.
                // Asserted INSIDE the using — disposing drains every live request as Cancelled, and those
                // terminals land after this one, so a search over the finished list would read the drain.
                int before = completions.Count;

                service.Show(new PopupKey<TestPayload>("interrupter"), new TestPayload("b2"),
                             new ShowOptions(PopupPriority.High, PopupSequencing.InterruptAndResume,
                                             PopupDuplicatePolicy.Allow));

                Assert.AreEqual(before + 1, completions.Count, "the second request terminated immediately");

                PopupResult second = completions[before].Result;
                Assert.AreEqual(PopupOutcome.Refused, second.Outcome);
                StringAssert.Contains("session cap", second.Reason);
                Assert.AreEqual("interrupter", service.CurrentKeyId, "and the popup already on screen is untouched");
            }
        }

        private sealed class FakeAnalytics : IPopupAnalytics
        {
            public void Shown(string keyId)
            {
            }

            public void Dismissed(string keyId, string action)
            {
            }

            public void Converted(string keyId, string action)
            {
            }
        }
    }
}
