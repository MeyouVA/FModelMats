using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;

namespace FModel.ViewModels;

/// <summary>How a blueprint statement node is coloured/categorised. Derived only from the decoded token.</summary>
public enum EBlueprintNodeKind
{
    FunctionHeader,
    Branch,
    Jump,
    Flow,
    Return,
    Call,
    Assign,
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
    public List<BlueprintGraphNode> Nodes { get; } = [];
    public List<BlueprintGraphConnection> Connections { get; } = [];
    public List<BlueprintFunctionInfo> Functions { get; } = [];
    /// <summary>Free-form class/blueprint facts for the viewer's details panel.</summary>
    public List<KeyValuePair<string, string>> ClassInfo { get; } = [];

    // dispatch calls (e.g. an event thunk → the ubergraph) target another function's byte offset,
    // so they are resolved after every function is laid out, keyed by (function name, statement offset)
    private readonly Dictionary<(string Func, int Offset), BlueprintGraphNode> _byFuncIndex = new();
    private readonly List<(BlueprintGraphNode Source, string Pin, string TargetFunc, int Offset)> _pendingCalls = [];

    /// <summary>
    /// Builds the graph for a package. <paramref name="classExport"/> supplies the class/parent
    /// name (may be null); <paramref name="functions"/> is every UFunction with bytecode to draw.
    /// </summary>
    public static BlueprintGraphViewModel Build(string packageName, UClass classExport, IReadOnlyList<UFunction> functions)
    {
        var vm = new BlueprintGraphViewModel { PackageName = packageName };

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

        double bandY = 0;
        foreach (var function in functions)
        {
            var height = vm.BuildFunction(function, bandY);
            bandY += height + BandGap;
        }

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

        return vm;
    }

    /// <summary>Builds one function's statement graph and lays it out as a left→right execution band at <paramref name="bandY"/>; returns the band height.</summary>
    private double BuildFunction(UFunction function, double bandY)
    {
        var bytecode = function.ScriptBytecode;
        if (bytecode == null || bytecode.Length == 0) return 0;

        BlueprintDecompilerUtils.Function = function;
        var funcName = function.Name;
        var info = new BlueprintFunctionInfo { Name = funcName, Signature = DescribeSignature(function) };

        // mirror the text decompiler: End*/Nothing markers are not real statements
        var statements = bytecode.Where(IsRealStatement).ToList();

        // header node the function's execution enters from
        var header = new BlueprintGraphNode
        {
            Name = $"{funcName}#header",
            Title = funcName,
            Subtitle = info.Signature,
            Body = string.Empty,
            TokenName = "UFunction",
            FunctionName = funcName,
            Kind = EBlueprintNodeKind.FunctionHeader,
        };
        header.DisplayProperties.Add(new("Function", funcName));
        header.DisplayProperties.Add(new("Signature", info.Signature));
        header.DisplayProperties.Add(new("Flags", function.FunctionFlags.ToString().Replace("FUNC_", "")));
        header.DisplayProperties.Add(new("Statements", statements.Count.ToString()));
        ComputeHeight(header);
        Nodes.Add(header);
        info.Nodes.Add(header);

        // one node per statement, in bytecode order
        var nodeByIndex = new List<(int Index, BlueprintGraphNode Node)>(statements.Count);
        var pending = new List<(BlueprintGraphNode Source, string Pin, int TargetOffset, EBlueprintEdgeKind Kind)>();

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

            ComputeHeight(node);
            Nodes.Add(node);
            info.Nodes.Add(node);
            nodeByIndex.Add((stmt.StatementIndex, node));
            _byFuncIndex[(funcName, stmt.StatementIndex)] = node;
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

        info.StatementCount = statements.Count;
        Functions.Add(info);
        return LayoutBand(info.Nodes, bandY);
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
            case EX_Let or EX_LetBase or EX_LetValueOnPersistentFrame: return "Assign";
            case EX_Context: return "Context";
        }
        var token = stmt.GetType().Name;
        return token.StartsWith("EX_") ? token[3..] : token;
    }

    private static string ShortName(string full)
    {
        if (string.IsNullOrEmpty(full)) return string.Empty;
        var name = full.Split('.').Last();
        var bracket = name.IndexOf('[');
        return bracket >= 0 ? name[..bracket] : name;
    }

    private static string DescribeSignature(UFunction function)
    {
        var flags = function.FunctionFlags.ToString().Replace("FUNC_", "");
        return string.IsNullOrEmpty(flags) || flags == "0" ? "function" : flags;
    }

    private static void AddPin(BlueprintGraphNode node, string name, EBlueprintEdgeKind kind)
    {
        if (node.OutputPins.Any(p => p.Name == name)) return;
        node.OutputPins.Add(new BlueprintGraphPin { Name = name, Kind = kind });
    }

    /// <summary>Deterministic node height from the (truncated) body text plus room for the output pins.</summary>
    private static void ComputeHeight(BlueprintGraphNode node)
    {
        var lines = 0;
        if (!string.IsNullOrEmpty(node.Body))
            foreach (var seg in node.Body.Split('\n'))
                lines += Math.Max(1, (int)Math.Ceiling(seg.Length / (double)CharsPerLine));
        lines = Math.Clamp(lines, node.Kind == EBlueprintNodeKind.FunctionHeader ? 0 : 1, MaxBodyLines);
        node.BodyLineCount = lines;

        var bodyHeight = HeaderHeight + BodyTopPad + lines * BodyLineHeight + BodyBottomPad;
        var pinHeight = HeaderHeight + BodyTopPad + Math.Max(node.OutputPins.Count, 1) * PinRowHeight + BodyBottomPad;
        node.Height = Math.Max(bodyHeight, pinHeight);
    }
}
