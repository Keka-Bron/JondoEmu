using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Cambiar un hechizo por su variante.
    ///
    /// Los hechizos van en parejas y el personaje lleva una de las dos mitades. Cuando el jugador
    /// elige la otra —desde el panel o con el botón derecho sobre la barra— el cliente manda un
    /// hmt con el hechizo que quiere, y el servidor contesta dos cosas:
    ///
    ///   iuq   por cada hueco de la barra que tuviera la mitad vieja, con la nueva dentro
    ///   hng   el hechizo nuevo y el grado que le corresponde al nivel del personaje
    ///
    /// Sacado de cuatro capturas reales: absorción por furia y la vuelta, liberación por magnetismo
    /// y llamilla por llamita. En la de magnetismo salieron dos iuq porque el hechizo viejo estaba
    /// puesto en dos huecos de la barra, lo que confirma que va uno por hueco y no uno por cambio.
    /// </summary>
    public static class SpellHandler
    {
        public static async Task HandleVariantAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hmt = ConnectionProtocol.ReadPayload(payload, "hmt");
            if (hmt == null) return;

            int wanted = 0;
            foreach (var field in ProtoMessage.Parse(hmt).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) wanted = (int)field.VarIntValue;
            }
            if (wanted == 0) return;

            var pair = SpellTable.PairOf(wanted);
            if (pair == null)
            {
                Console.WriteLine($"[Hechizos] El cliente pide el hechizo {wanted}, que no hace " +
                                  "pareja con ninguno. No se cambia nada.");
                return;
            }

            int level = GameState.CharacterLevel;
            int grade = SpellTable.GradeFor(wanted, level);
            if (grade == 0)
            {
                Console.WriteLine($"[Hechizos] {wanted} pide más nivel del que tiene el personaje " +
                                  $"({level}). No se cambia nada.");
                return;
            }

            // El hueco de la barra lo tenía la otra mitad, que es la que se va.
            int leaving = wanted == pair.Base ? pair.Variant : pair.Base;
            var slots = SpellChoices.SlotsHolding(leaving);

            SpellChoices.Choose(wanted);
            foreach (int slot in slots)
            {
                SpellChoices.PutInBar(slot, wanted);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push("iuq", ConnectionProtocol.BuildShortcutChanged(slot, wanted)));
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("hng", ConnectionProtocol.BuildSpellSwapped(wanted, grade)));

            Console.WriteLine($"[Hechizos] Pareja {pair.Id}: {leaving} -> {wanted} (grado {grade}), " +
                              $"{slots.Count} hueco(s) de la barra actualizados.");
        }
    }
}
