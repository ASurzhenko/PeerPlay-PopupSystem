using System.Runtime.CompilerServices;

// The EditMode suite is the evidence the queue works, so it needs the internal hooks the public
// surface deliberately does not expose: PopupRequest.HasAwaiter / Cts / IsRegistered, the recorded
// transition stream, and the ForceTerminate counter.
[assembly: InternalsVisibleTo("PeerPlay.Popups.Tests")]
