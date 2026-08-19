namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The typed half. A concrete popup derives from this and its Bind is the only place the payload is
    /// seen — the factory's cast to <see cref="IPopupDataReceiver{TData}"/> is what turns a
    /// mis-registration into a named exception instead of a wrong cast at runtime.
    /// </summary>
    public abstract class PopupView<TData> : PopupView, IPopupDataReceiver<TData>
    {
        public abstract void Bind(in TData data);
    }
}
