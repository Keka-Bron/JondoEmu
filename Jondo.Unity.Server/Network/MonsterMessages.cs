using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Shared monster appearance and grade policy, independent of packet serialization.</summary>
internal static class MonsterMessages
{
    private const int LookKind = 3;
    private const int MaxGrade = 5;
    private const int MaxDeclaredGrade = 6;

    internal static EntityLook Look(string look, bool hardcore)
    {
        var variants = string.IsNullOrEmpty(look) ? null : Npcs.Variantes(look);
        if (variants == null || variants.Count == 0)
            return new([], LookKind, checked((int)BonesOf(look)), hardcore ? [ServerSettings.HardcoreScale] : [], [], []);

        var first = variants[0];
        return new(first.Colors.Select(value => checked((int)value)).ToArray(), LookKind, checked((int)first.Bones),
            hardcore ? [ServerSettings.HardcoreScale] : first.Scales.Select(value => checked((int)value)).ToArray(),
            first.Skins.Select(value => checked((int)value)).ToArray(), []);
    }

    /// <summary>The rolled level, or the selected grade's level when none was rolled.</summary>
    internal static int LevelOf(MobSpawnManager.MobMember member)
    {
        if (member.Level > 0) return member.Level;
        var grades = member.Monster.Grades;
        return member.GradeIndex >= 0 && member.GradeIndex < grades.Count ? grades[member.GradeIndex].Level : 0;
    }

    /// <summary>
    /// Generated groups use grades 1–5. Authored monsters such as the level-200
    /// training dummy may use grade 6 when their own data declares it.
    /// </summary>
    internal static int GradeOf(MobSpawnManager.MobMember member)
    {
        int declared = member.Monster?.Grades?.Count ?? 0;
        int top = declared >= MaxDeclaredGrade ? MaxDeclaredGrade : MaxGrade;
        return Math.Clamp(member.GradeIndex + 1, 1, top);
    }

    private static long BonesOf(string look)
    {
        if (string.IsNullOrEmpty(look)) return 0;
        int start = look.IndexOf('{');
        if (start < 0) return 0;
        int end = start + 1;
        while (end < look.Length && char.IsDigit(look[end])) end++;
        return long.TryParse(look.Substring(start + 1, end - start - 1), out long bones) ? bones : 0;
    }
}
