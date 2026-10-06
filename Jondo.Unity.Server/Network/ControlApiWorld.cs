using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The control API's routes for acting on the world from the administrator's window (F10 in
    /// the client): what stands on his map, putting NPCs and monsters on it and taking them off,
    /// and the jail. Administrator only, as every route of this group (see the switch in
    /// <see cref="ControlApi"/>).
    /// </summary>
    public static partial class ControlApi
    {
        /// <summary>The caller's own character in the world, or null.</summary>
        private static GameSession? OwnCharacter(long account)
            => SessionRegistry.InWorld().FirstOrDefault(s => s.AccountId == account);

        /// <summary>
        /// POST /api/mapa: the caller's map, his cell, and what stands on it.
        /// <code>
        ///   { mapa, celda,
        ///     npcs:   [ { id, npc, celda } ],
        ///     grupos: [ { id, celda, miembros: [ { monstruo, grado, nivel } ] } ] }
        /// </code>
        /// </summary>
        private static Respuesta Mapa(long administrador)
        {
            var own = OwnCharacter(administrador);
            if (own == null) return Mal(404, "personaje-desconectado");
            return Bien(Describe(own.State.MapId, own.State.CellId));
        }

        private static object Describe(long mapId, int cell)
        {
            var (npcs, groups) = MapAdmin.On(mapId);
            return new
            {
                mapa = mapId,
                celda = cell,
                npcs = npcs.Select(n => new { id = n.ContextualId, npc = n.NpcId, celda = n.Cell }),
                grupos = groups.Select(g => new
                {
                    id = g.MobId,
                    celda = g.Cell,
                    miembros = g.Members.Select(m => new { monstruo = m.MonsterId, grado = m.Grade, nivel = m.Level }),
                }),
            };
        }

        /// <summary>
        /// POST /api/invocar: an NPC or a group of monsters on the caller's cell.
        /// <code>
        ///   { tipo: "npc", npc: 774 }
        ///   { tipo: "monstruos", miembros: [ { monstruo: 31, grado: 5 }, ... ] }   up to eight
        /// </code>
        /// Answers the map as /api/mapa does, with what was put.
        /// </summary>
        private static Respuesta Invocar(string cuerpo, long administrador)
        {
            var own = OwnCharacter(administrador);
            if (own == null) return Mal(404, "personaje-desconectado");
            if (own.State.IsInFight) return Mal(409, "personaje-en-combate");
            long mapId = own.State.MapId;
            int cell = own.State.CellId;

            string tipo = Texto(cuerpo, "tipo");
            if (tipo == "npc")
            {
                int npcId = (int)Numero(cuerpo, "npc");
                var spawn = MapAdmin.SpawnNpc(mapId, npcId, cell, own.State.Orientation);
                if (spawn == null) return Mal(400, "npc-invalido");
                ActivityJournal.Current.Write("admin.npc.spawned", administrador, own.CharacterId,
                    new { map = mapId, cell, npc = npcId });
            }
            else if (tipo == "monstruos")
            {
                var members = Members(cuerpo);
                if (members.Count == 0 || members.Count > MapAdmin.MaxMembers) return Mal(400, "miembros-invalidos");
                var group = MapAdmin.SpawnMonsters(mapId, cell, members);
                if (group == null) return Mal(400, "monstruo-invalido");
                ActivityJournal.Current.Write("admin.monsters.spawned", administrador, own.CharacterId,
                    new { map = mapId, cell, group = group.MobId, members = members.Select(m => new { m.Monster, m.Grade }) });
            }
            else return Mal(400, "tipo-invalido");

            MapAdmin.RefreshAsync(mapId).GetAwaiter().GetResult();
            Console.WriteLine($"[Control] {own.State.CharacterName} invoca {tipo} en el mapa {mapId}, casilla {cell}.");
            return Bien(Describe(mapId, cell));
        }

        /// <summary>
        /// POST /api/quitar: takes an NPC or a group off the caller's map.
        /// <code>  { tipo: "npc" | "grupo", id }  </code>
        /// </summary>
        private static Respuesta Quitar(string cuerpo, long administrador)
        {
            var own = OwnCharacter(administrador);
            if (own == null) return Mal(404, "personaje-desconectado");
            long mapId = own.State.MapId;
            long id = Numero(cuerpo, "id");

            string tipo = Texto(cuerpo, "tipo");
            bool gone = tipo switch
            {
                "npc" => MapAdmin.RemoveNpc(mapId, id),
                "grupo" => MapAdmin.RemoveGroup(mapId, id),
                _ => false,
            };
            if (!gone) return Mal(404, tipo is "npc" or "grupo" ? "no-esta" : "tipo-invalido");

            ActivityJournal.Current.Write("admin.removed", administrador, own.CharacterId, new { map = mapId, tipo, id });
            MapAdmin.RefreshAsync(mapId).GetAwaiter().GetResult();
            Console.WriteLine($"[Control] {own.State.CharacterName} quita {tipo} {id} del mapa {mapId}.");
            return Bien(Describe(mapId, own.State.CellId));
        }

        /// <summary>The members of a group to put: a list of { monstruo, grado }, the grade from 1.</summary>
        private static List<(int Monster, int Grade)> Members(string cuerpo)
        {
            var members = new List<(int, int)>();
            try
            {
                using var doc = JsonDocument.Parse(cuerpo.Length == 0 ? "{}" : cuerpo);
                if (!doc.RootElement.TryGetProperty("miembros", out var list) || list.ValueKind != JsonValueKind.Array)
                    return members;
                foreach (var one in list.EnumerateArray())
                {
                    if (one.ValueKind != JsonValueKind.Object) continue;
                    int monster = one.TryGetProperty("monstruo", out var m) && m.TryGetInt32(out int mv) ? mv : 0;
                    int grade = one.TryGetProperty("grado", out var g) && g.TryGetInt32(out int gv) ? gv : 1;
                    if (monster > 0) members.Add((monster, Math.Clamp(grade, 1, 6)));
                }
            }
            catch (JsonException) { }
            return members;
        }

        /// <summary>
        /// POST /api/carcel: sends a connected character to jail for ten minutes, and the caller to
        /// the corridor beside his cell. <code>  { personaje }  </code>
        /// </summary>
        private static Respuesta Carcel(string cuerpo, long administrador)
        {
            var prisoner = SessionRegistry.FindByName(Texto(cuerpo, "personaje"));
            if (prisoner == null || !prisoner.IsInWorld) return Mal(404, "personaje-desconectado");
            var warden = OwnCharacter(administrador);

            var (refusal, sentence) = Jail.LockUpAsync(prisoner, warden).GetAwaiter().GetResult();
            if (refusal != Jail.Refusal.None) return Mal(Code(refusal), Reason(refusal));

            ActivityJournal.Current.Write("admin.jailed", administrador, warden?.CharacterId ?? 0,
                new { prisoner = sentence!.CharacterId, name = sentence.Name, until = sentence.UntilUtc });
            return Bien(new { bien = true, personaje = sentence.Name, hasta = sentence.UntilUtc });
        }

        /// <summary>
        /// POST /api/liberar: lets a prisoner out before his time, back where he was taken from --
        /// now if he is connected, at his next login if not. <code>  { personaje }  </code>
        /// </summary>
        private static Respuesta Liberar(string cuerpo, long administrador)
        {
            string name = Texto(cuerpo, "personaje");
            var prisoner = Jail.Prisoners.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (prisoner == null) return Mal(404, "no-esta-preso");

            var refusal = Jail.ReleaseAsync(prisoner.CharacterId, "liberado por un administrador").GetAwaiter().GetResult();
            if (refusal != Jail.Refusal.None) return Mal(Code(refusal), Reason(refusal));

            ActivityJournal.Current.Write("admin.released", administrador, 0,
                new { prisoner = prisoner.CharacterId, name = prisoner.Name });
            return Bien(new { bien = true, personaje = prisoner.Name });
        }

        /// <summary>
        /// POST /api/coordenadas: the map at these coordinates, the one ".teleport [x,y]" takes
        /// (<see cref="MapLookup.AtCoordinates"/>). <code>  { x, y } -> { mapa }  </code>
        /// </summary>
        private static Respuesta Coordenadas(string cuerpo)
        {
            var match = MapLookup.AtCoordinates((int)Numero(cuerpo, "x"), (int)Numero(cuerpo, "y"));
            return match == null ? Mal(404, "sin-mapa") : Bien(new { mapa = match.Map.MapId });
        }

        /// <summary>POST /api/visitar-carcel: the caller to the prison's corridor.</summary>
        private static Respuesta VisitarCarcel(long administrador)
        {
            var own = OwnCharacter(administrador);
            if (own == null) return Mal(404, "personaje-desconectado");
            var refusal = Jail.VisitAsync(own).GetAwaiter().GetResult();
            return refusal == Jail.Refusal.None ? Bien(new { bien = true, mapa = Jail.MapId }) : Mal(Code(refusal), Reason(refusal));
        }

        /// <summary>
        /// POST /api/ficha: a connected character's level, base characteristics and kamas, as the
        /// character tab shows them before anything is changed. <code>  { personaje } </code>, empty
        /// for the caller's own.
        /// </summary>
        private static Respuesta Ficha(string cuerpo, long administrador)
        {
            string name = Texto(cuerpo, "personaje");
            var who = name.Length > 0 ? SessionRegistry.FindByName(name) : OwnCharacter(administrador);
            if (who == null || !who.IsInWorld) return Mal(404, "personaje-desconectado");
            var s = who.State;
            return Bien(new
            {
                personaje = s.CharacterName ?? "",
                nivel = s.CharacterLevel,
                vitalidad = s.StatVitality,
                sabiduria = s.StatWisdom,
                fuerza = s.StatStrength,
                inteligencia = s.StatIntelligence,
                suerte = s.StatChance,
                agilidad = s.StatAgility,
                kamas = s.Kamas,
            });
        }

        /// <summary>
        /// POST /api/buscar-mapas: maps from part of an area's or a subarea's name, a coordinate or
        /// a map id (<see cref="MapSearch"/>). <code>  { texto } -> { mapas: [ { mapa, x, y, zona, subzona, exterior } ] }  </code>
        /// </summary>
        private static Respuesta BuscarMapas(string cuerpo)
            => Bien(new
            {
                mapas = MapSearch.Find(Texto(cuerpo, "texto")).Select(p => new
                {
                    mapa = p.MapId, x = p.X, y = p.Y, zona = p.Area, subzona = p.SubArea, exterior = p.Outdoor,
                }),
            });

        /// <summary>
        /// POST /api/niveles-monstruos: each monster's level at each of its grades, as the server
        /// fights it: <code>{ niveles: { "31": [16, 17, 18, 19, 20], ... } }</code>. The client's own
        /// catalogue gives 1 for every grade, which made the spawn tab show "1-1" everywhere.
        /// </summary>
        private static Respuesta NivelesMonstruos()
            => Bien(new
            {
                niveles = MobSpawnManager.AllMonsters
                    .Where(m => m.Grades.Count > 0)
                    .ToDictionary(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                  m => m.Grades.Select(g => g.Level).ToArray()),
            });

        /// <summary>POST /api/presos: who is in jail, with the seconds left and whether he is connected.</summary>
        private static Respuesta Presos(long administrador)
            => Bien(new
            {
                presos = Jail.Prisoners.Select(p => new
                {
                    nombre = p.Name,
                    quedan = (int)Math.Max(0, (p.UntilUtc - DateTime.UtcNow).TotalSeconds),
                    conectado = SessionRegistry.FindByCharacter(p.CharacterId)?.IsInWorld ?? false,
                }),
            });

        private static int Code(Jail.Refusal refusal) => refusal switch
        {
            Jail.Refusal.NotConnected or Jail.Refusal.NotIn => 404,
            Jail.Refusal.NoPrison => 500,
            Jail.Refusal.Self => 400,
            _ => 409,
        };

        private static string Reason(Jail.Refusal refusal) => refusal switch
        {
            Jail.Refusal.NotConnected => "personaje-desconectado",
            Jail.Refusal.InFight => "personaje-en-combate",
            Jail.Refusal.Busy => "personaje-ocupado",
            Jail.Refusal.AlreadyIn => "ya-esta-preso",
            Jail.Refusal.NotIn => "no-esta-preso",
            Jail.Refusal.Self => "a-uno-mismo",
            Jail.Refusal.NoPrison => "sin-carcel",
            _ => "rechazado",
        };
    }
}
