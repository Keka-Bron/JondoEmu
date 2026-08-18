namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Qué puede hacer cada cuenta.
    ///
    /// Vive en el contrato porque lo necesitan los dos lados: el servidor para decidir, y el
    /// lanzador para saber si le enseña a esa persona los botones de administración o no. Pero que
    /// quede claro cuál de los dos manda: **la comprobación es siempre del servidor**. Lo que el
    /// lanzador hace con esto es cosmético —esconder un botón que de todas formas sería rechazado—
    /// porque el lanzador está en el ordenador del jugador y ahí no se puede confiar en nada.
    ///
    /// El número se guarda en la columna Role de la tabla Accounts y sube: cada nivel puede lo del
    /// anterior y algo más, así que la comprobación es siempre «tiene al menos tanto».
    /// </summary>
    public static class Roles
    {
        /// <summary>Un jugador. Es lo que se es al crear una cuenta.</summary>
        public const int Jugador = 1;

        /// <summary>Moderador: vigila el chat y se mueve por el mundo para atender.</summary>
        public const int Moderador = 2;

        /// <summary>Game master: además toca personajes —nivel, kamas, aspecto— para arreglar cosas.</summary>
        public const int GameMaster = 3;

        /// <summary>Administrador: además manda sobre el propio servidor.</summary>
        public const int Administrador = 4;

        /// <summary>El que se le pone a una cuenta nueva.</summary>
        public const int PorDefecto = Jugador;

        public static bool AlMenos(int rol, int hace_falta) => rol >= hace_falta;

        /// <summary>El nombre del rol, para los registros y para la ventana del servidor.</summary>
        public static string Nombre(int rol) => rol switch
        {
            Administrador => "administrador",
            GameMaster => "game master",
            Moderador => "moderador",
            Jugador => "jugador",
            _ => $"desconocido ({rol})",
        };
    }
}
