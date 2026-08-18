using System.Drawing;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Una ventana que ya tiene su fondo compuesto y deja que sus paneles lo recorten.
    ///
    /// WinForms no tiene transparencia de verdad: un panel "transparente" pinta lo que haya en su
    /// padre, no lo que haya detrás en la pantalla. Así que la ventana compone su fondo una vez
    /// —la foto recortada como haría un background-size: cover— y cada panel se recorta el trozo
    /// que le toca.
    ///
    /// Existe como interfaz, y no como una clase concreta, porque ahora hay DOS ventanas que lo
    /// hacen: la del lanzador y la del servidor. El logo se dibuja igual en las dos.
    /// </summary>
    public interface IVentanaConFondo
    {
        Image? ComposedBackground { get; }
    }
}
