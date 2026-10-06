using System.Drawing;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// A window that already has its background composed and lets its panels cut it out.
    ///
    /// WinForms has no real transparency: a "transparent" panel paints whatever is in its
    /// parent, not whatever is behind it on screen. So the window composes its background once
    /// —the photo cropped as a background-size: cover would— and each panel cuts out the piece
    /// that belongs to it.
    ///
    /// It exists as an interface, and not as a concrete class, because there are now TWO windows that
    /// do it: the launcher's and the server's. The logo is drawn the same in both.
    /// </summary>
    public interface IBackgroundWindow
    {
        Image? ComposedBackground { get; }
    }
}
