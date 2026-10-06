using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.World.Fights
{
    public class Fighter
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public int TeamId { get; set; } // 0 = Team Blue (Players), 1 = Team Red (Monsters)
        public int CellId { get; set; }
        public bool IsMonster { get; set; }
        public int MonsterId { get; set; }
        public int GradeIndex { get; set; } = 0;

        /// <summary>
        /// A character's class, the breed of its record: what the masks' B and b ask. Zero for a
        /// monster or a summon, which are of no class.
        /// </summary>
        public int Breed { get; set; }

        public int Level { get; set; }
        public int LookBoneId { get; set; }
        public string Look { get; set; } = "";

        // Combat Stats
        public int MaxHP { get; set; }
        public int CurrentHP { get; set; }
        public int MaxAP { get; set; }
        public int CurrentAP { get; set; }
        public int MaxMP { get; set; }
        public int CurrentMP { get; set; }
        public int Initiative { get; set; }

        /// <summary>Experience this fighter awards on death (gradeXp from its record).</summary>
        public int XpReward { get; set; }

        // Elemental Stats
        public int Strength { get; set; }
        public int Intelligence { get; set; }
        public int Chance { get; set; }
        public int Agility { get; set; }
        public int Power { get; set; }

        /// <summary>Vitality, base plus equipment. It goes in the sheet; life comes from MaxHP.</summary>
        public int Vitality { get; set; }

        /// <summary>Critical points granted by the equipment, added on top of the spell's own.</summary>
        public int CriticalBonus { get; set; }

        /// <summary>
        /// The damage it deals, in percent, applied last to every blow: 105 with a dream's "5%
        /// damage". "All the bonuses add up first and are multiplied last by the % of damage."
        /// </summary>
        public int DamageDealtPercent { get; set; } = 100;

        /// <summary>Rounds taken off the cooldown of every spell it casts: a dream's "-1 reactivation".</summary>
        public int CooldownReduction { get; set; }

        /// <summary>Casts more on the same target for every spell that caps them: a dream's "+1 cast per target".</summary>
        public int ExtraCastsPerTarget { get; set; }

        /// <summary>
        /// General fixed damage (characteristic 16). It is added at the end of the calculation, after
        /// multiplying by the elemental characteristic and the power.
        /// </summary>
        public int FlatDamage { get; set; }

        /// <summary>
        /// Critical damage (characteristic 86). It is only added when the hit is critical, and it goes
        /// where the fixed damage goes: at the end, not multiplied.
        /// </summary>
        public int CriticalDamage { get; set; }

        /// <summary>
        /// Fixed damage of each element (characteristics 88 to 92: earth, fire, water, air and
        /// neutral). Only that of the element hit with counts.
        /// </summary>
        public int EarthDamage { get; set; }
        public int FireDamage { get; set; }
        public int WaterDamage { get; set; }
        public int AirDamage { get; set; }
        public int NeutralDamage { get; set; }

        /// <summary>
        /// Push damage (characteristic 84) and range (19). The client asks for them to draw the
        /// preview: the displacement one comes from the push and the one of where one can throw,
        /// from the range.
        /// </summary>
        public int PushDamage { get; set; }
        public int Range { get; set; }

        /// <summary>
        /// Everything else on the sheet, by characteristic number: flee, tackle, dodges,
        /// percentage resistances, summons...
        ///
        /// It goes in a dictionary and not in a field for each because there are thirty-odd, they do not
        /// take part in any of the server's calculations and all that is needed is to send them. The
        /// day one of them is used for something —flee against tackle, for example— it is given its
        /// field and taken out of here.
        /// </summary>
        public Dictionary<int, int> Otras { get; } = new Dictionary<int, int>();

        public int Otra(int caracteristica)
            => Otras.TryGetValue(caracteristica, out int valor) ? valor : 0;

        /// <summary>The fixed damage of the element being hit with.</summary>
        public int GetFlatDamageForElement(ElementType element)
        {
            return element switch
            {
                ElementType.Earth => EarthDamage,
                ElementType.Fire => FireDamage,
                ElementType.Water => WaterDamage,
                ElementType.Air => AirDamage,
                ElementType.Neutral => NeutralDamage,
                _ => 0
            };
        }

        // Resistances (% and Flat)
        public int NeutralResPct { get; set; }
        public int EarthResPct { get; set; }
        public int FireResPct { get; set; }
        public int WaterResPct { get; set; }
        public int AirResPct { get; set; }

        public bool IsAlive => CurrentHP > 0;
        public bool IsReady { get; set; }

        /// <summary>
        /// A Koliseo JondoBot: shown as a character of its class, played by the server's tactics.
        /// It has no session and no row in Characters, so its look and sex travel here.
        /// </summary>
        public bool IsBot { get; set; }

        /// <summary>
        /// Where the other side last saw him while he is invisible: the cell he went invisible
        /// on, then each one he casts from. -1 before he ever was. What a JondoBot aims at when
        /// it cannot see him.
        /// </summary>
        public int LastSeenCell { get; set; } = -1;

        /// <summary>
        /// The tenths of a second this fighter kept from the turn he passed, for his next one
        /// (FightProtocol.SavedAfter). Only a character keeps any.
        /// </summary>
        public int SavedTurnTime { get; set; }

        /// <summary>A bot's sex, for its identity (a character reads it from his row).</summary>
        public int Sex { get; set; }

        /// <summary>A bot's look, built once when it is made.</summary>
        public byte[]? BotLook { get; set; }

        // Spells available to this fighter
        public List<int> SpellIds { get; set; } = new List<int>();

        /// <summary>Grade of each spell (monsters do not always have it at level 1).</summary>
        public Dictionary<int, int> SpellGrades { get; set; } = new Dictionary<int, int>();

        public int AccumulatedMpLoss { get; set; } = 0;
        public int AccumulatedApLoss { get; set; } = 0;

        /// <summary>
        /// Whether its template lets it tackle: the <c>CanTackle</c> bit (128) of a monster's or a
        /// summon's <c>m_flags</c>. People always can. The training dummies are among the 358
        /// templates without it, and walking away from one never costs a point in the class
        /// captures -- 26 walks out of their contact. See <see cref="Tackle"/>.
        /// </summary>
        public bool TemplateAllowsTackle { get; set; } = true;

        // ─── What limits the casts ──────────────────────────────────────────────

        /// <summary>
        /// The rounds each spell has left before it can be cast again.
        ///
        /// A key that goes in here is NOT DELETED: it goes down to zero and stays. It is what the
        /// real server does, whose jxc keeps naming the spells with a zero round after round.
        /// </summary>
        public Dictionary<int, int> Recarga { get; } = new Dictionary<int, int>();

        /// <summary>Times each spell has been cast in THIS turn.</summary>
        public Dictionary<int, int> LanzadosEsteTurno { get; } = new Dictionary<int, int>();

        /// <summary>Times each spell has been cast on each target in THIS turn.</summary>
        public Dictionary<(int Hechizo, long Objetivo), int> LanzadosPorObjetivo { get; }
            = new Dictionary<(int, long), int>();

        // ─── The summons ────────────────────────────────────────────────────────

        /// <summary>
        /// Whose it is, if someone summoned this one. Zero when it is not.
        ///
        /// A Cra beacon, a glyph or a trap are NOT buffs: they are fighters with their
        /// negative identifier, their cell, their side, their sheet and their turn. In the captures the
        /// server sends them with the same mould as a monster and then they get to play.
        /// </summary>
        public long Invocador { get; set; }

        public bool EsInvocado => Invocador != 0;

        /// <summary>
        /// The spell it behaves with, its grade's <c>startingSpellId</c> in the creature
        /// table. It is what gives it its behaviour: its effects are 792 hooks —"at the start of
        /// my turn cast my grade 2"— just like the attitudes the dofus give away.
        /// </summary>
        public int HechizoPropio { get; set; }

        /// <summary>
        /// Whether it gets a turn in the carousel.
        ///
        /// Not all summons play. Measured in the captures: the Baliza de Supervivencia
        /// receives its jzc with clock 150 right behind its Cra and closes it on the spot with a jyt;
        /// the Baliza Táctica does NOT receive a single one in the whole fight. The difference is in its
        /// spell: the first has a turn-start hook and the second only reacts
        /// to damage and pushes, so it has nothing to do when its turn would come.
        /// </summary>
        public bool JuegaTurno { get; set; } = true;

        /// <summary>
        /// The spells a summon casts, each at its grade, for the jyy of whoever controls it.
        /// Empty for anybody who is not a summon.
        /// </summary>
        public IReadOnlyList<(int Spell, int Grade)> HechizosDeInvocado { get; set; }
            = System.Array.Empty<(int, int)>();

        /// <summary>
        /// Whether <paramref name="characterId"/> plays this fighter: himself, or a summon of
        /// his that is his to play. The real server sends the owner a jyj when such a summon's
        /// turn comes, and the owner's jrw and jwh then move and cast it.
        /// </summary>
        public bool ControlledBy(long characterId)
            => Id == characterId || (EsInvocado && Invocador == characterId && !PlaysOnItsOwn);

        /// <summary>
        /// A summon with NOTHING TO PLAY: no step to take and no spell of its own. Its turn is
        /// its start-of-turn triggers and then the turn handed on, and nobody gets a jyj for
        /// it. Every other summon is its owner's to play, by hand: the Tymobot (jyj on its
        /// jzc, then the owner's jrw and jwh, in its capture), the Bomba Ambulante, the
        /// Osamodas' animals, the Enutrof's chests. What never gets one in the captures is what
        /// cannot act: both beacons (no MP, no spells), the Xelor's dials, the Pandawa's
        /// barrel. It was the owner's to play here too, which is what put a "pass turn" button
        /// on the beacon's fifteen seconds.
        /// </summary>
        /// <remarks>
        /// A live 2027 row, "Toma el control de la entidad", makes it its owner's whatever it
        /// has to play. Every summon with a step or a spell is his already -- the fight has no
        /// hand of its own for them -- so the row only matters to one that has neither. On
        /// somebody else's fighter it is not honoured: no class spell lays it there.
        /// </remarks>
        public bool PlaysOnItsOwn => EsInvocado && MaxMP <= 0 && HechizosDeInvocado.Count == 0 && !ControlTaken;

        /// <summary>
        /// The character this fighter is the double of (effect 180), or zero: his look and his
        /// identity are what the double shows.
        /// </summary>
        public long DoubleOf { get; set; }

        /// <summary>
        /// The facing of his last walk, or minus one before he has walked: what one who follows
        /// him (2184) ends up facing -- the f2 of the follower's jsj is the one of the leader's
        /// last jsj in both follows of the Osamodas capture, frames 2091/2125 and 2133/2140.
        /// </summary>
        public int LastFacing { get; set; } = -1;

        /// <summary>Whether a live 2027 row hands this fighter to its owner.</summary>
        public bool ControlTaken
            => Buffs.Puestos.Any(b => b.EffectId == Jondo.Unity.World.Combat.EffectSupport.TakesControl && !b.Pendiente);

        /// <summary>Who carries this fighter (effect 50), or zero. A carried fighter shares the carrier's cell and holds no cell of his own.</summary>
        public long CarriedBy { get; set; }

        /// <summary>Whom this fighter carries, or zero.</summary>
        public long Carrying { get; set; }

        public bool EstaCargado => CarriedBy != 0;

        /// <summary>
        /// A copy left by Tymadura: it holds a cell and can tackle, plays no turn, sits in no
        /// carousel, and goes with the first point of damage, with the whole set when its owner
        /// is hit, and at its owner's next turn in any case.
        /// </summary>
        public bool EsIlusion { get; set; }

        /// <summary>
        /// Set while the blow that finishes him is being resolved, so that what goes off on
        /// his death -- his attitudes, the spells hooked on him with an X trigger -- fires once,
        /// with him still standing, and not again from whatever it sets off.
        /// </summary>
        public bool Muriendo { get; set; }

        /// <summary>The copies this fighter has out, by id. Empty for everybody else.</summary>
        public List<long> Ilusiones { get; } = new List<long>();

        /// <summary>
        /// Whether his own side still sees him drawn as hidden -- the visibility switch that
        /// goes with the copies -- so that the switch back can be sent even after the last
        /// copy has gone on its own.
        /// </summary>
        public bool HiddenAmongCopies { get; set; }

        /// <summary>
        /// How much of its summoner's capacity this fighter takes up, copied from the template's
        /// <c>summonCost</c> when it is summoned. Zero means it is free: the Rogue's bombs, the
        /// Ocra's beacons and 483 other templates cost nothing at all, and a couple of the
        /// Osamodas' big summons cost two or three on their own.
        ///
        /// Meaningless on anyone who was not summoned, and left at one there so a fighter that
        /// somehow reaches the counter without a template still occupies a slot rather than
        /// silently occupying none.
        /// </summary>
        public int SummonCost { get; set; } = 1;

        /// <summary>
        /// What it carries on it: buffs, states and the attitudes its items give it.
        /// </summary>
        public Buffs Buffs { get; } = new Buffs();

        /// <summary>
        /// Whether it has been hit since its previous turn. The attitudes' "DBE" trigger looks at it,
        /// which is where the Ochre Dofus's rule comes from.
        /// </summary>
        public bool LeHanPegado { get; set; }

        /// <summary>
        /// Temporary bonus to the base damage of a specific spell, together with the round it
        /// expires on. Frozen Arrow, for instance, leaves +4 base damage for 3 turns, and recasting
        /// it refreshes the deadline instead of stacking again (max stack 1).
        /// </summary>
        public Dictionary<int, (int Bonus, int ExpiresRound)> SpellDamageBuffs { get; }
            = new Dictionary<int, (int, int)>();

        public void ApplySpellDamageBuff(int spellId, int bonus, int duration, int currentRound)
        {
            SpellDamageBuffs[spellId] = (bonus, currentRound + duration);
        }

        public int GetSpellDamageBonus(int spellId, int currentRound)
        {
            if (!SpellDamageBuffs.TryGetValue(spellId, out var b)) return 0;
            if (currentRound >= b.ExpiresRound)
            {
                SpellDamageBuffs.Remove(spellId);
                return 0;
            }
            return b.Bonus;
        }

        /// <summary>
        /// The points for a new turn: the maximum plus whatever the live buffs say. A buff of
        /// points changes the current ones the moment it lands, and from then on it is the
        /// turn start that carries it -- "+1 PA durante 3 turnos" is one more on each of those
        /// turns, and "-2 PA" put on somebody before his turn is two fewer when it starts.
        /// Without the buffs here, both ended with the turn they were cast in.
        /// </summary>
        public void StartTurn(int ronda)
        {
            CurrentAP = Math.Max(0, MaxAP + Buffs.De(CaracteristicaDePuntosDeAccion, ronda));
            CurrentMP = Math.Max(0, MaxMP + Buffs.De(CaracteristicaDePuntosDeMovimiento, ronda));
            AccumulatedMpLoss = 0;
            AccumulatedApLoss = 0;
        }

        /// <summary>The catalogue's numbers for the two kinds of points.</summary>
        public const int CaracteristicaDePuntosDeAccion = 1;
        public const int CaracteristicaDePuntosDeMovimiento = 23;

        /// <summary>Where it was before the last movement. Minus one if it has not moved.</summary>
        /// <remarks>
        /// Effect 1100 asks for it, «teletransporta a la posición anterior», which undoes the last
        /// displacement. Without this memory the effect has nowhere to send anyone back to, and sending
        /// them to some random place would be worse than doing nothing.
        /// </remarks>
        public int CasillaAnterior { get; private set; } = -1;

        /// <summary>
        /// A monster's behaviour spell -- its grade's startingSpellId -- cast when the fight
        /// begins, or when it joins one. Its triggered rows are armed for good on the fighters they
        /// name. (0, 0) for everybody else.
        /// </summary>
        public (int Spell, int Grade) Conducta { get; set; }

        /// <summary>
        /// Where the fighter stood when his last turn began: effect 1099, "Teletransporta a la
        /// posición de inicio de turno", sends him back there. Minus one before his first turn.
        /// </summary>
        public int CasillaAlEmpezarTurno { get; set; } = -1;

        /// <summary>Where he stood when the fight began: effect 784 sends him back there.</summary>
        public int CasillaAlEmpezarCombate { get; set; } = -1;

        /// <summary>Moves the fighter and remembers where it was.</summary>
        public void MoverA(int casilla)
        {
            if (casilla == CellId) return;
            CasillaAnterior = CellId;
            CellId = casilla;
        }

        public void TakeDamage(int damage)
        {
            CurrentHP = Math.Max(0, CurrentHP - damage);
        }

        // ─── The shield ─────────────────────────────────────────────────────────

        /// <summary>
        /// Shield points: they are spent before life and are not healed.
        /// </summary>
        /// <remarks>
        /// Two families of effects set them, 401 spells between the two: 1020, which gives a
        /// percentage of the caster's LEVEL, and 1039, a percentage of his LIFE.
        /// The client's catalogue declares them without a characteristic, so they do not come out of the
        /// boosts' generic path: they are needed here.
        ///
        /// It is not life: it is not healed, it does not count for death and it disappears when it expires. Putting it
        /// in CurrentHP would have been shorter and would have left a shielded character healing
        /// up to the shield's cap.
        /// </remarks>
        public int PuntosDeEscudo { get; private set; }

        /// <summary>The round in which the shield drops. Zero when there is no shield.</summary>
        public int EscudoCaducaEnRonda { get; private set; }

        /// <summary>Adds shield. It is added to whatever there was and keeps the furthest expiry.</summary>
        public void Escudar(int cuanto, int caducaEnRonda)
        {
            if (cuanto <= 0) return;

            PuntosDeEscudo += cuanto;
            if (caducaEnRonda > EscudoCaducaEnRonda) EscudoCaducaEnRonda = caducaEnRonda;
        }

        /// <summary>
        /// Takes points off the shield: what a replaced shield row was worth, when a spell that
        /// does not stack is cast again and its old row goes. Never below zero -- a shield
        /// already eaten by blows has nothing left to give back.
        /// </summary>
        public void Desescudar(int cuanto)
        {
            if (cuanto <= 0) return;
            PuntosDeEscudo = Math.Max(0, PuntosDeEscudo - cuanto);
            if (PuntosDeEscudo == 0) EscudoCaducaEnRonda = 0;
        }

        /// <summary>
        /// Puts a hit into the shield first and returns what reaches life.
        /// </summary>
        public int PasarPorElEscudo(int dano)
        {
            if (dano <= 0 || PuntosDeEscudo <= 0) return dano;

            int aguanta = Math.Min(PuntosDeEscudo, dano);
            PuntosDeEscudo -= aguanta;
            return dano - aguanta;
        }

        /// <summary>Removes the shield if its time to drop has come.</summary>
        public void CaducarElEscudo(int ronda)
        {
            if (PuntosDeEscudo <= 0) return;
            if (EscudoCaducaEnRonda == 0 || ronda < EscudoCaducaEnRonda) return;

            PuntosDeEscudo = 0;
            EscudoCaducaEnRonda = 0;
        }

        // ─── Erosion ────────────────────────────────────────────────────────────

        /// <summary>
        /// The MAXIMUM life points lost for good in this fight.
        ///
        /// Each hit does not only take life: it also takes a pinch of the cap. With a thousand life and
        /// a hit of a hundred with fifteen per cent erosion, one ends up at 900/985: the hundred
        /// of damage come out of the current life and fifteen out of the cap.
        ///
        /// It is kept apart from <see cref="MaxHP"/> —which is already going down— because there are effects that
        /// hit BASED ON what is eroded: Represalias's 1092 does twenty per cent of
        /// what one has eroded.
        /// </summary>
        public int VidaErosionada { get; private set; }

        /// <summary>The catalogue's characteristic 75: the percentage that erodes.</summary>
        public const int CaracteristicaDeErosion = 75;

        /// <summary>
        /// The erosion everybody starts with, as a percentage of every hit taken off the maximum.
        /// </summary>
        /// <remarks>
        /// Ten for players and monsters alike. Measured on the Ocra capture, whose sheet carries a
        /// 75 worth 10, and on the damage blocks themselves: 977 of the 986 in the captures carry
        /// an f5, and a hit of 413 on a monster carries 41. The sheet we send to the client
        /// already said ten; the server side said zero, so nobody ever eroded and no hit ever
        /// carried its f5.
        /// </remarks>
        public const int ErosionBase = 10;

        /// <summary>
        /// Erodes for a hit and returns how much cap has been lost.
        ///
        /// <paramref name="porciento"/> is the receiver's erosion, which comes from his
        /// characteristic 75 plus whatever has been put on him.
        /// </summary>
        public int Erosionar(int dano, int porciento)
        {
            if (dano <= 0 || porciento <= 0) return 0;

            int pierde = dano * porciento / 100;
            // It does not erode below half the starting cap, which is where the game
            // cuts it. The starting cap is the current one plus whatever has already been lost.
            int original = MaxHP + VidaErosionada;
            int cabe = Math.Max(0, (original / 2) - VidaErosionada);
            pierde = Math.Min(pierde, cabe);
            if (pierde <= 0) return 0;

            VidaErosionada += pierde;
            MaxHP -= pierde;
            if (CurrentHP > MaxHP) CurrentHP = MaxHP;
            return pierde;
        }

        public int GetStatForElement(ElementType element)
        {
            return element switch
            {
                ElementType.Earth => Strength,
                ElementType.Fire => Intelligence,
                ElementType.Water => Chance,
                ElementType.Air => Agility,
                ElementType.Neutral => Math.Max(Strength, Math.Max(Intelligence, Math.Max(Chance, Agility))),
                _ => 0
            };
        }

        public int GetResPctForElement(ElementType element)
        {
            return element switch
            {
                ElementType.Neutral => NeutralResPct,
                ElementType.Earth => EarthResPct,
                ElementType.Fire => FireResPct,
                ElementType.Water => WaterResPct,
                ElementType.Air => AirResPct,
                _ => 0
            };
        }
    }
}
