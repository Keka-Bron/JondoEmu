using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A player's card («Mostrar perfil», the client's PlayerInfoUi): kxh { 2 character } answered
    /// lad { 2 { 3 last updated, 4 the character } }.
    /// </summary>
    /// <remarks>
    /// MEASURED in «Busqueda grupo/busqueda de grupo-crear anuncio-postularse a otro…», four times;
    /// the character block's fields read off the client's PlayerInfoBase constructor, which takes
    /// it apart:
    /// <code>
    ///   1 ornament (the one of title, ornament and aura it carries: case 1, 2 or 4)
    ///   2 account   3 breed   4 character   6 online   7 level   8 the bare look
    ///   9 the card's background   10 gender, 0 male   11 name   14 the look with its outfit
    /// </code>
    /// The capture's own card: { 1 18, 2 60000001, 3 11, 4 300000000001, 6 1, 7 354, 8 look, 9 5,
    /// 11 "Hero-Example", 14 look }. The background (f9) is the one the player picks in the card's
    /// background window, kye { 2 background } (fhf::beyt copies dkv's only field there); with none
    /// picked it is left out and the client draws its default.
    /// </remarks>
    public static class PlayerCardHandler
    {
        /// <summary>kxh: the card of a character of this server, online or not.</summary>
        public static async Task ShowAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kxh = ConnectionProtocol.ReadPayload(payload, "kxh");
            if (kxh == null) return;
            long id = ProtoMessage.Parse(kxh).Fields
                .FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
            var character = id > 0 ? DatabaseManager.GetCharacterById(id) : null;
            if (character == null)
            {
                Console.WriteLine($"[Player card] No character {id}.");
                return;
            }

            // Asked with a request id -- the character sheet's «Mon profil» tab asks so, and waits
            // for an answer carrying that id: pushed, the card never opened -- it is answered so.
            long request = ConnectionProtocol.RequestId(payload);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, request > 0
                ? ConnectionProtocol.Answer("lad", Build(character), request)
                : ConnectionProtocol.Push("lad", Build(character)));
        }

        /// <summary>
        /// lag { 2 the tab, 3 the character }: a tab of the card, asked right after it and on every
        /// tab change. Answered kzl { 1 kyi { the tab's own } }: 3 kyr the general tab, 2 kyu the
        /// equipment, 1 kzi the ranking -- the client's GeneralViewData, EquipmentViewData,
        /// LadderViewData. Unanswered, the card stayed a background and a pedestal.
        /// </summary>
        /// <remarks>
        /// The general tab as the client reads it (PlayerInfoUi.GeneralViewData): f1 the alignment
        /// { 1 the figure under the side, 2 grade, 3 side }, f3 the guild { 1 id, 2 emblem, 3 name },
        /// f5 every job { job: level }, f7 the jobs picked to show and f10 the achievement points --
        /// each placed by sending marked values and reading where the window drew them. A capture
        /// of another server's card, opened on the same client one version later, carries the
        /// same blocks.
        /// </remarks>
        public static Task TabAsync(NetworkStream stream, byte[] payload)
        {
            var fields = ProtoMessage.Parse(ConnectionProtocol.ReadPayload(payload, "lag") ?? Array.Empty<byte>()).Fields;
            long tab = fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
            long id = fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 0)?.VarIntValue ?? 0;

            var content = tab switch
            {
                1 => Pb.New().Msg(2, Gear(id)),
                2 => Pb.New().Msg(1, Ranks(id)),
                _ => Pb.New().Msg(3, General(id)),
            };
            return Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("kzl", Pb.New().Msg(1, content).Build(), ConnectionProtocol.RequestId(payload)));
        }

        /// <summary>
        /// The equipment tab (kyu): f1 what he wears, { slot: llm { 1 gid, 2 uid, 3 effects } }, and
        /// f3 his characteristics, { id: lhi { 4 base, 3 bonus, 6 equipment } } -- the client's
        /// EquipmentViewData. f3 and f4 both have to be there: the client reads both without asking,
        /// and one left out was a NullReferenceException in EquipmentViewData's constructor and a tab
        /// that never opened. What he wears is the database's; his characteristics his session's,
        /// none for a character not in the world.
        /// </summary>
        internal static Pb Gear(long characterId)
        {
            var gear = Pb.New();
            var effects = ItemEffectMessages.FromCatalogue;
            foreach (var item in Equipment.WornItemsOf(characterId).Where(i => !InvisibleItems.Conceal(i.Template, i.Uid)))
            {
                var worn = Pb.New().Var(1, item.Template).Var(2, item.Uid);
                foreach (var effect in item.Effects.Select(effects.Effect).OfType<Jondo.Unity.Protocol.Game.ObjectEffect>())
                    worn.Msg(3, ItemEffectMessages.Write(effect));
                gear.Msg(1, Pb.New().Var(1, item.Position).Msg(2, worn));
            }

            var stats = Pb.New();
            var session = SessionRegistry.FindByCharacter(characterId);
            var sheet = session == null ? Array.Empty<Jondo.Unity.Protocol.Game.CharacterCharacteristic>()
                : CharacteristicsMessages.Live.Snapshot(session).Stats.Characteristics;
            foreach (var characteristic in sheet)
            {
                var line = characteristic.CharacteristicCase switch
                {
                    Jondo.Unity.Protocol.Game.CharacterCharacteristicCaseDetailed d => Pb.New()
                        .VarIfNotZero(3, d.Value.Additional).VarIfNotZero(4, d.Value.Base)
                        .VarIfNotZero(6, d.Value.Equipment + d.Value.Temporary),
                    Jondo.Unity.Protocol.Game.CharacterCharacteristicCaseUsable u => Pb.New()
                        .VarIfNotZero(4, u.Value.Base).VarIfNotZero(6, u.Value.Equipment + u.Value.Temporary),
                    Jondo.Unity.Protocol.Game.CharacterCharacteristicCaseValue v => Pb.New().VarIfNotZero(4, v.Value.Total),
                    _ => null,
                };
                if (line != null) stats.Msg(1, Pb.New().Var(1, characteristic.CharacteristicId).Msg(2, line));
            }
            gear.Msg(3, stats).EmptyMsg(4);
            return gear;
        }

        /// <summary>
        /// The ranking tab (kzi): his place in the ladder window's experience and achievement tabs.
        /// Only f2 is read -- LadderViewData's constructor walks f2 alone (GameAssembly 0x2736480),
        /// and lines put in f1 never showed. Each line is a kzg: 2 experience { 1 value, 2 rank },
        /// 4 achievements { 1 points, 2 rank }, 1 the Koliseo, 3 a dream.
        /// </summary>
        internal static Pb Ranks(long characterId)
        {
            var ranks = Pb.New();
            var experience = Ladders.Experience(0).FirstOrDefault(l => l.Who.Id == characterId);
            if (experience != null)
                ranks.Msg(2, Pb.New().Msg(2, Pb.New().Var(1, experience.Experience).Var(2, experience.Rank)));
            var achievements = Ladders.Achievement(0).FirstOrDefault(l => l.Who.Id == characterId);
            if (achievements != null)
                ranks.Msg(2, Pb.New().Msg(4, Pb.New().Var(1, achievements.Points).Var(2, achievements.Rank)));
            return ranks;
        }

        /// <summary>The general tab (kyr) of a character, online or not.</summary>
        internal static Pb General(long characterId)
        {
            var general = Pb.New();
            if (characterId <= 0) return general;

            var alignment = Alignments.Of(characterId);
            // f1 is the figure under the side's name (a marked 111 showed there): his alignment
            // level, the quests' -- his session's, none for a character not in the world.
            int level = 0;
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session != null && alignment.Sworn)
                using (SessionContext.Push(session)) level = Alignments.Level();
            general.Msg(1, Pb.New().VarIfNotZero(1, level).VarIfNotZero(2, alignment.HonourGrade).Var(3, alignment.Side));

            // Every job and its level, at 1 where he has none -- f5, as the other server's card lists
            // them all. A job picked (f7) missing from it threw KeyNotFoundException in
            // PlayerCardGeneral.RefreshJobs and left the general tab empty; put in f2, the jobs
            // were not that dictionary.
            var experience = DatabaseManager.LoadJobExperience(characterId);
            var jobs = ArtisanHandler.Jobs().Where(j => j != WorkshopHandler.BaseJob).Union(experience.Keys).OrderBy(j => j).ToList();
            foreach (int job in jobs)
                general.Msg(5, Pb.New().Var(1, job).Var(2, experience.TryGetValue(job, out long xp) ? JobExperience.LevelOf(xp) : 1));
            // The jobs he picked to show (kxs), packed.
            var shown = Wardrobe.CardJobsOf(characterId).Where(jobs.Contains).ToList();
            if (shown.Count > 0) general.Packed(7, shown.Select(j => (long)j));

            var guild = GuildStore.GuildOf(characterId);
            if (guild != null)
                general.Msg(3, Pb.New().Var(1, guild.Id).Msg(2, GuildProtocol.Emblem(guild)).Str(3, guild.Name));

            // His alliance (kyo): f1 its name, f2 its tag, f3 its emblem.
            var alliance = AllianceStore.AllianceOf(characterId);
            if (alliance != null)
                general.Msg(9, Pb.New().Str(1, alliance.Name).Str(2, alliance.Tag).Msg(3, AllianceProtocol.Emblem(alliance)));

            // A house of his account (kyp): f1 where it stands, as the house lists say it.
            long account = DatabaseManager.GetCharacterById(characterId)?.AccountId ?? 0;
            var house = account == 0 ? null
                : HouseStore.OwnedBy(account).Select(HouseHandler.OwnedOf).FirstOrDefault(h => h != null);
            if (house != null) general.Msg(8, Pb.New().Msg(1, HouseProtocol.Where(house)));

            // The trophy's figure is f10's: a marked value sent in f4 and in f10 showed f10's.
            general.VarIfNotZero(10, Achievements.PointsOf(characterId));
            return general;
        }

        /// <summary>
        /// kxs { 1 the jobs, packed }: the jobs his card shows, as the window's «Métiers à afficher
        /// (5 max)» leaves them after each tick -- the whole list every time. Nothing is answered: the
        /// window keeps what it sent, and the next card reads it back (kyr f7).
        /// </summary>
        public static Task SetJobsAsync(byte[] payload)
        {
            byte[]? kxs = ConnectionProtocol.ReadPayload(payload, "kxs");
            if (kxs == null) return Task.CompletedTask;
            var jobs = new List<int>();
            foreach (var field in ProtoMessage.Parse(kxs).Fields.Where(f => f.FieldNumber == 1))
            {
                if (field.WireType == 0) jobs.Add((int)field.VarIntValue);
                else if (field.WireType == 2) jobs.AddRange(ReadPacked(field.BytesValue));
            }
            var state = SessionContext.State;
            Wardrobe.SaveCardJobs(state.CharacterId, jobs);
            Console.WriteLine($"[Player card] {state.CharacterName} shows the jobs {string.Join(", ", jobs)}.");
            return Task.CompletedTask;
        }

        private static IEnumerable<int> ReadPacked(byte[] bytes)
        {
            int i = 0;
            while (i < bytes.Length)
            {
                long value = 0;
                int shift = 0;
                while (i < bytes.Length)
                {
                    byte b = bytes[i++];
                    value |= (long)(b & 0x7F) << shift;
                    if (b < 0x80) break;
                    shift += 7;
                }
                yield return (int)value;
            }
        }

        /// <summary>The highest background number taken; the client's window offers a handful.</summary>
        public const int MaxBackground = 999;

        /// <summary>
        /// kye { 2 background }: the player's own card gets that background, and is told so with
        /// kxr { 2 { 1 background } } -- the client's fhk.bfaf reads that branch into its
        /// background update (cgf), which the background window and the card draw. Unanswered, the
        /// pick was saved and the card kept its default background.
        /// </summary>
        public static async Task SetBackgroundAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kye = ConnectionProtocol.ReadPayload(payload, "kye");
            if (kye == null) return;
            long background = ProtoMessage.Parse(kye).Fields
                .FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
            if (background < 0 || background > MaxBackground) return;

            var state = SessionContext.State;
            Wardrobe.SaveCardBackground(state.CharacterId, (int)background);
            Console.WriteLine($"[Player card] {state.CharacterName} takes background {background}.");
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("kxr", Pb.New().Msg(2, Pb.New().VarIfNotZero(1, background)).Build()));
        }

        /// <summary>The card's tabs (the client's kxe): 0 General, 1 Equipment, 2 Ranking.</summary>
        private static readonly long[] Tabs = { 0, 1, 2 };

        /// <summary>lad { 2 { 1 the tabs, 3 when, 4 the character } }.</summary>
        internal static byte[] Build(DbCharacter character)
        {
            var (_, ornament) = Wardrobe.Of(character.Id);
            var card = Pb.New()
                .VarIfNotZero(1, ornament)
                .Var(2, character.AccountId)
                .Var(3, character.Breed)
                .Var(4, character.Id)
                .VarIfNotZero(6, SessionRegistry.FindByCharacter(character.Id) != null ? 1 : 0)
                .Var(7, character.Level)
                .Bytes(8, CharacterLookBuilder.BareLook(character))
                .VarIfNotZero(9, Wardrobe.CardBackgroundOf(character.Id))
                .VarIfNotZero(10, character.Sex)
                .Str(11, character.Name)
                .Bytes(14, CharacterLookBuilder.BuildLook(character, paraLaVentana: true));
            // f1: the card's tabs to draw -- General, Equipment, Ranking --, packed. Without them the
            // window opened on nothing at all: the background and its pedestal, and no tab.
            return Pb.New().Msg(2, Pb.New().Packed(1, Tabs).Str(3, CharacterPresets.Now()).Msg(4, card)).Build();
        }
    }
}
