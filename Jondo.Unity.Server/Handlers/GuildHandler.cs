using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// El gremio: crearlo, mostrarlo y abandonarlo. La base sobre la que irán la tienda, el cofre
    /// y las raids.
    ///
    /// Medido en las 12 capturas de Gremio/. Al crear (jjg) el servidor real contesta con el
    /// gremio al que ahora perteneces (jgw), sus rangos (jco), su cabecera (jhh) y tu ficha de
    /// miembro (jgu); al abrir la ventana repite jco, jgu y jhh; al salir (jho) confirma con khj.
    /// Lo que aún no se toca -candidaturas, permisos, contribuciones- se deja dicho.
    /// </summary>
    public static class GuildHandler
    {
        /// <summary>La gremialogema, el objeto que se gasta al fundar. «Gremialogema» en el catálogo del cliente.</summary>
        public const int GuildalogemTemplate = 1575;

        /// <summary>
        /// El Templo de los Gremios y su altar, donde empieza la fundación.
        /// </summary>
        /// <remarks>
        /// Medido en la captura de fundar «Jondo»: el jugador pulsa el elemento 480310 del mapa
        /// 106169344 -«The Guild Temple», al norte del pueblo de Amakna, casilla 326 en los datos
        /// del mapa-, el servidor contesta iwn con la habilidad 184 y un jjc vacío, y el cliente
        /// abre su editor de nombre y emblema. Sin ese jjc el editor no se abre nunca, que es lo
        /// que llevó a escribir el comando.
        /// </remarks>
        public const long FoundingMap = 106169344;
        public const int FoundingAltar = 480310;
        public const int FoundingSkill = 184;
        public const int FoundingType = -1;

        // El emblema con el que funda el COMANDO, que no tiene editor: el de la captura de «Jondo».
        // Por el altar el emblema lo elige el jugador y esto no se usa.
        private const int DefaultEmblemSymbol = 165;
        private const int DefaultEmblemSymbolColor = 8;
        private const int DefaultEmblemBackground = 16744448;
        private const int DefaultEmblemSymbolRgb = 9476018;

        private static async Task WriteAsync(NetworkStream stream, byte[] frame)
            => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>
        /// El altar del templo: se ha pulsado, y se le abre al jugador el editor de fundación.
        /// </summary>
        /// <remarks>
        /// Se abre aunque no lleve gremialogema ni pueda fundar: la captura no enseña qué hace el
        /// servidor real en ese caso, y lo que sí enseña es que la gremialogema se gasta en el jjg,
        /// no aquí. Quien no la tenga se enterará al firmar, que es donde se comprueba.
        /// </remarks>
        public static async Task OpenFoundingAsync(NetworkStream stream, int elementId, int skillId)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jjc, System.Array.Empty<byte>()));
            Console.WriteLine($"[Gremio] {SessionContext.State.CharacterName} abre el editor de fundación.");
        }

        /// <summary>
        /// Crear un gremio (jjg): f1 el emblema {símbolo, color símbolo, fondo, color fondo}, f2
        /// el nombre, tal como los deja el editor. Pasa por <see cref="FoundAsync"/>.
        /// </summary>
        public static async Task CreateAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjg = ConnectionProtocol.ReadPayload(frame, Op.Jjg);
            if (jjg == null) return;

            string name = "";
            int symbol = 0, symbolColor = 0, background = 0, symbolRgb = 0;
            foreach (var field in ProtoMessage.Parse(jjg).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 2)
                {
                    name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                }
                else if (field.FieldNumber == 1 && field.WireType == 2)
                {
                    foreach (var e in ProtoMessage.Parse(field.BytesValue).Fields)
                    {
                        if (e.WireType != 0) continue;
                        if (e.FieldNumber == 1) symbol = (int)e.VarIntValue;
                        else if (e.FieldNumber == 2) symbolColor = (int)e.VarIntValue;
                        else if (e.FieldNumber == 3) background = (int)e.VarIntValue;
                        else if (e.FieldNumber == 5) symbolRgb = (int)e.VarIntValue;
                    }
                }
            }

            string fallo = await FoundAsync(stream, SessionContext.State.CharacterId, name,
                                            symbol, symbolColor, background, symbolRgb);
            if (fallo != null)
            {
                // No hay trama medida con la que decirle al editor que no: la captura sólo tiene
                // el caso bueno. It is told as an information line, which only they see.
                Console.WriteLine($"[Gremio] Fundación de «{name}» rechazada: {fallo}.");
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice(CommandTexts.Get(fallo, name))));
            }
        }

        /// <summary>
        /// Funda por el comando, sin editor: el mismo camino con el emblema de la captura.
        /// Devuelve la clave del mensaje de error, o null si se ha creado.
        /// </summary>
        public static Task<string> CreateFromCommandAsync(NetworkStream stream, long founderCharacterId, string name)
            => FoundAsync(stream, founderCharacterId, name, DefaultEmblemSymbol, DefaultEmblemSymbolColor,
                          DefaultEmblemBackground, DefaultEmblemSymbolRgb);

        /// <summary>
        /// La fundación, una sola para el editor y para el comando: se comprueba todo, se gasta
        /// la gremialogema, se guarda el gremio y se le manda al fundador lo suyo.
        /// </summary>
        /// <remarks>
        /// Lo que sale después del jjg, en el orden de la captura: ium (la gremialogema que se
        /// va), jjs, jhq, jco, jgw, khi, jgu, jhh y un jsn que redibuja al fundador ya con el
        /// nombre del gremio debajo del suyo. El ium lo manda Equipment.TakeAsync, que es lo que
        /// quita el objeto; el iun de los pods que va detrás en la captura no se manda, que la
        /// gremialogema no pesa nada aquí; y el khi, con su 97 sin significado, tampoco.
        ///
        /// La gremialogema se gasta la ÚLTIMA, cuando todo lo demás ha pasado: una fundación que
        /// falle por el nombre no puede dejar al jugador sin la piedra.
        /// </remarks>
        private static async Task<string> FoundAsync(NetworkStream stream, long founderCharacterId, string name,
                                                     int symbol, int symbolColor, int background, int symbolRgb)
        {
            if (founderCharacterId == 0) return "guild.create.nocharacter";
            if (GuildStore.GuildOf(founderCharacterId) != null) return "guild.create.hasguild";

            name = (name ?? "").Trim();
            if (!IsValidGuildName(name)) return "guild.create.invalidname";
            if (GuildStore.ByName(name) != null) return "guild.create.exists";
            if (Equipment.HowMany(GuildalogemTemplate) < 1) return "guild.create.nogem";
            if (!await Equipment.TakeAsync(stream, GuildalogemTemplate, 1)) return "guild.create.nogem";

            var guild = GuildStore.Create(founderCharacterId, name, symbol, symbolColor, background, symbolRgb);

            // jjs, jhq, jco, jgw, khi, jgu, jhh: el orden de la captura.
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jjs, System.Array.Empty<byte>()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhq, System.Array.Empty<byte>()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildDefaultRanks()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jgw,
                GuildProtocol.BuildGuildJoined(guild, GuildStore.RankOf(founderCharacterId))));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Khi, GuildProtocol.BuildGoneNotice()));
            var members = GuildStore.Members(guild.Id);
            foreach (var frame in MemberFrames(members)) await WriteAsync(stream, frame);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh, GuildProtocol.BuildGuildInfo(guild, members.Count)));

            var character = DatabaseManager.GetCharacterById(founderCharacterId);
            if (character != null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    character, SessionContext.State.CellId, SessionContext.State.Orientation,
                    SessionContext.Current.AccountId)));
            }

            Console.WriteLine($"[Gremio] {SessionContext.State.CharacterName} funda «{name}» " +
                              "y gasta su gremialogema.");
            return null;
        }

        /// <summary>
        /// Lo que se admite como nombre de gremio.
        /// </summary>
        /// <remarks>
        /// NO está medido: la captura sólo funda «Jondo». Por el altar el nombre lo filtra el
        /// editor del propio cliente antes de mandarlo; esto es lo que se le pide a un nombre
        /// escrito a mano por el comando, y es la regla que trajo la PR #43.
        /// </remarks>
        internal static bool IsValidGuildName(string name)
        {
            if (name.Length < 3 || name.Length > 30) return false;
            foreach (char character in name)
            {
                if (!char.IsLetterOrDigit(character) && character != ' ' &&
                    character != '-' && character != '\'')
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Le manda a quien acaba de entrar en un gremio todo lo suyo: rangos, pertenencia, la
        /// lista de miembros y la cabecera.
        /// </summary>
        /// <remarks>
        /// Only on joining: the jgw is "you have just joined", with its chat line and its popup.
        /// It went out too on opening the window and on every tab of it (jml, jii), and that was
        /// the "acabas de unirte al gremio" at every click. The ranks go first, as in the capture
        /// ("jco jgw"): the client's jgw handler looks the rank up among them and does not ask
        /// whether it is there.
        /// </remarks>
        public static async Task SendGuildToOwnerAsync(NetworkStream stream, GuildStore.Guild guild, long characterId)
        {
            int rank = GuildStore.RankOf(characterId);
            var members = GuildStore.Members(guild.Id);

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildDefaultRanks()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jgw,
                GuildProtocol.BuildGuildJoined(guild, rank)));
            foreach (var frame in MemberFrames(members))
                await WriteAsync(stream, frame);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, members.Count)));
        }

        /// <summary>Una trama jgu por miembro, con el nombre, el nivel y la cuenta de cada uno.</summary>
        public static List<byte[]> MemberFrames(List<GuildStore.Member> members)
        {
            var fuera = new List<byte[]>();
            foreach (var member in members)
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character == null) continue;
                fuera.Add(ConnectionProtocol.Push(Op.Jgu,
                    GuildProtocol.BuildMember(member, character.Name, character.Level, character.AccountId,
                                              character.Breed, Achievements.PointsOf(member.CharacterId),
                                              GuildStore.ContributedBy(member.CharacterId),
                                              SessionRegistry.FindByCharacter(member.CharacterId) != null)));
            }
            return fuera;
        }

        /// <summary>
        /// The guild window opening (jlk): the chest's tabs (ivl) and the window's header (jhh).
        /// </summary>
        /// <remarks>
        /// The window opens with a burst -- jlk, jiy{4}, jii{1}, jfp, jiy, then jiy{1}, jml{1},
        /// jlx{8} -- and the real server answers ivl, jci, jff, jhh, jla, jgu, jmf, in all six
        /// captures of it. In the invitation's one the client waits for the first half's answers
        /// before sending the second, and the header comes with the first: the jlk's, since the
        /// jii is never answered anywhere (26 of 28).
        /// </remarks>
        public static async Task OpenWindowAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivl,
                StorageProtocol.BuildGuildChestTabs(GuildChests.TabsOfGuild(guild.Id))));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, GuildStore.Members(guild.Id).Count)));
        }

        /// <summary>
        /// The members (jml {f1: true}): one jgu per member. The jml of the perks tab and of
        /// closing the window come empty, and the capture answers them nothing.
        /// </summary>
        public static async Task MembersAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jml = ConnectionProtocol.ReadPayload(frame, Op.Jml);
            if (jml == null || FieldValue(jml, 1) == 0) return;

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            foreach (var member in MemberFrames(GuildStore.Members(guild.Id))) await WriteAsync(stream, member);
        }

        /// <summary>
        /// A tab whose contents this server does not keep -- the perks, the raids, the paged
        /// list, the collectors' -- answered as the captures answer it for a guild that has none:
        /// its empty message, on root 3 with the request's id.
        /// </summary>
        public static async Task EmptyTabAsync(NetworkStream stream, byte[] frame, string answer, int field)
            => await WriteAsync(stream, ConnectionProtocol.Answer(answer, GuildProtocol.BuildEmptyTab(field),
                                                                  ConnectionProtocol.RequestId(frame)));

        /// <summary>When the week starts again (jew → jez), on root 3 with the request's id.</summary>
        public static async Task WeeklyResetAsync(NetworkStream stream, byte[] frame)
            => await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jez, GuildProtocol.BuildWeeklyReset(DateTime.UtcNow),
                                                                  ConnectionProtocol.RequestId(frame)));

        /// <summary>El f2 del jiy que pide la ficha del anuario.</summary>
        public const int ProfileTab = 4;

        /// <summary>
        /// Una pestaña de la ventana (jiy). Con f2 = 4 se contesta la ficha del anuario (jci),
        /// que es el único par de la apertura medido suelto -dos veces en la captura de fundar
        /// «Jondo»-; sin f2, las contribuciones que quedan (jla), que es lo que ocupa su sitio en
        /// la ráfaga de apertura: cinco peticiones, cinco respuestas, y ésa es la que queda.
        /// </summary>
        public static async Task TabAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jiy = ConnectionProtocol.ReadPayload(frame, Op.Jiy);
            if (jiy == null) return;

            long tab = 0;
            foreach (var field in ProtoMessage.Parse(jiy).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) tab = field.VarIntValue;
            }

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            if (tab == ProfileTab)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jci, ProfileOf(guild)));
            }
            else if (tab == 0)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jla,
                    GuildProtocol.BuildContributionsLeft(GuildStore.ContributionsLeft(who))));
            }
        }

        /// <summary>La ficha del anuario de un gremio, con el nombre de su jefe puesto.</summary>
        private static byte[] ProfileOf(GuildStore.Guild guild)
        {
            var leader = GuildStore.LeaderOf(guild.Id);
            string leaderName = leader == null ? "" : DatabaseManager.GetCharacterById(leader.CharacterId)?.Name ?? "";
            return GuildProtocol.BuildProfile(guild, GuildStore.ProfileOf(guild.Id), leaderName);
        }

        /// <summary>El jfp de la apertura: se contesta con el jff de un gremio nuevo.</summary>
        /// <remarks>
        /// As an answer, root 3 with the request's id, as in all eight captured: it went out as a
        /// push, and the window waits for its answer before asking for the members.
        /// </remarks>
        public static async Task BenefitsAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            if (who == 0 || GuildStore.GuildOf(who) == null) return;
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jff, GuildProtocol.BuildNoBenefits(),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        // ─── Los rangos ─────────────────────────────────────────────────────────

        /// <summary>Abrir la gestión de rangos (jcs): el jco con los del gremio.</summary>
        public static async Task RanksAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await SendRanksAsync(stream, guild);
        }

        private static async Task SendRanksAsync(NetworkStream stream, GuildStore.Guild guild)
            => await WriteAsync(stream, ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildRanks(GuildStore.Ranks(guild.Id))));

        /// <summary>Sólo el jefe toca los rangos. Devuelve su gremio, o null si no toca.</summary>
        private static GuildStore.Guild GuildIfLeader(long who)
        {
            if (who == 0 || GuildStore.RankOf(who) != GuildStore.RankLeader) return null;
            return GuildStore.GuildOf(who);
        }

        /// <summary>
        /// Editar un rango (jct): el rango entero como lo deja el editor, y el jco de vuelta.
        /// </summary>
        /// <remarks>
        /// Medido dos veces en «muchas acciones»: renombrar el rango 1 a «Tesorero» con su f4
        /// vacío -y el servidor le deja el icono 116 que tenía- y renombrar el 2 a «Test rango».
        /// </remarks>
        public static async Task EditRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jct = ConnectionProtocol.ReadPayload(frame, Op.Jct);
            if (jct == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            var inner = ProtoMessage.Parse(jct).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            if (inner == null) return;

            string name = null;
            byte[] rights = null;
            bool? flag = null;
            int icon = 0, order = -1, id = 0;
            foreach (var field in ProtoMessage.Parse(inner.BytesValue).Fields)
            {
                switch (field.FieldNumber)
                {
                    case 2 when field.WireType == 2: name = System.Text.Encoding.UTF8.GetString(field.BytesValue); break;
                    case 3 when field.WireType == 2:
                        flag = false;
                        rights = System.Array.Empty<byte>();
                        foreach (var right in ProtoMessage.Parse(field.BytesValue).Fields)
                        {
                            if (right.FieldNumber == 1 && right.WireType == 0) flag = right.VarIntValue != 0;
                            else if (right.FieldNumber == 3 && right.WireType == 2) rights = right.BytesValue;
                        }
                        break;
                    case 4 when field.WireType == 2:
                        foreach (var look in ProtoMessage.Parse(field.BytesValue).Fields)
                        {
                            if (look.FieldNumber == 2 && look.WireType == 0) icon = (int)look.VarIntValue;
                            else if (look.FieldNumber == 3 && look.WireType == 0) order = (int)look.VarIntValue;
                        }
                        break;
                    case 5 when field.WireType == 0: id = (int)field.VarIntValue; break;
                }
            }

            var rank = GuildStore.Ranks(guild.Id).Find(r => r.Id == id);
            if (rank == null) return;

            if (name != null) rank.Name = name;
            if (rights != null) rank.Rights = rights;
            if (flag.HasValue) rank.Flag = flag.Value;
            if (icon != 0) rank.Icon = icon;
            if (order >= 0) rank.Order = order;
            GuildStore.SaveRank(rank);

            await SendRanksAsync(stream, guild);
            Console.WriteLine($"[Gremio] Rango {id} de «{guild.Name}» editado: «{rank.Name}».");
        }

        /// <summary>Los permisos de un rango (jck): f1 la lista tal cual, f2 el rango. La marca se queda.</summary>
        public static async Task SetRightsAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jck = ConnectionProtocol.ReadPayload(frame, Op.Jck);
            if (jck == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            byte[] rights = System.Array.Empty<byte>();
            int id = 0;
            foreach (var field in ProtoMessage.Parse(jck).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) rights = field.BytesValue;
                else if (field.FieldNumber == 2 && field.WireType == 0) id = (int)field.VarIntValue;
            }

            var rank = GuildStore.Ranks(guild.Id).Find(r => r.Id == id);
            if (rank == null) return;
            rank.Rights = rights;
            GuildStore.SaveRank(rank);

            await SendRanksAsync(stream, guild);
        }

        /// <summary>Crear un rango (jcv): f1 el orden, f4 el nombre, f5 el icono.</summary>
        public static async Task CreateRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jcv = ConnectionProtocol.ReadPayload(frame, Op.Jcv);
            if (jcv == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            int order = 0, icon = 0;
            string name = "";
            foreach (var field in ProtoMessage.Parse(jcv).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) order = (int)field.VarIntValue;
                else if (field.FieldNumber == 4 && field.WireType == 2) name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 5 && field.WireType == 0) icon = (int)field.VarIntValue;
            }

            var created = GuildStore.CreateRank(guild.Id, name, icon, order);
            await SendRanksAsync(stream, guild);
            Console.WriteLine($"[Gremio] Rango {created.Id} «{created.Name}» creado en «{guild.Name}».");
        }

        /// <summary>
        /// El rango de un miembro, por el comando: no hay captura de la petición con la que el
        /// cliente lo cambia. Devuelve la clave del error, o null si se ha cambiado.
        /// </summary>
        public static async Task<string> SetMemberRankAsync(long leaderCharacterId, string targetName, int rankId)
        {
            var guild = GuildStore.GuildOf(leaderCharacterId);
            if (guild == null) return "guild.noguild";
            if (GuildStore.RankOf(leaderCharacterId) != GuildStore.RankLeader) return "guild.rank.notleader";
            if (GuildStore.Ranks(guild.Id).Find(r => r.Id == rankId) == null) return "guild.rank.norank";

            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character == null || !string.Equals(character.Name, targetName, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (member.CharacterId == leaderCharacterId) return "guild.rank.self";

                var updated = GuildStore.SetRank(member.CharacterId, rankId);
                await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(updated, character)));
                return null;
            }

            return "guild.kick.notmember";
        }

        // ─── La nota, el diario y el anuario ────────────────────────────────────

        /// <summary>La entrada de un miembro puesta al día (jgz), con todo lo que se sabe de él.</summary>
        private static byte[] MemberUpdated(GuildStore.Member member, DatabaseManager.DbCharacter character)
            => GuildProtocol.BuildMemberUpdated(member, character.Name, character.Level, character.AccountId,
                                                character.Breed, Achievements.PointsOf(member.CharacterId),
                                                GuildStore.ContributedBy(member.CharacterId),
                                                SessionRegistry.FindByCharacter(member.CharacterId) != null);

        /// <summary>Una trama para todos los del gremio que estén conectados.</summary>
        private static async Task TellEveryoneAsync(GuildStore.Guild guild, byte[] frame)
        {
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(frame);
            }
        }

        /// <summary>
        /// La nota de un miembro (jjj): f1 el texto, f3 el personaje. Medido en «muchas
        /// acciones»: «hola» sobre el propio jefe, y de vuelta su entrada entera con la nota y
        /// la hora en el f7.f8.
        /// </summary>
        public static async Task NoteAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjj = ConnectionProtocol.ReadPayload(frame, Op.Jjj);
            if (jjj == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            string note = "";
            long target = 0;
            foreach (var field in ProtoMessage.Parse(jjj).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) note = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 3 && field.WireType == 0) target = field.VarIntValue;
            }

            var member = GuildStore.MemberOf(target);
            if (member == null || member.GuildId != guild.Id) return;
            var character = DatabaseManager.GetCharacterById(target);
            if (character == null) return;

            var updated = GuildStore.SetNote(target, note, System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(updated, character)));
        }

        /// <summary>El diario (jim → jil).</summary>
        public static async Task LogAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jil, GuildProtocol.BuildLog(GuildStore.LogOf(guild.Id))));
        }

        /// <summary>
        /// Escribir la ficha del anuario (jcc): se guarda como llega y se devuelve el jci con la
        /// hora y el jefe puestos. Medido en la captura de fundar «Jondo» y otra vez en la de
        /// contribuir, con la misma ficha.
        /// </summary>
        public static async Task SetProfileAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jcc = ConnectionProtocol.ReadPayload(frame, Op.Jcc);
            if (jcc == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            var body = ProtoMessage.Parse(jcc).Fields.Find(f => f.FieldNumber == 2 && f.WireType == 2);
            if (body == null) return;

            var profile = new GuildStore.Profile { GuildId = guild.Id, WhenMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            foreach (var field in ProtoMessage.Parse(body.BytesValue).Fields)
            {
                switch (field.FieldNumber)
                {
                    case 2 when field.WireType == 2: profile.Description = System.Text.Encoding.UTF8.GetString(field.BytesValue); break;
                    case 3 when field.WireType == 0: profile.MinLevel = (int)field.VarIntValue; break;
                    case 4 when field.WireType == 2: profile.Tags = field.BytesValue; break;
                    case 5 when field.WireType == 0: profile.F5 = (int)field.VarIntValue; break;
                    case 6 when field.WireType == 2: profile.F6 = field.BytesValue; break;
                    case 9 when field.WireType == 0: profile.MaxLevel = (int)field.VarIntValue; break;
                    case 13 when field.WireType == 2: profile.Title = System.Text.Encoding.UTF8.GetString(field.BytesValue); break;
                }
            }

            GuildStore.SaveProfile(profile);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jci, ProfileOf(guild)));
            Console.WriteLine($"[Gremio] Ficha de «{guild.Name}» escrita: «{profile.Title}».");
        }

        /// <summary>
        /// Buscar en el anuario (jjm): el acuse vacío (jme) y la lista de gremios (jiv).
        /// </summary>
        /// <remarks>
        /// Los filtros del jjm -niveles, actividades- no se aplican: con los gremios que hay en un
        /// servidor de estas dimensiones, la lista entera es la respuesta útil. Van todos los que
        /// tienen ficha escrita y también los que no, con la vacía.
        /// </remarks>
        public static async Task SearchAsync(NetworkStream stream, byte[] frame)
        {
            var entries = new List<GuildProtocol.DirectoryEntry>();
            foreach (var guild in GuildStore.AllGuilds())
            {
                var leader = GuildStore.LeaderOf(guild.Id);
                var leaderCharacter = leader == null ? null : DatabaseManager.GetCharacterById(leader.CharacterId);
                entries.Add(new GuildProtocol.DirectoryEntry
                {
                    Guild = guild,
                    Profile = GuildStore.ProfileOf(guild.Id),
                    LeaderId = leader?.CharacterId ?? 0,
                    LeaderName = leaderCharacter?.Name ?? "",
                    Members = GuildStore.Members(guild.Id).Count,
                });
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jme, System.Array.Empty<byte>()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jiv, GuildProtocol.BuildDirectory(entries)));
        }

        /// <summary>
        /// Abandonar el gremio (jho): f1 el personaje. Se saca y se confirma con khj, que es como
        /// el cliente vacía la ventana. Medido en «salir de mi gremio».
        /// </summary>
        public static Task LeaveAsync(NetworkStream stream, byte[] frame)
            => LeaveAsync(stream, SessionContext.State.CharacterId);

        /// <summary>Salir, venga del jho o del comando.</summary>
        public static async Task LeaveAsync(NetworkStream stream, long who)
        {
            if (who == 0) return;
            var guild = GuildStore.GuildOf(who);
            if (GuildStore.Leave(who) == null) return;

            await GoneAsync(stream, who);
            if (guild != null) await RefreshEveryoneAsync(guild, who);
            Console.WriteLine($"[Gremio] {SessionContext.State.CharacterName} deja «{guild?.Name}».");
        }

        /// <summary>
        /// Lo que recibe quien se queda sin gremio, salga o lo echen: la captura «salir de mi
        /// gremio» tras el jho es khj {f1: 97}, jhc vacío y un jsn que lo redibuja ya sin el
        /// nombre del gremio debajo del suyo. Se manda por la sesión que se pase, que puede no
        /// ser la que ha hablado: al expulsado se le avisa desde la sesión del que expulsa.
        /// </summary>
        private static async Task GoneAsync(NetworkStream stream, long characterId)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Khj, GuildProtocol.BuildGoneNotice()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhc, System.Array.Empty<byte>()));

            var session = SessionRegistry.FindByCharacter(characterId);
            var character = DatabaseManager.GetCharacterById(characterId);
            if (session != null && character != null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    character, session.State.CellId, session.State.Orientation, session.AccountId)));
            }
        }

        /// <summary>
        /// Expulsar a un miembro. Devuelve la clave del mensaje de error, o null si ha salido.
        /// </summary>
        /// <remarks>
        /// Sin captura: la petición con la que el cliente expulsa a alguien no está en ninguna, así
        /// que se hace por el comando. Lo que SÍ está medido es cómo queda cada uno: el expulsado
        /// recibe lo mismo que quien sale por su pie -khj, jhc y su jsn sin gremio-, y a los demás
        /// se les pone al día la lista y la cabecera.
        ///
        /// Sólo expulsa el jefe, y no a sí mismo: para irse está el jho.
        /// </remarks>
        public static async Task<string> KickAsync(long kickerCharacterId, string targetName)
        {
            var guild = GuildStore.GuildOf(kickerCharacterId);
            if (guild == null) return "guild.noguild";
            if (GuildStore.RankOf(kickerCharacterId) != GuildStore.RankLeader) return "guild.kick.notleader";

            GuildStore.Member target = null;
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character != null && string.Equals(character.Name, targetName, System.StringComparison.OrdinalIgnoreCase))
                {
                    target = member;
                    break;
                }
            }

            if (target == null) return "guild.kick.notmember";
            if (target.CharacterId == kickerCharacterId) return "guild.kick.self";

            GuildStore.Leave(target.CharacterId);

            var kicked = SessionRegistry.FindByCharacter(target.CharacterId);
            if (kicked != null)
            {
                using (SessionContext.Push(kicked))
                {
                    await GoneAsync(kicked.Stream, target.CharacterId);
                }
            }

            await RefreshEveryoneAsync(guild);
            Console.WriteLine($"[Gremio] {targetName} expulsado de «{guild.Name}».");
            return null;
        }

        /// <summary>
        /// Le pone al día la ventana a todos los del gremio que estén conectados: la lista de
        /// miembros y la cabecera con cuántos son. Es lo que hace falta cuando entra o sale uno.
        /// </summary>
        private static async Task RefreshEveryoneAsync(GuildStore.Guild guild, long exceptCharacter = 0)
        {
            var members = GuildStore.Members(guild.Id);
            var frames = MemberFrames(members);
            byte[] header = ConnectionProtocol.Push(Op.Jhh, GuildProtocol.BuildGuildInfo(guild, members.Count));

            foreach (var member in members)
            {
                if (member.CharacterId == exceptCharacter) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null) continue;
                foreach (var frame in frames) await session.SendAsync(frame);
                await session.SendAsync(header);
            }
        }

        // ─── La tienda del gremio ───────────────────────────────────────────────

        /// <summary>
        /// Abrir la tienda (jki) y contestar con sus cinco oráculos (jkh), con el precio ya
        /// multiplicado por las cuentas del gremio.
        /// </summary>
        public static async Task OpenShopAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkh, GuildProtocol.BuildShop(AccountsIn(guild))));
        }

        /// <summary>
        /// Las cuentas distintas que tiene el gremio, que es por lo que se multiplica el precio:
        /// cuatro cuentas, precios por cuatro. Dos personajes de la misma cuenta cuentan una vez.
        /// </summary>
        private static int AccountsIn(GuildStore.Guild guild)
        {
            var cuentas = new HashSet<long>();
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character != null) cuentas.Add(character.AccountId);
            }
            return cuentas.Count < 1 ? 1 : cuentas.Count;
        }

        /// <summary>
        /// Comprar un oráculo (jkw): si llegan los kamas de gremio se apunta y se contesta con el
        /// acuse, los kamas que quedan y lo que falta por activar; si no llegan, el acuse VACÍO,
        /// que es lo que manda el servidor real cuando lo rechaza.
        /// </summary>
        public static async Task BuyOracleAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jkw = ConnectionProtocol.ReadPayload(frame, Op.Jkw);
            if (jkw == null) return;
            int oracle = (int)FieldValue(jkw, 1);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null || GuildOracles.Of(oracle) == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkj, GuildProtocol.BuildShopRefused()));
                return;
            }

            int price = GuildOracles.PriceFor(oracle, AccountsIn(guild));
            if (!GuildStore.SpendGuildKamas(guild.Id, price))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkj, GuildProtocol.BuildShopRefused()));
                return;
            }

            var deadline = DateTimeOffset.UtcNow.AddHours(GuildOracles.HoursToActivate);
            GuildStore.BuyOracle(guild.Id, oracle, deadline);
            long left = GuildStore.GuildOf(who).GuildKamas;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkj, GuildProtocol.BuildShopBought(oracle)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jia, GuildProtocol.BuildGuildKamas(left)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkv,
                GuildProtocol.BuildPendingOracle(oracle, GuildStore.OracleDeadline(guild.Id, oracle))));
        }

        /// <summary>
        /// Activar un oráculo comprado (jky): el acuse y la alteración, que dura dos horas. Sólo
        /// si el gremio lo tiene comprado y el plazo no se ha pasado.
        /// </summary>
        public static async Task ActivateOracleAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jky = ConnectionProtocol.ReadPayload(frame, Op.Jky);
            if (jky == null) return;
            int oracle = (int)FieldValue(jky, 1);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            var catalogue = GuildOracles.Of(oracle);
            if (guild == null || catalogue == null) return;

            string deadline = GuildStore.OracleDeadline(guild.Id, oracle);
            if (deadline == null) return;
            if (DateTimeOffset.TryParse(deadline, null, System.Globalization.DateTimeStyles.AdjustToUniversal,
                                        out var cuando) && cuando < DateTimeOffset.UtcNow)
            {
                return;   // se pasó el plazo de un día
            }

            long from = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long to = DateTimeOffset.UtcNow.AddHours(GuildOracles.HoursActive).ToUnixTimeMilliseconds();

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkx, GuildProtocol.BuildOracleActivated(oracle)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lzs,
                GuildProtocol.BuildAlteration(catalogue.Alteration, from, to)));
        }

        // ─── Contribuir ─────────────────────────────────────────────────────────

        /// <summary>
        /// Contribuir (jlb): diez mil kamas del personaje por diez de gremio, cinco veces por
        /// semana como mucho. Se contesta con la contribución hecha, los kamas del gremio y los
        /// del personaje.
        /// </summary>
        public static async Task ContributeAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            if (GameState.Kamas < GuildStore.ContributionKamas) return;

            int left = GuildStore.Contribute(who, guild.Id);
            if (left < 0) return;   // ya no le quedaban esta semana

            GameState.Kamas -= GuildStore.ContributionKamas;
            DatabaseManager.SaveCurrentCharacter();

            // El orden de la captura: ivf, jgz, (ivj), jia, (iun, khd), jle.
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivf,
                ConnectionProtocol.BuildKamas(GameState.Kamas)));
            var me = GuildStore.MemberOf(who);
            var myself = DatabaseManager.GetCharacterById(who);
            if (me != null && myself != null)
            {
                await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(me, myself)));
            }
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jia,
                GuildProtocol.BuildGuildKamas(GuildStore.GuildOf(who).GuildKamas)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jle,
                GuildProtocol.BuildContribution(GuildStore.ContributionKamas, left)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jla, GuildProtocol.BuildContributionsLeft(left)));
        }

        // ─── Candidaturas ───────────────────────────────────────────────────────

        /// <summary>Lo que hace falta de cada candidato para armar su bloque.</summary>
        private static List<(GuildStore.Application, string, int, long, string, string)> Detailed(
            IEnumerable<GuildStore.Application> applications)
        {
            var fuera = new List<(GuildStore.Application, string, int, long, string, string)>();
            foreach (var application in applications)
            {
                var character = DatabaseManager.GetCharacterById(application.CharacterId);
                if (character == null) continue;
                fuera.Add((application, character.Name, character.Level, character.AccountId, character.Name, ""));
            }
            return fuera;
        }

        /// <summary>La pestaña de candidaturas (jlx/jml → jmf).</summary>
        public static async Task ApplicationsAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            byte[] jlx = ConnectionProtocol.ReadPayload(frame, Op.Jlx);
            int tab = jlx == null ? ApplicationsTab : (int)FieldValue(jlx, 1);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jmf,
                GuildProtocol.BuildApplications(tab, Detailed(GuildStore.Applications(guild.Id)))));
        }

        /// <summary>La pestaña que pide el cliente al abrir las candidaturas, medida en la captura.</summary>
        public const int ApplicationsTab = 8;

        /// <summary>Ver una candidatura (jlt → jly).</summary>
        public static async Task ApplicationDetailAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jlt = ConnectionProtocol.ReadPayload(frame, Op.Jlt);
            if (jlt == null) return;
            long applicant = FieldValue(jlt, 2);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            var application = GuildStore.ApplicationOf(guild.Id, applicant);
            if (application == null) return;
            var detailed = Detailed(new[] { application });
            if (detailed.Count == 0) return;
            var (one, name, level, accountId, nick, tag) = detailed[0];

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jly,
                GuildProtocol.BuildApplication(one, name, level, accountId, nick, tag, who)));
        }

        /// <summary>
        /// Aceptar una candidatura (jjn): el candidato entra de rango 4 y al que la acepta le
        /// llegan los dos avisos que salen en la captura, el de aceptada y el de que ya está
        /// dentro. Al que entra se le manda su gremio entero.
        /// </summary>
        public static async Task AcceptApplicationAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjn = ConnectionProtocol.ReadPayload(frame, Op.Jjn);
            if (jjn == null) return;
            long applicant = FieldValue(jjn, 2);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            if (GuildStore.ApplicationOf(guild.Id, applicant) == null) return;
            if (GuildStore.GuildOf(applicant) != null) return;   // ya está en otro

            var character = DatabaseManager.GetCharacterById(applicant);
            if (character == null) return;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jin,
                GuildProtocol.BuildApplicationStatus(character.Name, applicant, GuildProtocol.StatusAccepted)));

            GuildStore.Join(applicant, guild.Id);

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jin,
                GuildProtocol.BuildApplicationStatus(character.Name, applicant, GuildProtocol.StatusJoined)));

            await SendGuildToNewMemberAsync(applicant, guild);
            await RefreshEveryoneAsync(guild, applicant);
        }

        // ─── Invitaciones ───────────────────────────────────────────────────────

        /// <summary>
        /// Invitar a alguien. El botón del cliente no aparece en ninguna captura -las que hay son
        /// del lado de quien la recibe-, así que de momento se dispara desde el comando de chat
        /// y lo que viaja es lo medido: al invitado le llega el jiq con el gremio y el nombre de
        /// quien invita, y él contesta con el jiz.
        /// </summary>
        public static async Task<string> InviteAsync(long inviterCharacterId, string targetName)
        {
            var guild = GuildStore.GuildOf(inviterCharacterId);
            if (guild == null) return "guild.invite.noguild";

            var session = SessionRegistry.FindByName(targetName);
            if (session == null) return "guild.invite.notfound";
            if (GuildStore.GuildOf(session.CharacterId) != null) return "guild.invite.hasguild";

            var inviter = DatabaseManager.GetCharacterById(inviterCharacterId);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jiq,
                GuildProtocol.BuildInvitation(guild, inviter?.Name ?? "")));
            _invitations[session.CharacterId] = guild.Id;
            return null;
        }

        /// <summary>A qué gremio se ha invitado a cada personaje, mientras no conteste.</summary>
        private static readonly Dictionary<long, long> _invitations = new();

        /// <summary>
        /// La respuesta a la invitación (jiz): vacío la rechaza, f1 = 1 la acepta. Medido en la
        /// captura de recibirla, donde al aceptar llegan el gremio entero y el jij.
        /// </summary>
        public static async Task AnswerInvitationAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            if (who == 0 || !_invitations.TryGetValue(who, out long guildId)) return;

            byte[] jiz = ConnectionProtocol.ReadPayload(frame, Op.Jiz);
            bool accepts = jiz != null && FieldValue(jiz, 1) == 1;
            _invitations.Remove(who);
            if (!accepts) return;
            if (GuildStore.GuildOf(who) != null) return;

            GuildStore.Join(who, guildId);
            var guild = GuildStore.GuildOf(who);
            if (guild == null) return;

            await SendGuildToOwnerAsync(stream, guild, who);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jij, GuildProtocol.BuildJoinDone()));
            await RefreshEveryoneAsync(guild, who);
        }

        /// <summary>Le manda el gremio entero a quien acaba de entrar, si está conectado.</summary>
        private static async Task SendGuildToNewMemberAsync(long characterId, GuildStore.Guild guild)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            int rank = GuildStore.RankOf(characterId);
            var members = GuildStore.Members(guild.Id);

            await session.SendAsync(ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildDefaultRanks()));
            await session.SendAsync(ConnectionProtocol.Push(Op.Jgw, GuildProtocol.BuildGuildJoined(guild, rank)));
            foreach (var frame in MemberFrames(members)) await session.SendAsync(frame);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, members.Count)));
            await session.SendAsync(ConnectionProtocol.Push(Op.Jij, GuildProtocol.BuildJoinDone()));
        }

        /// <summary>
        /// Echar una candidatura a un gremio. Como con la invitación, el botón del cliente no
        /// está medido: se manda desde el comando de chat y lo que sí viaja medido es el aviso
        /// al gremio (jma).
        /// </summary>
        public static async Task<string> ApplyAsync(long characterId, string guildName, string message)
        {
            if (GuildStore.GuildOf(characterId) != null) return "guild.apply.hasguild";
            var guild = GuildStore.ByName(guildName);
            if (guild == null) return "guild.apply.notfound";

            var character = DatabaseManager.GetCharacterById(characterId);
            GuildStore.Apply(characterId, guild.Id, message ?? "");

            byte[] aviso = ConnectionProtocol.Push(Op.Jma,
                GuildProtocol.BuildApplicationArrived(characterId, character?.Name ?? ""));
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(aviso);
            }
            return null;
        }

        /// <summary>El primer varint de un campo, o cero.</summary>
        private static long FieldValue(byte[] payload, int number)
        {
            foreach (var field in ProtoMessage.Parse(payload).Fields)
            {
                if (field.FieldNumber == number && field.WireType == 0) return field.VarIntValue;
            }
            return 0;
        }
    }
}
