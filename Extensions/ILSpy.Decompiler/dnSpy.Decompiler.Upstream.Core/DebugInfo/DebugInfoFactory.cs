/*
    Copyright (C) 2026 dnSpy contributors

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Upstream.Bridge;
using dnSpy.Decompiler.Upstream.Output;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;
using UpstreamMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;
using UpstreamSequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace dnSpy.Decompiler.Upstream.DebugInfo {
	/// <summary>
	/// Converts the upstream decompiler's debug info (sequence points, variables, async info) to dnSpy's
	/// <see cref="MethodDebugInfo"/>, which the debugger uses for breakpoints, stepping and locals.
	/// </summary>
	static class DebugInfoFactory {
		/// <param name="repairs">Gets a description of each upstream sequence point that had to be dropped or fixed</param>
		public static List<MethodDebugInfo> Create(SyntaxTree syntaxTree, Dictionary<ILFunction, List<UpstreamSequencePoint>> sequencePoints,
			TrackingTextOutput output, ModuleBridgeCache cache, int decompilerSettingsVersion, List<string> repairs) {
			var spans = GetSpans(syntaxTree, output);
			var result = new List<MethodDebugInfo>(sequencePoints.Count);
			foreach (var kv in sequencePoints) {
				var info = Create(kv.Key, kv.Value, output, cache, spans, decompilerSettingsVersion, repairs);
				if (info is not null)
					result.Add(info);
			}
			return result;
		}

		static MethodDebugInfo? Create(ILFunction function, List<UpstreamSequencePoint> points, TrackingTextOutput output,
			ModuleBridgeCache cache, Spans spans, int decompilerSettingsVersion, List<string> repairs) {
			if (function.Method is null)
				return null;
			// IL offsets are offsets in the method whose body was decompiled: MoveNext() for async/iterator methods
			var bodyMethod = cache.ToDnlib(function.MoveNextMethod ?? function.Method) as MethodDef;
			if (bodyMethod is null)
				return null;
			MethodDef? kickoffMethod = null;
			if (function.MoveNextMethod is not null)
				kickoffMethod = cache.ToDnlib(function.Method) as MethodDef;

			var kind = function.IsAsync ? StateMachineKind.AsyncMethod :
				function.IsIterator ? StateMachineKind.IteratorMethod : StateMachineKind.None;
			var builder = new MethodDebugInfoBuilder(decompilerSettingsVersion, kind, bodyMethod, kickoffMethod) {
				CompilerName = null,
			};

			var body = bodyMethod.Body;
			var boundaries = GetInstructionBoundaries(body);
			foreach (var sp in points) {
				if (sp.IsHidden || sp.EndOffset <= sp.Offset)
					continue;
				int start = output.ToOffset(sp.StartLine, sp.StartColumn);
				int end = output.ToOffset(sp.EndLine, sp.EndColumn);
				if (sp.StartLine <= 0 || end <= start) {
					// The node has no text location (it wasn't written), nothing to map the IL to
					repairs.Add($"{bodyMethod.FullName}: dropped statement IL_{sp.Offset:X4}-IL_{sp.EndOffset:X4} (no text location)");
					continue;
				}
				uint ilStart = (uint)sp.Offset;
				uint ilEnd = (uint)sp.EndOffset;
				if (boundaries is not null) {
					// Upstream sometimes produces offsets inside an instruction (seen in async MoveNext methods).
					// Such a statement is attributed to the wrong IL: drop it rather than let a breakpoint on it
					// stop in other code. The IL is then treated as hidden by the stepper.
					if (!boundaries.Contains(ilStart)) {
						repairs.Add($"{bodyMethod.FullName}: dropped statement IL_{ilStart:X4}-IL_{ilEnd:X4} (starts inside an instruction)");
						continue;
					}
					if (!boundaries.Contains(ilEnd)) {
						uint fixedEnd = boundaries.Where(b => b > ilEnd).DefaultIfEmpty(ilEnd).Min();
						repairs.Add($"{bodyMethod.FullName}: statement IL_{ilStart:X4}-IL_{ilEnd:X4} ends inside an instruction, end moved to IL_{fixedEnd:X4}");
						ilEnd = fixedEnd;
					}
				}
				builder.Add(new SourceStatement(ILSpan.FromBounds(ilStart, ilEnd), TextSpan.FromBounds(start, end)));
			}

			builder.Scope.Span = ILSpan.FromBounds(0, (uint)(body is null ? function.CodeSize : body.GetCodeSize()));
			builder.Scope.Locals.AddRange(GetLocals(function, body, cache));
			builder.Parameters = GetParameters(function, kickoffMethod ?? bodyMethod, cache);
			if (function.IsAsync)
				builder.AsyncInfo = GetAsyncInfo(function, bodyMethod, kickoffMethod);

			// A method can be decompiled more than once (e.g. a lambda whose compiler generated names were
			// obfuscated: inline and as a method of its display class), so use the span of this function's node
			// The fallback is only safe for top-level functions: some lambdas (e.g. a group-by key selector turned into
			// a query clause) aren't annotated on any node, and their method may also be declared elsewhere.
			if (spans.ByFunction.TryGetValue(function, out var span) ||
				(function.Kind == ILFunctionKind.TopLevelFunction && spans.ByMethod.TryGetValue(function.Method, out span))) {
				builder.StartPosition = span.start;
				builder.EndPosition = span.end;
			}
			return builder.Create();
		}

		/// <summary>
		/// Offsets of all instructions plus the code size. Computed without updating the instructions' offsets,
		/// the body may be shared with the UI.
		/// </summary>
		static HashSet<uint>? GetInstructionBoundaries(CilBody? body) {
			if (body is null)
				return null;
			var set = new HashSet<uint>();
			uint offset = 0;
			foreach (var instr in body.Instructions) {
				set.Add(offset);
				offset += (uint)instr.GetSize();
			}
			set.Add(offset);
			return set;
		}

		static IEnumerable<SourceLocal> GetLocals(ILFunction function, CilBody? body, ModuleBridgeCache cache) {
			// Upstream can split one IL slot into several variables (e.g. a reused slot in optimized code). The
			// scope is method-wide, so keep one name per slot like upstream's own PDB writer does.
			var seenSlots = new HashSet<int>();
			var seenFields = new HashSet<FieldDef>();
			foreach (var v in function.Variables) {
				if (v.Kind == VariableKind.Parameter || v.Name is null)
					continue;
				// SourceLocal derives DecompilerGenerated itself (no IL local and no hoisted field)
				var flags = SourceVariableFlags.None;
				if (v.IsRefReadOnly)
					flags |= SourceVariableFlags.ReadOnlyReference;
				if (v.StateMachineField is not null) {
					if (cache.ToDnlib(v.StateMachineField) is FieldDef hoistedField && seenFields.Add(hoistedField))
						yield return new SourceLocal(null, v.Name, hoistedField, flags);
					continue;
				}
				if (v.Index is int index && body is not null && (uint)index < (uint)body.Variables.Count && IsILLocal(v.Kind) && seenSlots.Add(index)) {
					var local = body.Variables[index];
					yield return new SourceLocal(local, v.Name, local.Type, flags);
				}
			}
		}

		static bool IsILLocal(VariableKind kind) =>
			kind switch {
				VariableKind.Local or VariableKind.PinnedLocal or VariableKind.PinnedRegionLocal or VariableKind.UsingLocal or
				VariableKind.ForeachLocal or VariableKind.InitializerTarget or VariableKind.ExceptionLocal or VariableKind.DisplayClassLocal or
				VariableKind.PatternLocal => true,
				_ => false,
			};

		static SourceParameter[] GetParameters(ILFunction function, MethodDef method, ModuleBridgeCache cache) {
			var list = new List<SourceParameter>();
			var seenParameters = new HashSet<int>();
			foreach (var v in function.Variables) {
				if (v.Kind != VariableKind.Parameter || v.Index is not int index || v.Name is null)
					continue;
				// Upstream: -1 is the hidden 'this' parameter. dnlib: Parameters includes a hidden 'this' parameter at
				// index 0 only if the signature has one (HasThis without ExplicitThis), see MethodSigIndexBase.
				int dnlibIndex = index + method.Parameters.MethodSigIndexBase;
				if ((uint)dnlibIndex >= (uint)method.Parameters.Count || !seenParameters.Add(dnlibIndex))
					continue;
				var parameter = method.Parameters[dnlibIndex];
				if (v.StateMachineField is not null && cache.ToDnlib(v.StateMachineField) is FieldDef hoistedField)
					list.Add(new SourceParameter(parameter, v.Name, hoistedField, SourceVariableFlags.None));
				else
					list.Add(new SourceParameter(parameter, v.Name, parameter.Type, SourceVariableFlags.None));
			}
			return list.ToArray();
		}

		static AsyncMethodDebugInfo GetAsyncInfo(ILFunction function, MethodDef moveNext, MethodDef? kickoffMethod) {
			var info = function.AsyncDebugInfo;
			var stepInfos = info.Awaits.IsDefault ? Array.Empty<AsyncStepInfo>() :
				info.Awaits.Select(a => new AsyncStepInfo((uint)a.YieldOffset, moveNext, (uint)a.ResumeOffset)).ToArray();
			// dnSpy only uses it for async void methods
			bool returnsVoid = kickoffMethod?.MethodSig?.RetType.RemovePinnedAndModifiers().GetElementType() == ElementType.Void;
			uint catchHandlerOffset = returnsVoid && info.CatchHandlerOffset >= 0 ? (uint)info.CatchHandlerOffset : uint.MaxValue;
			var builderField = FindBuilderField(kickoffMethod, moveNext.DeclaringType);
			return new AsyncMethodDebugInfo(stepInfos, builderField, catchHandlerOffset, FindSetResultOffset(moveNext, builderField));
		}

		/// <summary>
		/// The builder field is the one whose Start() the kickoff method calls:
		/// stateMachine.&lt;&gt;t__builder.Start(ref stateMachine)
		/// </summary>
		static FieldDef? FindBuilderField(MethodDef? kickoffMethod, TypeDef? stateMachineType) {
			var instrs = kickoffMethod?.Body?.Instructions;
			if (instrs is not null) {
				for (int i = 0; i < instrs.Count; i++) {
					var instr = instrs[i];
					if (instr.OpCode.Code != Code.Call || instr.Operand is not dnlib.DotNet.IMethod called || called.Name != "Start")
						continue;
					// ldloca stateMachine; ldflda builder; ldloca stateMachine; call Start
					for (int j = i - 1; j >= Math.Max(0, i - 4); j--) {
						if ((instrs[j].OpCode.Code == Code.Ldflda || instrs[j].OpCode.Code == Code.Ldfld) && instrs[j].Operand is dnlib.DotNet.IField f &&
							f.ResolveFieldDef() is FieldDef fd && fd.DeclaringType == stateMachineType)
							return fd;
					}
				}
			}
			// Fall back to the C# compiler's name
			return stateMachineType?.Fields.FirstOrDefault(f => f.Name == "<>t__builder");
		}

		/// <summary>
		/// Offset of the first instruction of this.&lt;&gt;t__builder.SetResult(...)
		/// </summary>
		static uint FindSetResultOffset(MethodDef moveNext, FieldDef? builderField) {
			var instrs = moveNext.Body?.Instructions;
			if (instrs is null || builderField is null)
				return uint.MaxValue;
			var offsets = GetOffsets(instrs);
			for (int i = 0; i < instrs.Count; i++) {
				var instr = instrs[i];
				if (instr.OpCode.Code != Code.Call || instr.Operand is not dnlib.DotNet.IMethod called || called.Name != "SetResult")
					continue;
				// ldarg.0; ldflda builder; [ldloc result]; call SetResult
				for (int j = i - 1; j >= Math.Max(0, i - 3); j--) {
					if (instrs[j].OpCode.Code == Code.Ldflda && instrs[j].Operand is dnlib.DotNet.IField f && f.ResolveFieldDef() == builderField) {
						int start = j > 0 && instrs[j - 1].OpCode.Code == Code.Ldarg_0 ? j - 1 : j;
						return offsets[start];
					}
				}
			}
			return uint.MaxValue;
		}

		static uint[] GetOffsets(IList<Instruction> instrs) {
			var offsets = new uint[instrs.Count];
			uint offset = 0;
			for (int i = 0; i < instrs.Count; i++) {
				offsets[i] = offset;
				offset += (uint)instrs[i].GetSize();
			}
			return offsets;
		}

		sealed class Spans {
			public Dictionary<ILFunction, (int start, int end)> ByFunction { get; } = new Dictionary<ILFunction, (int, int)>();
			public Dictionary<UpstreamMethod, (int start, int end)> ByMethod { get; } = new Dictionary<UpstreamMethod, (int, int)>();
		}

		static bool IsFunctionDefinition(AstNode node) =>
			node is EntityDeclaration || node is LambdaExpression || node is AnonymousMethodExpression || node is LocalFunctionDeclarationStatement ||
			// Lambdas turned into query clauses (where, select, orderby...) keep their annotations
			node is QueryClause;

		static Spans GetSpans(SyntaxTree syntaxTree, TrackingTextOutput output) {
			var spans = new Spans();
			foreach (var node in syntaxTree.Descendants) {
				if (node.StartLocation.IsEmpty || node.EndLocation.IsEmpty)
					continue;
				var span = (output.ToOffset(node.StartLocation.Line, node.StartLocation.Column), output.ToOffset(node.EndLocation.Line, node.EndLocation.Column));
				// Nodes that define a function are annotated with its ILFunction. So are references to local functions
				// (call sites), which must be ignored.
				if (IsFunctionDefinition(node) && node.Annotation<ILFunction>() is ILFunction function && !spans.ByFunction.ContainsKey(function))
					spans.ByFunction.Add(function, span);
				if (node is EntityDeclaration decl && decl.GetSymbol() is UpstreamMethod method)
					spans.ByMethod[method] = span;
			}
			return spans;
		}
	}
}
