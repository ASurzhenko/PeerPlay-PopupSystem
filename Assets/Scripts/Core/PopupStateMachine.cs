using System;
using System.Collections.Generic;

namespace PeerPlay.Popups
{
    internal readonly struct PopupTransition
    {
        internal readonly ulong Id;
        internal readonly PopupState From;
        internal readonly PopupState To;

        internal PopupTransition(ulong id, PopupState from, PopupState to)
        {
            Id = id;
            From = from;
            To = to;
        }
    }

    /// <summary>
    /// The whole legal-transition table, as a bitmask per state. An illegal edge throws, naming both
    /// states and the request: it is a programmer error inside the queue, not a runtime data condition,
    /// and a loud failure is what "strict and predictable" has to mean to be worth claiming.
    ///
    /// Legitimate races never reach here — a second Close, or a user click landing while the core is
    /// already closing, is absorbed by the terminal latch and reported at debug level.
    /// </summary>
    internal static class PopupStateMachine
    {
        private static readonly int[] Allowed = BuildTable();

        /// <summary>
        /// The transition stream, for the property test's "every transition was legal" invariant. Off by
        /// default: the check costs one static bool read per transition and never allocates.
        /// </summary>
        internal static bool RecordTransitions;

        internal static readonly List<PopupTransition> Transitions = new List<PopupTransition>(128);

        internal static bool IsLegal(PopupState from, PopupState to)
        {
            return (Allowed[(int)from] & (1 << (int)to)) != 0;
        }

        internal static void Validate(ulong requestId, PopupState from, PopupState to)
        {
            if (IsLegal(from, to))
            {
                return;
            }

            throw new InvalidOperationException(
                $"{nameof(PopupStateMachine)}.{nameof(Validate)} illegal transition {from} -> {to} for request id={requestId}");
        }

        internal static void Transition(PopupRequest request, PopupState to)
        {
            PopupState from = request.State;
            Validate(request.Id, from, to);
            request.State = to;

            if (RecordTransitions)
            {
                Transitions.Add(new PopupTransition(request.Id, from, to));
            }
        }

        private static int[] BuildTable()
        {
            int[] table = new int[8];

            // None is where a rented request starts. Both edges out of it are real lifecycle events —
            // acceptance, and a rejection that happens before the request ever enters a registry — so
            // they belong in the table and in the recorded stream rather than being assigned around it.
            table[(int)PopupState.None] = Bit(PopupState.Pending) | Bit(PopupState.Disposed);
            table[(int)PopupState.Pending] = Bit(PopupState.Initializing) | Bit(PopupState.Disposed);
            table[(int)PopupState.Initializing] = Bit(PopupState.Opening) | Bit(PopupState.Disposed);
            table[(int)PopupState.Opening] = Bit(PopupState.Active) | Bit(PopupState.Closing) | Bit(PopupState.Disposed);
            table[(int)PopupState.Active] = Bit(PopupState.Suspended) | Bit(PopupState.Closing) | Bit(PopupState.Disposed);
            table[(int)PopupState.Suspended] = Bit(PopupState.Active) | Bit(PopupState.Closing) | Bit(PopupState.Disposed);
            table[(int)PopupState.Closing] = Bit(PopupState.Disposed);
            table[(int)PopupState.Disposed] = 0;
            return table;
        }

        private static int Bit(PopupState state)
        {
            return 1 << (int)state;
        }
    }
}
