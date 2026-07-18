using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;

namespace FModel.ViewModels;

/// <summary>How a blueprint statement node is coloured/categorised. Derived only from the decoded token.</summary>
public enum EBlueprintNodeKind
{
    FunctionHeader,
    Branch,
    /// <summary>A recognized Cast To X editor node (collapsed from its exact compiled statement sequence).</summary>
    Cast,
    Jump,
    Flow,
    Return,
    Call,
    /// <summary>A call to a proven BlueprintPure function — rendered editor-style with no exec pins, connected only by value wires.</summary>
    PureCall,
    Assign,
    /// <summary>A variable read shown as its own editor-style Get node (the read token is in the bytecode argument).</summary>
    VariableGet,
    Statement,
}

/// <summary>What a control-flow edge represents. Every edge is read straight from the bytecode.</summary>
public enum EBlueprintEdgeKind
{
    Entry,
    Then,
    True,
    False,
    Jump,
    Push,
    Call,
    /// <summary>Value flow through a variable: producer statement → consuming pin. Only drawn when the producer is unambiguous.</summary>
    Data,
}

public class BlueprintGraphNode
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    /// <summary>The decompiled line for this statement (may span several lines). Shown truncated on the node, full in the panel.</summary>
    public string Body { get; set; } = string.Empty;
    /// <summary>The raw EExprToken class name (e.g. "EX_JumpIfNot"); surfaced in the properties panel.</summary>
    public string TokenName { get; set; } = string.Empty;
    public string FunctionName { get; set; } = string.Empty;
    public int StatementIndex { get; set; } = -1;
    public EBlueprintNodeKind Kind { get; set; } = EBlueprintNodeKind.Statement;

    public double NodePosX { get; set; }
    public double NodePosY { get; set; }
    /// <summary>Pre-computed by the view model so the layout and the view agree on node size exactly.</summary>
    public double Height { get; set; }
    /// <summary>How many wrapped body lines the node reserves (0 for the header); the view trims to this.</summary>
    public int BodyLineCount { get; set; }

    public List<BlueprintGraphPin> InputPins { get; } = [];
    public List<BlueprintGraphPin> OutputPins { get; } = [];
    public List<KeyValuePair<string, string>> DisplayProperties { get; } = [];

    public override string ToString() => $"{TokenName} ({Name})";
}

public class BlueprintGraphPin
{
    public string Name { get; set; } = string.Empty;
    public EBlueprintEdgeKind Kind { get; set; } = EBlueprintEdgeKind.Then;
    /// <summary>True for value pins (function arguments, conditions, assignment targets); false for exec pins.</summary>
    public bool IsData { get; set; }
    /// <summary>For unwired data inputs: the decoded argument (a literal or variable name) shown next to the pin.</summary>
    public string Label { get; set; } = string.Empty;
}

public class BlueprintGraphConnection
{
    public BlueprintGraphNode SourceNode { get; set; } = null!;
    public string SourcePinName { get; set; } = string.Empty;
    public BlueprintGraphNode TargetNode { get; set; } = null!;
    public string TargetPinName { get; set; } = "In";
    public EBlueprintEdgeKind Kind { get; set; } = EBlueprintEdgeKind.Then;
}

/// <summary>One entry per decompiled function; drives the viewer's "Function" isolate filter.</summary>
public class BlueprintFunctionInfo
{
    public string Name { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public int StatementCount { get; set; }
    public readonly HashSet<BlueprintGraphNode> Nodes = [];
}

/// <summary>Which list of the cooked class a component row came from (drives the panel's grouping).</summary>
public enum EBlueprintComponentKind
{
    /// <summary>A SimpleConstructionScript node — part of the spawned actor's component tree.</summary>
    SceneTree,
    /// <summary>A template for a component the graphs add at runtime (AddComponent node).</summary>
    Runtime,
    /// <summary>A timeline template.</summary>
    Timeline,
}

/// <summary>
/// One component of the Blueprint's Components tree. Cooked assets keep the full
/// SimpleConstructionScript (it runs when the actor spawns), so name, class, hierarchy,
/// attachment and every template property are real serialized data.
/// </summary>
public class BlueprintComponentInfo
{
    public EBlueprintComponentKind Kind { get; set; } = EBlueprintComponentKind.SceneTree;
    public string Name { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    /// <summary>The main content asset on the template (static/skeletal mesh, sound, particle system…), when one is set.</summary>
    public string Asset { get; set; }
    /// <summary>Attachment target ("→ Parent [Socket]") when the node attaches to an inherited/native component.</summary>
    public string AttachInfo { get; set; }
    /// <summary>Tree depth for indentation (pre-order flattened).</summary>
    public int Depth { get; set; }
    /// <summary>Which class introduced it — set when it comes from a parent Blueprint's construction script.</summary>
    public string InheritedFrom { get; set; }
    /// <summary>Every property serialized on the component template, compact-formatted.</summary>
    public List<KeyValuePair<string, string>> TemplateProperties { get; } = [];
}

/// <summary>One class variable, read from the cooked class's serialized property list (never inferred).</summary>
public class BlueprintVariableInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    /// <summary>The raw EPropertyFlags string, e.g. "Edit | BlueprintVisible | Net".</summary>
    public string Flags { get; set; } = string.Empty;
    /// <summary>Value serialized on the class-default-object; null when the CDO holds no override.</summary>
    public string DefaultValue { get; set; }
}

/// <summary>
/// Reconstructs the control-flow / execution graph of a Blueprint (or any UStruct carrying Kismet
/// bytecode) directly from the serialized <see cref="KismetExpression"/> stream — one node per
/// top-level statement, edges taken from the real jump/branch/flow tokens. Nothing here is guessed:
/// statement text comes from the same <see cref="BlueprintDecompilerUtils.GetLineExpression"/> the
/// text decompiler uses, and every edge is a byte offset stored in the bytecode. Layout is the only
/// synthesized part (cooked assets do not keep the editor node positions), so it is a plain
/// per-function column waterfall the user can rearrange.
/// </summary>
public class BlueprintGraphViewModel
{
    // geometry constants — the view reads Node.Height, so these must stay the single source of truth
    private const double NodeWidth = 300;
    private const double HeaderHeight = 28;
    private const double BodyLineHeight = 15;
    private const double BodyTopPad = 6;
    private const double BodyBottomPad = 8;
    private const int MaxBodyLines = 6;
    private const int CharsPerLine = 36; // body text wraps narrower than the node (right edge reserved for pins)
    private const double PinRowHeight = 20;
    // layout: execution flows left→right (one layer per exec step); functions stack top→bottom
    private const double ColStride = NodeWidth + 90;
    private const double RowGap = 26;
    private const double BandGap = 100;

    public string PackageName { get; private set; } = string.Empty;
    public string ClassName { get; private set; } = string.Empty;
    public string ParentClass { get; private set; } = string.Empty;
    /// <summary>
    /// When false (default) statement nodes render compact — title + pins only, like editor nodes —
    /// and the decompiled statement stays in the properties panel. Function headers always keep
    /// their parameter listing. Toggle via <see cref="SetStatementTextVisible"/>.
    /// </summary>
    public bool ShowStatementText { get; private set; }
    public List<BlueprintGraphNode> Nodes { get; } = [];
    public List<BlueprintGraphConnection> Connections { get; } = [];
    public List<BlueprintFunctionInfo> Functions { get; } = [];
    /// <summary>Class variables (name/type/flags/default), straight from the cooked class + its CDO.</summary>
    public List<BlueprintVariableInfo> Variables { get; } = [];
    /// <summary>The Components tree (SimpleConstructionScript + AddComponent templates + timelines), pre-order flattened with Depth.</summary>
    public List<BlueprintComponentInfo> Components { get; } = [];
    /// <summary>Free-form class/blueprint facts for the viewer's details panel.</summary>
    public List<KeyValuePair<string, string>> ClassInfo { get; } = [];

    // dispatch calls (e.g. an event thunk → the ubergraph) target another function's byte offset,
    // so they are resolved after every function is laid out, keyed by (function name, statement offset)
    private readonly Dictionary<(string Func, int Offset), BlueprintGraphNode> _byFuncIndex = new();
    private readonly List<(BlueprintGraphNode Source, string Pin, string TargetFunc, int Offset)> _pendingCalls = [];

    // kept so the pattern pass can re-inspect each node's decoded statement after all edges exist
    private readonly Dictionary<BlueprintGraphNode, KismetExpression> _stmtByNode = new();
    // this package's own functions by name (purity of local virtual calls is read off their real flags)
    private readonly Dictionary<string, UFunction> _functionByName = new(StringComparer.Ordinal);
    private UClass _classExport;
    private readonly Dictionary<BlueprintFunctionInfo, List<(string Var, BlueprintGraphNode Node, string Pin)>> _writesByFunc = new();
    private readonly Dictionary<BlueprintFunctionInfo, List<(BlueprintGraphNode Node, string Pin, string Var, string Token)>> _readsByFunc = new();

    /// <summary>
    /// Builds the graph for a package. <paramref name="classExport"/> supplies the class/parent
    /// name (may be null); <paramref name="functions"/> is every UFunction with bytecode to draw.
    /// </summary>
    public static BlueprintGraphViewModel Build(string packageName, UClass classExport, IReadOnlyList<UFunction> functions)
    {
        var vm = new BlueprintGraphViewModel { PackageName = packageName, _classExport = classExport };

        if (classExport != null)
        {
            BlueprintDecompilerUtils.Mappings = classExport.Owner?.Mappings;
            vm.ClassName = classExport.Name;
            vm.ParentClass = classExport.SuperStruct?.Name ?? string.Empty;
        }
        else if (functions.Count > 0)
        {
            BlueprintDecompilerUtils.Mappings = functions[0].Owner?.Mappings;
        }

        if (!string.IsNullOrEmpty(vm.ClassName)) vm.ClassInfo.Add(new("Class", vm.ClassName));
        if (!string.IsNullOrEmpty(vm.ParentClass)) vm.ClassInfo.Add(new("Parent", vm.ParentClass));
        vm.ClassInfo.Add(new("Functions", functions.Count.ToString()));
        vm.CollectVariables(classExport);
        vm.CollectComponents(classExport);

        foreach (var function in functions)
            vm.BuildFunction(function);

        // resolve cross-function dispatch edges now that every function's statements exist
        foreach (var (source, pin, targetFunc, offset) in vm._pendingCalls)
        {
            if (!vm._byFuncIndex.TryGetValue((targetFunc, offset), out var target)) continue;
            vm.Connections.Add(new BlueprintGraphConnection
            {
                SourceNode = source, SourcePinName = pin,
                TargetNode = target, Kind = EBlueprintEdgeKind.Call,
            });
        }

        // event stubs collapse into the ubergraph so each event shows as its own graph, then:
        // collapse compiler idioms back into their editor nodes, wire value flow, detach proven
        // pure calls from the exec chain, and lay the bands out
        vm.MergeEventStubs(classExport);
        foreach (var fn in vm.Functions)
            vm.CollapsePatterns(fn);
        double bandY = 0;
        foreach (var fn in vm.Functions)
        {
            var wired = vm.WireDataFlow(fn.Nodes, vm._writesByFunc[fn], vm._readsByFunc[fn]);
            vm.DetachPureCalls(fn);
            vm.CreateGetNodes(fn, vm._readsByFunc[fn], wired);
            var height = vm.LayoutBand(fn.Nodes, bandY);
            bandY += height + BandGap;
        }

        return vm;
    }

    /// <summary>Builds one function's statement graph and resolves its intra-function exec edges (patterns, value wires and layout run later, from <see cref="Build"/>).</summary>
    private void BuildFunction(UFunction function)
    {
        BlueprintDecompilerUtils.Function = function;
        var funcName = function.Name;
        _functionByName[funcName] = function;

        // parameters are serialized properties on the UFunction itself (CPF_Parm flags) — real data, both
        // the FField path (4.25+) and the legacy UProperty-export path resolve here
        var parms = CollectParams(function);
        var info = new BlueprintFunctionInfo { Name = funcName, Signature = DescribeSignature(parms) };

        // delegate signatures and BlueprintEvent stubs carry no bytecode but still deserve their I/O shown
        var statements = function.ScriptBytecode?.Where(IsRealStatement).ToList() ?? [];
        var flags = function.FunctionFlags;
        var kindLabel = flags.HasFlag(EFunctionFlags.FUNC_Delegate)
            ? (flags.HasFlag(EFunctionFlags.FUNC_MulticastDelegate) ? "Event Dispatcher (multicast delegate signature)" : "Delegate Signature")
            : flags.HasFlag(EFunctionFlags.FUNC_BlueprintEvent) ? "Event" : "Function";

        // header node the function's execution enters from; its declared inputs surface as data
        // output pins (like the editor's function-entry node) added after the exec Entry pin below
        var header = new BlueprintGraphNode
        {
            Name = $"{funcName}#header",
            Title = funcName,
            Subtitle = info.Signature,
            Body = statements.Count == 0 ? "(signature only — no compiled body)" : string.Empty,
            TokenName = "UFunction",
            FunctionName = funcName,
            Kind = EBlueprintNodeKind.FunctionHeader,
        };
        header.DisplayProperties.Add(new("Kind", kindLabel));
        header.DisplayProperties.Add(new("Function", funcName));
        header.DisplayProperties.Add(new("Signature", info.Signature));
        foreach (var p in parms)
            header.DisplayProperties.Add(new($"{p.Dir} Param", $"{p.Name} : {p.Type}"));
        header.DisplayProperties.Add(new("Flags", flags.ToString().Replace("FUNC_", "")));
        header.DisplayProperties.Add(new("Statements", statements.Count.ToString()));
        Nodes.Add(header);
        info.Nodes.Add(header);

        // one node per statement, in bytecode order
        var nodeByIndex = new List<(int Index, BlueprintGraphNode Node)>(statements.Count);
        var pending = new List<(BlueprintGraphNode Source, string Pin, int TargetOffset, EBlueprintEdgeKind Kind)>();
        // value flow within the function: which statement wrote each variable (and out of which
        // pin — pattern nodes rename theirs, e.g. "As BP_Foo"), and which pins read it
        var dataWrites = new List<(string Var, BlueprintGraphNode Node, string Pin)>();
        var dataReads = new List<(BlueprintGraphNode Node, string Pin, string Var, string Token)>();

        for (var i = 0; i < statements.Count; i++)
        {
            var stmt = statements[i];
            var node = new BlueprintGraphNode
            {
                Name = $"{funcName}#{stmt.StatementIndex}",
                FunctionName = funcName,
                StatementIndex = stmt.StatementIndex,
                TokenName = stmt.GetType().Name,
            };
            node.InputPins.Add(new BlueprintGraphPin { Name = "In", Kind = EBlueprintEdgeKind.Then });
            node.Body = SafeDecompile(stmt);
            node.Title = DescribeTitle(stmt, node.Body);
            node.Kind = ClassifyKind(stmt);

            node.DisplayProperties.Add(new("Token", node.TokenName));
            node.DisplayProperties.Add(new("Statement Offset", $"0x{stmt.StatementIndex:X} ({stmt.StatementIndex})"));
            if (!string.IsNullOrWhiteSpace(node.Body))
                node.DisplayProperties.Add(new("Statement", node.Body));

            // outgoing control flow (adds the node's output pins) before sizing, so pins fit
            int? nextOffset = i + 1 < statements.Count ? statements[i + 1].StatementIndex : null;
            AnalyzeFlow(stmt, node, nextOffset, pending);
            AnalyzeData(stmt, node, dataWrites, dataReads);
            TagMacroPattern(node);

            ComputeHeight(node);
            Nodes.Add(node);
            info.Nodes.Add(node);
            nodeByIndex.Add((stmt.StatementIndex, node));
            _byFuncIndex[(funcName, stmt.StatementIndex)] = node;
            _stmtByNode[node] = stmt;
        }

        // entry edge into the first real statement
        if (nodeByIndex.Count > 0)
        {
            AddPin(header, "Entry", EBlueprintEdgeKind.Entry);
            Connections.Add(new BlueprintGraphConnection
            {
                SourceNode = header, SourcePinName = "Entry",
                TargetNode = nodeByIndex[0].Node, Kind = EBlueprintEdgeKind.Entry,
            });
        }

        // the function's declared inputs come out of the entry node, like in the editor
        foreach (var p in parms)
        {
            if (p.Dir is not ("In" or "Ref")) continue;
            header.OutputPins.Add(new BlueprintGraphPin { Name = p.Name, Kind = EBlueprintEdgeKind.Data, IsData = true });
            dataWrites.Add((p.Name, header, p.Name));
        }
        ComputeHeight(header);

        // resolve control flow: a jump target offset lands on the real statement at that byte offset
        BlueprintGraphNode ResolveOffset(int offset)
        {
            foreach (var (index, node) in nodeByIndex)
                if (index == offset) return node;
            // targets should be statement boundaries; if one isn't, snap to the next statement
            BlueprintGraphNode fallback = null;
            var best = int.MaxValue;
            foreach (var (index, node) in nodeByIndex)
                if (index >= offset && index < best) { best = index; fallback = node; }
            return fallback;
        }

        foreach (var (source, pin, targetOffset, kind) in pending)
        {
            var target = ResolveOffset(targetOffset);
            if (target == null) continue;
            Connections.Add(new BlueprintGraphConnection
            {
                SourceNode = source, SourcePinName = pin,
                TargetNode = target, Kind = kind,
            });
        }

        // value wiring and layout run from Build() once every function (and its call edges) exists
        _writesByFunc[info] = dataWrites;
        _readsByFunc[info] = dataReads;
        info.StatementCount = statements.Count;
        Functions.Add(info);
    }

    /// <summary>
    /// Places a function's nodes as a Blueprint-style left→right execution flow: each node's column
    /// is its longest execution distance from the entry (following the real exec edges), and nodes
    /// sharing a column stack vertically. Loops/back-edges don't push columns. Returns the band height.
    /// </summary>
    private double LayoutBand(ICollection<BlueprintGraphNode> nodes, double bandY)
    {
        var set = new HashSet<BlueprintGraphNode>(nodes);
        var outEdges = new Dictionary<BlueprintGraphNode, List<BlueprintGraphNode>>();
        foreach (var n in nodes) outEdges[n] = [];
        foreach (var c in Connections)
            if (set.Contains(c.SourceNode) && set.Contains(c.TargetNode))
                outEdges[c.SourceNode].Add(c.TargetNode);

        // longest-path columns over forward edges (target after source in bytecode; header index is -1)
        var col = new Dictionary<BlueprintGraphNode, int>();
        foreach (var n in nodes) col[n] = 0;
        foreach (var n in nodes.OrderBy(n => n.StatementIndex))
            foreach (var t in outEdges[n])
                if (t.StatementIndex > n.StatementIndex && col[n] + 1 > col[t])
                    col[t] = col[n] + 1;
        // keep statements only reachable via back-edges out of the header's column
        foreach (var n in nodes)
            if (n.StatementIndex >= 0 && col[n] == 0) col[n] = 1;

        // pure calls carry no exec flow either: place each one column left of its nearest consumer
        // (descending statement order so chained pure calls cascade leftwards)
        foreach (var n in nodes.Where(n => n.Kind == EBlueprintNodeKind.PureCall).OrderByDescending(n => n.StatementIndex))
        {
            var consumers = outEdges[n].Where(t => t != n).ToList();
            if (consumers.Count > 0) col[n] = Math.Max(0, consumers.Min(c => col[c]) - 1);
        }

        // Get nodes sit one column left of the node they feed (they carry no exec flow of their own)
        foreach (var n in nodes)
        {
            if (n.Kind != EBlueprintNodeKind.VariableGet) continue;
            var consumer = outEdges[n].FirstOrDefault();
            if (consumer != null) col[n] = Math.Max(0, col[consumer] - 1);
        }

        // stack the nodes of each column, top to bottom, within this band
        double maxBottom = bandY;
        foreach (var group in nodes.GroupBy(n => col[n]))
        {
            var y = bandY;
            foreach (var n in group.OrderBy(n => n.StatementIndex))
            {
                n.NodePosX = group.Key * ColStride;
                n.NodePosY = y;
                y += n.Height + RowGap;
            }
            maxBottom = Math.Max(maxBottom, y);
        }
        return maxBottom - bandY;
    }

    /// <summary>
    /// Records this statement's outgoing control flow by inspecting the typed token — exactly the
    /// jump/branch/flow semantics the UE VM uses. Fall-through goes to the next statement unless the
    /// token ends the flow (return / unconditional jump / dynamic pop).
    /// </summary>
    private void AnalyzeFlow(KismetExpression stmt, BlueprintGraphNode node, int? nextOffset,
        List<(BlueprintGraphNode, string, int, EBlueprintEdgeKind)> pending)
    {
        void Then()
        {
            if (nextOffset == null) return;
            AddPin(node, "Then", EBlueprintEdgeKind.Then);
            pending.Add((node, "Then", nextOffset.Value, EBlueprintEdgeKind.Then));
        }
        void Edge(string pin, EBlueprintEdgeKind kind, int offset)
        {
            AddPin(node, pin, kind);
            pending.Add((node, pin, offset, kind));
        }

        switch (stmt)
        {
            case EX_Return:
            case EX_EndOfScript:
                break; // terminators: no outgoing flow

            case EX_JumpIfNot j:
                // false branch jumps to the offset; true branch falls through to the next statement
                Edge("False", EBlueprintEdgeKind.False, (int)j.CodeOffset);
                if (nextOffset != null)
                {
                    AddPin(node, "True", EBlueprintEdgeKind.True);
                    pending.Add((node, "True", nextOffset.Value, EBlueprintEdgeKind.True));
                }
                break;

            case EX_Skip s:
                // skips its wired expression when unused, then continues at the offset
                Edge("Skip", EBlueprintEdgeKind.Jump, (int)s.CodeOffset);
                Then();
                break;

            case EX_Jump j:
                Edge("→", EBlueprintEdgeKind.Jump, (int)j.CodeOffset);
                break; // unconditional

            case EX_PushExecutionFlow p:
                Edge("Push", EBlueprintEdgeKind.Push, (int)p.PushingAddress);
                Then();
                break;

            case EX_AutoRtfmTransact t:
                Edge("Abort", EBlueprintEdgeKind.Jump, (int)t.CodeOffset);
                Then();
                break;

            case EX_PopExecutionFlow:
            case EX_ComputedJump:
                break; // returns to a runtime-pushed address; no statically known target

            default:
                Then();
                break;
        }

        // ubergraph dispatch (and similar) is a local/virtual call whose single arg is the entry offset;
        // the offset is into the *target* function, so it's resolved globally after all functions exist
        foreach (var (targetFunc, offset) in FindCallOffsets(stmt))
        {
            AddPin(node, "Call", EBlueprintEdgeKind.Call);
            _pendingCalls.Add((node, "Call", targetFunc, offset));
        }
    }

    /// <summary>
    /// Adds the statement's value pins, all read from the decoded tokens: call arguments and branch
    /// conditions become data input pins (with the decoded literal/variable shown as the pin label),
    /// assignment targets become a data output pin. Variable reads/writes are recorded so value flow
    /// can be wired producer → consumer afterwards.
    /// </summary>
    private void AnalyzeData(KismetExpression stmt, BlueprintGraphNode node,
        List<(string Var, BlueprintGraphNode Node, string Pin)> writes,
        List<(BlueprintGraphNode Node, string Pin, string Var, string Token)> reads)
    {
        KismetExpression rhs = null;
        string destName = null;
        switch (stmt)
        {
            // delegate statements carry their operands directly on the token (editor: Bind/Unbind/Create Event)
            case EX_AddMulticastDelegate am:
                AddDataInput(node, "Target", am.Delegate, reads);
                AddDataInput(node, "Event", am.DelegateToAdd, reads);
                break;
            case EX_RemoveMulticastDelegate rem:
                AddDataInput(node, "Target", rem.Delegate, reads);
                AddDataInput(node, "Event", rem.DelegateToAdd, reads);
                break;
            case EX_ClearMulticastDelegate clear:
                AddDataInput(node, "Target", clear.DelegateToClear, reads);
                break;
            case EX_BindDelegate bind:
                AddDataInput(node, "Target", bind.ObjectTerm, reads);
                destName = VarName(bind.Delegate); // the created delegate flows out of this node
                break;
            // container fills (editor: Make Array / Make Set / Make Map): one input per element
            case EX_SetArray setArray:
                destName = VarName(setArray.AssigningProperty);
                for (var e = 0; e < setArray.Elements.Length; e++)
                    AddDataInput(node, $"[{e}]", setArray.Elements[e], reads);
                break;
            case EX_SetSet setSet:
                destName = VarName(setSet.SetProperty);
                for (var e = 0; e < setSet.Elements.Length; e++)
                    AddDataInput(node, $"[{e}]", setSet.Elements[e], reads);
                break;
            case EX_SetMap setMap:
                destName = VarName(setMap.MapProperty);
                for (var e = 0; e < setMap.Elements.Length; e++)
                    AddDataInput(node, e % 2 == 0 ? $"[{e / 2}] Key" : $"[{e / 2}] Value", setMap.Elements[e], reads);
                break;
            case EX_Let let:
                destName = (let.Variable as EX_VariableBase)?.Variable?.ToString() ?? let.Property?.ToString();
                rhs = let.Assignment;
                break;
            case EX_LetBase letBase:
                destName = (letBase.Variable as EX_VariableBase)?.Variable?.ToString();
                rhs = letBase.Assignment;
                break;
            case EX_LetValueOnPersistentFrame frame:
                destName = frame.DestinationProperty?.ToString();
                rhs = frame.AssignmentExpression;
                break;
            case EX_JumpIfNot jumpIfNot:
                AddDataInput(node, "Condition", jumpIfNot.BooleanExpression, reads);
                break;
            case EX_PopExecutionFlowIfNot popIfNot:
                AddDataInput(node, "Condition", popIfNot.BooleanExpression, reads);
                break;
            case EX_Return ret when ret.ReturnExpression is not (null or EX_Nothing):
                AddDataInput(node, "Return Value", ret.ReturnExpression, reads);
                break;
            default:
                rhs = stmt; // top-level call statement (possibly wrapped in a context)
                break;
        }

        if (rhs != null)
        {
            // unwrap object contexts: each contributes the editor's Target pin
            var expr = rhs;
            var guard = 0;
            while (expr is EX_Context context && guard++ < 8)
            {
                AddDataInput(node, "Target", context.ObjectExpression, reads);
                expr = context.ContextExpression;
            }
            switch (expr)
            {
                case EX_FinalFunction finalFn: // includes EX_CallMath / EX_LocalFinalFunction
                    AddCallInputs(node, finalFn.StackNode, finalFn.Parameters, reads);
                    break;
                case EX_VirtualFunction virtualFn:
                    AddCallInputs(node, null, virtualFn.Parameters, reads, ShortName(virtualFn.VirtualFunctionName.Text));
                    break;
                case EX_CallMulticastDelegate multicast:
                    AddDataInput(node, "Delegate", multicast.Delegate, reads);
                    AddCallInputs(node, null, multicast.Parameters, reads);
                    break;
                default:
                    if (destName != null) AddDataInput(node, "Value", expr, reads);
                    break;
            }
        }

        if (destName == null) return;
        node.OutputPins.Add(new BlueprintGraphPin { Name = destName, Kind = EBlueprintEdgeKind.Data, IsData = true });
        writes.Add((destName, node, destName));
    }

    /// <summary>
    /// Draws value wires producer → consumer. A read is wired only when exactly ONE definition of
    /// the variable can reach it along the decoded exec edges (reaching-definitions over the real
    /// CFG) — anything ambiguous stays an unwired, labelled pin rather than a guess. Dynamic flow
    /// is over-approximated (pops may resume at any pushed address, computed jumps anywhere), which
    /// can only suppress wires, never invent them. Returns the (node, pin) reads that got a wire so
    /// the rest can surface as Get nodes instead.
    /// </summary>
    private HashSet<(BlueprintGraphNode, string)> WireDataFlow(HashSet<BlueprintGraphNode> functionNodes,
        List<(string Var, BlueprintGraphNode Node, string Pin)> writes,
        List<(BlueprintGraphNode Node, string Pin, string Var, string Token)> reads)
    {
        var wired = new HashSet<(BlueprintGraphNode, string)>();
        if (writes.Count == 0 || reads.Count == 0) return wired;

        // successor map over this function's exec edges (data/call edges excluded)
        var successors = new Dictionary<BlueprintGraphNode, List<BlueprintGraphNode>>();
        foreach (var n in functionNodes) successors[n] = [];
        var pushTargets = new List<BlueprintGraphNode>();
        foreach (var c in Connections)
        {
            if (c.Kind is EBlueprintEdgeKind.Data or EBlueprintEdgeKind.Call) continue;
            if (!functionNodes.Contains(c.SourceNode) || !functionNodes.Contains(c.TargetNode)) continue;
            successors[c.SourceNode].Add(c.TargetNode);
            if (c.Kind == EBlueprintEdgeKind.Push) pushTargets.Add(c.TargetNode);
        }
        foreach (var n in functionNodes)
        {
            // a pop resumes at some pushed address; a computed jump may land on any statement
            if (n.TokenName is nameof(EX_PopExecutionFlow) or nameof(EX_PopExecutionFlowIfNot))
                successors[n].AddRange(pushTargets);
            else if (n.TokenName == nameof(EX_ComputedJump))
                successors[n].AddRange(functionNodes.Where(m => m.StatementIndex >= 0));
        }

        var defsByVar = writes.GroupBy(w => w.Var)
            .ToDictionary(g => g.Key, g => g.Select(w => (w.Node, w.Pin)).Distinct().ToList());

        // can execution get from a definition to the read without passing through a competing definition?
        bool Reaches(BlueprintGraphNode def, BlueprintGraphNode read, List<BlueprintGraphNode> kills)
        {
            var visited = new HashSet<BlueprintGraphNode>();
            var stack = new Stack<BlueprintGraphNode>(successors[def]);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n == read) return true;
                if (!visited.Add(n)) continue;
                if (n != def && kills.Contains(n)) continue; // redefined on this path
                foreach (var s in successors[n]) stack.Push(s);
            }
            return false;
        }

        foreach (var (readNode, pinName, varName, _) in reads)
        {
            if (!defsByVar.TryGetValue(varName, out var defs)) continue;
            (BlueprintGraphNode Node, string Pin)? producer = null;
            if (defs.Count == 1)
            {
                if (defs[0].Node != readNode) producer = defs[0];
            }
            else
            {
                var kills = defs.Select(d => d.Node).ToList();
                var reaching = defs.Where(d => d.Node != readNode && Reaches(d.Node, readNode, kills)).ToList();
                if (reaching.Count == 1) producer = reaching[0];
            }
            if (producer == null) continue;
            Connections.Add(new BlueprintGraphConnection
            {
                SourceNode = producer.Value.Node, SourcePinName = producer.Value.Pin,
                TargetNode = readNode, TargetPinName = pinName,
                Kind = EBlueprintEdgeKind.Data,
            });
            wired.Add((readNode, pinName));
        }
        return wired;
    }

    /// <summary>
    /// Every variable read that couldn't be wired to a producer becomes an editor-style Get node
    /// feeding the reading pin — the read token (EX_InstanceVariable / EX_LocalVariable / …) sits
    /// right in the statement's argument tree, so this only re-presents what the bytecode already
    /// says. One Get node per (consumer, variable); literals and self stay inline pin labels.
    /// </summary>
    private void CreateGetNodes(BlueprintFunctionInfo info,
        List<(BlueprintGraphNode Node, string Pin, string Var, string Token)> reads,
        HashSet<(BlueprintGraphNode, string)> wired)
    {
        foreach (var group in reads.Where(r => !wired.Contains((r.Node, r.Pin))).GroupBy(r => (r.Node, r.Var)))
        {
            var (consumer, variable) = group.Key;
            var get = new BlueprintGraphNode
            {
                Name = $"{consumer.Name}#get:{variable}",
                Title = $"Get {variable}",
                FunctionName = consumer.FunctionName,
                StatementIndex = consumer.StatementIndex, // layout: sits one column left of its consumer
                TokenName = group.First().Token,
                Kind = EBlueprintNodeKind.VariableGet,
            };
            get.OutputPins.Add(new BlueprintGraphPin { Name = variable, Kind = EBlueprintEdgeKind.Data, IsData = true });
            get.DisplayProperties.Add(new("Variable", variable));
            get.DisplayProperties.Add(new("Read Token", group.First().Token));
            foreach (var (_, pin, _, _) in group)
            {
                get.DisplayProperties.Add(new("Feeds", $"{consumer.Title} · {pin}"));
                Connections.Add(new BlueprintGraphConnection
                {
                    SourceNode = get, SourcePinName = variable,
                    TargetNode = consumer, TargetPinName = pin,
                    Kind = EBlueprintEdgeKind.Data,
                });
            }
            ComputeHeight(get);
            Nodes.Add(get);
            info.Nodes.Add(get);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // Editor-node pattern pass. The Kismet compiler lowers several editor nodes into fixed
    // multi-statement sequences; these recognizers match those exact sequences (taken from the
    // engine's own node handlers — FKCHandler_DynamicCast, FKCHandler_ExecutionSequence,
    // FKCHandler_Switch in Editor/BlueprintGraph) and collapse each back into the single editor
    // node it came from, with the editor's pin names. A group is only collapsed when its interior
    // statements are reachable exclusively from inside the group, so no foreign jump target is
    // ever swallowed — anything that doesn't match exactly stays as raw statements.
    // ────────────────────────────────────────────────────────────────────────────────────────

    private void CollapsePatterns(BlueprintFunctionInfo info)
    {
        var writes = _writesByFunc[info];
        var reads = _readsByFunc[info];
        var changed = true;
        while (changed)
        {
            changed = false;
            var stmts = info.Nodes.Where(n => n.StatementIndex >= 0).OrderBy(n => n.StatementIndex).ToList();
            for (var i = 0; i < stmts.Count && !changed; i++)
            {
                changed = TryCollapseCast(info, stmts[i], writes, reads)
                       || TryCollapseSequence(info, stmts[i])
                       || TryCollapseSwitch(info, stmts[i], writes, reads);
            }
        }
    }

    /// <summary>The statement this exec pin flows into, or null (data/call edges don't count).</summary>
    private BlueprintGraphNode ExecTarget(BlueprintGraphNode source, string pin)
    {
        foreach (var c in Connections)
            if (c.SourceNode == source && c.SourcePinName == pin
                && c.Kind is not (EBlueprintEdgeKind.Data or EBlueprintEdgeKind.Call))
                return c.TargetNode;
        return null;
    }

    /// <summary>True when every exec/call edge into <paramref name="node"/> comes from an allowed source — the safety gate for merging interior statements.</summary>
    private bool OnlyEnteredFrom(BlueprintGraphNode node, BlueprintGraphNode allowed)
    {
        foreach (var c in Connections)
            if (c.TargetNode == node && c.Kind != EBlueprintEdgeKind.Data && c.SourceNode != allowed)
                return false;
        return true;
    }

    /// <summary>Groups containing a cross-function dispatch pin are never merged (the Call edge would lose its true source).</summary>
    private static bool NoCallPins(IEnumerable<BlueprintGraphNode> group) =>
        group.All(n => n.OutputPins.All(p => p.Kind != EBlueprintEdgeKind.Call));

    /// <summary>
    /// FKCHandler_DynamicCast emits exactly: Let(result = DynamicCast&lt;T&gt;(obj)) →
    /// Let(bool = PrimitiveCast&lt;ObjectToBool&gt;(result)) → [JumpIfNot(bool) → CastFailed] for an
    /// impure cast (the success goto is usually peepholed into fall-through). Collapses to the
    /// editor's Cast To T node: Object in, then / Cast Failed exec outs, "As T" data out.
    /// </summary>
    private bool TryCollapseCast(BlueprintFunctionInfo info, BlueprintGraphNode n0,
        List<(string Var, BlueprintGraphNode Node, string Pin)> writes,
        List<(BlueprintGraphNode Node, string Pin, string Var, string Token)> reads)
    {
        if (_stmtByNode.GetValueOrDefault(n0) is not EX_Let let0
            || let0.Assignment is not EX_CastBase cast
            || cast is not (EX_DynamicCast or EX_ObjToInterfaceCast or EX_CrossInterfaceCast or EX_InterfaceToObjCast or EX_MetaCast))
            return false;
        var castVar = (let0.Variable as EX_VariableBase)?.Variable?.ToString() ?? let0.Property?.ToString();
        if (castVar == null) return false;

        var n1 = ExecTarget(n0, "Then");
        if (n1 == null || !info.Nodes.Contains(n1) || !OnlyEnteredFrom(n1, n0)) return false;
        if (_stmtByNode.GetValueOrDefault(n1) is not EX_Let let1
            || let1.Assignment is not EX_Cast toBool
            || toBool.ConversionType is not (ECastToken.CST_ObjectToBool or ECastToken.CST_InterfaceToBool
                or ECastToken.CST_ObjectToBool2 or ECastToken.CST_InterfaceToBool2)
            || VarName(toBool.Target) != castVar)
            return false;
        var boolVar = (let1.Variable as EX_VariableBase)?.Variable?.ToString() ?? let1.Property?.ToString();
        if (boolVar == null) return false;

        var group = new List<BlueprintGraphNode> { n0, n1 };
        var pinMap = new Dictionary<(BlueprintGraphNode, string), string>();
        var className = ShortName(cast.ClassPtr?.Name);
        var execPins = new List<BlueprintGraphPin>();

        var n2 = ExecTarget(n1, "Then");
        var impure = n2 != null && info.Nodes.Contains(n2)
            && _stmtByNode.GetValueOrDefault(n2) is EX_JumpIfNot branch
            && VarName(branch.BooleanExpression) == boolVar
            && OnlyEnteredFrom(n2, n1);
        if (impure)
        {
            group.Add(n2);
            // success path: fall-through, or one absorbed EX_Jump the peephole left behind
            var success = ExecTarget(n2, "True");
            if (success != null && info.Nodes.Contains(success)
                && _stmtByNode.GetValueOrDefault(success) is EX_Jump && OnlyEnteredFrom(success, n2)
                && NoCallPins([success]))
            {
                group.Add(success);
                pinMap[(success, "→")] = "Then";
            }
            else pinMap[(n2, "True")] = "Then";
            pinMap[(n2, "False")] = "Cast Failed";
            execPins.Add(new BlueprintGraphPin { Name = "Then", Kind = EBlueprintEdgeKind.Then });
            execPins.Add(new BlueprintGraphPin { Name = "Cast Failed", Kind = EBlueprintEdgeKind.False });
        }
        else
        {
            pinMap[(n1, "Then")] = "Then";
            execPins.Add(new BlueprintGraphPin { Name = "Then", Kind = EBlueprintEdgeKind.Then });
        }
        if (!NoCallPins(group)) return false;

        var merged = NewPatternNode(n0, group, $"Cast To {className}", EBlueprintNodeKind.Cast,
            "K2Node_DynamicCast lowering (FKCHandler_DynamicCast)");
        merged.InputPins.Add(new BlueprintGraphPin { Name = "In", Kind = EBlueprintEdgeKind.Then });
        merged.InputPins.Add(new BlueprintGraphPin
        {
            Name = "Object", Kind = EBlueprintEdgeKind.Data, IsData = true, Label = DescribeArg(cast.Target),
        });
        merged.OutputPins.AddRange(execPins);
        merged.OutputPins.Add(new BlueprintGraphPin { Name = $"As {className}", Kind = EBlueprintEdgeKind.Data, IsData = true });

        reads.RemoveAll(r => group.Contains(r.Node));
        writes.RemoveAll(w => group.Contains(w.Node));
        writes.Add((castVar, merged, $"As {className}"));
        if (reads.Any(r => r.Var == boolVar))
        {
            // the success bool is read beyond the branch (pure-cast style) — expose it like the editor does
            merged.OutputPins.Add(new BlueprintGraphPin { Name = "Success", Kind = EBlueprintEdgeKind.Data, IsData = true });
            writes.Add((boolVar, merged, "Success"));
        }
        if (VarName(cast.Target) is { } sourceVar)
            reads.Add((merged, "Object", sourceVar, cast.Target.GetType().Name));

        ReplaceGroup(info, group, merged, pinMap);
        return true;
    }

    /// <summary>
    /// FKCHandler_ExecutionSequence (non-instrumented, the cooked case) emits one KCST_PushState per
    /// output pin except the first — in reverse order — then jumps to the first pin. So a run of
    /// EX_PushExecutionFlow statements is exactly a Sequence node: fall-through/jump = Then 0, the
    /// pushed addresses = Then 1..N in reverse statement order (the flow stack pops LIFO).
    /// </summary>
    private bool TryCollapseSequence(BlueprintFunctionInfo info, BlueprintGraphNode n0)
    {
        if (_stmtByNode.GetValueOrDefault(n0) is not EX_PushExecutionFlow) return false;

        // the first statement of any flow-stack function is the compiler's own return-address push
        // (FScriptBuilderBase::PushReturnAddress, emitted before any node's statements) — it is not
        // a Sequence node, so label it for what it is and leave it alone. Only the function's OWN
        // header counts: after the ubergraph split, event headers also carry Entry edges, and a
        // push right at an event's entry point IS a real Sequence.
        if (Connections.Any(c => c.Kind == EBlueprintEdgeKind.Entry && c.TargetNode == n0
                                 && c.SourceNode.FunctionName == n0.FunctionName))
        {
            if (n0.Title == "Push Flow")
            {
                n0.Title = "Push Return Address";
                n0.DisplayProperties.Add(new("Pattern",
                    "Function prologue: pushes the Return address for latent/flow-stack execution (PushReturnAddress)"));
            }
            return false;
        }

        var group = new List<BlueprintGraphNode> { n0 };
        var cur = n0;
        while (ExecTarget(cur, "Then") is { } next
               && info.Nodes.Contains(next)
               && _stmtByNode.GetValueOrDefault(next) is EX_PushExecutionFlow
               && OnlyEnteredFrom(next, cur))
        {
            group.Add(next);
            cur = next;
        }
        if (!NoCallPins(group)) return false;

        var pinMap = new Dictionary<(BlueprintGraphNode, string), string>();
        var last = group[^1];
        var then0Target = ExecTarget(last, "Then");
        if (then0Target == null) return false;
        if (info.Nodes.Contains(then0Target)
            && _stmtByNode.GetValueOrDefault(then0Target) is EX_Jump
            && OnlyEnteredFrom(then0Target, last) && NoCallPins([then0Target]))
        {
            group.Add(then0Target);
            pinMap[(then0Target, "→")] = "Then 0";
        }
        else pinMap[(last, "Then")] = "Then 0";

        var merged = NewPatternNode(n0, group, "Sequence", EBlueprintNodeKind.Flow,
            "K2Node_ExecutionSequence lowering (KCST_PushState run, pins pop in push-reverse order)");
        merged.InputPins.Add(new BlueprintGraphPin { Name = "In", Kind = EBlueprintEdgeKind.Then });
        merged.OutputPins.Add(new BlueprintGraphPin { Name = "Then 0", Kind = EBlueprintEdgeKind.Then });
        var pushes = group.Where(g => _stmtByNode.GetValueOrDefault(g) is EX_PushExecutionFlow).ToList();
        for (var k = 0; k < pushes.Count; k++)
        {
            var pinName = $"Then {k + 1}";
            merged.OutputPins.Add(new BlueprintGraphPin { Name = pinName, Kind = EBlueprintEdgeKind.Push });
            pinMap[(pushes[pushes.Count - 1 - k], "Push")] = pinName; // pushed last = popped first
        }

        ReplaceGroup(info, group, merged, pinMap);
        return true;
    }

    // the four switch node types and the comparison each one's handler calls per case (K2Node_Switch*.cpp)
    private static readonly Dictionary<string, string> SwitchCmpFunctions = new()
    {
        ["NotEqual_IntInt"] = "Int",
        ["NotEqual_ByteByte"] = "Byte/Enum",
        ["NotEqual_NameName"] = "Name",
        ["NotEqual_StrStr"] = "String",
        ["NotEqual_StriStri"] = "String",
    };

    /// <summary>
    /// FKCHandler_Switch emits, per linked case: Let(CmpSuccess = NotEqual_XX(Selection, CaseLiteral))
    /// then GotoIfNot(CmpSuccess → case body) — i.e. jump when the values are EQUAL — and finally
    /// falls through to the default. Two or more chained pairs on the same bool/selection collapse to
    /// the editor's Switch node: Selection in, one exec out per case plus Default. (A single pair is
    /// left alone: it is indistinguishable from an authored Branch on an equality test.)
    /// </summary>
    private bool TryCollapseSwitch(BlueprintFunctionInfo info, BlueprintGraphNode head,
        List<(string Var, BlueprintGraphNode Node, string Pin)> writes,
        List<(BlueprintGraphNode Node, string Pin, string Var, string Token)> reads)
    {
        var pairs = new List<(BlueprintGraphNode LetNode, BlueprintGraphNode JumpNode, KismetExpression CaseLiteral)>();
        string boolVar = null, cmpFn = null, selText = null;
        KismetExpression selection = null;
        BlueprintGraphNode cur = head, prev = null;

        while (cur != null && info.Nodes.Contains(cur))
        {
            if (_stmtByNode.GetValueOrDefault(cur) is not EX_Let letN
                || letN.Assignment is not EX_FinalFunction cmp || cmp.StackNode == null
                || cmp.Parameters is not { Length: 2 }
                || !SwitchCmpFunctions.ContainsKey(ShortName(cmp.StackNode.Name)))
                break;
            var bv = (letN.Variable as EX_VariableBase)?.Variable?.ToString();
            var fn = ShortName(cmp.StackNode.Name);
            var sel = DescribeArg(cmp.Parameters[0]);
            if (boolVar == null) { boolVar = bv; cmpFn = fn; selText = sel; selection = cmp.Parameters[0]; }
            else if (bv != boolVar || fn != cmpFn || sel != selText) break;
            if (bv == null) break;
            if (pairs.Count > 0 && !OnlyEnteredFrom(cur, prev)) break;

            var jumpNode = ExecTarget(cur, "Then");
            if (jumpNode == null || !info.Nodes.Contains(jumpNode)
                || _stmtByNode.GetValueOrDefault(jumpNode) is not EX_JumpIfNot cond
                || VarName(cond.BooleanExpression) != boolVar
                || !OnlyEnteredFrom(jumpNode, cur))
                break;

            pairs.Add((cur, jumpNode, cmp.Parameters[1]));
            prev = jumpNode;
            cur = ExecTarget(jumpNode, "True");
        }
        if (pairs.Count < 2) return false;

        var group = pairs.SelectMany(p => new[] { p.LetNode, p.JumpNode }).ToList();
        var pinMap = new Dictionary<(BlueprintGraphNode, string), string>();
        var lastJump = pairs[^1].JumpNode;

        // default path: fall-through from the last comparison, or one absorbed trailing EX_Jump
        var defaultTarget = ExecTarget(lastJump, "True");
        if (defaultTarget == null) return false;
        if (info.Nodes.Contains(defaultTarget)
            && _stmtByNode.GetValueOrDefault(defaultTarget) is EX_Jump
            && OnlyEnteredFrom(defaultTarget, lastJump) && NoCallPins([defaultTarget]))
        {
            group.Add(defaultTarget);
            pinMap[(defaultTarget, "→")] = "Default";
        }
        else pinMap[(lastJump, "True")] = "Default";
        if (!NoCallPins(group)) return false;

        var merged = NewPatternNode(head, group, $"Switch on {SwitchCmpFunctions[cmpFn]}", EBlueprintNodeKind.Branch,
            "K2Node_Switch lowering (FKCHandler_Switch compare/branch chain)");
        merged.InputPins.Add(new BlueprintGraphPin { Name = "In", Kind = EBlueprintEdgeKind.Then });
        merged.InputPins.Add(new BlueprintGraphPin
        {
            Name = "Selection", Kind = EBlueprintEdgeKind.Data, IsData = true, Label = selText,
        });
        foreach (var (_, jumpNode, caseLiteral) in pairs)
        {
            var pinName = DescribeArg(caseLiteral);
            var suffix = 2;
            while (merged.OutputPins.Any(p => p.Name == pinName)) pinName = $"{DescribeArg(caseLiteral)} ({suffix++})";
            merged.OutputPins.Add(new BlueprintGraphPin { Name = pinName, Kind = EBlueprintEdgeKind.True });
            pinMap[(jumpNode, "False")] = pinName; // GotoIfNot(NotEqual) jumps when EQUAL — that is the case hit
        }
        merged.OutputPins.Add(new BlueprintGraphPin { Name = "Default", Kind = EBlueprintEdgeKind.False });

        reads.RemoveAll(r => group.Contains(r.Node));
        writes.RemoveAll(w => group.Contains(w.Node));
        if (VarName(selection) is { } selVar)
            reads.Add((merged, "Selection", selVar, selection.GetType().Name));

        ReplaceGroup(info, group, merged, pinMap);
        return true;
    }

    /// <summary>A collapsed pattern node keeps every original statement (offsets + decompiled text) in its panel and body — nothing is hidden, just regrouped.</summary>
    private static BlueprintGraphNode NewPatternNode(BlueprintGraphNode first, List<BlueprintGraphNode> group,
        string title, EBlueprintNodeKind kind, string pattern)
    {
        var merged = new BlueprintGraphNode
        {
            Name = first.Name,
            Title = title,
            Subtitle = $"{group.Count} compiled statements",
            Body = string.Join("\n", group.Select(g => g.Body).Where(b => !string.IsNullOrWhiteSpace(b))),
            TokenName = string.Join(" + ", group.Select(g => g.TokenName)),
            FunctionName = first.FunctionName,
            StatementIndex = first.StatementIndex,
            Kind = kind,
        };
        merged.DisplayProperties.Add(new("Pattern", pattern));
        foreach (var g in group)
            merged.DisplayProperties.Add(new($"@0x{g.StatementIndex:X}",
                string.IsNullOrWhiteSpace(g.Body) ? g.TokenName : g.Body));
        return merged;
    }

    /// <summary>
    /// Swaps a recognized statement group for its merged editor node. Outgoing edges listed in
    /// <paramref name="pinMap"/> move onto the merged node's new pins; edges internal to the group
    /// are consumed by the pattern; every inbound edge re-targets the merged node.
    /// </summary>
    private void ReplaceGroup(BlueprintFunctionInfo info, List<BlueprintGraphNode> group, BlueprintGraphNode merged,
        Dictionary<(BlueprintGraphNode, string), string> pinMap)
    {
        var groupSet = new HashSet<BlueprintGraphNode>(group);
        for (var i = Connections.Count - 1; i >= 0; i--)
        {
            var c = Connections[i];
            var srcIn = groupSet.Contains(c.SourceNode);
            var tgtIn = groupSet.Contains(c.TargetNode);
            if (!srcIn && !tgtIn) continue;
            if (srcIn)
            {
                if (pinMap.TryGetValue((c.SourceNode, c.SourcePinName), out var newPin))
                {
                    c.SourceNode = merged;
                    c.SourcePinName = newPin;
                    if (tgtIn) c.TargetNode = merged; // a pattern looping back into itself stays visible
                }
                else Connections.RemoveAt(i); // edge between two group members — consumed by the pattern
            }
            else c.TargetNode = merged;
        }

        var position = Nodes.IndexOf(group[0]);
        Nodes[position] = merged;
        foreach (var g in group)
        {
            if (g != group[0]) Nodes.Remove(g);
            info.Nodes.Remove(g);
            _stmtByNode.Remove(g);
            _byFuncIndex[(g.FunctionName, g.StatementIndex)] = merged;
        }
        info.Nodes.Add(merged);
        ComputeHeight(merged);
    }

    /// <summary>
    /// The engine's StandardMacros keep their internal temp-variable names through cooking; seeing
    /// one identifies the inlined macro. This only labels the node (macros leave no explicit marker
    /// in bytecode, so it is presented as a recognized pattern, never wired differently).
    /// </summary>
    private static void TagMacroPattern(BlueprintGraphNode node)
    {
        var macro = Mentions("Temp_int_Array_Index_Variable") ? "ForEachLoop"
            : Mentions("Temp_int_Loop_Counter_Variable") ? "ForLoop"
            : null;
        if (macro == null) return;
        if (string.IsNullOrEmpty(node.Subtitle)) node.Subtitle = $"{macro} macro (inlined)";
        node.DisplayProperties.Add(new("Macro Pattern", $"{macro} — engine StandardMacros, inlined at compile time"));
        return;

        bool Mentions(string name) =>
            node.InputPins.Any(p => p.Name.Contains(name) || (p.Label?.Contains(name) ?? false)) ||
            node.OutputPins.Any(p => p.Name.Contains(name));
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // Ubergraph split. The compiler collapses every event graph into one ExecuteUbergraph
    // function; each event becomes a tiny stub that copies its parameters onto the ubergraph's
    // persistent frame and dispatches to a literal entry offset. Merging the stubs back and
    // partitioning the ubergraph by entry offset restores the editor's view: one graph per event,
    // with the event node providing its parameters. Statements are laid down contiguously per
    // entry, and any exec edge that does cross a partition boundary is still drawn — the split
    // only regroups the display, it never hides or invents flow.
    // ────────────────────────────────────────────────────────────────────────────────────────

    private void MergeEventStubs(UClass classExport)
    {
        // the class itself names its event graph function; the name prefix is the compiler's convention
        var uberNames = new HashSet<string>();
        if (classExport is UBlueprintGeneratedClass { UberGraphFunction: { IsNull: false } ug })
            uberNames.Add(ug.Name);
        foreach (var fn in Functions)
            if (fn.Name.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
                uberNames.Add(fn.Name);
        foreach (var uberName in uberNames)
            if (Functions.FirstOrDefault(f => f.Name == uberName) is { } uber)
                SplitUbergraph(uber);
    }

    private void SplitUbergraph(BlueprintFunctionInfo uber)
    {
        var uberWrites = _writesByFunc[uber];
        var uberReads = _readsByFunc[uber];
        var merged = new List<(int Entry, BlueprintGraphNode Header, string StubName, string Signature)>();

        foreach (var stub in Functions.Where(f => f != uber).ToList())
        {
            var stmts = stub.Nodes.Where(n => n.StatementIndex >= 0).OrderBy(n => n.StatementIndex).ToList();
            if (stmts.Count == 0) continue;
            var header = stub.Nodes.FirstOrDefault(n => n.Kind == EBlueprintNodeKind.FunctionHeader);
            if (header == null) continue;

            // exact stub shape: [param → ubergraph-frame copies]*, one dispatch call into this
            // ubergraph, then only Return / End of Script — anything else is a real function
            var copies = new List<(string FrameVar, string Param)>();
            BlueprintGraphConnection callEdge = null;
            var ok = true;
            foreach (var n in stmts)
            {
                var s = _stmtByNode.GetValueOrDefault(n);
                if (callEdge == null)
                {
                    switch (s)
                    {
                        case EX_LetValueOnPersistentFrame frame
                            when frame.DestinationProperty?.ToString() is { } dest && VarName(frame.AssignmentExpression) is { } src:
                            copies.Add((dest, src));
                            continue;
                        case EX_Let let
                            when let.Assignment is EX_VariableBase
                                 && ((let.Variable as EX_VariableBase)?.Variable?.ToString() ?? let.Property?.ToString()) is { } dest:
                            copies.Add((dest, VarName(let.Assignment)));
                            continue;
                    }
                    if (s != null && FindCallOffsets(s).Any(co => co.TargetFunc == uber.Name)
                        && Connections.FirstOrDefault(c => c.SourceNode == n && c.Kind == EBlueprintEdgeKind.Call
                                                           && uber.Nodes.Contains(c.TargetNode)) is { } edge)
                    {
                        callEdge = edge;
                        continue;
                    }
                }
                else if (s is EX_Return or EX_EndOfScript) continue;
                ok = false;
                break;
            }
            if (!ok || callEdge == null) continue;
            // every copied value must be one of the event's own declared parameters (a header pin)
            if (!copies.All(c => header.OutputPins.Any(p => p.IsData && p.Name == c.Param))) continue;

            var entry = callEdge.TargetNode;
            var removed = new HashSet<BlueprintGraphNode>(stmts);
            Connections.RemoveAll(c => removed.Contains(c.SourceNode) || removed.Contains(c.TargetNode));
            foreach (var n in stmts)
            {
                Nodes.Remove(n);
                _stmtByNode.Remove(n);
            }
            Functions.Remove(stub);
            _writesByFunc.Remove(stub);
            _readsByFunc.Remove(stub);

            // the event's parameters land on the ubergraph frame: the header pin (named like the
            // parameter) now provides each frame variable, so reads inside the event wire back to it
            foreach (var (frameVar, param) in copies)
                uberWrites.Add((frameVar, header, param));

            Connections.Add(new BlueprintGraphConnection
            {
                SourceNode = header, SourcePinName = "Entry",
                TargetNode = entry, Kind = EBlueprintEdgeKind.Entry,
            });
            header.DisplayProperties.Add(new("Event Graph",
                $"Stub merged: the compiled event copied its parameters onto the ubergraph frame and dispatched to offset 0x{entry.StatementIndex:X}"));
            uber.Nodes.Add(header);
            merged.Add((entry.StatementIndex, header, stub.Name, stub.Signature));
        }
        if (merged.Count == 0) return;

        // partition by entry offset — the backend lays each event's statements down contiguously,
        // so [entry, next entry) is that event's code. Everything before the first entry is the
        // dispatcher prologue; the pushed return address onwards is the shared tail.
        var stmtsAll = uber.Nodes.Where(n => n.StatementIndex >= 0).OrderBy(n => n.StatementIndex).ToList();
        var firstEntry = merged.Min(m => m.Entry);
        var lastEntry = merged.Max(m => m.Entry);
        var tailStart = int.MaxValue;
        if (stmtsAll.Count > 0 && _stmtByNode.GetValueOrDefault(stmtsAll[0]) is EX_PushExecutionFlow
            && ExecTarget(stmtsAll[0], "Push") is { } returnNode && returnNode.StatementIndex > lastEntry)
            tailStart = returnNode.StatementIndex;

        var entryOffsets = merged.Select(m => m.Entry).Distinct().OrderBy(x => x).ToList();
        var newInfos = new List<BlueprintFunctionInfo>();
        for (var i = 0; i < entryOffsets.Count; i++)
        {
            var entry = entryOffsets[i];
            var end = i + 1 < entryOffsets.Count ? entryOffsets[i + 1] : tailStart;
            var events = merged.Where(m => m.Entry == entry).ToList(); // two events can share one entry
            var info = new BlueprintFunctionInfo
            {
                Name = string.Join(" / ", events.Select(ev => ev.StubName)),
                Signature = events[0].Signature,
            };
            foreach (var ev in events) info.Nodes.Add(ev.Header);
            foreach (var n in stmtsAll.Where(n => n.StatementIndex >= entry && n.StatementIndex < end))
                info.Nodes.Add(n);
            info.StatementCount = info.Nodes.Count(n => n.StatementIndex >= 0);
            newInfos.Add(info);
        }

        var moved = new HashSet<BlueprintGraphNode>(newInfos.SelectMany(ni => ni.Nodes));
        uber.Nodes.RemoveWhere(moved.Contains);
        uber.StatementCount = uber.Nodes.Count(n => n.StatementIndex >= 0);
        uber.Name += " (dispatcher)";

        foreach (var info in newInfos)
        {
            _writesByFunc[info] = uberWrites.Where(w => info.Nodes.Contains(w.Node)).ToList();
            _readsByFunc[info] = uberReads.Where(r => info.Nodes.Contains(r.Node)).ToList();
        }
        _writesByFunc[uber] = uberWrites.Where(w => uber.Nodes.Contains(w.Node)).ToList();
        _readsByFunc[uber] = uberReads.Where(r => uber.Nodes.Contains(r.Node)).ToList();
        Functions.InsertRange(Functions.IndexOf(uber), newInfos); // events first, dispatcher after
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // Pure calls. The editor draws calls to BlueprintPure functions without exec pins; the
    // compiler still emits them as ordinary Let(temp = Call(...)) statements. When purity is
    // PROVEN — the called UFunction's serialized FUNC_BlueprintPure flag, or its
    // UFUNCTION(BlueprintPure) declaration mined from the engine source — and the statement's
    // flow is strictly linear, the exec wire is routed straight through and the node keeps only
    // its value pins. Anything unproven keeps its exec pins.
    // ────────────────────────────────────────────────────────────────────────────────────────

    private void DetachPureCalls(BlueprintFunctionInfo info)
    {
        foreach (var node in info.Nodes.Where(n => n.StatementIndex >= 0).OrderBy(n => n.StatementIndex).ToList())
        {
            var rhs = _stmtByNode.GetValueOrDefault(node) switch
            {
                EX_Let let => let.Assignment,
                EX_LetBase letBase => letBase.Assignment,
                _ => null,
            };
            if (rhs == null) continue;
            var (pure, evidence) = IsPureCall(rhs);
            if (!pure) continue;

            BlueprintGraphConnection execIn = null, execOut = null;
            int execInCount = 0, execOutCount = 0;
            var hasDataOut = false;
            foreach (var c in Connections)
            {
                if (c.Kind == EBlueprintEdgeKind.Data)
                {
                    if (c.SourceNode == node) hasDataOut = true;
                    continue;
                }
                if (c.Kind == EBlueprintEdgeKind.Call) continue;
                if (c.TargetNode == node) { execIn = c; execInCount++; }
                if (c.SourceNode == node) { execOut = c; execOutCount++; }
            }
            // only a straight pass-through with a visible consumer detaches — jump targets,
            // branch sources and dangling results keep their exec pins
            if (execInCount != 1 || execOutCount != 1 || !hasDataOut || execOut.TargetNode == node) continue;

            execIn.TargetNode = execOut.TargetNode;
            execIn.TargetPinName = execOut.TargetPinName;
            Connections.Remove(execOut);
            node.InputPins.RemoveAll(p => !p.IsData);
            node.OutputPins.RemoveAll(p => !p.IsData);
            node.Kind = EBlueprintNodeKind.PureCall;
            if (string.IsNullOrEmpty(node.Subtitle)) node.Subtitle = "pure";
            node.DisplayProperties.Add(new("Pure", evidence));
            ComputeHeight(node);
        }
    }

    /// <summary>Whether the expression is a call to a provably BlueprintPure function, and the evidence for it.</summary>
    private (bool Pure, string Evidence) IsPureCall(KismetExpression rhs)
    {
        var expr = rhs;
        var guard = 0;
        while (expr is EX_Context context && guard++ < 8) expr = context.ContextExpression;

        switch (expr)
        {
            case EX_FinalFunction finalFn when finalFn.StackNode != null:
            {
                // the called function's own serialized flags, when it lives in the paks
                try
                {
                    if (finalFn.StackNode.TryLoad(out var loaded) && loaded is UFunction called)
                        return called.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintPure)
                            ? (true, "FUNC_BlueprintPure flag on the called function")
                            : (false, null);
                }
                catch { /* native /Script imports aren't in the paks — engine-source table below */ }
                var name = ShortName(finalFn.StackNode.Name);
                var outer = finalFn.StackNode.ResolvedObject?.Outer?.Name.Text;
                if (outer != null && BlueprintPureTable.PureFull.Contains($"{ShortName(outer)}:{name}"))
                    return (true, $"UFUNCTION(BlueprintPure) on {ShortName(outer)}::{name} in the engine source");
                if (BlueprintPureTable.PureNameOnly.Contains(name))
                    return (true, $"UFUNCTION(BlueprintPure) on {name} in the engine source (name is never impure there)");
                return (false, null);
            }
            case EX_VirtualFunction virtualFn:
            {
                var name = ShortName(virtualFn.VirtualFunctionName.Text);
                if (_functionByName.TryGetValue(name, out var own))
                    return own.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintPure)
                        ? (true, "FUNC_BlueprintPure flag on this class's function")
                        : (false, null);
                if (FindClassFunction(name) is { } inherited)
                    return inherited.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintPure)
                        ? (true, "FUNC_BlueprintPure flag on the inherited function")
                        : (false, null);
                return BlueprintPureTable.PureNameOnly.Contains(name)
                    ? (true, $"UFUNCTION(BlueprintPure) on {name} in the engine source (name is never impure there)")
                    : (false, null);
            }
        }
        return (false, null);
    }

    /// <summary>Resolves a function by name through the class's serialized FuncMap chain (parent Blueprints in the paks).</summary>
    private UFunction FindClassFunction(string name)
    {
        var cls = _classExport;
        for (var guard = 0; cls != null && guard < 16; guard++)
        {
            try
            {
                if (cls.FuncMap != null)
                    foreach (var (fname, index) in cls.FuncMap)
                        if (fname.Text == name && index.TryLoad(out var obj) && obj is UFunction found)
                            return found;
                cls = cls.SuperStruct != null && cls.SuperStruct.TryLoad(out var super) && super is UClass superClass
                    ? superClass : null;
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>The plain variable name behind an expression, when it is a direct variable token.</summary>
    private static string VarName(KismetExpression e) => (e as EX_VariableBase)?.Variable?.ToString();

    /// <summary>
    /// One data input pin per call argument. Parameter names come from real evidence only:
    /// the called UFunction's serialized property list when it is loadable from the paks, else the
    /// mined C++ declaration of the native function in the engine source — and the mined list is
    /// used only when its length matches the argument count exactly. Anything unproven stays
    /// positional ("Param N"), and the evidence source is recorded on the node.
    /// </summary>
    private void AddCallInputs(BlueprintGraphNode node, FPackageIndex stackNode, KismetExpression[] parameters,
        List<(BlueprintGraphNode Node, string Pin, string Var, string Token)> reads, string virtualName = null)
    {
        string[] names = null;
        string evidence = null;
        try
        {
            if (stackNode != null && stackNode.TryLoad(out var loaded) && loaded is UFunction called)
            {
                names = CollectParams(called).Where(p => p.Dir != "Return").Select(p => p.Name).ToArray();
                evidence = "serialized parameter properties of the called function";
            }
        }
        catch { /* imports from script packages aren't in the paks — engine-source table below */ }

        if (names == null && virtualName != null)
        {
            // virtual calls carry only a name; resolve through this class's own/inherited functions
            var fn = _functionByName.TryGetValue(virtualName, out var own) ? own : FindClassFunction(virtualName);
            if (fn != null)
            {
                names = CollectParams(fn).Where(p => p.Dir != "Return").Select(p => p.Name).ToArray();
                evidence = "serialized parameter properties of the resolved function";
            }
        }

        if (names == null)
        {
            var fnName = virtualName ?? (stackNode != null ? ShortName(stackNode.Name) : null);
            var outer = stackNode?.ResolvedObject?.Outer?.Name.Text;
            string joined = null;
            if (fnName != null)
            {
                if (outer != null && BlueprintParamTable.ParamsFull.TryGetValue($"{ShortName(outer)}:{fnName}", out joined))
                    evidence = $"C++ declaration of {ShortName(outer)}::{fnName} in the engine source";
                else if (BlueprintParamTable.ParamsByName.TryGetValue(fnName, out joined))
                    evidence = $"C++ declaration of {fnName} in the engine source (every declaration of that name agrees)";
            }
            if (joined != null)
            {
                var mined = joined.Split(',');
                if (mined.Length == parameters.Length) names = mined;
                else { evidence = null; } // count mismatch (engine version drift) — don't mislabel
            }
            else evidence = null;
        }

        if (names != null && evidence != null && parameters.Length > 0)
            node.DisplayProperties.Add(new("Param Names", evidence));

        for (var i = 0; i < parameters.Length; i++)
        {
            var name = names != null && i < names.Length ? names[i] : $"Param {i + 1}";
            AddDataInput(node, name, parameters[i], reads);
        }
    }

    private void AddDataInput(BlueprintGraphNode node, string name, KismetExpression argument,
        List<(BlueprintGraphNode Node, string Pin, string Var, string Token)> reads)
    {
        if (argument is null or EX_Nothing) return;
        var pinName = name;
        var suffix = 2;
        while (node.InputPins.Any(p => p.Name == pinName)) pinName = $"{name} ({suffix++})";
        node.InputPins.Add(new BlueprintGraphPin
        {
            Name = pinName,
            Kind = EBlueprintEdgeKind.Data,
            IsData = true,
            Label = DescribeArg(argument),
        });
        if (argument is EX_VariableBase { Variable: { } pointer })
            reads.Add((node, pinName, pointer.ToString(), argument.GetType().Name));
    }

    /// <summary>Short decoded text for an argument (a literal, variable name or expression), for the pin label.</summary>
    private static string DescribeArg(KismetExpression argument)
    {
        var text = argument switch
        {
            EX_VariableBase { Variable: { } pointer } => pointer.ToString(),
            EX_Self => "self",
            EX_ObjectConst { Value: { } objRef } => ShortName(objRef.Name), // e.g. a static function library class
            _ => SafeDecompile(argument),
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            text = argument.GetType().Name;
            if (text.StartsWith("EX_")) text = text[3..];
        }
        text = text.Replace('\n', ' ').Trim();
        return text.Length > 34 ? text[..34] + "…" : text;
    }

    /// <summary>Finds every local/virtual function call whose only parameter is an int offset (an execution-flow dispatch), recursively.</summary>
    private static IEnumerable<(string TargetFunc, int Offset)> FindCallOffsets(KismetExpression expr)
    {
        var results = new List<(string, int)>();
        void Visit(KismetExpression e, int depth)
        {
            if (e == null || depth > 24) return;
            switch (e)
            {
                case EX_LocalFinalFunction f when f.Parameters is [EX_IntConst c]:
                    results.Add((ShortName(f.StackNode?.Name), c.Value));
                    break;
                case EX_VirtualFunction v when v.Parameters is [EX_IntConst c]:
                    results.Add((ShortName(v.VirtualFunctionName.Text), c.Value));
                    break;
            }
            foreach (var child in EnumerateChildren(e))
                Visit(child, depth + 1);
        }
        Visit(expr, 0);
        return results;
    }

    /// <summary>Best-effort walk of an expression's child expressions for the container tokens that can hold a dispatch call.</summary>
    private static IEnumerable<KismetExpression> EnumerateChildren(KismetExpression e)
    {
        switch (e)
        {
            case EX_Let let:
                if (let.Variable != null) yield return let.Variable;
                if (let.Assignment != null) yield return let.Assignment;
                break;
            case EX_LetBase letBase:
                if (letBase.Variable != null) yield return letBase.Variable;
                if (letBase.Assignment != null) yield return letBase.Assignment;
                break;
            case EX_LetValueOnPersistentFrame p:
                if (p.AssignmentExpression != null) yield return p.AssignmentExpression;
                break;
            case EX_Context ctx:
                if (ctx.ObjectExpression != null) yield return ctx.ObjectExpression;
                if (ctx.ContextExpression != null) yield return ctx.ContextExpression;
                break;
            case EX_FinalFunction fn: // includes EX_CallMath, EX_LocalFinalFunction
                foreach (var p2 in fn.Parameters) yield return p2;
                break;
            case EX_VirtualFunction vf: // includes EX_LocalVirtualFunction
                foreach (var p2 in vf.Parameters) yield return p2;
                break;
            case EX_CallMulticastDelegate mc:
                if (mc.Delegate != null) yield return mc.Delegate;
                foreach (var p2 in mc.Parameters) yield return p2;
                break;
            case EX_Return r:
                if (r.ReturnExpression != null) yield return r.ReturnExpression;
                break;
            case EX_JumpIfNot j:
                if (j.BooleanExpression != null) yield return j.BooleanExpression;
                break;
            case EX_PopExecutionFlowIfNot pf:
                if (pf.BooleanExpression != null) yield return pf.BooleanExpression;
                break;
            case EX_Cast c:
                if (c.Target != null) yield return c.Target;
                break;
            case EX_CastBase cb:
                if (cb.Target != null) yield return cb.Target;
                break;
            case EX_StructConst sc:
                foreach (var p2 in sc.Properties) yield return p2;
                break;
            case EX_ArrayConst ac:
                foreach (var p2 in ac.Elements) yield return p2;
                break;
            case EX_SetConst setc:
                foreach (var p2 in setc.Elements) yield return p2;
                break;
            case EX_SetArray sa:
                if (sa.AssigningProperty != null) yield return sa.AssigningProperty;
                foreach (var p2 in sa.Elements) yield return p2;
                break;
            case EX_SetSet ss:
                if (ss.SetProperty != null) yield return ss.SetProperty;
                foreach (var p2 in ss.Elements) yield return p2;
                break;
            case EX_SetMap sm:
                if (sm.MapProperty != null) yield return sm.MapProperty;
                foreach (var p2 in sm.Elements) yield return p2;
                break;
            case EX_SwitchValue sw:
                if (sw.IndexTerm != null) yield return sw.IndexTerm;
                foreach (var cse in sw.Cases)
                {
                    if (cse.CaseIndexValueTerm != null) yield return cse.CaseIndexValueTerm;
                    if (cse.CaseTerm != null) yield return cse.CaseTerm;
                }
                if (sw.DefaultTerm != null) yield return sw.DefaultTerm;
                break;
            case EX_StructMemberContext smc:
                if (smc.StructExpression != null) yield return smc.StructExpression;
                break;
            case EX_InterfaceContext ic:
                if (ic.InterfaceValue != null) yield return ic.InterfaceValue;
                break;
            case EX_ArrayGetByRef ag:
                if (ag.ArrayVariable != null) yield return ag.ArrayVariable;
                if (ag.ArrayIndex != null) yield return ag.ArrayIndex;
                break;
            case EX_Skip sk:
                if (sk.SkipExpression != null) yield return sk.SkipExpression;
                break;
            case EX_AddMulticastDelegate am:
                if (am.Delegate != null) yield return am.Delegate;
                if (am.DelegateToAdd != null) yield return am.DelegateToAdd;
                break;
            case EX_RemoveMulticastDelegate rm:
                if (rm.Delegate != null) yield return rm.Delegate;
                if (rm.DelegateToAdd != null) yield return rm.DelegateToAdd;
                break;
        }
    }

    private static bool IsRealStatement(KismetExpression e) => e is not (
        EX_Nothing or EX_NothingInt32 or EX_EndFunctionParms or EX_EndStructConst or EX_EndArray or
        EX_EndArrayConst or EX_EndSet or EX_EndMap or EX_EndMapConst or EX_EndSetConst or EX_EndParmValue);

    /// <summary>Runs the shared decompiler for the node body; falls back to the token name if it can't render one.</summary>
    private static string SafeDecompile(KismetExpression stmt)
    {
        try
        {
            var line = BlueprintDecompilerUtils.GetLineExpression(stmt);
            return string.IsNullOrWhiteSpace(line) ? string.Empty : line.Trim();
        }
        catch
        {
            return string.Empty; // unsupported token: the title still names it, the panel shows the raw token
        }
    }

    private static EBlueprintNodeKind ClassifyKind(KismetExpression stmt) => stmt switch
    {
        EX_JumpIfNot => EBlueprintNodeKind.Branch,
        EX_Jump or EX_ComputedJump or EX_Skip => EBlueprintNodeKind.Jump,
        EX_PushExecutionFlow or EX_PopExecutionFlow or EX_PopExecutionFlowIfNot => EBlueprintNodeKind.Flow,
        EX_Return or EX_EndOfScript => EBlueprintNodeKind.Return,
        EX_LocalFinalFunction or EX_VirtualFunction or EX_FinalFunction or EX_CallMulticastDelegate => EBlueprintNodeKind.Call,
        EX_Let or EX_LetBase or EX_LetValueOnPersistentFrame or EX_SetArray or EX_SetMap or EX_SetSet => EBlueprintNodeKind.Assign,
        _ => EBlueprintNodeKind.Statement,
    };

    private static string DescribeTitle(KismetExpression stmt, string body)
    {
        switch (stmt)
        {
            case EX_JumpIfNot: return "Branch";
            case EX_Skip: return "Skip"; // EX_Skip : EX_Jump — must precede the EX_Jump case
            case EX_Jump: return "Jump";
            case EX_ComputedJump: return "Computed Jump";
            case EX_Return: return "Return";
            case EX_EndOfScript: return "End of Script";
            case EX_PushExecutionFlow: return "Push Flow";
            case EX_PopExecutionFlow: return "Pop Flow";
            case EX_PopExecutionFlowIfNot: return "Pop Flow If Not";
            case EX_LocalFinalFunction f when f.Parameters is [EX_IntConst]: return $"Call {ShortName(f.StackNode?.Name)}";
            case EX_VirtualFunction v when v.Parameters is [EX_IntConst]: return $"Call {ShortName(v.VirtualFunctionName.Text)}";

            // delegate statements title like their editor nodes (Bind/Unbind/Create Event/Call)
            case EX_CallMulticastDelegate mcd:
                return VarName(mcd.Delegate) is { } called2 ? $"Call {called2}" : "Call Delegate";
            case EX_AddMulticastDelegate am:
                return $"Bind Event to {VarName(am.Delegate) ?? "Delegate"}";
            case EX_RemoveMulticastDelegate rem:
                return $"Unbind Event from {VarName(rem.Delegate) ?? "Delegate"}";
            case EX_ClearMulticastDelegate clear:
                return $"Unbind All Events ({VarName(clear.DelegateToClear) ?? "Delegate"})";
            case EX_BindDelegate bind:
                return $"Create Event {ShortName(bind.FunctionName.Text)}";
            case EX_SetArray: return "Make Array";
            case EX_SetSet: return "Make Set";
            case EX_SetMap: return "Make Map";

            // assignments read like editor nodes: the called function's name when the right-hand
            // side is a call, otherwise "Set <property>" — both straight from the decoded tokens
            case EX_Let let:
                return FindCallName(let.Assignment) ?? SetTitle(let.Variable)
                       ?? (let.Property != null ? $"Set {let.Property}" : "Assign");
            case EX_LetBase letBase:
                return FindCallName(letBase.Assignment) ?? SetTitle(letBase.Variable) ?? "Assign";
            case EX_LetValueOnPersistentFrame frame:
                return FindCallName(frame.AssignmentExpression)
                       ?? (frame.DestinationProperty != null ? $"Set {frame.DestinationProperty}" : "Assign");
            case EX_Context:
                return FindCallName(stmt) is { } ctxCall ? ctxCall : "Context";
        }
        // bare statement-level calls (e.g. EX_CallMath) title by the function they invoke
        if (FindCallName(stmt) is { } called) return called;
        var token = stmt.GetType().Name;
        return token.StartsWith("EX_") ? token[3..] : token;
    }

    /// <summary>"Set X" title for an assignment target, when the target token carries a property pointer.</summary>
    private static string SetTitle(KismetExpression target) =>
        target is EX_VariableBase { Variable: { } pointer } ? $"Set {pointer}" : null;

    /// <summary>First function name invoked anywhere inside the expression (decoded call tokens only), or null.</summary>
    private static string FindCallName(KismetExpression e, int depth = 0)
    {
        if (e == null || depth > 24) return null;
        switch (e)
        {
            case EX_FinalFunction f when f.StackNode != null: // includes EX_CallMath / EX_LocalFinalFunction
                return ShortName(f.StackNode.Name);
            case EX_VirtualFunction v:
                return ShortName(v.VirtualFunctionName.Text);
        }
        foreach (var child in EnumerateChildren(e))
            if (FindCallName(child, depth + 1) is { } name)
                return name;
        return null;
    }

    private static string ShortName(string full)
    {
        if (string.IsNullOrEmpty(full)) return string.Empty;
        var name = full.Split('.').Last();
        var bracket = name.IndexOf('[');
        return bracket >= 0 ? name[..bracket] : name;
    }

    /// <summary>
    /// Reads a function's parameters from its serialized property list. Direction comes from the real
    /// CPF_Parm/OutParm/ReferenceParm/ReturnParm flags; the type string is rendered by CUE4Parse's own
    /// property-type decoder. Nothing is inferred from names.
    /// </summary>
    private static List<(string Dir, string Name, string Type)> CollectParams(UFunction function)
    {
        var list = new List<(string, string, string)>();
        foreach (var (name, type, flags) in EnumerateProperties(function))
        {
            if (!flags.HasFlag(EPropertyFlags.Parm)) continue;
            var dir = flags.HasFlag(EPropertyFlags.ReturnParm) ? "Return"
                : flags.HasFlag(EPropertyFlags.OutParm) ? (flags.HasFlag(EPropertyFlags.ReferenceParm) ? "Ref" : "Out")
                : "In";
            list.Add((dir, name, type));
        }
        return list;
    }

    private static string DescribeSignature(List<(string Dir, string Name, string Type)> parms)
    {
        var inputs = parms.Where(p => p.Dir is "In" or "Ref").Select(p => $"{p.Name}: {p.Type}").ToList();
        var outputs = parms.Where(p => p.Dir == "Out").Select(p => $"{p.Name}: {p.Type}").ToList();
        var ret = parms.FirstOrDefault(p => p.Dir == "Return");
        var sig = $"({string.Join(", ", inputs)})";
        if (outputs.Count > 0) sig += $" out({string.Join(", ", outputs)})";
        if (ret.Name != null) sig += $" → {ret.Type}";
        return sig;
    }

    /// <summary>
    /// Enumerates a struct's serialized properties across both cooked layouts: FField
    /// <see cref="UStruct.ChildProperties"/> (4.25+) and legacy <see cref="UStruct.Children"/>
    /// UProperty exports (older games such as 4.23).
    /// </summary>
    private static IEnumerable<(string Name, string Type, EPropertyFlags Flags)> EnumerateProperties(UStruct owner)
    {
        if (owner.ChildProperties is { Length: > 0 })
            foreach (var field in owner.ChildProperties)
                if (field is FProperty prop)
                    yield return (prop.Name.Text, DescribePropertyType(prop), prop.PropertyFlags);

        if (owner.Children is { Length: > 0 })
            foreach (var index in owner.Children)
                if (index.TryLoad(out var child) && child is UProperty uprop)
                    yield return (uprop.Name, DescribePropertyType(uprop), uprop.PropertyFlags);
    }

    private static string DescribePropertyType(FProperty prop)
    {
        // delegate properties carry their signature function directly; CUE4Parse's decoder doesn't cover them
        switch (prop)
        {
            case FDelegateProperty d: return $"delegate {d.SignatureFunction.Name}";
            case FMulticastDelegateProperty m: return $"multicast delegate {m.SignatureFunction.Name}";
            case FMulticastInlineDelegateProperty mi: return $"multicast delegate {mi.SignatureFunction.Name}";
        }
        try
        {
            var (_, type) = BlueprintDecompilerUtils.GetPropertyType(prop);
            if (!string.IsNullOrWhiteSpace(type)) return type;
        }
        catch { /* unsupported property class: fall back to its serialized type name below */ }
        var n = prop.GetType().Name;
        return n.StartsWith('F') ? n[1..].Replace("Property", string.Empty) : n;
    }

    private static string DescribePropertyType(UProperty prop)
    {
        try
        {
            var (_, type) = BlueprintDecompilerUtils.GetPropertyType(prop);
            if (!string.IsNullOrWhiteSpace(type)) return type;
        }
        catch { /* unsupported property class: fall back to its serialized type name below */ }
        return prop.ExportType.Replace("Property", string.Empty);
    }

    /// <summary>
    /// Fills <see cref="Variables"/> from the class's serialized property list, with default values
    /// taken from the class-default-object export where one is serialized (absent tags simply mean
    /// "inherits the parent/C++ default", so those stay null rather than being guessed).
    /// </summary>
    private void CollectVariables(UClass classExport)
    {
        if (classExport == null) return;

        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (classExport.ClassDefaultObject.TryLoad(out var cdo) && cdo?.Properties != null)
                foreach (var tag in cdo.Properties)
                    defaults[tag.Name.Text] = FormatTagValue(tag);
        }
        catch { /* a missing/unloadable CDO only costs the defaults column */ }

        foreach (var (name, type, flags) in EnumerateProperties(classExport))
        {
            var variable = new BlueprintVariableInfo
            {
                Name = name,
                Type = type,
                Flags = flags.ToString().Replace("CPF_", string.Empty),
            };
            if (defaults.TryGetValue(name, out var def)) variable.DefaultValue = def;
            Variables.Add(variable);
        }
        if (Variables.Count > 0) ClassInfo.Add(new("Variables", Variables.Count.ToString()));
    }

    /// <summary>
    /// Fills <see cref="Components"/> with the Blueprint's Components tree. Cooked classes keep the
    /// SimpleConstructionScript (it builds the actor's components at spawn), the AddComponent node
    /// templates and the timeline templates — all real exports, walked here 1:1. Parent Blueprint
    /// classes in the paks contribute their (inherited) components; native C++ base components live
    /// in engine code and are not in the asset, so they can't be listed.
    /// </summary>
    private void CollectComponents(UClass classExport)
    {
        var cls = classExport;
        for (var guard = 0; cls != null && guard < 8; guard++)
        {
            var inheritedFrom = cls == classExport ? null : cls.Name;
            if (cls is UBlueprintGeneratedClass bpgc)
            {
                try
                {
                    if (bpgc.SimpleConstructionScript != null
                        && bpgc.SimpleConstructionScript.TryLoad(out var scsObj) && scsObj is USimpleConstructionScript scs)
                        foreach (var root in scs.GetRootNodes())
                            AddComponentNode(root, 0, inheritedFrom);

                    // templates for components the graphs spawn at runtime (AddComponent nodes)
                    foreach (var index in bpgc.ComponentTemplates)
                        if (index != null && index.TryLoad(out var template) && template != null)
                            AddTemplateEntry(template, "added at runtime (AddComponent node)", inheritedFrom);

                    foreach (var index in bpgc.Timelines)
                        if (index != null && index.TryLoad(out var timeline) && timeline != null)
                            Components.Add(new BlueprintComponentInfo
                            {
                                Kind = EBlueprintComponentKind.Timeline,
                                Name = timeline.Name.EndsWith("_Template") ? timeline.Name[..^9] : timeline.Name,
                                Class = "Timeline",
                                InheritedFrom = inheritedFrom,
                            });
                }
                catch { /* a broken SCS export only costs the components panel */ }
            }

            UClass next = null;
            try
            {
                // parent Blueprints live in the paks and load; native /Script parents don't (chain ends there)
                if (cls.SuperStruct != null && cls.SuperStruct.TryLoad(out var super) && super is UClass superClass)
                    next = superClass;
            }
            catch { }
            cls = next;
        }
        if (Components.Count > 0) ClassInfo.Add(new("Components", Components.Count.ToString()));
    }

    private void AddComponentNode(USCS_Node node, int depth, string inheritedFrom)
    {
        if (node == null || Components.Count > 512) return;
        var info = new BlueprintComponentInfo
        {
            Name = node.InternalVariableName.Text,
            Depth = depth,
            InheritedFrom = inheritedFrom,
        };

        // attachment to a component of the parent/native class (in-tree parenting is the ChildNodes hierarchy)
        var parentName = node.GetOrDefault<FName>("ParentComponentOrVariableName");
        var socket = node.GetOrDefault<FName>("AttachToName");
        if (!parentName.IsNone)
            info.AttachInfo = $"→ {parentName.Text}" + (socket.IsNone ? string.Empty : $" [{socket.Text}]");

        if (node.GetOrDefault<FPackageIndex>("ComponentClass") is { } compClass && !compClass.IsNull)
            info.Class = ShortName(compClass.Name);
        try
        {
            if (node.GetComponentTemplateAsIndex().TryLoad(out var template) && template != null)
            {
                info.Class ??= template.Class != null ? ShortName(template.Class.Name.Text) : null;
                FillTemplateProperties(info, template);
            }
        }
        catch { /* unloadable template: the node row still shows name/class/hierarchy */ }
        if (string.IsNullOrEmpty(info.Class)) info.Class = "Component";

        Components.Add(info);
        foreach (var child in node.GetChildNodes())
            AddComponentNode(child, depth + 1, inheritedFrom);
    }

    private void AddTemplateEntry(CUE4Parse.UE4.Assets.Exports.UObject template, string note, string inheritedFrom)
    {
        var name = template.Name;
        if (name.EndsWith("_GEN_VARIABLE")) name = name[..^13];
        var info = new BlueprintComponentInfo
        {
            Kind = EBlueprintComponentKind.Runtime,
            Name = name,
            Class = template.Class != null && ShortName(template.Class.Name.Text) is { Length: > 0 } c ? c : "Component",
            AttachInfo = note,
            InheritedFrom = inheritedFrom,
        };
        FillTemplateProperties(info, template);
        Components.Add(info);
    }

    /// <summary>Copies the template's serialized tags (compact-formatted) and picks out its main content asset for the row.</summary>
    private static void FillTemplateProperties(BlueprintComponentInfo info, CUE4Parse.UE4.Assets.Exports.UObject template)
    {
        foreach (var tag in template.Properties)
        {
            var value = FormatTagValue(tag);
            if (value == null) continue;
            if (info.TemplateProperties.Count < 48)
                info.TemplateProperties.Add(new(tag.Name.Text, value));
            if (info.Asset == null && tag.Name.Text
                    is "StaticMesh" or "SkeletalMesh" or "SkeletalMeshAsset" or "Sound" or "Template"
                    or "Mesh" or "WidgetClass" or "DecalMaterial")
                info.Asset = ShortName(value).Trim('\'', '"');
        }
    }

    private static string FormatTagValue(FPropertyTag tag)
    {
        try
        {
            var text = FormatTagType(tag.Tag, 0);
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Renders a serialized property value compactly; structs/arrays expand one level deep.</summary>
    private static string FormatTagType(FPropertyTagType tagType, int depth)
    {
        var generic = tagType?.GenericValue;
        if (generic is FScriptStruct scriptStruct) generic = scriptStruct.StructType; // unwrap to the actual struct value
        switch (generic)
        {
            case null:
                return null;
            case FStructFallback fallback:
            {
                if (depth >= 2 || fallback.Properties.Count == 0) return "{…}";
                var parts = fallback.Properties.Take(8)
                    .Select(p => $"{p.Name.Text}={FormatTagType(p.Tag, depth + 1) ?? "?"}");
                return "{" + string.Join(", ", parts) + (fallback.Properties.Count > 8 ? ", …" : "") + "}";
            }
            case UScriptArray array:
            {
                if (depth >= 2 || array.Properties.Count == 0) return $"[{array.Properties.Count} elements]";
                var parts = array.Properties.Take(4).Select(p => FormatTagType(p, depth + 1) ?? "?");
                return "[" + string.Join(", ", parts) + (array.Properties.Count > 4 ? ", …" : "") + "]";
            }
            case var value:
                return value.ToString();
        }
    }

    private static void AddPin(BlueprintGraphNode node, string name, EBlueprintEdgeKind kind)
    {
        if (node.OutputPins.Any(p => p.Name == name)) return;
        node.OutputPins.Add(new BlueprintGraphPin { Name = name, Kind = kind });
    }

    /// <summary>Deterministic node height: pin rows first (exec + data, deepest side wins), then the optional body text below them.</summary>
    private void ComputeHeight(BlueprintGraphNode node)
    {
        // headers always show their body note (if any); statement bodies only in Show-Code mode
        var showBody = node.Kind == EBlueprintNodeKind.FunctionHeader || ShowStatementText;
        var lines = 0;
        if (showBody && !string.IsNullOrEmpty(node.Body))
            foreach (var seg in node.Body.Split('\n'))
                lines += Math.Max(1, (int)Math.Ceiling(seg.Length / (double)CharsPerLine));
        lines = Math.Min(lines, MaxBodyLines);
        node.BodyLineCount = lines;

        var pinRows = Math.Max(Math.Max(node.InputPins.Count, node.OutputPins.Count), 1);
        node.Height = HeaderHeight + BodyTopPad + pinRows * PinRowHeight + lines * BodyLineHeight + BodyBottomPad;
    }

    /// <summary>Toggles on-node statement text, recomputing every node's height and re-laying out the bands.</summary>
    public void SetStatementTextVisible(bool show)
    {
        if (show == ShowStatementText) return;
        ShowStatementText = show;
        foreach (var node in Nodes) ComputeHeight(node);
        double bandY = 0;
        foreach (var fn in Functions)
        {
            var height = LayoutBand(fn.Nodes, bandY);
            bandY += height + BandGap;
        }
    }
}
