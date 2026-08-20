using System;
using System.Windows.Forms;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Admin
{
    /// <summary>
    /// El panel: una ventana, dos pestañas. Explorar las bases y mandar sobre el servidor.
    ///
    /// Es una herramienta de quien lleva el emulador, no algo que se reparta: va junto al servidor,
    /// lee sus bases directamente y habla con su canal de mando. No arranca nada ni para nada: si
    /// el servidor no está, la pestaña en vivo lo dice y la de la base sigue funcionando.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
            Application.Run(new AdminWindow());
        }
    }
}
