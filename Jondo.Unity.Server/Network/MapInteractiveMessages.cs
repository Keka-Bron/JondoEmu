using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Server.Network;

/// <summary>Viewer-specific declarations and their dynamic-state subset.</summary>
internal static class MapInteractiveMessages
{
    internal static (InteractiveElement[] Elements, StatedElement[] States) Snapshot(long mapId, long viewerAccountId)
    {
        var elements = new List<InteractiveElement>();
        var states = new List<StatedElement>();
        var questElements = QuestElementsShown(mapId).ToArray();
        var questIds = questElements.Select(shown => shown.ElementId).ToHashSet();
        foreach (var interactive in InteractiveRegistry.OnMap(mapId))
        {
            if (interactive.Actions.Count == 0 && questIds.Contains(interactive.Element.Id)) continue;
            var (element, state) = Registered(interactive, viewerAccountId);
            elements.Add(element);
            if (state != null) states.Add(state);
        }

        void Add(int elementId, int skillId, int typeId)
        {
            var element = Interactives.ByElementId(mapId, elementId);
            if (element.Id == 0) return;
            elements.Add(Declaration(elementId, typeId,
                [new(skillId, Interactives.SkillInstanceOf(elementId), null)], true));
            states.Add(Placement(element, (int)ResourceState.Full));
        }

        foreach (var (binding, elementId) in questElements)
            Add(elementId, binding.SkillId, binding.TypeId);
        foreach (int elementId in Readables.OnMap(mapId))
            Add(elementId, Readables.Skill, Readables.Of(mapId, elementId)?.Type is int own && own != 0 ? own : QuestBinding.DefaultType);
        // What only speaks when pressed, unless a quest or the registry has declared it already with
        // something to do. A declaration with no skill enabled -- the registry's decor, like the
        // Astrub bank's 515346, which the official server offers «Usar» on -- is replaced.
        foreach (int elementId in InteractiveMessages.OnMap(mapId))
        {
            if (elements.Any(e => e.ElementId == elementId && e.EnabledSkills.Count > 0)) continue;
            elements.RemoveAll(e => e.ElementId == elementId);
            states.RemoveAll(s => s.ElementId == elementId);
            var said = InteractiveMessages.Of(mapId, elementId);
            Add(elementId, said?.Skill ?? InteractiveMessages.Skill, said is { Type: not 0 } ? said.Type : QuestBinding.DefaultType);
        }
        // An element whose use starts a written fight (npc_fights.json «element»), while that fight
        // is still to be fought by this character: the Xelorium's old chest, «Old Chest XXIII» on hover.
        foreach (var scripted in NpcFights.OnMap(mapId))
        {
            int elementId = scripted.ElementId;
            if (NpcFights.ForElement(mapId, elementId, SessionContext.State.ElementsUsed.Contains) == null) continue;
            if (elements.Any(e => e.ElementId == elementId && e.EnabledSkills.Count > 0)) continue;
            elements.RemoveAll(e => e.ElementId == elementId);
            states.RemoveAll(s => s.ElementId == elementId);
            Add(elementId, QuestBinding.DefaultSkill, scripted.ElementType != 0 ? scripted.ElementType : QuestBinding.DefaultType);
        }
        int altar = AlmanaxOfferingHandler.AltarOn(mapId);
        if (altar != 0 && AlmanaxOfferingHandler.MayPray()) Add(altar, QuestBinding.DefaultSkill, QuestBinding.DefaultType);
        return (elements.ToArray(), states.ToArray());
    }

    private static IEnumerable<(QuestBinding Binding, int ElementId)> QuestElementsShown(long mapId)
    {
        foreach (var binding in Quests.Bindings.OnMap(mapId))
        {
            if (!Quests.ShouldSee(binding)) continue;
            bool ready = binding.AfterElement == 0 || SessionContext.State.ElementsUsed.Contains(binding.AfterElement);
            foreach (var (where, elementId) in ready ? binding.Elements.Concat(binding.Props) : binding.Props)
                if (where == mapId && Interactives.ByElementId(mapId, elementId).Id != 0)
                    yield return (binding, elementId);
        }
    }

    internal static InteractiveElement Declaration(int elementId, int typeId, IReadOnlyList<InteractiveElementSkill> skills, bool usable, int? ageBonus = null)
        => new(elementId, typeId, usable ? skills.ToArray() : [], usable ? [] : skills.ToArray(), true, ageBonus);

    internal static StatedElement Placement(Interactives.Element element, int state) => new(element.Id, element.Cell, state, true);

    private static (InteractiveElement Element, StatedElement? State) Registered(RegisteredInteractive interactive, long viewerAccountId)
    {
        bool gathering = Resources.Is(interactive.MapId, interactive.Element.Id);
        var resourceState = gathering ? Resources.StateOf(interactive.MapId, interactive.Element.Id) : ResourceState.Full;
        bool withinReach = !gathering || Resources.WithinReach(interactive.MapId, interactive.Element.Id);
        bool usable = !gathering || resourceState == ResourceState.Full && withinReach;
        long viewer = SessionContext.State.CharacterId;
        long map = interactive.MapId;
        int elementId = interactive.Element.Id;
        var enigma = interactive.Actions.FirstOrDefault(a => a.Kind is InteractiveActionKind.RaidFlower
            or InteractiveActionKind.RaidGardenSquare or InteractiveActionKind.RaidGardenButton
            or InteractiveActionKind.RaidPaperBoat or InteractiveActionKind.RaidCannonBall or InteractiveActionKind.RaidChessPiece);
        if (enigma != null)
        {
            bool offered = enigma.Kind switch
            {
                InteractiveActionKind.RaidFlower => GuildRaidMonochrome.Offers(viewer, map, elementId),
                InteractiveActionKind.RaidPaperBoat or InteractiveActionKind.RaidCannonBall => GuildRaidBelladona.Offers(viewer, map, elementId),
                InteractiveActionKind.RaidChessPiece => GuildRaidEfedra.Offers(viewer, map, elementId),
                _ => GuildRaidEnclave.Offers(viewer, map, elementId),
            };
            if (!offered)
                return (Declaration(elementId, Interactives.TypeOfGfx(interactive.Element.Gfx), [], true), null);
            usable = enigma.Kind switch
            {
                InteractiveActionKind.RaidFlower => GuildRaidMonochrome.Usable(viewer, map, elementId),
                InteractiveActionKind.RaidPaperBoat or InteractiveActionKind.RaidCannonBall => GuildRaidBelladona.Usable(viewer, map, elementId),
                InteractiveActionKind.RaidChessPiece => GuildRaidEfedra.Usable(viewer, map, elementId),
                _ => GuildRaidEnclave.Usable(viewer, map, elementId),
            };
        }

        var visible = TravelersTowerHandler.VisibleActions(interactive, viewer)
            ?? TreasureHuntHandler.VisibleActions(interactive)
            ?? InteractiveWaysHandler.VisibleActions(interactive)
            ?? HouseHandler.VisibleActions(interactive, viewerAccountId);
        var skills = visible.Select(action => new InteractiveElementSkill(action.SkillId, action.SkillInstanceId,
            action.SkillNameId == 0 ? null : action.SkillNameId)).ToArray();
        var declaration = Declaration(elementId, interactive.Type, skills, usable, gathering && usable ? 0 : null);

        // A gate that also offers a passage still needs its open/closed placement.
        if (PlateGates.IsGateElement(map, elementId)) return (declaration, Placement(interactive.Element, PlateGates.StateOf(map)));
        if (interactive.Actions.Count == 0)
        {
            if (AbyssalBase.IsDoorOrLight(map, elementId)) return (declaration, Placement(interactive.Element, AbyssalBase.StateOf(map, elementId)));
            return (declaration, null);
        }

        // Passive routes, dream doors and counters are declared without an invented
        // dynamic placement; otherwise the client cannot attach them to its map data.
        if (interactive.Actions.Any(action => action.Kind is InteractiveActionKind.Teleport or InteractiveActionKind.HouseExit
            or InteractiveActionKind.Dream or InteractiveActionKind.DreamDoor or InteractiveActionKind.Marketplace
            or InteractiveActionKind.TowerRoom or InteractiveActionKind.TowerCreator or InteractiveActionKind.TowerPortal
            or InteractiveActionKind.DimensionExit or InteractiveActionKind.FissureModel or InteractiveActionKind.TreasureBox
            or InteractiveActionKind.Way)) return (declaration, null);

        int state = interactive.Actions.Any(action => action.Kind == InteractiveActionKind.RaidStatue)
            ? GuildRaidExecrabe.StateFor(viewer, map, elementId)
            : interactive.Actions.Any(action => action.Kind == InteractiveActionKind.RaidLantern)
                ? GuildRaidLuminarium.StateFor(viewer, map, elementId)
                : (int)(gathering && !usable ? resourceState : ResourceState.Full);
        return (declaration, Placement(interactive.Element, state));
    }
}
