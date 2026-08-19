using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// Marks which member of a mutually exclusive button row is the current one.
    ///
    /// A segmented control that never shows its own state is not a cosmetic gap: the aspect row and the
    /// transition row both change something the viewer is then asked to judge, and without a visible
    /// selection they cannot tell a click that landed from one that did nothing.
    ///
    /// It tints rather than swapping sprites so it works on any button in the demo without adding a
    /// serialized sprite reference to every row that wants it.
    /// </summary>
    internal static class DemoSegmentedHighlight
    {
        /// <summary>#A96400 — the amber the kit uses for a primary action.</summary>
        private static readonly Color Selected = new Color(0.663f, 0.392f, 0f, 1f);

        /// <summary>#333D52 — the panel tone the demo's buttons are authored in, so an unselected member
        /// looks exactly as it did before anything was clicked rather than dimmed-out.</summary>
        private static readonly Color Unselected = new Color(0.200f, 0.239f, 0.322f, 1f);

        internal static void Apply(Button selected, params Button[] group)
        {
            if (group == null)
            {
                return;
            }

            for (int i = 0; i < group.Length; i++)
            {
                Button button = group[i];

                if (button == null)
                {
                    continue;
                }

                Graphic target = button.targetGraphic != null
                    ? button.targetGraphic
                    : button.GetComponent<Graphic>();

                if (target != null)
                {
                    target.color = ReferenceEquals(button, selected) ? Selected : Unselected;
                }
            }
        }
    }
}
