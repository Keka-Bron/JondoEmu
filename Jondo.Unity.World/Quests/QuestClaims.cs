namespace Jondo.Unity.World.Quests
{
    /// <summary>
    /// Which objectives the client may say it has finished (<c>idw</c>).
    /// </summary>
    /// <remarks>
    /// Across the 401 captures there are exactly 16 <c>idw</c> frames, all of them free-text
    /// objectives (type 0) of the guided tutorial: «Aprende a luchar», «Equipa a tu personaje»...
    /// things done in the interface, which the server never sees. Every other type the server
    /// works out for itself -- a conversation, an item handed over, a map entered, a fight won,
    /// something crafted -- and so does a free-text objective a binding row ties to something the
    /// server sees (content/quests/objectives.json).
    ///
    /// The client used to be believed about any objective of the step in hand. That made a fight
    /// objective, or one that takes items, a single message away: «Vence a los Malters» and «Entrega
    /// 20 bayas maléficas» were ticked off with neither the fight nor the berries, and the quest's
    /// experience and kamas paid out (bugs/foret-malefique, 2026-11-17). Only what the client alone
    /// can know is taken from it now.
    /// </remarks>
    public static class QuestClaims
    {
        /// <summary>
        /// Whether the client's word is enough to finish <paramref name="objective"/>: free text or
        /// an escort, with nothing the server watches for it. An «order» row only says where the
        /// objective stands among its step's others, not what finishes it.
        /// </summary>
        /// <remarks>
        /// An escort (type 10) is as blind to the server as free text: its parameter is a text, not
        /// who is escorted, and no binding row names one, so the client's word is all there is.
        /// </remarks>
        public static bool ClientMayClaim(QuestObjective? objective, QuestBinding? binding)
            => objective != null
               && (objective.TypeId == QuestCatalogue.FreeText || objective.TypeId == QuestCatalogue.Escort)
               && (binding == null || binding.Kind == QuestBindingKind.Order);
    }
}
