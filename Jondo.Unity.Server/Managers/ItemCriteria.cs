using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// An item's criterion -- the «criterions» of its data -- judged for whoever uses it, with the
    /// same reader the quests and the achievements use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The travel potions ask for little: «Po!45» (not in area 45, Incarnam), «PZ=1» (subscribed),
    /// «PL&gt;39» (above level 39), «Qf=2442» (a quest finished) and an ankla's «OA=1088» (an
    /// achievement). The scrolls and the breads that raise a characteristic for good ask how many
    /// points the scrolls have already given it: «ca&lt;25» is fewer than 25 in agility, the small
    /// scroll's bound, so whoever has more needs the medium one («ca&lt;50»), the large («&lt;80»)
    /// or the powerful («&lt;100»). The reader knows PL, the quests and OA; this adds the rest.
    /// </para>
    /// <para>
    /// The candies ask for their effect not to be there yet: «PO!1723», not holding the boost food
    /// they give, and «HA!586» not having the alteration. The reader counts the items; this answers
    /// the alterations.
    /// </para>
    /// <para>
    /// What nobody answers is let through and named, as everywhere else: «Pc=11» on the class
    /// elixirs is not even in the client's own list of criteria (it would say «unknown or unused»),
    /// so letting it pass is what the client does too.
    /// </para>
    /// </remarks>
    public static class ItemCriteria
    {
        /// <summary>Whether the user meets an item's criterion. An empty one is always met.</summary>
        public static bool Holds(string criterion)
            => string.IsNullOrWhiteSpace(criterion) || Judge(criterion, Lenient.None);

        /// <summary>
        /// Whether what a failing criterion lacks is an achievement: the same criterion, judged as if
        /// the user had every achievement, holds.
        /// </summary>
        public static bool LacksOnlyAchievements(string criterion)
            => !Holds(criterion) && Judge(criterion, Lenient.Achievements);

        /// <summary>
        /// The refusal a failing criterion is answered with, an info message of type 1: «Logro
        /// necesario» (371) when an achievement is what is missing, as the real server answers an
        /// ankla; «Tus características no te lo permiten.» (19) when it is the characteristics, as
        /// it answers a small agility scroll on a character past 25 («intentar usar pergamino
        /// agilidad-rechazado por tener ya mas base», lqn { f1: 1, f2: 19 }); and «No cumples los
        /// requisitos.» (1) otherwise, which no capture shows.
        /// </summary>
        public static int RefusalOf(string criterion)
        {
            if (Judge(criterion, Lenient.Achievements)) return InfoMessages.AchievementNeeded;
            if (Judge(criterion, Lenient.Characteristics)) return InfoMessages.CharacteristicsForbid;
            return InfoMessages.RequirementsNotMet;
        }

        /// <summary>What a judgement takes as met whatever the user has.</summary>
        private enum Lenient { None, Achievements, Characteristics }

        private static bool Judge(string criterion, Lenient lenient)
            => QuestCriterion.Judge(criterion, new UserFacts(lenient)).Met;

        /// <summary>
        /// The points the scrolls have given a characteristic, by the letter its criterion uses:
        /// a agility, c chance, i intelligence, s strength, v vitality, w wisdom.
        /// </summary>
        internal static long? ScrolledBy(string op, SessionState state) => op switch
        {
            "ca" => state.ScrolledAgility,
            "cc" => state.ScrolledChance,
            "ci" => state.ScrolledIntelligence,
            "cs" => state.ScrolledStrength,
            "cv" => state.ScrolledVitality,
            "cw" => state.ScrolledWisdom,
            _ => null,
        };

        /// <summary>
        /// Whether whoever is in session has a spell: a special one learnt, itself or its pair, or one
        /// of his class's at his level, either half of its pair.
        /// </summary>
        private static bool KnowsSpell(int spell)
        {
            var learned = LearnedSpells.OfSession();
            if (LearnedSpells.Holds(learned, spell)) return true;
            var state = SessionContext.State;
            foreach (var known in SpellTable.KnownFor(state.Breed, state.CharacterLevel, SpellChoices.Chosen, learned))
            {
                if (known.SpellId == spell || SpellTable.PairOf(known.SpellId)?.Holds(spell) == true) return true;
            }
            return false;
        }

        /// <summary>
        /// The user as an item's criterion asks about him: the character the achievement engine
        /// knows, his achievements, his subscription, the area he stands in and his scrolls.
        /// </summary>
        private sealed class UserFacts : IQuestFacts
        {
            private static readonly IQuestFacts Character = new Achievements.CharacterFacts();
            private readonly Lenient _lenient;

            public UserFacts(Lenient lenient) => _lenient = lenient;

            private static SessionState S => SessionContext.State;

            public int Level => Character.Level;
            public long MapId => Character.MapId;
            public bool Finished(int questId) => Character.Finished(questId);
            public bool Active(int questId) => Character.Active(questId);
            public bool ObjectiveDone(int objectiveId) => Character.ObjectiveDone(objectiveId);

            public bool AchievementDone(int achievementId)
                => _lenient == Lenient.Achievements || (S.Achievements?.Has(achievementId) ?? false);

            public long? Scalar(string op)
            {
                // Leniency leaves the term unanswered, which the reader lets through.
                long? scrolled = ScrolledBy(op, S);
                if (scrolled != null) return _lenient == Lenient.Characteristics ? null : scrolled;

                return op switch
                {
                    // Every account is subscribed here: its end date is a year away (Subscription).
                    "PZ" => 1,

                    // The area of the subarea he stands in: 45 is Incarnam, where no potion works.
                    "Po" => Interactives.AreaOfSubArea(DatabaseManager.SubAreaOfMap(S.MapId)),

                    _ => Character.Scalar(op),
                };
            }

            public long? Count(string op, long key, string flag) => op switch
            {
                // An alteration had: the mercenary's chew asks «HA!129», not while its own alteration
                // is on, which is what keeps a dragobone or an elixir from being taken twice.
                "HA" => Alterations.Has(S.CharacterId, (int)key) ? 1 : 0,

                // A spell known: «PT!24006&PT!350» on Llamita's scroll, neither of its pair yet; and
                // the soul stones' «PT=413», Captura de Almas.
                "PT" => KnowsSpell((int)key) ? 1 : 0,

                // An item worn: «POE15990,1», the rikiki wand in hand.
                "POE" => Equipment.HowManyWorn((int)key),
                _ => Character.Count(op, key, flag),
            };
        }
    }
}
