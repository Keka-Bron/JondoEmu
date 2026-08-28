using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Lo que mide el personaje, en tanto por ciento de lo que mide su raza.
    ///
    /// La escala viaja en el f5 del bloque de aspecto, empaquetada, y es un MULTIPLICADOR: en la
    /// notación del propio cliente —la que guarda NpcSpawns.Look, "{4907|||130}"— el último número
    /// es eso mismo. Los NPC van casi todos a 100 y un dragopavo a 120, que es el 20 % más grande.
    ///
    /// Ojo con el número de partida: las razas NO valen 100. breed_looks.json, que sale del bundle
    /// del cliente, les da entre 43 y 55 según raza y sexo (un feca macho 53, una feca hembra 52).
    /// Por eso el comando no manda el número que escribe el jugador tal cual: guarda un porcentaje
    /// y multiplica por él la escala que declara la raza. Así el 100 es el tamaño normal de ESE
    /// personaje —sea 43 o 55 lo que valga por dentro— y el 200 es el doble, que es lo pedido.
    ///
    /// Vive aquí y no en GameState porque el aspecto se construye también para personajes que no
    /// se están jugando —la pantalla de selección los pinta todos— y cada uno tiene el suyo.
    /// </summary>
    public static class CharacterSize
    {
        /// <summary>El tamaño de siempre: el que declara la raza, sin tocar.</summary>
        public const int Normal = 100;

        /// <summary>
        /// Los topes. No son de protocolo, son de sentido común: por debajo de 5 el muñeco
        /// desaparece de la pantalla y por encima de 1000 tapa el mapa entero, y en los dos casos
        /// el jugador se queda sin poder verse para arreglarlo.
        /// </summary>
        public const int Minimum = 5;
        public const int Maximum = 1000;

        private static readonly Dictionary<long, int> _cache = new Dictionary<long, int>();
        private static readonly object _lock = new object();

        /// <summary>El tamaño de un personaje. Uno que nunca lo haya tocado mide lo normal.</summary>
        public static int Of(long characterId)
        {
            if (characterId <= 0) return Normal;

            lock (_lock)
            {
                if (_cache.TryGetValue(characterId, out int cached)) return cached;
            }

            int size = Read(characterId);

            lock (_lock)
            {
                _cache[characterId] = size;
            }
            return size;
        }

        /// <summary>
        /// Cambia el tamaño y lo deja escrito. Devuelve el que ha quedado, que puede no ser el
        /// pedido si venía fuera de los topes.
        /// </summary>
        public static int Set(long characterId, int percent)
        {
            int size = Math.Clamp(percent, Minimum, Maximum);
            if (characterId <= 0) return size;

            lock (_lock)
            {
                _cache[characterId] = size;
            }

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Characters SET Size = $size WHERE Id = $id;";
                command.Parameters.AddWithValue("$size", size);
                command.Parameters.AddWithValue("$id", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // Que no se guarde no puede impedir que se vea: el aspecto ya lleva el tamaño
                // nuevo en memoria y lo que se pierde es que sobreviva a cerrar el juego.
                Console.WriteLine($"[Tamaño] No se pudo guardar el tamaño de {characterId}: {ex.Message}");
            }

            return size;
        }

        /// <summary>
        /// Las escalas de la raza ya multiplicadas por el tamaño del personaje.
        ///
        /// Se redondea hacia arriba con un suelo de 1: un tamaño pequeño sobre una escala pequeña
        /// da cero al redondear, y un cero en el f5 es "sin escala", que el cliente dibuja al
        /// tamaño de por defecto. Es decir, encoger de más devolvía al muñeco a su tamaño normal.
        /// </summary>
        public static List<long> Applied(IReadOnlyList<long> scales, long characterId)
        {
            var salida = new List<long>();
            if (scales == null) return salida;

            int size = Of(characterId);
            foreach (long scale in scales)
            {
                if (size == Normal) { salida.Add(scale); continue; }
                salida.Add(Math.Max(1, (long)Math.Round(scale * size / 100.0)));
            }
            return salida;
        }

        private static int Read(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(Size, $normal) FROM Characters WHERE Id = $id;";
                command.Parameters.AddWithValue("$normal", Normal);
                command.Parameters.AddWithValue("$id", characterId);

                var result = command.ExecuteScalar();
                if (result != null && result != DBNull.Value &&
                    int.TryParse(result.ToString(), out int size) && size > 0)
                {
                    return Math.Clamp(size, Minimum, Maximum);
                }
            }
            catch (Exception ex)
            {
                // Una base sin la columna todavía —o un personaje que no está— no puede dejar al
                // muñeco sin dibujar: se mide como todo el mundo.
                Console.WriteLine($"[Tamaño] No se pudo leer el tamaño de {characterId}: {ex.Message}");
            }
            return Normal;
        }
    }
}
