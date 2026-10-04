using System;
using System.Collections.Generic;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Las tramas del gremio, cada una medida en las 12 capturas de Gremio/.
    ///
    /// Lo que va byte a byte contra la captura -el gremio al que se pertenece (jgw), la cabecera
    /// de su ventana (jhh) y los cuatro rangos por defecto (jco)- lleva su test. La lista de
    /// miembros (jgu) se arma con los campos que sí se entienden y unos pocos constantes que se
    /// copian de la captura del fundador sin saber qué significan; van marcados donde toca.
    /// </summary>
    public static class GuildProtocol
    {
        /// <summary>
        /// El bloque del gremio que comparten el jgw, el jhe y el actor del mapa: el emblema, el
        /// id, el nombre y el nivel. En la captura de crear «Jondo»: f1{f3 emblema}, f2 42043,
        /// f3 «Jondo», f4 1. En el actor va como opción f5 { f4: este bloque }, y es lo que pone
        /// el nombre del gremio bajo el del personaje.
        /// </summary>
        public static Pb GuildBlock(GuildStore.Guild guild)
            => Pb.New()
                .Msg(1, Pb.New().Msg(3, Emblem(guild)))
                .Var(2, guild.Id)
                .Str(3, guild.Name)
                .Var(4, guild.Level);

        /// <summary>El emblema, los cuatro números del jjg de creación en sus campos 1, 2, 3 y 5.</summary>
        private static Pb Emblem(GuildStore.Guild guild)
            => Pb.New()
                .Var(1, guild.EmblemSymbol)
                .Var(2, guild.EmblemSymbolColor)
                .Var(3, guild.EmblemBackground)
                .Var(5, guild.EmblemSymbolRgb);

        /// <summary>
        /// «Perteneces a este gremio» (jgw): f2 el puesto del personaje, f3 el bloque del gremio.
        /// Byte a byte contra la creación de «Jondo».
        /// </summary>
        public static byte[] BuildGuildJoined(GuildStore.Guild guild, int rank)
            => Pb.New()
                .Var(2, rank)
                .Msg(3, GuildBlock(guild))
                .Build();

        /// <summary>
        /// Belonging to a guild, said silently (jhe): f1 the guild's block, f2 the rank, f3 the
        /// member's contribution. What world entry sends; the jgw is joining, and its handler
        /// prints "you have just joined" and opens a popup every time it comes.
        /// </summary>
        /// <remarks>
        /// Against the captures of «Jondo»: f3 is 10 and then 20 around "contribuir en el gremio
        /// 10 puntos", the {20, 20} of the member's own jgu row. The f4 (1791 for that member,
        /// 4568 for another) is not known and does not go; a member who has just joined has it at
        /// zero, since the jgw does not carry it.
        /// </remarks>
        public static byte[] BuildMembership(GuildStore.Guild guild, int rank, long contribution)
            => Pb.New()
                .Msg(1, GuildBlock(guild))
                .Var(2, rank)
                .VarIfNotZero(3, contribution)
                .Build();

        /// <summary>
        /// The next weekly reset (jez), as an ISO string: Tuesday at 05:00 UTC. The five captures
        /// that ask for it agree -- asked on Sunday the 9th it is the 11th, on Wednesday the 12th
        /// and Saturday the 15th the 18th, on Saturday the 29th 1 September, and on Tuesday the
        /// 1st at 23:12, the reset of that morning already past, the 8th.
        /// </summary>
        public static byte[] BuildWeeklyReset(DateTime utcNow)
            => Pb.New().Str(1, NextWeeklyReset(utcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                                                                  System.Globalization.CultureInfo.InvariantCulture)).Build();

        internal static DateTime NextWeeklyReset(DateTime utcNow)
        {
            int ahead = ((int)DayOfWeek.Tuesday - (int)utcNow.DayOfWeek + 7) % 7;
            var reset = DateTime.SpecifyKind(utcNow.Date.AddDays(ahead).AddHours(5), DateTimeKind.Utc);
            return reset > utcNow ? reset : reset.AddDays(7);
        }

        /// <summary>
        /// A guild tab's answer for a guild that has none of what it lists -- no perks, no raids,
        /// nothing in the paged list -- as the captures of a new guild answer it: the empty
        /// message ("0a00" or "1a00") in the one field they carry, or nothing at all.
        /// </summary>
        public static byte[] BuildEmptyTab(int field)
            => field == 0 ? Array.Empty<byte>() : Pb.New().Bytes(field, Array.Empty<byte>()).Build();

        /// <summary>
        /// La cabecera de la ventana de gremio (jhh): f1 la fecha de fundación, f3 el nivel, f9
        /// el máximo de miembros y f10 cuántos hay. Un gremio de nivel 1 recién creado manda
        /// exactamente esos cuatro campos -sin las barras de experiencia f5/f6/f7 que sólo salen
        /// en gremios con experiencia-, y así se reproduce.
        /// </summary>
        public static byte[] BuildGuildInfo(GuildStore.Guild guild, int memberCount)
            => Pb.New()
                .Str(1, guild.FoundedUtc)
                .Var(3, guild.Level)
                .Var(9, GuildStore.MaxMembers(guild.Level))
                .Var(10, memberCount)
                .Build();

        /// <summary>
        /// Los cuatro rangos por defecto de un gremio recién creado (jco). Son un molde fijo,
        /// medido una vez en la creación de «Jondo»: los nombres son claves de traducción
        /// (guild.rank.N.name), y los permisos de cada rango van como una ristra de bytes que
        /// este emulador todavía no interpreta -se copian tal cual, que es lo que hace un gremio
        /// nuevo-. El f4{f2} es el icono del rango y el f5 su número.
        /// </summary>
        public static byte[] BuildDefaultRanks() => BuildRanks(GuildStore.DefaultRanks(0));

        /// <summary>
        /// Los rangos de un gremio (jco), uno por f2 { f2 nombre, f3 permisos, f4 { f2 icono,
        /// f3 orden }, f5 id }. Los permisos son { f1 marca, f3 lista } tal como llegaron.
        /// </summary>
        /// <remarks>
        /// Medido dos veces: el molde de un gremio nuevo, y el mismo gremio después de renombrar
        /// el rango 1, tocar los permisos del 2 y crear un quinto -jct, jck y jcv-, que el servidor
        /// contesta con este mismo jco entero, con el nuevo en su sitio y el 4 corrido un puesto.
        /// </remarks>
        public static byte[] BuildRanks(IReadOnlyList<GuildStore.Rank> ranks)
        {
            var jco = Pb.New();
            foreach (var rank in ranks)
            {
                jco.Msg(2, Pb.New()
                    .Str(2, rank.Name)
                    .Msg(3, Pb.New().VarIfNotZero(1, rank.Flag ? 1 : 0).BytesIfNotEmpty(3, rank.Rights))
                    .Msg(4, Pb.New().Var(2, rank.Icon).VarIfNotZero(3, rank.Order))
                    .Var(5, rank.Id));
            }
            return jco.Build();
        }

        /// <summary>
        /// El gremio visto desde un actor del mapa (jhe): el bloque del gremio, el puesto, la
        /// experiencia del miembro y un f4 constante (1791 en todas las capturas de «Jondo», sin
        /// significado reconstruido). Es lo que el cliente pinta al pasar el ratón por encima.
        /// </summary>
        public const int ActorGuildTrailer = 1791;

        public static byte[] BuildActorGuild(GuildStore.Guild guild, int rank, long memberExperience)
            => Pb.New()
                .Msg(1, GuildBlock(guild))
                .Var(2, rank)
                .VarIfNotZero(3, memberExperience)
                .Var(4, ActorGuildTrailer)
                .Build();

        /// <summary>
        /// Un miembro para la lista de la ventana (jgu).
        /// </summary>
        /// <remarks>
        /// Medido sobre los siete miembros de dos gremios de las capturas: f3 el nivel (con los
        /// omega dentro: 354, 352, 369), f5.f2 el puesto, f5.f4 cuándo entró, f5.f6 LA CLASE -12
        /// el pandawa, 10 el sadida, 11 el sacrógrito; por copiar el 11 del fundador todo miembro
        /// salía dibujado como sacrógrito-, f5.f7.f1 los puntos de logro -8094 el fundador, 2466,
        /// 732 y 55 los demás, que es lo que la columna «Logros» enseña-, f5.f8 si está conectado y
        /// f5.f10 la cuenta. El f5.f3 (1 en el fundador, ausente en los demás) y el f5.f7.f3 (3 en
        /// el fundador, 2 en los demás) siguen sin significado y van como en el fundador.
        /// </remarks>
        public static byte[] BuildMember(GuildStore.Member member, string name, int level, long accountId,
                                         int breed, int achievementPoints, int contributed = 0, bool online = true)
            => Pb.New().Msg(1, MemberEntry(member, name, level, accountId, breed, achievementPoints, contributed, online)).Build();

        /// <summary>
        /// Un miembro puesto al día (jgz): la misma entrada que el jgu, en el f2. Es lo que
        /// contesta el servidor a la nota (jjj) y lo que manda tras una contribución.
        /// </summary>
        public static byte[] BuildMemberUpdated(GuildStore.Member member, string name, int level, long accountId,
                                                int breed, int achievementPoints, int contributed = 0, bool online = true)
            => Pb.New().Msg(2, MemberEntry(member, name, level, accountId, breed, achievementPoints, contributed, online)).Build();

        /// <summary>
        /// La entrada de un miembro, la que va dentro del jgu y del jgz.
        /// </summary>
        /// <remarks>
        /// Del jgz de «hola» y de los jgu de después de contribuir: el f7.f2 son las gremichas,
        /// dos veces el mismo número -{10, 10} tras una contribución, {20, 20} tras dos- y vacío
        /// antes de ninguna; el f7.f8 es la nota, { f1 texto, f2 cuándo }, y vacío sin nota. El
        /// f8 { f1: 1 } es que está conectado; sin conexión el servidor manda f8 vacío y un
        /// f9 = 1 detrás, que es como salen los cuatro miembros desconectados de «Hezbola».
        /// </remarks>
        private static Pb MemberEntry(GuildStore.Member member, string name, int level, long accountId,
                                      int breed, int achievementPoints, int contributed, bool online)
        {
            var status = online ? Pb.New().Var(1, 1) : Pb.New();
            var details = Pb.New()
                .Var(2, member.Rank)
                .Var(3, 1)                                   // measured constant, meaning unknown
                .Var(4, member.JoinedUtcMs)
                .Var(6, breed)
                .Msg(7, Pb.New()
                    .VarIfNotZero(1, achievementPoints)
                    .Msg(2, Pb.New().VarIfNotZero(1, contributed).VarIfNotZero(2, contributed))
                    .Var(3, 3)
                    .Msg(8, Pb.New().StrIfNotEmpty(1, member.Note).VarIfNotZero(2, member.NoteMs)))
                .Msg(8, status);
            if (!online) details.Var(9, 1);
            details.Var(10, accountId);

            return Pb.New()
                .Msg(1, Pb.New().Str(2, name).Var(3, level).Msg(5, details))
                .Var(2, member.CharacterId);
        }

        /// <summary>
        /// El diario (jil): una línea por f1 { f11 gremio, f17 '' si es la fundación, f19 cuándo,
        /// f20 { f2 personaje, f3 nombre, f4 2 en el tipo que no se ha entendido } }.
        /// </summary>
        /// <remarks>
        /// Medido en «muchas acciones»: tres líneas, la fundación de «Jondo» a los dos milisegundos
        /// de crearse, y dos de Hiierbita-Xx: una con f4 = 2 y, medio minuto después, la de su
        /// entrada, cuyo f19 es exactamente el JoinedUtcMs de su jgu.
        /// </remarks>
        public static byte[] BuildLog(IReadOnlyList<GuildStore.LogEntry> entries)
        {
            var jil = Pb.New();
            foreach (var entry in entries)
            {
                var line = Pb.New().Var(11, entry.GuildId);
                if (entry.Kind == GuildStore.LogFounded) line.Str(17, "");
                line.Var(19, entry.WhenMs);
                if (entry.Kind != GuildStore.LogFounded)
                {
                    line.Msg(20, Pb.New()
                        .Var(2, entry.CharacterId)
                        .Str(3, entry.Name)
                        .VarIfNotZero(4, entry.Kind == GuildStore.LogJoined ? 0 : entry.Kind));
                }
                jil.Msg(1, line);
            }
            return jil.Build();
        }

        /// <summary>
        /// La ficha del anuario de un gremio (jci), o la vacía de uno recién fundado.
        /// </summary>
        /// <remarks>
        /// Medida las dos: la vacía es f2 { f3: 1, f5: 2, f6: 0x01, f10: gremio }, y la escrita
        /// -«Hola!», niveles 20 a 100, nueve etiquetas, título «Dragon Ball»- devuelve lo que
        /// mandó el jcc con el f1 (cuándo) y el f8 (el jefe) que pone el servidor.
        /// </remarks>
        public static byte[] BuildProfile(GuildStore.Guild guild, GuildStore.Profile profile, string leaderName)
            => Pb.New().Msg(2, ProfileBody(guild, profile, leaderName)).Build();

        private static Pb ProfileBody(GuildStore.Guild guild, GuildStore.Profile profile, string leaderName)
        {
            if (profile == null || profile.WhenMs == 0)
            {
                return Pb.New().Var(3, 1).Var(5, 2).Bytes(6, new byte[] { 0x01 }).Var(10, guild.Id);
            }

            return Pb.New()
                .Var(1, profile.WhenMs)
                .StrIfNotEmpty(2, profile.Description)
                .VarIfNotZero(3, profile.MinLevel)
                .BytesIfNotEmpty(4, profile.Tags)
                .VarIfNotZero(5, profile.F5)
                .BytesIfNotEmpty(6, profile.F6)
                .StrIfNotEmpty(8, leaderName)
                .VarIfNotZero(9, profile.MaxLevel)
                .Var(10, guild.Id)
                .StrIfNotEmpty(13, profile.Title);
        }

        /// <summary>Un gremio del anuario, con su ficha, su jefe, cuántos son y su emblema.</summary>
        public sealed class DirectoryEntry
        {
            public GuildStore.Guild Guild { get; init; }
            public GuildStore.Profile Profile { get; init; }
            public long LeaderId { get; init; }
            public string LeaderName { get; init; } = "";
            public int Members { get; init; }
        }

        /// <summary>
        /// El anuario (jiv): un f1 por gremio, { f1 { f1 { f1 jefe, f6 ficha, f7 miembros },
        /// f3 emblema }, f2 id, f3 nombre, f4 nivel }.
        /// </summary>
        /// <remarks>
        /// Del jiv de 6.754 bytes que contesta a la búsqueda del anuario: el jefe por su id, la
        /// ficha con la misma forma que el jci, cuántos miembros tiene (91, 339), el emblema con
        /// los cuatro números del jjg, y el gremio con su id, su nombre y su nivel.
        /// </remarks>
        public static byte[] BuildDirectory(IReadOnlyList<DirectoryEntry> entries)
        {
            var jiv = Pb.New();
            foreach (var entry in entries)
            {
                jiv.Msg(1, Pb.New()
                    .Msg(1, Pb.New()
                        .Msg(1, Pb.New()
                            .Var(1, entry.LeaderId)
                            .Msg(6, ProfileBody(entry.Guild, entry.Profile, entry.LeaderName))
                            .Var(7, entry.Members))
                        .Msg(3, Emblem(entry.Guild)))
                    .Var(2, entry.Guild.Id)
                    .Str(3, entry.Guild.Name)
                    .Var(4, entry.Guild.Level));
            }
            return jiv.Build();
        }

        /// <summary>
        /// Las contribuciones que quedan esta semana (jla): 5, 4, 3 en la captura de contribuir,
        /// una menos por cada una, y vacío en un gremio que no tiene ninguna.
        /// </summary>
        public static byte[] BuildContributionsLeft(int left) => Pb.New().VarIfNotZero(1, left).Build();

        /// <summary>El jff de un gremio recién fundado: f3 vacío. Lo que llena un gremio con recorrido no está entendido.</summary>
        public static byte[] BuildNoBenefits() => Pb.New().Str(3, "").Build();

        /// <summary>
        /// El mensaje del gremio (jci), vacío: lo que contesta el servidor real al jiy {f2: 4} de
        /// un gremio recién fundado, byte a byte salvo el id. Medido dos veces en la captura de
        /// fundar «Jondo»: f2 { f3: 1, f5: 2, f6: 0x01, f10: el gremio }.
        /// </summary>
        public static byte[] BuildEmptyGuildMessage(long guildId)
            => Pb.New()
                .Msg(2, Pb.New()
                    .Var(3, 1)
                    .Var(5, 2)
                    .Bytes(6, new byte[] { 0x01 })
                    .Var(10, guildId))
                .Build();

        /// <summary>
        /// El aviso de que ya no se está en el gremio (khj), y el de que se acaba de entrar (khi):
        /// los dos llevan f1 = 97, medido al fundar «Jondo» y al salir de él. No es el puesto ni el
        /// gremio -es el mismo número en las dos direcciones-, y va tal cual.
        /// </summary>
        public const int GuildNotice = 97;

        public static byte[] BuildGoneNotice() => Pb.New().Var(1, GuildNotice).Build();

        // ─── La tienda del gremio ───────────────────────────────────────────────

        /// <summary>
        /// La tienda (jkh): f2 { f1 las cuentas activas, y un f2 por artículo con su número y su
        /// precio ya multiplicado }. El f2{f2{f3}} vacío de cada artículo va tal cual, que es lo
        /// que manda el servidor real en los dos gremios medidos -uno de una cuenta y otro de
        /// cuatro-.
        /// </summary>
        public static byte[] BuildShop(int accounts)
        {
            var jkh = Pb.New();
            var cuerpo = Pb.New().Var(1, accounts);
            foreach (var oracle in GuildOracles.All)
            {
                cuerpo.Msg(2, Pb.New()
                    .Var(1, oracle.Id)
                    .Msg(2, Pb.New()
                        .Var(1, GuildOracles.PriceFor(oracle.Id, accounts))
                        .Msg(2, Pb.New().Str(3, ""))));
            }
            return jkh.Msg(2, cuerpo).Build();
        }

        /// <summary>Comprado (jkj con el artículo). El rechazo es el mismo mensaje VACÍO.</summary>
        public static byte[] BuildShopBought(int oracle) => Pb.New().Var(1, oracle).Build();
        public static byte[] BuildShopRefused() => System.Array.Empty<byte>();

        /// <summary>Los kamas de gremio que quedan (jia).</summary>
        public static byte[] BuildGuildKamas(long kamas) => Pb.New().Var(1, kamas).Build();

        /// <summary>
        /// Lo comprado que falta por activar (jkv): f1 { f2 { f1 …, f3 el plazo } }, f2 el
        /// artículo. El f1 de dentro valía 5 en la única compra medida y no se sabe qué es -no
        /// es el artículo, que era el 1, ni las cuentas, que eran cuatro-, así que va como
        /// estaba.
        /// </summary>
        public const int PendingOracleUnknown = 5;

        public static byte[] BuildPendingOracle(int oracle, string deadlineUtc)
            => Pb.New()
                .Msg(1, Pb.New().Msg(2, Pb.New().Var(1, PendingOracleUnknown).Str(3, deadlineUtc)))
                .Var(2, oracle)
                .Build();

        /// <summary>Activado (jkx): f1 el artículo.</summary>
        public static byte[] BuildOracleActivated(int oracle) => Pb.New().Var(1, oracle).Build();

        /// <summary>
        /// Una alteración puesta (lzs): f1 { f1 desde, f2 cuál, f4 2, f5 hasta } en milisegundos.
        /// </summary>
        /// <remarks>
        /// La del «Oráculo de saber» de la captura lleva además dos bloques f3 con el detalle de
        /// lo que hace -efectos 2833 y 2867-. No se reproducen: de los cinco oráculos sólo está
        /// medido ese, y copiarle los efectos a los otros cuatro sería inventárselos. El cliente
        /// pinta el icono y el tiempo con lo que va aquí.
        /// </remarks>
        public static byte[] BuildAlteration(int alteration, long fromMs, long toMs)
            => Pb.New()
                .Msg(1, Pb.New()
                    .Var(1, fromMs)
                    .Var(2, alteration)
                    .Var(4, 2)
                    .Var(5, toMs))
                .Build();

        // ─── Contribuir ─────────────────────────────────────────────────────────

        /// <summary>La contribución hecha (jle): f1 los kamas que se dieron, f2 las que quedan.</summary>
        public static byte[] BuildContribution(long kamas, int left)
            => Pb.New().Var(1, kamas).Var(2, left).Build();

        // ─── Candidaturas e invitaciones ────────────────────────────────────────

        /// <summary>
        /// El bloque de una candidatura, el mismo dentro del jmf y del jly: cuándo se mandó, con
        /// qué texto, y quién la manda -su cuenta, su nombre, su etiqueta, su personaje, el
        /// apodo de la cuenta y su nivel-.
        /// </summary>
        private static Pb ApplicationBlock(GuildStore.Application application, string name, int level,
                                           long accountId, string accountNick, string tag)
            => Pb.New()
                .Var(1, application.WhenMs)
                .Str(3, application.Message)
                .Msg(4, Pb.New()
                    .Var(1, 10)                       // measured on the one application; not understood
                    .Var(3, 1)                        // idem
                    .Var(4, accountId)
                    .Str(5, name)
                    .Str(6, tag)
                    .Var(7, application.CharacterId)
                    .Str(8, accountNick)
                    .Msg(9, Pb.New().Var(1, 1))
                    .Var(10, level));

        /// <summary>
        /// La pestaña de candidaturas (jmf): f2 la pestaña que se pidió, un f3 por candidatura y
        /// f4 cuántas hay. Sin ninguna va sólo la pestaña, que es lo que llega en los gremios sin
        /// candidaturas de las capturas.
        /// </summary>
        public static byte[] BuildApplications(int tab, IReadOnlyList<(GuildStore.Application Application,
                                                                       string Name, int Level, long AccountId,
                                                                       string Nick, string Tag)> applications)
        {
            var jmf = Pb.New().Var(2, tab);
            foreach (var (application, name, level, accountId, nick, tag) in applications)
            {
                jmf.Msg(3, ApplicationBlock(application, name, level, accountId, nick, tag));
            }
            if (applications.Count > 0) jmf.Var(4, applications.Count);
            return jmf.Build();
        }

        /// <summary>Una candidatura suelta (jly): f1 1, f2 la candidatura, f3 a quién se le enseña.</summary>
        public static byte[] BuildApplication(GuildStore.Application application, string name, int level,
                                              long accountId, string nick, string tag, long toCharacter)
            => Pb.New()
                .Var(1, 1)
                .Msg(2, ApplicationBlock(application, name, level, accountId, nick, tag))
                .Var(3, toCharacter)
                .Build();

        /// <summary>«Alguien ha echado una candidatura» (jma): f1 su personaje, f2 su nombre.</summary>
        public static byte[] BuildApplicationArrived(long characterId, string name)
            => Pb.New().Var(1, characterId).Str(2, name).Build();

        /// <summary>
        /// Cómo va una candidatura o una invitación (jin): f1 el nombre, f2 un número que no se
        /// ha reconstruido, f3 el estado. Medido: 1 al aceptarla y 3 cuando el otro entra, con el
        /// sello de entrada del miembro cayendo justo en el segundo jin.
        /// </summary>
        public const int StatusAccepted = 1;
        public const int StatusJoined = 3;

        public static byte[] BuildApplicationStatus(string name, long number, int status)
            => Pb.New().Str(1, name).Var(2, number).Var(3, status).Build();

        /// <summary>
        /// «Te invitan a un gremio» (jiq): f1 el bloque del gremio -el mismo del jgw- y f2 quién
        /// invita. Medido byte a byte en la captura de recibir una invitación.
        /// </summary>
        public static byte[] BuildInvitation(GuildStore.Guild guild, string inviterName)
            => Pb.New()
                .Msg(1, GuildBlock(guild))
                .Str(2, inviterName)
                .Build();

        /// <summary>Lo que cierra la entrada al gremio (jij). Valía 3 al aceptar la invitación.</summary>
        public static byte[] BuildJoinDone() => Pb.New().Var(1, StatusJoined).Build();
    }
}
