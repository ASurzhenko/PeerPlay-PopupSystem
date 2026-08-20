using System;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// A key is registered against a view that cannot accept its payload type. A mis-registration, not a
    /// runtime condition: the core turns it into a LoadFailed carrying this message, so the message names
    /// both types.
    /// </summary>
    public sealed class PopupBindException : Exception
    {
        public PopupBindException(string message) : base(message)
        {
        }
    }
}
