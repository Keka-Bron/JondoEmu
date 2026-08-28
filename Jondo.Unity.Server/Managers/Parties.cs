using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los grupos: quién va con quién y quién manda.
    ///
    /// ─── El identificador ───────────────────────────────────────────────────────────────────
    ///
    /// Un grupo tiene número propio, y no es el de nadie: en las cuatro capturas salen 69145,
    /// 69158, 69186 y 71272, seguidos y bajos, o sea un contador del servidor. Aquí se hace igual
    /// y se empieza donde ellos, para que los números tengan la pinta que el cliente espera.
    ///
    /// ─── No se guarda en la base ────────────────────────────────────────────────────────────
    ///
    /// Un grupo dura lo que dura la sesión: si se cae el servidor, no hay grupo que recuperar,
    /// igual que en el juego real. Por eso vive en memoria y no en SQLite.
    ///
    /// ─── Las reglas que salen de las capturas ───────────────────────────────────────────────
    ///
    /// Un grupo con UNA persona se deshace: al rechazar la invitación, el servidor real manda el
    /// <c>iko</c> y el <c>imy</c> pegados, en el mismo segmento TCP. O sea que invitar crea el
    /// grupo antes de que el otro conteste, y si dice que no, se deshace solo.
    ///
    /// Y las plazas son ocho, que es lo que lleva el f10 del ing y el f3 del ijz.
    /// </summary>
    public static class Parties
    {
        /// <summary>Cuánta gente cabe. Del f10 del ing y del f3 del ijz.</summary>
        public const int MaxMembers = 8;

        /// <summary>Por dónde empiezan los números, para que se parezcan a los de verdad.</summary>
        private const int FirstId = 69000;

        public sealed class Party
        {
            public int Id { get; init; }

            /// <summary>Quién manda. Es el f4 del ing.</summary>
            public long LeaderId { get; set; }

            /// <summary>Los que ya están dentro, en el orden en que entraron.</summary>
            public List<long> Members { get; } = new();

            /// <summary>Los que tienen la invitación abierta: invitado → quién le invitó.</summary>
            public Dictionary<long, long> Pending { get; } = new();

            public object Gate { get; } = new();
        }

        private static int _next = FirstId;
        private static readonly ConcurrentDictionary<int, Party> _parties = new();

        /// <summary>En qué grupo está cada personaje, ya sea dentro o invitado.</summary>
        private static readonly ConcurrentDictionary<long, int> _of = new();

        public static int Count => _parties.Count;

        public static Party? Get(int id) => _parties.TryGetValue(id, out var p) ? p : null;

        /// <summary>El grupo de un personaje, esté dentro o tenga una invitación abierta.</summary>
        public static Party? Of(long characterId)
            => _of.TryGetValue(characterId, out int id) ? Get(id) : null;

        /// <summary>¿Está DENTRO de un grupo? Tener una invitación abierta no cuenta.</summary>
        public static bool IsInParty(long characterId)
        {
            var party = Of(characterId);
            if (party == null) return false;
            lock (party.Gate) return party.Members.Contains(characterId);
        }

        /// <summary>
        /// Crea el grupo alrededor de quien invita. El servidor real lo crea al mandar el ime,
        /// antes de que el otro conteste: por eso el ing con un solo miembro llega enseguida.
        /// </summary>
        public static Party Create(long leaderId)
        {
            var party = new Party { Id = System.Threading.Interlocked.Increment(ref _next), LeaderId = leaderId };
            party.Members.Add(leaderId);
            _parties[party.Id] = party;
            _of[leaderId] = party.Id;
            return party;
        }

        /// <summary>
        /// Deja la invitación abierta. Falso si el invitado ya está en algún grupo.
        ///
        /// Si ya la tenía abierta y le vuelve a invitar el mismo, vale igual y se le manda otra
        /// vez: cerrar la ventanita por la equis no avisa al servidor, y si esto no valiera, el
        /// invitado se quedaría atascado para siempre sin poder volver a ser invitado.
        /// </summary>
        public static bool Invite(Party party, long guestId, long hostId)
        {
            if (IsInParty(guestId)) return false;

            lock (party.Gate)
            {
                if (party.Pending.TryGetValue(guestId, out long antes)) return antes == hostId;
                if (party.Members.Count + party.Pending.Count >= MaxMembers) return false;
                party.Pending[guestId] = hostId;
            }
            _of[guestId] = party.Id;
            return true;
        }

        /// <summary>Acepta: pasa de invitado a miembro.</summary>
        public static bool Accept(Party party, long guestId)
        {
            lock (party.Gate)
            {
                if (!party.Pending.Remove(guestId)) return false;
                if (party.Members.Contains(guestId)) return true;
                party.Members.Add(guestId);
            }
            _of[guestId] = party.Id;
            return true;
        }

        /// <summary>Rechaza. Devuelve quién había invitado, o cero si no había tal invitación.</summary>
        public static long Refuse(Party party, long guestId)
        {
            long host;
            lock (party.Gate)
            {
                if (!party.Pending.TryGetValue(guestId, out host)) return 0;
                party.Pending.Remove(guestId);
            }
            _of.TryRemove(guestId, out _);
            return host;
        }

        /// <summary>
        /// Se va del grupo. Devuelve a quién hay que avisar y si el grupo se ha deshecho.
        ///
        /// Si el que se va era el jefe, manda el siguiente que entró: un grupo sin jefe no lo
        /// entiende el cliente, y dejar el mando en alguien que ya no está es peor.
        /// </summary>
        public static (IReadOnlyList<long> Remaining, bool Dissolved, long NewLeader) Leave(
            Party party, long characterId)
        {
            bool dissolved;
            long newLeader = 0;
            List<long> remaining;

            lock (party.Gate)
            {
                party.Members.Remove(characterId);
                party.Pending.Remove(characterId);

                if (party.LeaderId == characterId && party.Members.Count > 0)
                {
                    party.LeaderId = party.Members[0];
                    newLeader = party.LeaderId;
                }

                dissolved = party.Members.Count <= 1;
                remaining = new List<long>(party.Members);
            }

            _of.TryRemove(characterId, out _);
            if (dissolved) Dissolve(party);
            return (remaining, dissolved, newLeader);
        }

        /// <summary>Cede el mando. Falso si el destinatario no está dentro.</summary>
        public static bool Promote(Party party, long newLeaderId)
        {
            lock (party.Gate)
            {
                if (!party.Members.Contains(newLeaderId)) return false;
                party.LeaderId = newLeaderId;
            }
            return true;
        }

        /// <summary>Deshace el grupo y suelta a todos.</summary>
        public static void Dissolve(Party party)
        {
            long[] todos;
            lock (party.Gate)
            {
                todos = party.Members.Concat(party.Pending.Keys).ToArray();
                party.Members.Clear();
                party.Pending.Clear();
            }
            foreach (long quien in todos) _of.TryRemove(quien, out _);
            _parties.TryRemove(party.Id, out _);
        }

        /// <summary>Los miembros, para mandarles algo a todos.</summary>
        public static IReadOnlyList<long> MembersOf(Party party)
        {
            lock (party.Gate) return new List<long>(party.Members);
        }
    }
}
