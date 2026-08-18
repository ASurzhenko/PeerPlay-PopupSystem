using System.Collections.Generic;

namespace PeerPlay.Popups
{
    /// <summary>
    /// One free list per payload type, so a steady stream of popups allocates no request objects. The
    /// pool is safe precisely because nothing outside the live map holds a request: handles and queue
    /// bands hold ids, and a recycled object is rented back with a fresh id.
    /// </summary>
    internal static class PopupRequestPool<TData>
    {
        private const int Capacity = 32;

        private static readonly Stack<PopupRequest<TData>> Free = new Stack<PopupRequest<TData>>(4);

        internal static int FreeCount => Free.Count;

        internal static PopupRequest<TData> Rent()
        {
            return Free.Count > 0 ? Free.Pop() : new PopupRequest<TData>();
        }

        internal static void Return(PopupRequest<TData> request)
        {
            if (Free.Count >= Capacity)
            {
                return;
            }

            Free.Push(request);
        }

        /// <summary>
        /// Test hook: a recycled request must carry nothing forward — no token source, no view, no
        /// registration in the queue. A dirty object in here is a leak the next rent would inherit.
        /// </summary>
        internal static bool AllPooledAreClean()
        {
            foreach (PopupRequest<TData> request in Free)
            {
                if (request.Cts != null || request.View != null || request.IsRegistered || request.HasAwaiter)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
