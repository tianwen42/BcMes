using Opc.Ua;
using Opc.Ua.Server;

namespace BcMes.Simulator.Opc;

/// <summary>
/// 化成柜通道节点的地址空间管理。
///
/// 节点树：Devices / EQ-FORM-01 / C01 → { CellSn, Voltage, Current, Temperature, Capacity, Step, Phase }
/// NodeId 规则：ns=2;s=EQ-FORM-01.C01.Voltage —— 和真实化成柜上位机的命名风格一致。
/// MES 侧不需要知道模拟器的存在，只按这套地址空间订阅。
/// </summary>
public sealed class ChannelNodeManager : CustomNodeManager2
{
    public const string NamespaceUri = "urn:bcmes:simulator:channels";

    private readonly IReadOnlyList<(string Code, int Channels)> _devices;
    private readonly Dictionary<string, BaseDataVariableState> _vars = new(StringComparer.Ordinal);

    public ChannelNodeManager(
        IServerInternal server, ApplicationConfiguration config, IReadOnlyList<(string Code, int Channels)> devices)
        : base(server, config, new[] { NamespaceUri })
    {
        _devices = devices;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        var root = CreateFolder(null, "BcMes");

        // 把根文件夹挂到标准 Objects 节点下，否则客户端浏览不到
        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
            externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();
        refs.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root));

        foreach (var (code, channels) in _devices)
        {
            var deviceFolder = CreateFolder(root, code);
            // NodeId 必须与 _vars 字典键一致（平铺路径），不能带父级前缀 —— 否则客户端按
            // "EQ-FORM-01.C01.Voltage" 寻址会 BadNodeIdUnknown（ComposePath 会拼出 BcMes. 前缀）
            deviceFolder.NodeId = new NodeId(code, NamespaceIndex);
            for (var ch = 1; ch <= channels; ch++)
            {
                var name = $"C{ch:D2}";
                var chFolder = CreateFolder(deviceFolder, name);
                chFolder.NodeId = new NodeId($"{code}.{name}", NamespaceIndex);
                var path = $"{code}.{name}";

                _vars[$"{path}.CellSn"] = CreateVariable(chFolder, "CellSn", "", DataTypeIds.String);
                _vars[$"{path}.Voltage"] = CreateVariable(chFolder, "Voltage", 0.0, DataTypeIds.Double);
                _vars[$"{path}.Current"] = CreateVariable(chFolder, "Current", 0.0, DataTypeIds.Double);
                _vars[$"{path}.Temperature"] = CreateVariable(chFolder, "Temperature", 0.0, DataTypeIds.Double);
                _vars[$"{path}.Capacity"] = CreateVariable(chFolder, "Capacity", 0.0, DataTypeIds.Double);
                _vars[$"{path}.Step"] = CreateVariable(chFolder, "Step", 0, DataTypeIds.Int32);
                _vars[$"{path}.Phase"] = CreateVariable(chFolder, "Phase", "IDLE", DataTypeIds.String);
            }
        }

        // 关键：把整棵节点树递归注册进 PredefinedNodes 索引表。
        // 只 AddChild 不注册的话，客户端 Read/订阅全部返回 BadNodeIdUnknown。
        AddPredefinedNode(SystemContext, root);
    }

    /// <summary>产线节拍回调：更新整组通道值并触发订阅通知。</summary>
    public void UpdateChannel(string device, int channel, string sn,
        double voltage, double current, double temperature, double capacity, int step, string phase)
    {
        var c = $"C{channel:D2}";
        Set($"{device}.{c}.CellSn", sn);
        Set($"{device}.{c}.Voltage", voltage);
        Set($"{device}.{c}.Current", current);
        Set($"{device}.{c}.Temperature", temperature);
        Set($"{device}.{c}.Capacity", capacity);
        Set($"{device}.{c}.Step", step);
        Set($"{device}.{c}.Phase", phase);
    }

    /// <summary>调试：读节点当前值（模拟器 HTTP 调试端点用）。</summary>
    public object? GetNodeValue(string path) =>
        _vars.TryGetValue(path, out var v) ? v.Value : null;

    public int VarCount => _vars.Count;

    private void Set(string path, object value)
    {
        if (!_vars.TryGetValue(path, out var node)) return;
        node.Value = value;
        node.Timestamp = DateTime.UtcNow;
        node.StatusCode = global::Opc.Ua.StatusCodes.Good;
        node.ClearChangeMasks(SystemContext, false);   // 关键：不调这个，订阅端收不到数据变更
    }

    private FolderState CreateFolder(NodeState? parent, string name)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None,
        };
        folder.NodeId = new NodeId(ComposePath(parent, name), NamespaceIndex);
        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(NodeState parent, string name, object value, NodeId dataType)
    {
        var path = ComposePath(parent, name);
        var v = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.HasComponent,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Historizing = false,
            StatusCode = global::Opc.Ua.StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
            Value = value,
        };
        v.NodeId = new NodeId(path, NamespaceIndex);
        parent.AddChild(v);
        return v;
    }

    private static string ComposePath(NodeState? parent, string name) =>
        parent is null ? name : $"{parent.NodeId.Identifier}.{name}";
}
