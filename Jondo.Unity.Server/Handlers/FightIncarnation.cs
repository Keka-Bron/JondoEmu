using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A character fighting as a monster, and the choices a fight proposes him.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three of the dream trials make every player a monster for their fights (DreamTrials'
    /// incarnation): REVERSED's «Retazos de guardián» (8211), DOFOUSSE's «Púrpura» (8350) and
    /// SINJSONJ's «Simueño» (8264). The template is the client's, a monster like any other: its
    /// grade's life, points, characteristics and resistances -- brought to the level the dream's
    /// monsters fight at, as theirs are --, its look, its spells at their grades, and its starting
    /// spell, held as a monster holds its behaviour: Simueño's summons its Monórico, the other two
    /// propose a choice (3008).
    /// </para>
    /// <para>
    /// The choice is the ankla's message, kss: «Elige al guardián de mazmorra que deseas encarnar»
    /// (33) offers three of its ten guardians -- «Each character is offered 3 bosses at the start of
    /// every combat, and the offered options differ» (the guide) --, «Choix de héros» (95) its four
    /// heroes. The option's reward is a spell level (rewardType 5) cast on him and held as his
    /// attitude: the guardian's or hero's look (149), its spells for the fight (3405), a hero's
    /// state and passives. INFERRED: a choice of four or fewer shows them all; one left unanswered
    /// is made for him when its time is up or his turn comes.
    /// </para>
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>The options a long choice shows: three of REVERSED's ten guardians.</summary>
        public const int ChoiceOptionsShown = 3;

        /// <summary>A choice with this many options or fewer shows them all: DOFOUSSE's four heroes.</summary>
        public const int ChoiceShownWhole = 4;

        /// <summary>A choice's option whose reward is a spell level cast on the chooser.</summary>
        public const int CastsSpellLevel = 5;

        /// <summary>
        /// How long a choice waits, in seconds: its own «duration» in the client's ChoicesDataRoot --
        /// 30 for the guardians, 60 for the heroes --, or this when it has none (-1).
        /// </summary>
        public const int ChoiceSecondsWithoutDuration = 60;

        private sealed record PendingChoice(long FightId, TravelData.Choice Shown);

        /// <summary>The choices waiting for their answer, by the character they were put to.</summary>
        private static readonly ConcurrentDictionary<long, PendingChoice> _choices = new();

        // ─── The incarnation ─────────────────────────────────────────────────────

        /// <summary>
        /// The client's «Invulnerable» state (269, Influencia's): every blow takes nothing. The
        /// Entrenador of «Barnabé en el espacio» has one life point and is «invulnérable» (Dofus
        /// pour les noobs); its template has nothing that makes it so, so the fight does.
        /// </summary>
        public const int InvulnerableState = 269;

        /// <summary>A monster a character is to fight as in the fight a reply is about to start (NpcFights).</summary>
        internal readonly record struct NextIncarnation(int Monster, int Grade, bool Invulnerable);

        /// <summary>The incarnations waiting for their fight, by character.</summary>
        private static readonly ConcurrentDictionary<long, NextIncarnation> _nextIncarnations = new();

        /// <summary>
        /// Makes <paramref name="characterId"/> fight his next fight as <paramref name="monsterId"/>
        /// at <paramref name="grade"/> (1-based): the reply that starts a quest's fight says so just
        /// before starting it, and the player's fighter takes it when it is built.
        /// </summary>
        internal static void IncarnateInNextFight(long characterId, int monsterId, int grade, bool invulnerable)
            => _nextIncarnations[characterId] = new NextIncarnation(monsterId, Math.Max(1, grade), invulnerable);

        /// <summary>The incarnation waiting for this character's fight, taken so that it lasts one fight only.</summary>
        internal static NextIncarnation? TakeNextIncarnation(long characterId)
            => _nextIncarnations.TryRemove(characterId, out var next) ? next : null;

        /// <summary>
        /// Makes <paramref name="player"/> fight as <paramref name="monsterId"/> at the grade
        /// <paramref name="gradeIndex"/>, its sheet brought to <paramref name="level"/> when it is
        /// not zero: see the class remarks. A mutation (Mutations) is its grade as it stands. False,
        /// and nothing changed, when the template is not in the data.
        /// </summary>
        internal static bool Incarnate(Fighter player, int monsterId, int level, int gradeIndex = 0)
        {
            MobSpawnManager.EnsureMonsterData();
            var data = MobSpawnManager.GetMonsterData(monsterId);
            if (data == null || data.Grades.Count == 0) return false;

            var member = new MobSpawnManager.MobMember
            {
                Monster = data,
                GradeIndex = Math.Clamp(gradeIndex, 0, data.Grades.Count - 1),
            };
            var template = BuildMonsterFighter(member, player.Id, player.CellId);
            if (level > 0) Managers.Dreams.ScaleTo(template, level);

            player.IncarnatedMonster = monsterId;
            player.IncarnationLook = template.Look;
            player.LookBoneId = template.LookBoneId;
            player.MaxHP = template.MaxHP;
            player.CurrentHP = template.MaxHP;
            player.MaxAP = template.MaxAP;
            player.CurrentAP = template.MaxAP;
            player.MaxMP = template.MaxMP;
            player.CurrentMP = template.MaxMP;
            player.Initiative = template.Initiative;
            player.Strength = template.Strength;
            player.Intelligence = template.Intelligence;
            player.Chance = template.Chance;
            player.Agility = template.Agility;
            player.NeutralResPct = template.NeutralResPct;
            player.EarthResPct = template.EarthResPct;
            player.FireResPct = template.FireResPct;
            player.WaterResPct = template.WaterResPct;
            player.AirResPct = template.AirResPct;

            // Nothing of his gear: the template's own sheet.
            player.Vitality = 0;
            player.Power = 0;
            player.CriticalBonus = 0;
            player.FlatDamage = 0;
            player.CriticalDamage = 0;
            player.EarthDamage = 0;
            player.FireDamage = 0;
            player.WaterDamage = 0;
            player.AirDamage = 0;
            player.NeutralDamage = 0;
            player.PushDamage = 0;
            player.Range = 0;
            player.Otras.Clear();
            foreach (var (characteristic, value) in template.Otras) player.Otras[characteristic] = value;

            player.IncarnationSpells.Clear();
            foreach (int spell in template.SpellIds)
            {
                int grade = template.SpellGrades.TryGetValue(spell, out int g) ? g : 1;
                player.IncarnationSpells.Add((spell, Math.Max(1, grade)));
                int wait = LimitesDeGrado(spell, Math.Max(1, grade)).EsperaInicial;
                if (wait > 0) player.Recarga[spell] = wait;
            }
            player.Conducta = template.Conducta;

            Program.LogDebug($"[Dreams] {player.Name} fights as monster {monsterId} at level {level}: " +
                             $"{player.MaxHP} life, {player.MaxAP} AP, {player.MaxMP} MP, " +
                             $"{player.IncarnationSpells.Count} spell(s), starting spell {player.Conducta.Spell}.");
            return true;
        }

        /// <summary>The level a dream's room fight puts its monsters at: the band's, or the Fin du rêve's first wave.</summary>
        internal static int IncarnationLevel(Managers.Dreams.Sueno dream)
            => dream.SalaActual?.EsFinal == true
                ? Managers.Dreams.FinalRulesOf(dream.Dificultad).BaseLevel
                : Managers.Dreams.MonsterLevel(dream);

        /// <summary>The spells and the grade an incarnated character casts with: his template's and what the fight gave him.</summary>
        internal static IEnumerable<(int Spell, int Grade)> IncarnationCasts(Fighter fighter)
            => fighter.IncarnationSpells.Concat(fighter.GrantedSpells.Where(g => !fighter.TakenSpells.Contains(g.Spell)));

        /// <summary>
        /// The spells look of a guardian a choice gives: the monster whose spells are the ones the
        /// option's level gives for the fight (3405) -- the Jalató Real's «Cura Jalató», «Mordedura
        /// de Jalató Real» and «Coraza Lanuda» are the 147's three --, its look; null when none has
        /// them all.
        /// </summary>
        internal static string? LookOfTheSpells(int spell, int grade)
        {
            var given = Managers.SpellEffects.De(spell, grade)
                .Where(e => e.EffectId == Jondo.Unity.World.Combat.EffectSupport.GivesSpell)
                .Select(e => Managers.SpellCriteria.SpellOfLevel(e.Value).Spell)
                .Where(s => s > 0)
                .ToHashSet();
            if (given.Count == 0) return null;

            int monster = DatabaseManager.MonsterWithSpells(given);
            if (monster == 0) return null;
            MobSpawnManager.EnsureMonsterData();
            string? look = MobSpawnManager.GetMonsterData(monster)?.Look;
            return string.IsNullOrEmpty(look) ? null : look;
        }

        // ─── The choices ─────────────────────────────────────────────────────────

        /// <summary>Whether a choice of a fight waits for this character's answer.</summary>
        public static bool WaitsForChoice(long characterId) => _choices.ContainsKey(characterId);

        /// <summary>
        /// Puts a choice to a character of the fight (kss): the options it shows, and a clock that
        /// makes it for him when its time is up.
        /// </summary>
        internal static async Task ProposeChoiceAsync(FightInstance fight, Fighter who, int choiceId)
        {
            var choice = TravelData.ChoiceOf(choiceId);
            if (choice == null || choice.Options.Count == 0)
            {
                Program.LogDebug($"[Fight] Choice {choiceId} is not in the data; nothing is proposed to {who.Id}.");
                return;
            }

            List<TravelData.ChoiceOption> shown;
            if (choice.Options.Count <= ChoiceShownWhole) shown = choice.Options.ToList();
            else
            {
                var dice = new Random(unchecked((int)(fight.FightId * 31 + who.Id)));
                shown = choice.Options.OrderBy(_ => dice.Next()).Take(ChoiceOptionsShown).ToList();
            }
            var proposed = new TravelData.Choice(choice.Id, choice.NameId, shown, choice.Duration);
            _choices[who.Id] = new PendingChoice(fight.FightId, proposed);

            await ACadaUnoAsync(fight, async sesion =>
            {
                if (sesion.State.CharacterId != who.Id) return;
                await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Kss, TravelProtocol.BuildChoice(proposed)));
            });
            Program.LogDebug($"[Fight] Choice {choiceId} put to {who.Id}: options " +
                             string.Join(", ", shown.Select(o => o.Id)) + ".");

            long chooser = who.Id;
            var pending = _choices[chooser];
            _ = Task.Run(async () =>
            {
                await Task.Delay((proposed.Duration > 0 ? proposed.Duration : ChoiceSecondsWithoutDuration) * 1000);
                await MakeTheChoiceForAsync(chooser, pending);
            });
        }

        /// <summary>
        /// The option chosen (kso { f1: its place }) of a choice a fight put to him: its spell cast
        /// on him. False when no choice of a fight waits for him, so the kso is somebody else's.
        /// </summary>
        public static async Task<bool> AnswerChoiceAsync(NetworkStream stream, byte[] frame)
        {
            long me = SessionContext.State.CharacterId;
            if (!_choices.ContainsKey(me)) return false;

            byte[] kso = ConnectionProtocol.ReadPayload(frame, Op.Kso);
            int place = 0;
            if (kso != null)
                foreach (var field in ProtoMessage.Parse(kso).Fields)
                    if (field.FieldNumber == 1 && field.WireType == 0) place = (int)field.VarIntValue;

            // A choice of a fight that is over is let go, and the kso is the ankla's.
            var fight = GetCurrentFight();
            if (fight == null || !_choices.TryGetValue(me, out var pending) || pending.FightId != fight.FightId)
            {
                _choices.TryRemove(me, out _);
                return false;
            }
            await InTurnAsync(fight, () => ChooseAsync(stream, fight, me, place));
            return true;
        }

        /// <summary>
        /// The choice made for him: when its time is up, or when his turn comes and he has not
        /// answered. The first option shown, at random among them.
        /// </summary>
        private static async Task MakeTheChoiceForAsync(long chooser, PendingChoice pending)
        {
            if (!_choices.TryGetValue(chooser, out var still) || !ReferenceEquals(still, pending)) return;
            var sesion = SessionRegistry.FindByCharacter(chooser);
            var fight = FightById(pending.FightId);
            if (sesion?.Stream == null || fight == null)
            {
                _choices.TryRemove(chooser, out _);
                return;
            }

            using (SessionContext.Push(sesion))
            {
                var turno = sesion.UnoCadaVez;
                await turno.WaitAsync();
                try
                {
                    int place = new Random().Next(pending.Shown.Options.Count);
                    await InTurnAsync(fight, () => ChooseAsync(sesion.Stream, fight, chooser, place));
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Fight] The choice made for {chooser} failed: {ex.Message}");
                }
                finally
                {
                    turno.Release();
                }
            }
        }

        /// <summary>
        /// Called on a turn's start: a choice still waiting for this fighter is made for him now,
        /// so that he never plays without what it gives. Under the fight's lock already.
        /// </summary>
        private static async Task ChooseBeforeHisTurnAsync(NetworkStream stream, FightInstance fight, Fighter who)
        {
            if (who == null || !_choices.TryGetValue(who.Id, out var pending) || pending.FightId != fight.FightId) return;
            int place = new Random().Next(pending.Shown.Options.Count);

            // In his own context, which his bar is drawn from; his session's lock is not taken under
            // the fight's, as ACadaUnoAsync does not.
            var sesion = SessionRegistry.FindByCharacter(who.Id);
            if (sesion?.Stream == null) { await ChooseAsync(stream, fight, who.Id, place); return; }
            using (SessionContext.Push(sesion)) await ChooseAsync(sesion.Stream, fight, who.Id, place);
        }

        /// <summary>Applies the option at <paramref name="place"/> of the choice waiting for <paramref name="chooser"/>.</summary>
        private static async Task ChooseAsync(NetworkStream stream, FightInstance fight, long chooser, int place)
        {
            if (!_choices.TryRemove(chooser, out var pending) || pending.FightId != fight.FightId) return;
            if (place < 0 || place >= pending.Shown.Options.Count) place = 0;
            var option = pending.Shown.Options[place];
            var who = fight.Buscar(chooser);
            if (who == null || !who.IsAlive) return;

            if (option.RewardType != CastsSpellLevel || !int.TryParse(option.RewardValue, out int levelId))
            {
                Program.LogDebug($"[Fight] Choice {pending.Shown.Id}, option {option.Id}: reward type {option.RewardType} is not cast.");
                return;
            }
            var (spell, grade) = Managers.SpellCriteria.SpellOfLevel(levelId);
            if (spell <= 0) return;

            // A guardian's look, which its own appearance (149) does not carry to the client.
            if (who.IncarnatedMonster != 0 && LookOfTheSpells(spell, grade) is { } look) who.IncarnationLook = look;

            Console.WriteLine($"[Fight] {who.Name} chooses option {option.Id} of choice {pending.Shown.Id}: spell {spell}, grade {grade}.");
            await CastAsAttitudeAsync(stream, fight, who, spell, grade);
        }

        /// <summary>
        /// A spell cast on a fighter by himself and kept as his attitude from now on, as the
        /// attitudes are before the first turn: its rows of the moment applied, the rest waiting on
        /// their triggers.
        /// </summary>
        internal static async Task CastAsAttitudeAsync(NetworkStream stream, FightInstance fight, Fighter quien, int spell, int grade)
        {
            quien.Buffs.PonerActitud(spell, grade, givenInRound: fight.RoundNumber);
            int nivelId = LimitesDeGrado(spell, grade).LevelId;

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(quien.Id, Network.FightProtocol.SpellSequence)));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(
                    quien.Id, Network.FightProtocol.Cast,
                    Network.FightProtocol.CastAt(quien.Id, quien.Id, quien.CellId, spell, nivelId, critical: false),
                    Network.FightProtocol.CastDetail)));
            await AplicarEfectosAsync(stream, fight, quien, spell, grade, quien,
                                      Managers.EffectEngine.AlLanzar, quien.CellId, armar: false);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                       Network.FightProtocol.SpellSequence)));
        }
    }
}
