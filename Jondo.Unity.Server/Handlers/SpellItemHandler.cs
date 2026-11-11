using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The spell scrolls: effect 604, «Aprende el nivel de hechizo #3», thirteen of them -- Llamita,
    /// Liberación, Fulminación de Grunob, the Arakna and Chaferloko summons, Zanahowia, Captura de
    /// Almas, Martillo de Moon, Bumerán Pérfido, Dominio del Arma, Manticolmillo, Kaniburbuja and
    /// Dominio de Invocaciones.
    /// </summary>
    /// <remarks>
    /// The spell is learnt for good (<see cref="LearnedSpells"/>) and the spell list and the bar go
    /// again, as a level-up sends them, with the new one on the bar's first free slot; the info says
    /// «Has aprendido el hechizo». A spell already known keeps the scroll with «Ya has aprendido el
    /// hechizo». Neither is captured: the words are the client's own messages 3 and 628. A spell whose
    /// first grade asks for more level than the character has is learnt all the same, and shows up
    /// when he reaches it, as a class spell does.
    /// </remarks>
    public static class SpellItemHandler
    {
        /// <summary>Effects that say something of the item and do nothing on use.</summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        /// <summary>Whether an item teaches a spell and does nothing else.</summary>
        internal static bool TeachesASpell(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
            => effects.Any(e => e.EffectId == LearnedSpells.LearnEffect)
               && effects.All(e => e.EffectId == LearnedSpells.LearnEffect || Markers.Contains(e.EffectId));

        /// <summary>A spell scroll read: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            if (!TeachesASpell(effects)) return false;

            int spell = LearnedSpells.TaughtBy(item.Template);
            if (spell == 0)
            {
                Console.WriteLine($"[Items] Scroll {item.Template} names no spell this client has.");
                return false;
            }

            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;

            if (LearnedSpells.Holds(LearnedSpells.OfSession(), spell))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.SpellAlreadyLearned, spell.ToString())));
                return true;
            }
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid, weigh: false)) return true;
            LearnedSpells.Learn(me.CharacterId, spell);
            SpellChoices.PlaceLearned(me.Breed, me.CharacterLevel, spell);

            await SpellHandler.SendSpellsAndBarAsync(SessionContext.Current);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.SpellLearned, spell.ToString())));
            await ItemUseHandler.WeighAsync(stream);

            Console.WriteLine($"[Items] {me.CharacterName} learns spell {spell} from scroll {item.Template}.");
            return true;
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
