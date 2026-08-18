using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Lo que el jugador ha elegido de sus hechizos: qué mitad de cada pareja lleva, y en qué hueco
    /// de la barra puso cada uno.
    ///
    /// Es lo único de los hechizos que no sale de los datos del cliente. Las parejas y los niveles
    /// que pide cada grado son suyos y se leen de ahí (<see cref="SpellTable"/>); esto es del
    /// jugador, y por eso vive en world.db y sobrevive a cerrar el juego.
    /// </summary>
    public static class SpellChoices
    {
        /// <summary>pareja -> hechizo elegido.</summary>
        private static Dictionary<int, int> ChosenStore => SessionContext.State.ChosenSpells;

        /// <summary>hueco de la barra -> hechizo.</summary>
        private static Dictionary<int, int> BarStore => SessionContext.State.SpellBar;

        public static IReadOnlyDictionary<int, int> Chosen => ChosenStore;
        public static IReadOnlyDictionary<int, int> Bar => BarStore;

        public static void LoadFrom(long characterId)
        {
            SessionContext.State.SpellChoicesCharacterId = characterId;
            ChosenStore.Clear();
            BarStore.Clear();

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var picks = connection.CreateCommand();
                picks.CommandText = "SELECT PairId, SpellId FROM CharacterSpellChoices WHERE CharacterId = $id;";
                picks.Parameters.AddWithValue("$id", characterId);
                using (var reader = picks.ExecuteReader())
                {
                    while (reader.Read()) ChosenStore[reader.GetInt32(0)] = reader.GetInt32(1);
                }

                var bar = connection.CreateCommand();
                bar.CommandText = "SELECT Slot, SpellId FROM CharacterSpellBar WHERE CharacterId = $id;";
                bar.Parameters.AddWithValue("$id", characterId);
                using (var reader = bar.ExecuteReader())
                {
                    while (reader.Read()) BarStore[reader.GetInt32(0)] = reader.GetInt32(1);
                }

                Console.WriteLine($"[SpellChoices] {ChosenStore.Count} variantes elegidas y " +
                                  $"{BarStore.Count} huecos de barra guardados.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellChoices] No se pudieron leer las elecciones: {ex.Message}");
            }
        }

        /// <summary>
        /// Guarda que de esta pareja el personaje lleva este hechizo. Se comprueba que el hechizo
        /// sea de verdad uno de los dos: un id que no lo sea dejaría al personaje sin ese hueco.
        /// </summary>
        public static bool Choose(int spellId)
        {
            var pair = SpellTable.PairOf(spellId);
            if (pair == null) return false;

            ChosenStore[pair.Id] = spellId;
            Write("INSERT INTO CharacterSpellChoices (CharacterId, PairId, SpellId) VALUES ($c, $p, $s) " +
                  "ON CONFLICT(CharacterId, PairId) DO UPDATE SET SpellId = $s;",
                  ("$p", pair.Id), ("$s", spellId));
            return true;
        }

        /// <summary>Los huecos de la barra que tienen puesto este hechizo.</summary>
        public static List<int> SlotsHolding(int spellId)
        {
            var slots = new List<int>();
            foreach (var pair in BarStore)
            {
                if (pair.Value == spellId) slots.Add(pair.Key);
            }
            slots.Sort();
            return slots;
        }

        /// <summary>Recuerda en qué hueco de la barra quedó un hechizo.</summary>
        public static void PutInBar(int slot, int spellId)
        {
            if (spellId == 0)
            {
                BarStore.Remove(slot);
                Write("DELETE FROM CharacterSpellBar WHERE CharacterId = $c AND Slot = $t;",
                      ("$t", slot));
                return;
            }

            BarStore[slot] = spellId;
            Write("INSERT INTO CharacterSpellBar (CharacterId, Slot, SpellId) VALUES ($c, $t, $s) " +
                  "ON CONFLICT(CharacterId, Slot) DO UPDATE SET SpellId = $s;",
                  ("$t", slot), ("$s", spellId));
        }

        /// <summary>
        /// Deja apuntada la barra que se le acaba de mandar al cliente, para que la próxima sesión
        /// la encuentre igual. Solo se escribe la primera vez: si el jugador ya la ha tocado, lo
        /// que manda es lo suyo.
        /// </summary>
        public static void RememberBar(IEnumerable<(int Slot, int SpellId)> slots)
        {
            if (BarStore.Count > 0) return;
            foreach (var (slot, spellId) in slots) PutInBar(slot, spellId);
        }

        private static void Write(string sql, params (string Name, object Value)[] parameters)
        {
            long characterId = SessionContext.State.SpellChoicesCharacterId;
            if (characterId == 0) return;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$c", characterId);
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellChoices] No se pudo guardar: {ex.Message}");
            }
        }
    }
}
