using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// «El cementerio primitivo» (485): Warko Linch's brew turns the character into a soul in the
    /// primitive cemetery, and its phoenix brings him back (Managers.Phantoms).
    /// </summary>
    public class PhantomTests
    {
        private const int Warko = 1219;
        private const long Hut = 73402891, Cemetery = 64488707, Phoenix = 64489222;

        [Fact]
        public async Task Map_hook_restores_phantom_once_per_session_and_resolves_the_look_for_each_viewer()
        {
            const long account = 9_900_485_002, map = 9_900_485_003, marker = 9_900_485_004;
            long who = DatabaseManager.CreateCharacter(account, 1, "Phantom-Hook", 4, 0, 1,
                [-1, -1, -1, -1, -1], Cemetery, 200, 0, 0, []);
            Assert.True(who > 0);
            var peer = new HookCodec(2);
            var elsewhere = new HookCodec(3);
            try
            {
                await using var viewer = await ClientPipe.OpenAsync(account + 1, who + 1_000_000, map, codec: peer);
                await using var other = await ClientPipe.OpenAsync(account + 2, who + 1_000_001, map + 1, codec: elsewhere);
                Phantoms.Become(who, marker);
                for (byte login = 0; login < 2; login++)
                {
                    var codec = new HookCodec(login);
                    await using var owner = await ClientPipe.OpenAsync(account, who, map, codec: codec);
                    using var ambient = SessionContext.Push(other.Session);
                    await PhantomHandler.OnMapLoadedAsync(owner.Session);
                    await PhantomHandler.OnMapLoadedAsync(owner.Session);
                    Assert.Collection(codec.Frames,
                        frame => Assert.Equal(new CharacterLifeStatusEvent(CharacterLifeStatus.Phantom, marker), frame.Content),
                        frame =>
                        {
                            var update = Assert.IsType<ContextRefreshEntityLookEvent>(frame.Content);
                            Assert.Equal(who, update.EntityId);
                            Assert.Contains(322, update.Look!.Skins);
                            Assert.Same(update, peer.Frames[login].Content);
                        });
                    Assert.Equal(new byte[] { 0xC9, login, 1 }, await Read(owner));
                    Assert.Equal(new byte[] { 0xC9, login, 2 }, await Read(owner));
                    Assert.Equal(new byte[] { 0xC9, 2, (byte)(login + 1) }, await Read(viewer));
                    Assert.Equal(login + 1, peer.Frames.Count);
                    Assert.Empty(elsewhere.Frames);
                }
            }
            finally
            {
                Phantoms.Rise(who);
                DatabaseManager.DeleteCharacter(who, account);
            }
        }

        private static Task<byte[]> Read(ClientPipe pipe)
            => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));

        private sealed class HookCodec(byte recipient) : IGameCodec
        {
            internal readonly List<GameFrame> Frames = [];
            public byte[] Encode(GameFrame frame)
            {
                Frames.Add(frame);
                return [0xC9, recipient, checked((byte)Frames.Count)];
            }
            public GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("A send cannot decode.");
        }

        [Fact]
        public void A_soul_is_one_until_he_rises()
        {
            const long id = 990_485_001;
            Assert.False(Phantoms.Is(id));
            Phantoms.Become(id, Phoenix);
            Assert.True(Phantoms.Is(id));
            Assert.Equal(Phoenix, Phantoms.PhoenixOf(id));
            Assert.True(Phantoms.Rise(id));
            Assert.False(Phantoms.Is(id));
            Assert.False(Phantoms.Rise(id));
            Assert.Equal(0, Phantoms.PhoenixOf(id));
        }

        [Fact]
        public void A_soul_wears_the_souls_body()
        {
            Assert.StartsWith("{3|322,", Phantoms.LookOf(0));
            Assert.StartsWith("{3|323,", Phantoms.LookOf(1));
        }

        [Fact]
        public void Warko_takes_the_flowers_and_his_brew_leaves_a_soul_by_the_phoenix()
        {
            var tree = NpcDialogues.For(Warko, Hut);
            Assert.NotNull(tree);
            Assert.Empty(Jondo.Unity.World.Content.NpcDialogueContent.Complaints(tree));

            var flowers = tree.Line(5446)?.Choice(7477);
            Assert.NotNull(flowers);
            Assert.Equal(3463, flowers.Completes);
            Assert.Equal(7904, flowers.Holds);
            Assert.Equal(5, flowers.HoldsCount);
            Assert.Equal(5442, flowers.Next);

            var drink = tree.Line(5442)?.Choice(7475);
            Assert.NotNull(drink);
            Assert.Equal(3464, drink.Completes);
            Assert.Equal(Cemetery, drink.TeleportsTo);
            Assert.Equal(Phoenix, drink.PhantomPhoenix);
        }
    }
}
