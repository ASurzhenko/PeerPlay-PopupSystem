namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The typed half of data injection. The factory casts the rented instance to this and the cast is
    /// the only runtime type check in the pipeline — everything above it is a compile-time
    /// <c>PopupKey&lt;TData&gt;</c>.
    /// </summary>
    public interface IPopupDataReceiver<TData>
    {
        void Bind(in TData data);
    }
}
