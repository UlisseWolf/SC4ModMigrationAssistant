using System;

namespace SC4ModMigrationAssistant.Models;

/// <summary>
/// Compact value-type key (Type, Group, Instance) used everywhere in the scanner instead of
/// csDBPF's own TGI struct. Implementing <see cref="IEquatable{TgiKey}"/> ourselves guarantees
/// the runtime never boxes instances during hashing/equality checks in a HashSet/Dictionary -
/// something that would otherwise be easy to trigger accidentally and that, across the tens of
/// millions of comparisons a large Plugins folder can involve, would show up as exactly the
/// kind of runaway memory/CPU usage this type exists to avoid.
/// </summary>
public readonly struct TgiKey : IEquatable<TgiKey>
{
    public readonly uint TypeId;
    public readonly uint GroupId;
    public readonly uint InstanceId;

    public TgiKey(uint typeId, uint groupId, uint instanceId)
    {
        TypeId = typeId;
        GroupId = groupId;
        InstanceId = instanceId;
    }

    public bool Equals(TgiKey other) =>
        TypeId == other.TypeId && GroupId == other.GroupId && InstanceId == other.InstanceId;

    public override bool Equals(object? obj) => obj is TgiKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(TypeId, GroupId, InstanceId);

    public override string ToString() => $"{TypeId:X8}-{GroupId:X8}-{InstanceId:X8}";
}
