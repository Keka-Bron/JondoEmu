using System.Runtime.CompilerServices;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers;

public static partial class FightHandler
{
    private sealed record ControlledBar(FightSpellLayout.Layout Layout, IReadOnlyList<(int Slot, int Spell)> Slots);

    private static readonly ConditionalWeakTable<Fighter, ControlledBar> _controlledBars = new();

    private static void AdmitOpeningParticipants(FightInstance fight, GameSession opener,
        IFightParticipantPolicy policy, IReadOnlyList<ControlledFightParticipant> participants)
    {
        int places = CapOf(fight) - fight.PeopleIn(FightInstance.Azules);
        foreach (var participant in participants.Take(places))
        {
            if (participant.Session is not GameSession session || session.State.IsInFight
                || session.HasTransport || session.AccountId != opener.AccountId
                || participant.ControllerId != opener.CharacterId || session.CharacterId <= 0
                || fight.Buscar(session.CharacterId) != null)
                continue;

            Fighter fighter;
            FightSpellLayout.Layout layout;
            IReadOnlyList<(int Slot, int Spell)> bar;
            using (SessionContext.Push(session))
            {
                var state = session.State;
                layout = FightSpellLayout.ForFight(state.Breed, state.CharacterLevel, session.AccountId);
                SpellChoices.RememberBar(layout.Bar.Where(slot => slot.Spell != FightProtocol.HechizoCuerpoACuerpo));
                bar = policy.ArrangeControlledBar(FightProtocol.HechizoCuerpoACuerpo,
                    layout.WithWeapon, layout.Bar, layout.Spells);
                fighter = BuildPlayerFighter(fight);
                state.IsInFight = true;
                state.FightId = fight.FightId;
            }

            try
            {
                if (session.MapId > 0)
                    MapPresenceService.Live.RemoveAsync(SessionRegistry.OnMap(session.MapId), fighter.Id).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Fight] Could not remove participant {fighter.Id} from the map: {ex.Message}");
            }

            fighter.IsReady = true;
            AssignAllyController(fighter, participant.ControllerId);
            fighter.HechizosDeInvocado = layout.Spells.ToList();
            _controlledBars.Add(fighter, new(layout, bar));
            fight.AddPlayer(fighter);
        }
    }

    internal static Fighter SheetOf(GameSession session)
    {
        using (SessionContext.Push(session)) return SheetFromState();
    }

    private static GameSession? SessionToPay(long characterId)
        => SessionRegistry.FindByCharacter(characterId)
           ?? FightParticipants.Policy?.FindParticipant(characterId)?.Session as GameSession;

    private static void SettleControlledParticipants(FightInstance fight)
    {
        var policy = FightParticipants.Policy;
        if (policy == null) return;
        foreach (var fighter in fight.Azul.Concat(fight.Rojo).ToList())
        {
            if (policy.FindParticipant(fighter.Id) is not { } participant
                || participant.Session is not GameSession session || session.State.FightId != fight.FightId)
                continue;

            try
            {
                using var context = SessionContext.Push(session);
                var reward = RewardOf(fight, fighter.Id);
                if (reward != null)
                {
                    if (reward.Xp > 0) GiveControlledParticipantExperience(reward.Xp);
                    if (reward.Kamas > 0) GameState.Kamas += reward.Kamas;
                    if (reward.Loot.Count > 0)
                        EntregarBotin(GuildRaidManager.ForTheBag(fighter.Id, reward.Loot), out _, reward.Perfect);
                }
                AchievementWatcher.FightEnded(fight, fight.HaGanado(fighter.Id));
                ChallengeWatcher.RecordDoneFor(fight, fighter.Id);
                session.State.IsInFight = false;
                session.State.FightId = 0;
                bool won = fight.SigueVivo(fighter.TeamId);
                if (fight.Reglas.AvanzaDeSala && (won || DefeatCostsIn(fight)))
                    DungeonHandler.RecordFightProgress(fighter.Id, fight.RoleplayMapId, won);
                if (SessionRegistry.FindByCharacter(participant.ControllerId) is { } controller)
                {
                    session.State.MapId = fight.RoleplayMapId;
                    session.State.CellId = controller.State.CellId;
                }
                DatabaseManager.SaveCurrentCharacter();
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Fight] Participant {fighter.Id}'s settlement for #{fight.FightId} failed: {ex.Message}");
            }
        }
    }

    private static void GiveControlledParticipantExperience(long xp)
    {
        GameState.Experience += xp;
        int newLevel = ExperienceTable.LevelForXp(GameState.Experience);
        if (newLevel <= GameState.CharacterLevel) return;
        int levelsWithPoints = Math.Max(0, Math.Min(newLevel, MaxLevelWithPoints) - Math.Min(GameState.CharacterLevel, MaxLevelWithPoints));
        GameState.CharacterRemainingPoints += levelsWithPoints * 5;
        GameState.CharacterLevel = newLevel;
    }
}
