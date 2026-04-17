namespace StardewMod.Models;

/// <summary>
/// tool 的业务类型，用于区分查询、状态修改和动作请求。
/// </summary>
public enum NpcToolKind
{
    Query = 0,
    Mutation = 1,
    ActionRequest = 2
}

/// <summary>
/// tool 结果的本地落地策略。
/// </summary>
public enum NpcToolDispatchPolicy
{
    None = 0,
    Immediate = 1,
    Deferred = 2,
    Mixed = 3
}

/// <summary>
/// 按触发事件限制可用工具集合。
/// </summary>
public enum NpcToolAccessProfile
{
    Full = 0,
    Maintenance = 1,
    NpcSync = 2,
    Ambient = 3,
    Reactive = 4,
    Broadcast = 5
}
