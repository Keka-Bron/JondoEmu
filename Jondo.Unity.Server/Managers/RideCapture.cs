using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Catching wild rides with a net: "hay que equipar una de las redes de captura que pueden
    /// fabricar los ganaderos. A continuación, hay que usar el hechizo de domesticación que la red
    /// añade a la barra de hechizos, apuntar al vueloceronte o vuelocerontes que se quieren capturar
    /// y ganar el combate" (the client's guide, texts 1183244-1183246).
    /// </summary>
    /// <remarks>
    /// THE NET goes in the combat consumables' slot (INVENTORY_POSITION_CONSUMABLE, 31) and gives its
    /// spell for the fight (its effect 722, CharacterLearnSpellTemporary): the universal one
    /// "Domesticación de Montura", the reinforced ones "Domesticación de Dragopavos"... in a circle,
    /// the multipliers twice each. Which net gives which spell, what each takes and how far, is the
    /// client's data (tools/extract_breeding.py, captureNets).
    ///
    /// THE SPELL "Consume una red de captura para atrapar a la montura objetivo al final del
    /// combate. Utilizable una única vez por combate." It spends the net as it is cast, and every
    /// wild ride it reaches not yet caught is the caster's: "Cuando un ganadero marca una mulagua en
    /// combate, sus aliados ya no pueden capturarla." Its own effects mark them on the board.
    ///
    /// THE END: the fight won, each catch comes out of it as a ride of its colour, born at level 1
    /// (the wild "Dragopavo almendrado salvaje" is the "Dragopavo almendrado"), and the breeder
    /// gains 30 experience a ride (a guide: "30 XP per mount captured").
    /// </remarks>
    public static class RideCapture
    {
        /// <summary>The combat consumables' slot (Core.Enums.CharacterInventoryPositionEnum).</summary>
        public const int ConsumableSlot = 31;

        /// <summary>The breeder's experience for each ride caught (the guides).</summary>
        public const int ExperiencePerRide = 30;

        /// <summary>A fight's catches: who caught what, and who has used his net.</summary>
        private sealed class Catches
        {
            public readonly ConcurrentDictionary<long, long> CaughtBy = new();     // fighter → caster
            public readonly ConcurrentDictionary<long, int> Monster = new();       // fighter → template
            public readonly ConcurrentDictionary<long, int> Times = new();         // fighter → how many rides
            public readonly ConcurrentDictionary<long, byte> Used = new();         // casters
        }

        private static readonly ConcurrentDictionary<long, Catches> _fights = new();

        /// <summary>The net equipped in the consumables' slot of the character being served, and its data.</summary>
        public static (Equipment.Item Item, Breeding.CaptureNet Net)? EquippedNet()
        {
            foreach (var item in Equipment.All)
            {
                if (item.Position != ConsumableSlot) continue;
                if (Breeding.NetByItem(item.Template) is { } net) return (item, net);
            }
            return null;
        }

        /// <summary>The domestication spell the character's equipped net gives for the fight, if any.</summary>
        public static int SpellOfEquippedNet() => EquippedNet()?.Net.Spell ?? 0;

        public static bool IsDomestication(int spell) => Breeding.NetBySpell(spell) != null;

        /// <summary>Whether the caster may still cast a domestication spell in this fight: once a fight.</summary>
        public static bool MayCast(FightInstance fight, Fighter caster, int spell)
            => !IsDomestication(spell) || !(_fights.TryGetValue(fight.FightId, out var c) && c.Used.ContainsKey(caster.Id));

        /// <summary>The wild rides a cast takes: those of its net's list, alive, not caught yet, in its reach.</summary>
        public static List<Fighter> Reached(FightInstance fight, Breeding.CaptureNet net, int cell)
        {
            _fights.TryGetValue(fight.FightId, out var catches);
            return fight.Rojo.Concat(fight.Azul)
                .Where(f => f.IsAlive && f.IsMonster && !f.EsInvocado && net.Wild.Contains(f.MonsterId)
                         && Breeding.RideOfWild(f.MonsterId) != null
                         && (catches == null || !catches.CaughtBy.ContainsKey(f.Id))
                         && Jondo.Unity.World.Maps.MapGeometry.Distance(f.CellId, cell) <= net.Radius)
                .ToList();
        }

        /// <summary>
        /// A domestication spell cast: the caster's net spent, and the wild rides it reached his.
        /// What it caught.
        /// </summary>
        public static async Task<List<Fighter>> CastAsync(NetworkStream stream, FightInstance fight, Fighter caster, int spell, int cell)
        {
            var net = Breeding.NetBySpell(spell);
            if (net == null || caster.IsMonster) return new List<Fighter>();
            var catches = _fights.GetOrAdd(fight.FightId, _ => new Catches());
            if (!catches.Used.TryAdd(caster.Id, 0)) return new List<Fighter>();

            if (EquippedNet() is { } equipped && equipped.Net.Spell == spell) await SpendAsync(stream, equipped.Item);

            var reached = Reached(fight, net, cell);
            foreach (var wild in reached)
            {
                catches.CaughtBy[wild.Id] = caster.Id;
                catches.Monster[wild.Id] = wild.MonsterId;
                catches.Times[wild.Id] = net.Multiplier;
            }
            Program.LogDebug($"[Breeding] {caster.Id} casts {spell} at {cell}: {reached.Count} wild ride(s) marked.");
            return reached;
        }

        /// <summary>The rides a fight's catches give one caster: by RidesData id, how many.</summary>
        public static Dictionary<int, int> RidesOf(long fightId, long caster)
        {
            var rides = new Dictionary<int, int>();
            if (!_fights.TryGetValue(fightId, out var catches)) return rides;
            foreach (var (fighter, by) in catches.CaughtBy)
            {
                if (by != caster || !catches.Monster.TryGetValue(fighter, out int monster)) continue;
                if (Breeding.RideOfWild(monster) is not { } ride) continue;
                rides[ride.Id] = (rides.TryGetValue(ride.Id, out int n) ? n : 0) + (catches.Times.TryGetValue(fighter, out int t) ? t : 1);
            }
            return rides;
        }

        /// <summary>The fight over, for the character being served: won, his catches become rides in his bag.</summary>
        public static async Task FightEndedAsync(GameSession session, FightInstance fight, bool won)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            long characterId = session.CharacterId;
            var rides = won ? RidesOf(fight.FightId, characterId) : new Dictionary<int, int>();
            int caught = 0;
            foreach (var (rideId, count) in rides)
            {
                var ride = Breeding.RideOf(rideId);
                if (ride == null || ride.Item <= 0) continue;
                for (int i = 0; i < count; i++)
                {
                    if (!await Equipment.GiveAsync(stream, ride.Item, 1)) continue;
                    caught++;
                    await Achievements.CountAsync(session, "HD", ride.Item, defer: true);   // «Cazamonturas», back on the map
                }
            }
            if (caught > 0)
            {
                await Handlers.WorkshopHandler.GiveJobExperienceAsync(session, Breeding.BreederJob, (long)ExperiencePerRide * caught);
                Console.WriteLine($"[Breeding] {characterId} catches {caught} ride(s).");
            }
            Forget(fight.FightId, characterId);
        }

        /// <summary>The fight's catches of one caster forgotten; the fight's once nobody's are left.</summary>
        private static void Forget(long fightId, long characterId)
        {
            if (!_fights.TryGetValue(fightId, out var catches)) return;
            foreach (var (fighter, by) in catches.CaughtBy.ToArray())
                if (by == characterId) catches.CaughtBy.TryRemove(fighter, out _);
            catches.Used.TryRemove(characterId, out _);
            if (catches.CaughtBy.IsEmpty && catches.Used.IsEmpty) _fights.TryRemove(fightId, out _);
        }

        /// <summary>A net spent: one unit of the slot's stack.</summary>
        private static async Task SpendAsync(NetworkStream stream, Equipment.Item net)
        {
            long character = SessionContext.State.CharacterId;
            bool whole = net.Quantity <= 1;
            if (!DatabaseManager.DestroyCharacterItem(character, net.Uid, 1)) return;
            Equipment.Remove(net.Uid, 1);
            if (stream == null) return;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, whole
                ? ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Ium, ConnectionProtocol.BuildItemGone(net.Uid))
                : ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Ivj, ConnectionProtocol.BuildItemQuantity(net.Uid, net.Quantity)));
            await Handlers.ItemUseHandler.WeighAsync(stream);
        }
    }
}
