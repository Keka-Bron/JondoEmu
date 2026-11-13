using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

public sealed class CharacteristicRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Experience_reward_uses_the_owner_codec_before_the_gain_and_stops_on_encoding_failure(bool fail)
    {
        var codec = new RecordingCodec(fail);
        long character = fail ? 8_941_730_002 : 8_941_730_001;
        await using var pipe = await ClientPipe.OpenAsync(character, character, 191105026, "Reward", codec);
        var session = pipe.Session;
        session.State.CharacterLevel = 200;
        session.State.Experience = ExperienceTable.LevelFloor(200);
        session.State.StatStrength = 41;
        long before = session.State.Experience;
        using var context = SessionContext.Push(session);
        using var turn = await session.EnterTurnAsync();

        if (fail)
        {
            Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(
                () => CharacterRewards.GiveExperienceAsync(pipe.ToClient, 1000, serverBonus: false)));
            Assert.False(pipe.FromServer.DataAvailable);
        }
        else
        {
            Assert.False(await CharacterRewards.GiveExperienceAsync(pipe.ToClient, 1000, serverBonus: false));
            Assert.Equal(RecordingCodec.Token, await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer)
                .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(CharacterRewards.BuildExperienceGained(1000), await pipe.Next(Op.Kuf));
        }

        var frame = Assert.Single(codec.Frames);
        var stats = Assert.IsType<CharacterCharacteristicsEvent>(frame.Content).Stats;
        Assert.Equal(before + 1000, stats.Experience);
        Assert.Equal(before + 1000, session.State.Experience);
        Assert.Equal(41, Detail(stats, Stat.Strength).Base);
        Assert.Null(frame.Uid);
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(0, session.UnoCadaVez.CurrentCount);
        Assert.Same(session, SessionContext.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_expiration_sends_losses_before_the_typed_sheet_for_its_explicit_owner(bool fail)
    {
        var codec = new RecordingCodec(fail);
        long character = fail ? 8_941_730_004 : 8_941_730_003;
        await using var pipe = await ClientPipe.OpenAsync(character, character, 191105026, "Expiry", codec);
        pipe.Session.State.StatStrength = 41;
        var wrong = GameSession.SinSocket();
        wrong.State.StatStrength = 999;
        using var context = SessionContext.Push(wrong);
        using var turn = await pipe.Session.EnterTurnAsync();
        try
        {
            var now = DateTime.UtcNow;
            Assert.NotNull(Alterations.Gain(character, 625, now, new DateTimeOffset(now).AddMinutes(-1).ToUnixTimeMilliseconds()));
            Assert.True(AlterationHandler.GivesCharacteristics(Assert.Single(Alterations.Of(character))));

            if (fail)
                Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => AlterationHandler.TickAsync(pipe.Session)));
            else
                await AlterationHandler.TickAsync(pipe.Session);

            await pipe.Next(Op.Lzo);
            await pipe.Next(Op.Lqn);
            if (!fail)
                Assert.Equal(RecordingCodec.Token, await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer)
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(pipe.FromServer.DataAvailable);

            var frame = Assert.Single(codec.Frames);
            var stats = Assert.IsType<CharacterCharacteristicsEvent>(frame.Content).Stats;
            Assert.Equal(41, Detail(stats, Stat.Strength).Base);
            Assert.Equal(0, Detail(stats, Stat.Initiative).Temporary);
            Assert.False(Alterations.Has(character, 625));
            Assert.Null(frame.Uid);
            Assert.Equal(0, codec.Decodes);
            Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
            Assert.Same(wrong, SessionContext.Actual);

            await AlterationHandler.TickAsync(pipe.Session);
            Assert.Single(codec.Frames);
            Assert.False(pipe.FromServer.DataAvailable);
        }
        finally
        {
            Alterations.Lose(character, 625);
            Alterations.Forget(character);
        }
    }

    private static CharacterCharacteristicDetailed Detail(CharacterCharacteristics stats, int id)
        => Assert.IsType<CharacterCharacteristicCaseDetailed>(
            stats.Characteristics.Single(value => value.CharacteristicId == id).CharacteristicCase).Value;

    private sealed class RecordingCodec(bool fail) : IGameCodec
    {
        internal static readonly byte[] Token = [0xC7, 0x51]; // Deliberately not a protobuf message.
        internal readonly IOException Failure = new("characteristic encoding failed");
        internal readonly List<GameFrame> Frames = [];
        internal int Decodes;

        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            Assert.IsType<CharacterCharacteristicsEvent>(frame.Content);
            if (fail) throw Failure;
            return Token;
        }

        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            throw new InvalidOperationException("Characteristic refresh must not decode an intermediate payload.");
        }
    }
}
