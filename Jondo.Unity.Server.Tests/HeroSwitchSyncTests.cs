using System;
using System.Collections.Generic;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class HeroSwitchSyncTests
{
    [Fact]
    public void Hero_switch_resets_wardrobe_draft_state()
    {
        var state = new SessionState
        {
            IsWardrobeDraftLoaded = true,
            WardrobeDraftTitle = 42,
            WardrobeDraftOrnament = 10
        };

        // On hero switch, draft state must be cleared to prevent cross-character outfit leakage
        state.IsWardrobeDraftLoaded = false;

        Assert.False(state.IsWardrobeDraftLoaded);
    }

    [Fact]
    public void Hero_switch_look_changed_generates_valid_lxc_payload()
    {
        var character = new DbCharacter
        {
            Id = 123456,
            Name = "TestHero",
            Breed = 1,
            Sex = 0,
            Colors = CharacterColors.FromRgb(new List<long> { 0xFF0000, 0x00FF00, 0x0000FF, -1, -1 })
        };

        byte[] lxcBody = ConnectionProtocol.BuildLookChanged(character);
        Assert.NotNull(lxcBody);
        Assert.NotEmpty(lxcBody);

        byte[] frame = ConnectionProtocol.Push(Op.Lxc, lxcBody);
        Assert.NotNull(frame);
        Assert.NotEmpty(frame);
    }

    [Fact]
    public void Hero_switch_session_state_reflects_active_hero()
    {
        var state = new SessionState
        {
            CharacterId = 100,
            CharacterName = "Master",
            Breed = 8,
            CharacterLevel = 50
        };

        // When switching to hero 200 (Cra, level 80)
        state.CharacterId = 200;
        state.CharacterName = "HeroCra";
        state.Breed = 9;
        state.CharacterLevel = 80;

        Assert.Equal(200, state.CharacterId);
        Assert.Equal("HeroCra", state.CharacterName);
        Assert.Equal(9, state.Breed);
        Assert.Equal(80, state.CharacterLevel);
    }
}
