using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The heroes in their master's fights (Managers.Heroes): they come in beside him, his client
    /// plays them in their turns, and what they win is theirs.
    /// </summary>
    /// <remarks>
    /// Each hero is built as a player is, from his own sheet -- inside his session with no socket,
    /// where BuildPlayerFighter reads him and not his master --, and handed to his master as a
    /// played ally: his bar, his own as he set it, and his turns go to the master's client.
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>Each hero's bar in his fight, as he would fight with it himself.</summary>
        private static readonly ConcurrentDictionary<long, FightSpellLayout.Layout> _heroBars = new();

        /// <summary>
        /// The master's connected heroes into the fight he is opening, on his side, each with his
        /// own sheet, ready from the start. One who is in a fight already stays out of this one.
        /// </summary>
        internal static void AddHeroes(FightInstance fight, long masterId)
        {
            if (!Heroes.On) return;
            foreach (var hero in Heroes.Of(masterId))
            {
                var state = hero.Session.State;
                if (state.IsInFight) continue;
                // No more than the fight's side takes (a temporal anomaly's four): the rest stay out.
                if (fight.Aliados(masterId).Count(f => !f.IsMonster && !f.EsInvocado) >= CapOf(fight)) break;
                Fighter fighter;
                FightSpellLayout.Layout layout;
                using (SessionContext.Push(hero.Session))
                {
                    // The bar first: drawing it writes the level it was given, and one never written
                    // is only filled whole the first time. Kept then, as a player's first bar is.
                    layout = FightSpellLayout.ForFight(state.Breed, state.CharacterLevel, hero.Session.AccountId);
                    SpellChoices.RememberBar(layout.Bar.Where(b => b.Spell != Network.FightProtocol.HechizoCuerpoACuerpo));
                    fighter = BuildPlayerFighter(fight);
                    state.IsInFight = true;
                    state.FightId = fight.FightId;
                }
                // Off the map he stood on beside his master: he is in the fight now.
                try { HeroPresence.LeaveAsync(hero).GetAwaiter().GetResult(); } catch { }
                fighter.IsReady = true;
                PlayAsHero(fighter, masterId);
                fighter.HechizosDeInvocado = layout.Spells.ToList();
                _heroBars[fighter.Id] = layout;
                fight.AddPlayer(fighter);
                Program.LogDebug($"[Heroes] {state.CharacterName} ({fighter.Id}) comes into fight #{fight.FightId} beside {masterId}.");
            }
        }

        /// <summary>
        /// A hero's own bar for his master's client, or null for an ally who is no hero. Packed:
        /// the weapon on slot 0 and his spells after it in his own order, with no gaps -- the
        /// master's client does not turn the pages of a bar that is not its character's, so what
        /// is past the first page cannot be reached.
        /// </summary>
        private static byte[]? HeroBar(Fighter played, long me)
        {
            if (!_heroBars.TryGetValue(played.Id, out var layout)) return null;
            var packed = new List<(int Slot, int Spell)>();
            if (layout.WithWeapon) packed.Add((0, Network.FightProtocol.HechizoCuerpoACuerpo));
            foreach (var (_, spell) in layout.Bar.Where(b => b.Spell != Network.FightProtocol.HechizoCuerpoACuerpo).OrderBy(b => b.Slot))
                packed.Add((packed.Count, spell));
            // What he knows and did not put on his bar comes after, so that every spell he has is in hand.
            foreach (var (spell, _) in layout.Spells)
                if (!packed.Any(p => p.Spell == spell)) packed.Add((packed.Count, spell));
            return Network.FightProtocol.BuildSpellBar(played.Id, layout.Spells, packed, layout.WithWeapon, controllerId: me);
        }

        internal static byte[] PlayedAllyBarForTests(Fighter played, long me) => PlayedAllyBar(played, me);

        /// <summary>
        /// A hero's sheet as he would fight with it -- life, points, characteristics, what his gear
        /// gives -- read in his own session: the Heroes mod's characteristics window.
        /// </summary>
        internal static Fighter SheetOf(GameSession hero)
        {
            using (SessionContext.Push(hero)) return SheetFromState();
        }

        /// <summary>
        /// The items a master's group won, all to the one he named its loot recipient (Heroes.LootTo)
        /// when that one is among the winners: each keeps his experience and his kamas, the items
        /// -- the perfect pieces with them -- go to one bag. But for what dropped for him alone
        /// (Reward.Own), a quest's item: the fight then ticks his objective, its row's criterion no
        /// longer lets it fall, and in another's bag it left him with a quest he could not finish.
        /// </summary>
        internal static void GatherHeroLoot(Dictionary<long, Reward> plan)
        {
            var masters = plan.Keys.Select(id => Heroes.AsHero(id)?.MasterId ?? id).Distinct().ToList();
            foreach (long master in masters)
            {
                long to = Heroes.LootTo(master);
                if (to == 0 || !plan.TryGetValue(to, out var recipient)) continue;
                foreach (var (id, reward) in plan)
                {
                    if (id == to || (id != master && Heroes.AsHero(id)?.MasterId != master)) continue;
                    var kept = new Dictionary<int, int>();
                    foreach (var (gid, count) in reward.Loot)
                    {
                        int own = Math.Min(count, reward.Own.TryGetValue(gid, out int mine) ? mine : 0);
                        if (own > 0) kept[gid] = own;
                        if (count - own > 0)
                            recipient.Loot[gid] = (recipient.Loot.TryGetValue(gid, out int had) ? had : 0) + count - own;
                    }
                    recipient.Perfect.AddRange(reward.Perfect);
                    reward.Loot = kept;
                    reward.Perfect = new List<RandomLoot.Drop>();
                }
            }
        }

        /// <summary>A winner's session to pay: his own, or a hero's.</summary>
        private static GameSession? SessionToPay(long characterId)
            => SessionRegistry.FindByCharacter(characterId) ?? Heroes.SessionOf(characterId);

        /// <summary>
        /// The end of a fight for its heroes, who have no screen to be shown it on: what each won
        /// -- his share, planned with his master's (PlanRewards) and shown in his master's end
        /// screen -- into his own sheet and bag, and he stands where his master does, saved.
        /// </summary>
        private static void HeroesFightOver(FightInstance fight)
        {
            foreach (var fighter in fight.Azul.Concat(fight.Rojo).ToList())
            {
                _heroBars.TryRemove(fighter.Id, out _);
                var hero = Heroes.AsHero(fighter.Id);
                if (hero == null || hero.Session.State.FightId != fight.FightId) continue;
                try
                {
                    using (SessionContext.Push(hero.Session))
                    {
                        var reward = RewardOf(fight, fighter.Id);
                        if (reward != null)
                        {
                            if (reward.Xp > 0) GiveHeroExperience(reward.Xp);
                            if (reward.Kamas > 0) GameState.Kamas += reward.Kamas;
                            if (reward.Loot.Count > 0)
                                EntregarBotin(GuildRaidManager.ForTheBag(GameState.CharacterId, reward.Loot), out _, reward.Perfect);
                        }
                        // What the achievements count, in his own tallies as in his master's: looked
                        // at when he is at his side again (Achievements.ForHeroAsync).
                        AchievementWatcher.FightEnded(fight, fight.HaGanado(fighter.Id));
                        // And the boss challenges met, done for him too: not imposed on him again.
                        ChallengeWatcher.RecordDoneFor(fight, fighter.Id);
                        GameState.IsInFight = false;
                        hero.Session.State.FightId = 0;
                        bool won = fight.SigueVivo(fighter.TeamId);
                        if (fight.Reglas.AvanzaDeSala && (won || DefeatCostsIn(fight)))
                            DungeonHandler.RecordFightProgress(fighter.Id, fight.RoleplayMapId, won);
                        var master = SessionRegistry.FindByCharacter(hero.MasterId);
                        if (master != null)
                        {
                            GameState.MapId = fight.RoleplayMapId;
                            GameState.CellId = master.State.CellId;
                        }
                        DatabaseManager.SaveCurrentCharacter();
                        Program.LogDebug($"[Heroes] {GameState.CharacterName} out of fight #{fight.FightId}: " +
                                         $"{reward?.Xp ?? 0} xp, {reward?.Kamas ?? 0} kamas, {reward?.Loot.Sum(l => l.Value) ?? 0} item(s).");
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Heroes] Hero {fighter.Id}'s end of fight #{fight.FightId} failed: {ex.Message}");
                }
            }
        }

        /// <summary>A hero's experience, and the levels it brings: five points each up to 200, as a player's.</summary>
        private static void GiveHeroExperience(long xp)
        {
            GameState.Experience += xp;
            int newLevel = ExperienceTable.LevelForXp(GameState.Experience);
            if (newLevel <= GameState.CharacterLevel) return;
            int upToTwoHundred = Math.Max(0, Math.Min(newLevel, MaxLevelWithPoints) - Math.Min(GameState.CharacterLevel, MaxLevelWithPoints));
            GameState.CharacterRemainingPoints += upToTwoHundred * 5;
            Program.LogDebug($"[Heroes] {GameState.CharacterName} levels up: {GameState.CharacterLevel} -> {newLevel}.");
            GameState.CharacterLevel = newLevel;
        }
    }
}
