using System;
using System.Net;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// A qué se atan los puertos del emulador: sólo a esta máquina, o a toda la red.
    ///
    /// De los cuatro que abre el servidor, dos estaban atados a <c>127.0.0.1</c> —el proxy del
    /// servidor de juego y el zaap— y los otros dos a <c>IPAddress.Any</c>, o sea abiertos a
    /// cualquiera que compartiera la red: el chat y el nodo de juego. No parece una decisión, sino
    /// dos ficheros escritos en momentos distintos, porque nada del emulador necesita esos dos
    /// puertos desde fuera: el cliente de Dofus habla con ellos por <c>localhost</c>, que es adonde
    /// le manda el mod.
    ///
    /// Así que ahora los cuatro van igual, y para abrirlos hay que pedirlo a propósito con
    /// <c>JONDO_PUBLIC_BIND=1</c>. Eso hace falta si algún día el servidor vive en otra máquina;
    /// mientras tanto, cerrado.
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
