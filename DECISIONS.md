# Decisions

One row per decision: what was chosen, why, and what it cost. The last column is the one worth reading — a
decision with no cost was not a decision.

Written because the next conversation is built on this test, and because a reviewer should be able to tell an
argued choice from an accident.

---

## 1. Stack and framing

| # | Decision | Chosen | Why | What it cost |
|---|---|---|---|---|
| D1 | Editor version | `6000.0.64f1` | The version the reviewer runs, so opening the project offers no upgrade dialog on the one action they must perform | One editor install; no 6.3 features |
| D2 | Async primitive | **UniTask** | Allocation-free and cancellation-native, which is what "the UI must remain responsive" actually requires | A git-URL dependency in `manifest.json`; UniTask's await-once rule becomes a documented caller obligation on `ShowAsync` |
| D3 | DI container | **None — a hand-rolled composition root** | Everything is constructor-injected against interfaces and wired in one file (`App/PopupSystemInstaller.cs`); adopting a container afterwards is a bindings file, not a refactor. Avoids installer-scope risk inside a two-day budget while keeping the discipline the container exists for | No scene-scoped lifetimes and no automatic disposal — teardown is written by hand in `OnDestroy`. Six seam interfaces exist under `Core/Seams/`; `PopupService`'s constructor takes three of them (`IPopupViewFactory`, `IPopupPolicy`, `IPopupAnalytics`), and the rest are consumed by the view and sourcing layers |
| D4 | Reactive layer | **None — plain events + UniTask completion** | A queue of a handful of popups buys little from streams, and a second paradigm costs more readability than the stack-fit signal is worth | No declarative composition of popup streams; consumers subscribe to one `event Action<PopupCompletion>` |
| D5 | Tweening | **DOTween**, UI module only | The transition system plugs into an easing library rather than reinventing one. `DOTWEEN_NO*` defines compile out audio, physics, physics2D and sprites | The `.dll` and modules are committed (D28), and `UNITASK_DOTWEEN_SUPPORT` is a define the next person must know about — it is set for Android and Standalone but not iPhone (limitation 7) |
| D6 | Asset delivery | **Addressables** | The only honest way to do "remote popups"; a `Resources.Load` with a remote-sounding name is not one | A content build is needed for the remote *asset* surface, which the supported play mode does not exercise (limitation 1) |
| D7 | Remote delivery | **Real HTTPS**, on a CloudFront distribution under a project-specific prefix | Mocking it would dodge the exact thing the task is about. The config and the offer image are genuinely fetched over the network in every play mode | The endpoints are the author's and will not live forever, which is why the demo also ships local fixtures for the incident beats |
| D8 | Render pipeline | The 2D URP template, untouched | Matches a 2D mobile product; nothing in the deliverable depends on it — the system is UGUI on a Screen-Space-Overlay canvas and is pipeline-agnostic | URP config assets in the repo that the system never reads |
| D9 | Orientation | Portrait, 1080×1920, `CanvasScaler` Match = 0 (width) | A portrait popup is width-constrained, which is the harder and more honest layout problem. Match = 0 gives tall phones *more* vertical room | The **tablet** becomes the dangerous case: at 3:4 the canvas is only 1440 units tall, so every popup carries a ≤ ~1300-unit height budget. The demo's aspect switcher exists to make that visible |
| D10 | Screen adaptation | `CanvasScaler` + anchors + a safe-area fitter + the demo's aspect switcher | A per-aspect override subsystem would need per-aspect data authored on every element | No per-aspect layout overrides; a popup that needs one has to be authored for it |

## 2. Architecture

| # | Decision | Chosen | Why | What it cost |
|---|---|---|---|---|
| D11 | The core is MonoBehaviour-free and UI-free | Plain C#, one assembly that references UniTask and nothing else | It is what "queue logic independent of UI rendering" means, and it makes the queue testable in EditMode with no scene | **UI-free, not engine-free**: five files reach `UnityEngine`, four of them for `Debug` and one for `Time.realtimeSinceStartup`. The queue itself takes no clock — `IPopupClock` was removed from `PopupService`'s constructor and injected into the policy that actually reads it, because a seam every composition root must supply for nothing is a cost with no buyer |
| D12 | **Four runtime assemblies**, not one | Core / View / Sourcing / App, plus one Editor-only demo assembly and two test assemblies | It turns the independence claim from prose into something the compiler enforces | Five `InternalsVisibleTo` grants across three `AssemblyInfo.cs` files, and `Sourcing → View` is a one-way reference that has to stay one-way |
| D13 | Interrupt semantics on **two axes** | The queue owns sequencing (`Queue` / `InterruptAndResume` / `Replace`); whether an interrupted popup hides or stays visible is a **view-level** flag | Putting visibility into the queue's enum would break "queue independent of rendering" at the most visible point, and a confirm-over-settings dialog falls out naturally instead of needing a fourth enum value | Two concepts instead of one; the demo needs two buttons to show what one enum would have shown in one |
| D14 | One latched terminal path | Every outcome — completed, refused, duplicate, load-failed, superseded, cancelled, faulted — leaves through one idempotent method that then advances the queue | A second exit is how a queue wedges. The latch makes a double terminal unrepresentable rather than merely avoided | The method is long and numbered; it is the file's centre of gravity and reads that way |
| D15 | The queue holds **ids**, not request references | `PopupQueue` stores `ulong` ids | Requests are pooled, and holding references makes aliasing a recycled object into two places representable. Ids make it impossible | One dictionary lookup on every queue operation |
| D16 | Every call into foreign code is guarded | View, factory, policy, analytics, awaiter, completion observers | A third-party throw must not pin the slot. Both core Criticals found in review were exactly this shape: an unguarded analytics call wedged the queue permanently, and an unguarded policy call destroyed the popup on screen for a fault that belonged to a queued request | Two call sites are still unguarded and are named as limitation 5 rather than claimed clean |
| D17 | Pooled request objects | A static free list per payload type | No per-request closures or boxing on the steady path | It is the one piece of global mutable state in the system — stated here (limitation 9) rather than left to be found |
| D18 | Seams that ship with a default and no real implementation | `IPopupPolicy`, `IPopupAnalytics`, `IPopupTextProvider` | A popup in a live game is an offer, and offers need frequency caps, analytics and localised copy in week two. One shallow interface each, so that week is an implementation and not a refactor | Three interfaces a reader has to recognise as seams rather than as unfinished features |

## 3. Config safety — the failure this system is built around

| # | Decision | Chosen | Why | What it cost |
|---|---|---|---|---|
| D19 | Remote config is **validated before adoption**; a rejected payload keeps the last-known-good | 13 structural rules in pure C# plus a 14th that probes asset resolution; a device-cached copy wins at boot | Publishing a config is the most common way to break a live game without shipping a release. An empty or malformed publish must not be able to take the popups away | Validation is a maintenance surface: a new config field means a new rule and a new test |
| D20 | A per-popup **kill switch** in the config | `state`, consulted at submit **and** re-consulted at admission | A server-side off switch that needs no release, and the second consultation is why a popup already queued behind a blocker still gets stopped | The refusal has to be *reported* rather than swallowed, so it is a terminal with a reason string — one more outcome every caller can receive |
| D21 | The kill switch is a **string**, not a `bool` | `"state": "enabled"` | `JsonUtility` cannot tell an absent field from `false`. A `bool enabled` would mean that a config which omits the field **disables every popup** — the exact failure this section exists to prevent, in undetectable form | The whole DTO is strings and is parsed by hand: more parsing code, more tests |
| D22 | A config that skipped asset resolution is **not** cached as last-known-good | The cache write is gated on the resolution having actually run | Otherwise an unverified payload becomes the boot config permanently and can repoint a popup at an asset that resolves nowhere — every launch, forever | A payload adopted for the current session is deliberately not persisted, and says so in a warning |
| D23 | Two bounds on the HTTP path, with a **reason** | A per-request timeout clamped strictly below the 8 s whole-operation deadline (`MaxRequestTimeoutSeconds`, derived from the deadline); the failure names which one fired (`Timeout` vs `Deadline`) | Two safeguards that share one visible outcome let a system behave correctly for the wrong reason, permanently and quietly — no passing run distinguishes them. So the ordering is enforced where the value is consumed rather than asked for in a comment, and the reason discriminator makes the difference observable | A caller asking for a timeout at or above the deadline is quietly given a smaller one and warned, instead of getting what it asked for. The enforcement is pinned by a test that fails when the clamp is deleted — which is the only thing that can catch this class, since both bounds look identical from outside |
| D24 | Retry policy | 3 attempts, fixed backoff `0.5 s, 1.5 s`, retrying transport / timeout / 5xx and never a 4xx | A 4xx is a publishing mistake — retrying it wastes the deadline and hides the cause | The backoff table is indexed by attempt, so its length is a function of the attempt count; that coupling is invisible in the code and is pinned by a test rather than by the type system |
| D24b | **Log level tracks the consequence, not the event** | On this subsystem exactly three things log at **error**: a boot with no usable cache and no usable built-in (`AdoptBestAvailable`, both arms), and a cache the device could not write (`PopupConfigCache.Write`). Everything else a refusal touches — a payload the validator rejected, a fetch that never arrived, an `assetId` that resolves nowhere, a superseded refresh — logs at **warning**, because in every one of those the config that was already live keeps serving | An error should mean "something is now missing". A refusal is the mechanism working, and logging it loudly pages someone for a success, buries the one case that is genuinely lost, and forces the tests around it to silence logs wholesale — which is how a suite stops noticing that the character of its logs changed. The cache write is on the loud side because a permanently unwritable cache is not self-healing: every future boot pays for it | The rule has to be applied by hand at each new call site, and the level is asserted by test in only two places (`C12b`, `C16`) rather than everywhere |

## 4. View, pooling, performance

| # | Decision | Chosen | Why | What it cost |
|---|---|---|---|---|
| D25 | Pooling keyed by popup key, cap 2 per key, refcount per **instance** | Idle instances keep holding their prefab refcount | Rent/return is the reuse the task asks for, and per-instance accounting is what makes acquire/release balance provable instead of asserted | Changing a live key's `assetId` in a published config needs a session restart; the swap is refused loudly rather than releasing an asset a live popup is using (limitation 2) |
| D26 | Modality via a counted input gate plus a backdrop that owns its own `CanvasGroup` | The backdrop's group sets `ignoreParentGroups` | The obvious implementation — one gate on the layer root — unblocks background input **for the duration of every transition**, the exact inverse of the requirement. It shipped that way and the review caught it | A second `CanvasGroup`, and a rule about how raycasts walk up the hierarchy that a maintainer has to know |
| D27 | Transitions behind a **string id in a registry** | `IPopupTransition` + `PopupTransitionRegistry` | A new transition is a new file, and the remote config can select one by id with no rebuild | A string is not compile-checked; an unknown id falls back to `instant` with a warning rather than throwing |
| D28 | UI art **generated for this project**, not a purchased asset pack | A generated kit, cut, verified and 9-sliced by hand | With a store pack the *files are the value*, and redistributing them in a public repository is what the licence does not allow. Generating the kit keeps the repository publishable and lets us set our own 9-slice borders | Two to three hours, for art that is competent rather than beautiful |
| D29 | **DOTween is committed** to the repository | The free version 1.3.030, under `Assets/Plugins/Demigiant`, licence at `dotween.demigiant.com/license.php` | Without it the project does not compile on clone, and compiling on clone is the one thing the reviewer must be able to do. That is what separates it from D28's asset pack: DOTween is a *dependency* whose value is that the build works, while a GUI pack's value is the files themselves | A vendored binary in a submission repo, and one more thing to keep in step |
| D30 | Font under an open licence | Fredoka (SIL OFL), licence file committed beside the asset | Everything in a public repository has to be redistributable | One font, and no variable-weight authoring beyond the SDF asset |
| D31 | Texture compression **ASTC**, block size per atlas | Verified applied (`overridden=True format=ASTC_6x6`), not merely requested | ETC1 carries no alpha, so every UI sprite would fall back to uncompressed RGBA and inflate the build; ETC2 has alpha but bands visibly on gold bevels and soft glows. ASTC does not require power-of-two textures — that constraint belongs to the older ETC1/PVRTC path | GLES 3.1 / Vulkan devices only, which is the honest target for a 2026 mobile title |
| D32 | Atlas packing: rotation off, tight packing off, alpha dilation on, padding 4 | Plus a read-back verifier that throws if any of them drift | With tight packing, a concave sprite's bounding rectangle can contain a slice of its neighbour and a UI `Image` samples the rectangle — foreign artwork inside icons. The build tool also once reported "ASTC 6×6" while the importer held `overridden=false`, which is why the verifier reads the setting back instead of trusting the request | A few percent of atlas area, and a verifier that must be kept in step with the settings |
| D33 | Group layout: local group `PackTogether`, remote group `PackSeparately`, LZ4 for both | Plus the shared atlas marked addressable in the local group | The atlas is referenced by a local prefab and by the remote offer prefab; unless it is an entry itself it is implicit to both groups and packed into both bundles. Measured: duplicate rows 38 → 0 | LZ4 is larger over the wire than LZMA, which only pays when download size dominates and costs a full decompress on first use |
| D34 | The built-in default config is a direct `[SerializeField]`, not an addressable | One `TextAsset` on the composition root | It is needed in `Awake`, before Addressables exists. An asset that is both an addressable entry and a direct scene reference ships twice — the same duplication D33 is about | The default config cannot be updated remotely, which is what the remote config is for; and the suite's structural guard for it currently validates a copy rather than the shipped file (limitation 10) |

## 5. Process, and the uncomfortable rows

| # | Decision | Chosen | Why | What it cost |
|---|---|---|---|---|
| D35 | The supported Addressables play mode is **Use Asset Database** | Stated in the README rather than worked around | The mode selection lives in `Library/`, which is gitignored, so a fresh clone starts there whatever the author selected. Designing for another mode designs for a machine the reviewer does not have | The remote *asset* surface is real only in a packed build and is described as such, never demonstrated as if bytes came over the wire |
| D36 | The demo ships in the **same runtime assembly** as the composition root | Only the diagnostics overlay is compiled out of a release player | A separate demo assembly needs the same UI references plus two more `InternalsVisibleTo` grants, for no benefit in a project with no player build | A reviewer will notice; it is a decision rather than an oversight |
| D37 | Every Critical gets fixed and tested; most Warnings get named instead | Three review passes over the code. Every Critical they found is fixed, each with a test that fails when the fix is removed, and the fixes were re-verified by a separate reviewer reading the code rather than the fix list. The Warnings were triaged: a few closed, the rest carried as named limitations in the README's list. The measurement run then added one the reviews had missed — the HTTP budget's timer outliving the token source it cancels, logging an `ObjectDisposedException` after every successful request — fixed, with its own discriminating test | A Critical is a defect a player can hit; a late Warning in merged, covered code is usually a worse trade to touch than to name. The last defect is also the argument for measuring at all: it was invisible to three reviews and to a green test suite, and one run of the demo surfaced it | Publishing the list invites *what else is in there?* — answered by the README's Known limitations, in full. It also means the submission ships with known-open findings, deliberately, and says which |

---

## Known limitation in the demo, in full: the config fixtures are Editor/desktop only

The popup system fetches its config and its remote content over real HTTPS on every platform. The **demo's**
fixtures — the payloads behind the incident buttons — do not, and this is deliberate.

`Application.streamingAssetsPath` is not a filesystem path on Android; it is a `jar:file://…!/assets/…` URL
inside the APK. Two places in the demo assume it is one, and **both** would have to change to fix it:

- **`DemoConfigPublisher.FixtureUrl`** builds a `file://` URL from it. Neither `Path.Combine` nor a round-trip
  through `System.Uri` produces something `UnityWebRequest` can read on Android, and every fixture button
  routes through it.
- **`DemoResetController.TryWriteBaselineToCache`** reads the baseline fixture with `File.ReadAllText` on the
  raw path — it never touches `FixtureUrl` — to seed the last-known-good cache, both on the reset button and
  once at start-up. On Android that read fails, the helper reports it, the cache keeps whatever it held, and
  the reset says on screen that the cache was not written.

Naming only one of the two would be the seam a reviewer walks into: patching `FixtureUrl` alone leaves the
seed broken.

Why it stays:

- The demo is played in the Editor. That is where the system is reviewed and the one environment the whole
  submission is guaranteed to run in.
- The fixtures exist so the incident block — empty config, malformed config, kill switch — is reachable with
  no network and no upload. Their job is to make a failure path pressable.
- The system itself has no such limitation. The boot fetch in the demo scene goes to the live endpoint and
  reports its outcome as the first status line on screen.

If they ever need to run on device, the fix is a platform branch in both places: pass the `jar:file://` URL
through unchanged instead of reconstructing it, and read the baseline through `UnityWebRequest` instead of
`File.ReadAllText`.
