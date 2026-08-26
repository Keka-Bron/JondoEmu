using System;
using System.Net;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// A qué se atan los puertos del emulador: sólo a esta máquina, o a toda la red.
    ///
    /// De los cinco servicios que abre el servidor, el proxy de juego, el zaap y el HAAPI estaban
    /// atados a <c>127.0.0.1</c>, mientras que el chat y el nodo de juego usaban
    /// <c>IPAddress.Any</c>. No era una decisión de despliegue: eran listeners escritos en momentos
    /// distintos. Los cinco siguen cerrados por defecto y se abren juntos cuando se pide el modo
    /// remoto.
    ///
    /// Así que ahora los cuatro van igual, y para abrirlos hay que pedirlo a propósito con
    /// <c>JONDO_PUBLIC_BIND=1</c>. Hace falta cuando el servidor vive en otra máquina; mientras
    /// tanto, cerrado.
    ///
    /// La idea es de la pull request de Raphaël, que traía un fichero equivalente. El código es
    /// nuestro: aquí sólo hacen falta las dos líneas que de verdad se usan.
    /// </summary>
    public static class ServerBinding
    {
        /// <summary>¿Se han pedido los puertos abiertos a la red?</summary>
        public static bool Public
        {
            get
            {
                string valor = (Environment.GetEnvironmentVariable("JONDO_PUBLIC_BIND") ?? "").Trim();
                return valor == "1"
                    || valor.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || valor.Equals("si", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>La dirección a la que atarse. Cerrado salvo que se pida lo contrario.</summary>
        public static IPAddress TcpAddress => Public ? IPAddress.Any : IPAddress.Loopback;

        /// <summary>Para el registro del servidor, que diga con qué puerta ha arrancado.</summary>
        public static string Description => Public ? "toda la red" : "sólo esta máquina";
    }
}
