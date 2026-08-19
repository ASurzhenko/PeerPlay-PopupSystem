# Decisions

Why this submission is shaped the way it is, and what it deliberately does not do. Started during the demo
scene work; the architecture rationale is added alongside the README.

## Known limitations, stated rather than discovered

### The demo's config fixtures are Editor/desktop only

`Application.streamingAssetsPath` is not a filesystem path on Android — it is a `jar:file://…!/assets/…` URL
inside the APK. Two places in the demo assume it is one, and both are Editor/desktop only:

- **`DemoConfigPublisher.FixtureUrl`** builds a `file://` URL from it. Neither `Path.Combine` nor a
  round-trip through `System.Uri` produces something `UnityWebRequest` can read on Android, and every
  fixture button routes through it.
- **`DemoResetController.TryWriteBaselineToCache`** reads the baseline fixture with `File.ReadAllText` to
  seed the last-known-good cache, on the button and once at start-up. On Android that read fails; the helper
  reports it, the cache keeps whatever it held, and the reset says on screen that the cache was not written.

Both halves are named here on purpose: fixing one and leaving the other undocumented is the seam a reviewer
walks into.

This is a decision, not an oversight:

- The demo is played in the Editor. That is where the system is reviewed, and the Editor is the one
  environment the whole submission is guaranteed to run in.
- The fixtures exist so the incident block — empty config, malformed config, kill switch — works with no
  network and no CDN upload. Their job is to make a failure path reachable by a button.
- The popup system itself has no such limitation: the remote config and the remote content both go over real
  HTTPS through `UnityWebRequestHttpClient`, on every platform. The boot fetch in the demo scene is the live
  CloudFront endpoint, and the scene reports its outcome as its first status line.

If the fixtures ever need to run on device, the fix is a platform branch that passes the `jar:file://` URL
through unchanged rather than reconstructing it — one method, `FixtureUrl`.
