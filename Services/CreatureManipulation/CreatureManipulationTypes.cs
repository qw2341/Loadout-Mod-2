#nullable enable

namespace Loadout.Services.CreatureManipulation;

using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Powers;

public enum CreatureManipulationOperation
{
    AdjustPower,
    ClearPowersByType,
    SetStat,
    SetLock,
    Kill,
    Duplicate
}

public enum CreatureManipulationStat
{
    CurrentHp,
    MaxHp,
    Block
}

public enum CreatureDragPhase
{
    Begin,
    Update,
    End,
    Cancel
}

public sealed class CreatureManipulationPayload
{
    public int CombatEpoch { get; set; }
    public ulong RequesterNetId { get; set; }
    public uint TargetCombatId { get; set; }
    public CreatureManipulationOperation Operation { get; set; }
    public CreatureManipulationStat Stat { get; set; }
    public int Value { get; set; }
    public bool Locked { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public int Amount { get; set; }
    public PowerType PowerType { get; set; }
    public CreatureDuplicateSnapshot? Duplicate { get; set; }
}

public sealed class CreatureDuplicateSnapshot
{
    public string MonsterId { get; set; } = string.Empty;
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public string SlotName { get; set; } = string.Empty;
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public List<CreaturePowerSnapshot> Powers { get; set; } = [];
}

public sealed class CreaturePowerSnapshot
{
    public string ModelId { get; set; } = string.Empty;
    public int Amount { get; set; }
}

public sealed class CreatureManipulationCombatSnapshot
{
    public int CombatEpoch { get; set; }
    public Dictionary<uint, SerializableVector2> Positions { get; set; } = [];
    public Dictionary<uint, CreatureStatLockSnapshot> Locks { get; set; } = [];
}

public sealed class CreatureStatLockSnapshot
{
    public int? CurrentHp { get; set; }
    public int? MaxHp { get; set; }
    public int? Block { get; set; }

    public bool IsEmpty => !CurrentHp.HasValue && !MaxHp.HasValue && !Block.HasValue;
}

public readonly record struct SerializableVector2(float X, float Y)
{
    public Vector2 ToVector2() => new(X, Y);
    public static SerializableVector2 From(Vector2 value) => new(value.X, value.Y);
}
