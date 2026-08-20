using System;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Jondo.Unity.Protocol.Messages;

namespace Jondo.Unity.Protocol
{
    public static class TypeRegistry
    {
        private static readonly Dictionary<string, MessageDescriptor> AliasToDescriptor = new();
        private static readonly Dictionary<Type, string> TypeToAlias = new();

        static TypeRegistry()
        {
            Register(Op.Ise, GameMapMovementRequestMessage.Descriptor);
            Register(Op.Hhf, hhf.Descriptor);
            Register(Op.Hhh, hhh.Descriptor);
            Register(Op.SpellVariantActivationRequestMessage, hmt.Descriptor);
            Register(Op.Ilc, ilc.Descriptor);
            Register(Op.Iry, iry.Descriptor);
            Register(Op.Isf, isf.Descriptor);
            Register(Op.Isi, isi.Descriptor);
            Register("jnx", jnx.Descriptor);
            Register(Op.Joa, joa.Descriptor);
            Register(Op.Jog, jog.Descriptor);
            Register(Op.Joh, joh.Descriptor);
            Register("joi", joi.Descriptor);
            Register(Op.Jol, jol.Descriptor);
            Register("joo", joo.Descriptor);
            Register(Op.Jos, jos.Descriptor);
            Register(Op.Jpb, jpb.Descriptor);
            Register(Op.Jpg, jpg.Descriptor);
            Register(Op.Jpj, jpj.Descriptor);
            Register("jpp", jpp.Descriptor);
            Register(Op.Jps, jps.Descriptor);
            Register(Op.Jpv, jpv.Descriptor);
            Register(Op.Jqb, jqb.Descriptor);
            Register(Op.Kkr, kkr.Descriptor);
            Register(Op.Kku, kku.Descriptor);
            Register(Op.Kns, kns.Descriptor);
            Register("knx", knx.Descriptor);
            Register(Op.Kod, kod.Descriptor);
            Register(Op.CharactersListRequestMessage, kpa.Descriptor);
            Register("kpc", kpc.Descriptor);
            Register("kqn", kqn.Descriptor);
            Register(Op.Kqp, kqp.Descriptor);
            Register("krb", krb.Descriptor);
            Register(Op.Krc, krc.Descriptor);
            Register(Op.Kri, kri.Descriptor);
            Register(Op.Ksl, ksl.Descriptor);
            Register("ksq", ksq.Descriptor);
            Register(Op.Ksx, ksx.Descriptor);
            Register(Op.Ktw, ktw.Descriptor);
            Register("lai", lai.Descriptor);
            Register(Op.Lar, lar.Descriptor);
            Register("lcd", lcd.Descriptor);
            Register(Op.Lcj, lcj.Descriptor);
            Register("lct", lct.Descriptor);
            Register("lep", lep.Descriptor);
            Register(Op.Ley, ley.Descriptor);
            Register(Op.Lfj, lfj.Descriptor);
            Register(Op.Lfo, lfo.Descriptor);
            Register(Op.Lfx, lfx.Descriptor);
            Register(Op.Lgz, lgz.Descriptor);
            Register(Op.Lhi, lhi.Descriptor);
            Register("lhr", lhr.Descriptor);
            Register("lhy", lhy.Descriptor);
            Register(Op.Lif, lif.Descriptor);
            Register("ljk", ljk.Descriptor);
            Register(Op.Lkr, lkr.Descriptor);
            Register(Op.Lkt, lkt.Descriptor);
            Register(Op.Lnk, lnk.Descriptor);
            Register(Op.Loy, loy.Descriptor);
            Register(Op.Lpj, lpj.Descriptor);
            Register(Op.Lsy, lsy.Descriptor);
            Register(Op.Luq, luq.Descriptor);
            Register(Op.Luy, luy.Descriptor);
            Register(Op.Lxd, lxd.Descriptor);
        }

        private static void Register(string alias, MessageDescriptor descriptor)
        {
            AliasToDescriptor[alias] = descriptor;
            TypeToAlias[descriptor.ClrType] = alias;
        }

        public static MessageDescriptor GetDescriptorByAlias(string alias)
        {
            if (AliasToDescriptor.TryGetValue(alias, out var descriptor))
            {
                return descriptor;
            }
            return null;
        }

        public static string GetAliasByType(Type type)
        {
            if (TypeToAlias.TryGetValue(type, out var alias))
            {
                return alias;
            }
            return null;
        }

        /// <summary>
        /// Creates an Any message with the correct type.ankama.com URL
        /// </summary>
        public static Google.Protobuf.WellKnownTypes.Any Pack<T>(T message) where T : IMessage<T>
        {
            var alias = GetAliasByType(typeof(T));
            if (alias == null)
            {
                throw new Exception($"Message {typeof(T).Name} is not registered with an alias.");
            }

            return new Google.Protobuf.WellKnownTypes.Any
            {
                TypeUrl = $"type.ankama.com/{alias}",
                Value = message.ToByteString()
            };
        }
    }
}
