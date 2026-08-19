using System.Runtime.CompilerServices;

// The composition root constructs PopupPool and calls PopupLayer.Bind, both internal on purpose: the
// alternative is a wider public surface a reviewer has to read past.
[assembly: InternalsVisibleTo("PeerPlay.Popups.App")]

// The suite asserts the pooling invariant, the input gate and the backdrop, none of which are public.
[assembly: InternalsVisibleTo("PeerPlay.Popups.ViewSourcing.Tests")]
