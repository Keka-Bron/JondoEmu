using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Network
{
    /// <summary>Class-scoped visual messages; the shared builders retain their existing behaviour.</summary>
    internal static class ForgelanceProtocol
    {
        // TridentDeLaMer packet 5, EstocBrulant packet 5, VoleeDairain packet 8.
        // f25.f2 is the rank (1 at low level), never a SpellLevels.Id; f1 is present on criticals.
        internal static byte[] Script(long caster, int spell, int grade, int cell, int marker, bool critical)
            => Pb.New().Var(3, caster).Var(14, 3792)
                .Msg(25, Pb.New().VarIfNotZero(1, critical ? 1 : 0).Var(2, grade)
                    .Var(3, cell).Var(4, spell).Var(5, marker)).Build();

        // LanceDuLac packets 26/27 and Holmgang packets 5/6 use distinct aura footprints.
        internal static byte[] Aura(Glifo glyph, int grade)
        {
            var body = Pb.New();
            bool lance = glyph.HechizoQueLoPuso == ForgelanceCombat.LanceAura;
            if (lance && glyph.Grado == 2)
            {
                foreach (int cell in glyph.Casillas)
                    body.Msg(1, Pb.New().Var(2, glyph.Color).Var(3, cell));
            }
            else if (!lance && glyph.Hechizo == 23847)
            {
                body.Msg(1, Pb.New().Var(1, 2).Var(2, glyph.Color).Var(3, glyph.Centro));
            }

            body.Var(2, lance ? glyph.Grado : 1).Var(3, glyph.Hechizo).Var(4, glyph.Id)
                .Var(6, grade).Var(7, glyph.Color).Var(9, glyph.HechizoQueLoPuso)
                .Var(10, glyph.Centro).Var(11, 1).Var(12, glyph.Dueno);
            return FightProtocol.BuildAction(glyph.Dueno, 1091, Pb.New().Msg(1, body), detailField: 32);
        }

        // LancePierre packets 20/55: disarming removes skin 3650; arming restores it.
        // Effondrement packets 22/49: guard uses bones 7156, returning to the turn uses bones 1.
        internal static byte[] Look(byte[] normal, Fighter fighter, int appearance)
        {
            int bones = appearance == ForgelanceCombat.GuardAppearance ? 7156 : 1;
            bool armed = appearance == ForgelanceCombat.ArmedAppearance
                         || !fighter.Buffs.TieneEstado(ForgelanceCombat.Disarmed);
            var look = ProtoMessage.Parse(FightProtocol.WithRootBones(normal, bones));
            var skins = new List<long>();
            foreach (var field in look.Fields.Where(f => f.FieldNumber == 6))
            {
                if (field.WireType == 0) skins.Add(field.VarIntValue);
                else if (field.WireType == 2)
                {
                    int offset = 0;
                    while (offset < field.BytesValue.Length)
                    {
                        long value = 0; int shift = 0; byte next;
                        do
                        {
                            next = field.BytesValue[offset++];
                            value |= (long)(next & 127) << shift;
                            shift += 7;
                        } while ((next & 128) != 0 && offset < field.BytesValue.Length);
                        skins.Add(value);
                    }
                }
            }
            skins.RemoveAll(s => s == ForgelanceCombat.LanceSkin);
            if (armed) skins.Add(ForgelanceCombat.LanceSkin);
            look.Fields.RemoveAll(f => f.FieldNumber == 6);
            look.Fields.AddRange(ProtoMessage.Parse(Pb.New().Packed(6, skins).Build()).Fields);
            return look.ToByteArray();
        }
    }
}
