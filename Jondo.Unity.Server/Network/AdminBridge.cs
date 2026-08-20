using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Handlers;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// El puente de administración: lo que el panel usa para tocar el servidor EN MARCHA.
    ///
    /// Va colgado del canal de mando, como el resto de las rutas del lanzador, y comparte su forma
    /// de contestar —un código y un cuerpo JSON— para que el panel hable con los dos por el mismo
    /// sitio. Todo lo de aquí exige el rol más alto, comprobado contra la base en cada petición:
    /// es el mismo trato que ya recibían apagar y cambiar roles.
    ///
    /// Lo que se puede hacer:
    ///
    ///   admin/sesiones   quién está dentro: cuenta, personaje, mapa, casilla, si pelea
    ///   admin/expulsar   cerrar el socket de una cuenta
    ///   admin/comando    ejecutar sobre una cuenta un .kamas/.level/.teleport/.size/.shop
    ///   admin/difundir   una línea de chat a todo el que está en el mundo
    ///
    /// Los comandos se ejecutan con la SESIÓN del objetivo puesta en el contexto, que es de donde
    /// cuelga todo el estado que leen (la casilla, las kamas, el nivel); sin ponerla, tocarían al
    /// último que habló con el servidor. La comprobación de rol del comando se salta a propósito:
    /// aquí quien manda ya es administrador —por encima de lo que pide cualquier comando— y el
    /// comando recae sobre la cuenta de otro, cuyo rol no es el que decide.
    /// </summary>
    public static class AdminBridge
    {
        /// <summary>Responde una ruta de administración, o null si no es de aquí.</summary>
        public static ControlApi.Respuesta? Responder(string ruta, string cuerpo)
        {
            if (!ruta.StartsWith(ControlApi.Prefijo + "admin/", StringComparison.Ordinal)) return null;

            long quien = DebeSerAdministrador(cuerpo);
            if (quien < 0) return new ControlApi.Respuesta(403, JsonSerializer.Serialize(new { error = "rol" }));

            return ruta switch
            {
                ControlApi.Prefijo + "admin/sesiones" => Sesiones(),
                ControlApi.Prefijo + "admin/expulsar" => Expulsar(cuerpo),
                ControlApi.Prefijo + "admin/comando" => Comando(cuerpo),
                ControlApi.Prefijo + "admin/difundir" => Difundir(cuerpo),
                _ => new ControlApi.Respuesta(404, JsonSerializer.Serialize(new { error = "ruta" })),
            };
        }

        // ─── Quién puede ──────────────────────────────────────────────────────────────────────

        /// <summary>La cuenta de la petición si es administrador; -1 si no lo es.</summary>
        private static long DebeSerAdministrador(string cuerpo)
        {
            string token = Texto(cuerpo, "token");
            long cuenta = ClientLaunchRegistry.ResolveToken(token);
            if (cuenta <= 0) return -1;

            int rol = DatabaseManager.GetAccountRole(cuenta);
            return Roles.AlMenos(rol, Roles.Administrador) ? cuenta : -1;
        }

        // ─── Las sesiones ─────────────────────────────────────────────────────────────────────

        private static ControlApi.Respuesta Sesiones()
        {
            var sesiones = new List<object>();
            foreach (var par in GameNodeProxy.SesionesVivas)
            {
                var s = par.Value;
                sesiones.Add(new
                {
                    id = par.Key.ToString(),
                    cuenta = s.AccountId,
                    personaje = s.State.CharacterName,
                    idPersonaje = s.CharacterId,
                    nivel = s.State.CharacterLevel,
                    mapa = s.MapId,
                    casilla = s.State.CellId,
                    enElMundo = s.IsInWorld,
                    enCombate = s.State.IsInFight,
                    conectado = s.ConnectedAtUtc,
                });
            }
            return Bien(new { cuantas = sesiones.Count, sesiones });
        }

        /// <summary>La sesión viva de una cuenta, o null si no la tiene abierta.</summary>
        private static GameSession? SesionDe(long cuenta)
            => GameNodeProxy.SesionesVivas.Values.FirstOrDefault(s => s.AccountId == cuenta);

        // ─── Expulsar ──────────────────────────────────────────────────────────────────────────

        private static ControlApi.Respuesta Expulsar(string cuerpo)
        {
            long cuenta = Numero(cuerpo, "cuenta");
            var sesion = cuenta > 0 ? SesionDe(cuenta) : null;
            if (sesion == null || sesion.Stream == null)
                return Bien(new { bien = false, motivo = "no-esta" });

            // Cerrar el socket basta: el bucle de la conexión termina, saca la sesión del registro
            // y el propio cliente muestra su «conexión perdida». No hay que sacar nada a mano.
            Console.WriteLine($"[Admin] La cuenta {cuenta} ({sesion.State.CharacterName}) " +
                              "ha sido expulsada desde el panel.");
            try { sesion.Stream.Close(); } catch { }
            return Bien(new { bien = true });
        }

        // ─── Comandos en vivo ──────────────────────────────────────────────────────────────────

        private static ControlApi.Respuesta Comando(string cuerpo)
        {
            long cuenta = Numero(cuerpo, "cuenta");
            string orden = Texto(cuerpo, "orden").Trim();
            var sesion = cuenta > 0 ? SesionDe(cuenta) : null;

            if (sesion == null || sesion.Stream == null)
                return Bien(new { bien = false, motivo = "no-esta" });
            if (orden.Length == 0 || orden[0] != '.')
                return Bien(new { bien = false, motivo = "orden" });

            // Con la sesión del objetivo puesta: todo lo que el comando lee —casilla, kamas,
            // nivel— sale de este jugador y no del último que pasó por aquí. El using la quita al
            // terminar, que es lo que evita que se quede enganchada.
            bool atendido;
            using (SessionContext.Push(sesion))
            {
                // Bloquear aquí es aceptable a propósito: el canal de mando se sirve en su propio
                // hilo y el comando es corto —tocar la base y contestar al socket—; hacerlo por el
                // camino asíncrono obligaría a revolver todas las rutas del canal.
                atendido = CommandHandler.TryHandleAsync(sesion.Stream, orden, 0, cuenta,
                                                         saltarRol: true)
                                                    .GetAwaiter().GetResult();
            }

            return Bien(new { bien = atendido });
        }

        // ─── Difundir ──────────────────────────────────────────────────────────────────────────

        private static ControlApi.Respuesta Difundir(string cuerpo)
        {
            string texto = Texto(cuerpo, "texto").Trim();
            if (texto.Length == 0) return Bien(new { bien = false, motivo = "texto" });

            byte[] paquete = Handlers.ChatHandler.BuildChatBroadcastPacket(texto, "Jondo");
            int cuantos = 0;
            foreach (var sesion in GameNodeProxy.SesionesVivas.Values)
            {
                if (!sesion.IsInWorld || sesion.Stream == null) continue;
                _ = sesion.SendAsync(paquete);
                cuantos++;
            }

            Console.WriteLine($"[Admin] Difusión del panel a {cuantos} en el mundo: {texto}");
            return Bien(new { bien = true, cuantos });
        }

        // ─── Contestar ─────────────────────────────────────────────────────────────────────────

        private static ControlApi.Respuesta Bien(object cuerpo)
            => new ControlApi.Respuesta(200, JsonSerializer.Serialize(cuerpo));

        private static string Texto(string json, string campo)
        {
            try
            {
                using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
                return doc.RootElement.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "")
                    : "";
            }
            catch { return ""; }
        }

        private static long Numero(string json, string campo)
        {
            try
            {
                using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
                return doc.RootElement.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt64()
                    : 0;
            }
            catch { return 0; }
        }
    }
}
