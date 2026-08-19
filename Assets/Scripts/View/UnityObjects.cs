using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Destruction behind one call, because <see cref="Object.Destroy"/> throws
    /// "Destroy may not be called from edit mode" outside play mode and the whole suite is EditMode.
    /// Public rather than internal: the sprite cache in the sourcing assembly calls it, and an asmdef
    /// reference does not grant access to internals.
    /// </summary>
    public static class UnityObjects
    {
        public static void Destroy(Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
