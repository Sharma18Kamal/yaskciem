namespace CloudEntitle.Domain.ValueObjects;

public readonly record struct CloudAccountId(Guid Value)
{
    public static CloudAccountId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct PrincipalId(Guid Value)
{
    public static PrincipalId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct ResourceId(Guid Value)
{
    public static ResourceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct RoleId(Guid Value)
{
    public static RoleId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct PolicyId(Guid Value)
{
    public static PolicyId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct PermissionId(Guid Value)
{
    public static PermissionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct RiskFindingId(Guid Value)
{
    public static RiskFindingId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
