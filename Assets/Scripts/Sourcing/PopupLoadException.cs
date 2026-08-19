using System;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// A prefab could not be produced. The core turns it into a LoadFailed carrying this message and the
    /// queue advances — which is the whole point of naming the failure rather than swallowing it.
    /// </summary>
    public sealed class PopupLoadException : Exception
    {
        public PopupLoadException(string message) : base(message)
        {
        }
    }
}
