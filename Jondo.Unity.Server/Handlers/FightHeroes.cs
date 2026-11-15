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
                fighter.IsReady = true;
                PlayAsHero(fighter, masterId);
                fighter.HechizosDeInvocado = layout.Spells.ToList();
                _heroBars[fighter.Id] = layout;
                fight.AddPlayer(fighter);
                Program.LogDebug($"[Heroes] {state.CharacterName} ({fighter.Id}) comes into fight #{fight.FightId} beside {masterId}.");
            }
        }

        /// <summary>A hero's own bar for his master's client, or null for an ally who is no hero.</summary>
        private static byte[]? HeroBar(Fighter played, long me)
            => _heroBars.TryGetValue(played.Id, out var layout)
                ? Network.FightProtocol.BuildSpellBar(played.Id, layout.Spells, layout.Bar, layout.WithWeapon, controllerId: me)
                : null;

        internal static byte[] PlayedAllyBarForTests(Fighter played, long me) => PlayedAllyBar(played, me);

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
                        GameState.IsInFight = false;
                        hero.Session.State.FightId = 0;
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
