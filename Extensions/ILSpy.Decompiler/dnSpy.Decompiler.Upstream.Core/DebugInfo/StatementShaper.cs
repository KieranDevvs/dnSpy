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

namespace dnSpy.Decompiler.Upstream.DebugInfo {
	enum StatementKind {
		/// <summary>Control flow (if/loop/switch headers, braces...) or unknown</summary>
		Other,
		/// <summary>A statement that doesn't branch elsewhere: expression, declaration, return, throw, yield return</summary>
		Simple,
		/// <summary>break, continue, goto, yield break</summary>
		Jump,
	}

	/// <summary>
	/// What the shaper knows about the decompiled code of a method
	/// </summary>
	sealed class ShapeContext {
		public Func<TextSpan, StatementKind> GetKind { get; }
		/// <summary>IL locals that the decompiled code shows as variables (the others are compiler temporaries)</summary>
		public ISet<int> VisibleLocals { get; }
		/// <summary>Closing brace of the method's body, if it has a block body</summary>
		public TextSpan? ClosingBrace { get; }
		/// <summary>The method is a state machine's MoveNext (its 'ret' isn't the end of the decompiled method)</summary>
		public bool IsStateMachine { get; }

		public ShapeContext(Func<TextSpan, StatementKind> getKind, ISet<int> visibleLocals, TextSpan? closingBrace, bool isStateMachine) {
			GetKind = getKind;
			VisibleLocals = visibleLocals;
			ClosingBrace = closingBrace;
			IsStateMachine = isStateMachine;
		}
	}

	/// <summary>
	/// Fixes the IL spans of statements so that breakpoints, stepping and the current statement work like they do
	/// with a compiler's PDB. Statements whose IL is already well formed are left unchanged, so this is a no-op if
	/// upstream's sequence points get better.
	/// </summary>
	/// <remarks>
	/// <para>dnSpy binds a breakpoint to the first instruction of each IL span of a statement, steps over a
	/// statement by running until the IL leaves its spans (and any IL without a statement), and shows the
	/// statement whose span contains a frame's IL offset. ICSharpCode.Decompiler 11.2 produces spans that break
	/// this:</para>
	/// <list type="bullet">
	/// <item>A branch of another statement (e.g. a 'continue' or the jump to a switch's default case) is
	/// attributed to the next statement, so the span starts with an instruction that jumps away and its real code
	/// is entered from elsewhere. A breakpoint fires in the wrong case. Fix: split spans at every instruction that
	/// can be entered from outside the span (or that starts an exception handler), and drop the pieces that only
	/// jump or that are in another exception handling block than the statement's own code (the last piece), e.g.
	/// the end of a filter attributed to the catch block's opening brace.</item>
	/// <item>Part of an expression's IL isn't attributed (calls of transformed code like cached delegates,
	/// display class field accesses, delegate invocations), so a frame stopped in a call has no current
	/// statement. Fix: a statement's span is extended over following unattributed IL while the evaluation stack
	/// isn't empty (that IL is still computing the statement's expression), and over unattributed IL that reads a
	/// compiler temporary the statement stored (the decompiler removed the temporary).</item>
	/// <item>The epilogue of a method with return statements (load the return value, ret) isn't attributed. The
	/// compiler maps it to the closing brace, where a step over the last statement stops. Fix: same.</item>
	/// <item>The tail of a condition (e.g. 'ldloc; brtrue' of an if or do-while) is attributed to the next
	/// statement, so a breakpoint on a 'break' or 'return' fires each time the condition is tested. Fix: a simple
	/// statement (not an if/loop/switch header) can't contain a conditional branch to another statement's code;
	/// such a prefix is moved to the previous statement if it's a control flow header, else it's hidden.</item>
	/// </list>
	/// </remarks>
	static class StatementShaper {
		public static List<SourceStatement> Shape(List<SourceStatement> statements, MethodDef method, ShapeContext context) {
			if (statements.Count == 0)
				return statements;
			var body = method.Body;
			if (body is null || body.Instructions.Count == 0)
				return statements;
			try {
				var flow = MethodFlow.TryCreate(method);
				if (flow is null)
					return statements;
				return Shape(statements, flow, context);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException) {
				// Never fail the decompilation because of invalid IL: keep upstream's statements
				return statements;
			}
		}

		static List<SourceStatement> Shape(List<SourceStatement> statements, MethodFlow flow, ShapeContext context) {
			var getKind = context.GetKind;
			int count = flow.Count;
			// Statement (index into 'spans') that owns each instruction, or -1
			var owner = new int[count];
			for (int i = 0; i < owner.Length; i++)
				owner[i] = -1;
			var spans = new List<(int start, int end, TextSpan text)>(statements.Count);
			foreach (var s in statements.OrderBy(a => a.ILSpan.Start)) {
				if (!flow.TryGetIndex(s.ILSpan.Start, out int start) || !flow.TryGetIndex(s.ILSpan.End, out int end) || end <= start)
					return statements;
				for (int i = start; i < end; i++) {
					if (owner[i] >= 0)
						return statements;
					owner[i] = spans.Count;
				}
				spans.Add((start, end, s.TextSpan));
			}

			// Extend over unattributed IL while the statement's expression is still being evaluated, or while that IL
			// uses a compiler temporary the statement stored
			for (int si = 0; si < spans.Count; si++) {
				var (start, end, text) = spans[si];
				int newEnd = end;
				while (newEnd < count && owner[newEnd] < 0 && flow.Block[newEnd] == flow.Block[newEnd - 1]) {
					if (flow.StackDepth[newEnd] > 0) {
						owner[newEnd++] = si;
						continue;
					}
					int runEnd = GetTemporaryRunEnd(flow, owner, start, newEnd, context.VisibleLocals);
					if (runEnd <= newEnd)
						break;
					while (newEnd < runEnd)
						owner[newEnd++] = si;
				}
				if (newEnd != end)
					spans[si] = (start, newEnd, text);
			}

			// The epilogue (load the return value, ret) is the closing brace. In an async method, it's where the state
			// is set to 'finished' (-2) before the result is set (and not in the catch handler, the exception path).
			if (context.ClosingBrace is TextSpan closingBrace && !spans.Any(a => a.text == closingBrace)) {
				bool added = false;
				for (int i = 0; i < count; i++) {
					if (owner[i] >= 0)
						continue;
					if (context.IsStateMachine) {
						if (i + 2 < count && IsFinishedStateStore(flow, i) && owner[i + 1] < 0 && owner[i + 2] < 0 && !flow.IsInHandler(i) &&
							flow.Block[i] == flow.Block[i + 2] && !flow.BranchSources(i + 1).Any() && !flow.BranchSources(i + 2).Any()) {
							for (int j = i; j < i + 3; j++)
								owner[j] = spans.Count;
							spans.Add((i, i + 3, closingBrace));
							added = true;
						}
						continue;
					}
					if (flow.Instructions[i].OpCode.Code != Code.Ret || flow.Block[i] != -1)
						continue;
					int epilogueStart = i;
					while (epilogueStart > 0 && owner[epilogueStart - 1] < 0 && IsEpilogueInstruction(flow.Instructions[epilogueStart - 1]))
						epilogueStart--;
					// Only entered at its start (it's the return statements' common target)
					bool singleEntry = true;
					for (int j = epilogueStart + 1; j <= i; j++) {
						if (flow.BranchSources(j).Any())
							singleEntry = false;
					}
					if (!singleEntry)
						continue;
					for (int j = epilogueStart; j <= i; j++)
						owner[j] = spans.Count;
					spans.Add((epilogueStart, i + 1, closingBrace));
					added = true;
				}
				if (added)
					RebuildOwners(spans, owner);
			}

			// Conditions' tails attributed to the next statement
			for (int si = 0; si < spans.Count; si++) {
				var (start, end, text) = spans[si];
				var kind = getKind(text);
				if (kind == StatementKind.Other)
					continue;
				int cut = -1;
				for (int i = start; i < end; i++) {
					var instr = flow.Instructions[i];
					if (instr.OpCode.FlowControl != FlowControl.Cond_Branch)
						continue;
					foreach (int target in flow.BranchTargets(i)) {
						bool inside = target >= start && (target < end || (target == end && kind != StatementKind.Jump));
						if (!inside && target < count && owner[target] >= 0 && spans[owner[target]].text != text)
							cut = i + 1;
					}
				}
				if (cut < 0 || cut >= end)
					continue;
				// The previous statement gets it if the condition is its own
				int prev = start > 0 ? owner[start - 1] : -1;
				bool toPrevious = prev >= 0 && spans[prev].end == start && getKind(spans[prev].text) == StatementKind.Other && flow.Block[start - 1] == flow.Block[start];
				for (int i = start; i < cut; i++)
					owner[i] = toPrevious ? prev : -1;
				if (toPrevious)
					spans[prev] = (spans[prev].start, cut, spans[prev].text);
				spans[si] = (cut, end, text);
			}

			// Instructions that belong to statements with the same text (the same statement in the source)
			var textOf = new TextSpan?[count];
			foreach (var (start, end, text) in spans) {
				for (int i = start; i < end; i++)
					textOf[i] = text;
			}

			var result = new List<SourceStatement>(spans.Count);
			foreach (var (start, end, text) in spans) {
				var pieces = Split(flow, start, end, text, textOf);
				if (pieces.Count == 1) {
					result.Add(new SourceStatement(flow.ToILSpan(start, end), text));
					continue;
				}
				// The statement's own code is the last piece (upstream attributes leftover IL to the next statement)
				int homeBlock = flow.Block[pieces[pieces.Count - 1].start];
				var kept = pieces.Where(p => flow.Block[p.start] == homeBlock).ToList();
				var withEffect = kept.Where(p => !IsJumpOnly(flow, p.start, p.end)).ToList();
				if (withEffect.Count != 0)
					kept = withEffect;
				foreach (var (pStart, pEnd) in kept)
					result.Add(new SourceStatement(flow.ToILSpan(pStart, pEnd), text));
			}
			return result;
		}

		/// <summary>
		/// Splits a span at every instruction that can be entered without executing the previous instruction of
		/// the span: a branch target (from code that isn't part of the same statement) or the start of an exception
		/// handler or filter. (Entering a try block isn't one: it's reached by falling through, e.g. lock and using
		/// statements start before their try block.)
		/// </summary>
		static List<(int start, int end)> Split(MethodFlow flow, int start, int end, TextSpan text, TextSpan?[] textOf) {
			var pieces = new List<(int, int)>();
			int pieceStart = start;
			for (int i = start + 1; i < end; i++) {
				bool cut = flow.IsHandlerStart[i];
				if (!cut) {
					foreach (int source in flow.BranchSources(i)) {
						if (textOf[source] != text) {
							cut = true;
							break;
						}
					}
				}
				if (cut) {
					pieces.Add((pieceStart, i));
					pieceStart = i;
				}
			}
			pieces.Add((pieceStart, end));
			return pieces;
		}

		/// <summary>
		/// If the stack-empty run of unattributed IL at <paramref name="runStart"/> reads a compiler temporary that the
		/// statement stored and doesn't branch, returns its end, else <paramref name="runStart"/>
		/// </summary>
		static int GetTemporaryRunEnd(MethodFlow flow, int[] owner, int statementStart, int runStart, ISet<int> visibleLocals) {
			var stored = new HashSet<int>();
			for (int i = statementStart; i < runStart; i++) {
				if (flow.Instructions[i].IsStloc() && GetLocalIndex(flow.Instructions[i]) is int index && !visibleLocals.Contains(index))
					stored.Add(index);
			}
			if (stored.Count == 0)
				return runStart;
			bool readsTemporary = false;
			int end = runStart;
			do {
				if (owner[end] >= 0 || flow.Block[end] != flow.Block[runStart] || flow.StackDepth[end] < 0)
					return runStart;
				var instr = flow.Instructions[end];
				if (instr.OpCode.FlowControl != FlowControl.Next && instr.OpCode.FlowControl != FlowControl.Call)
					return runStart;
				if ((instr.IsLdloc() || instr.OpCode.Code == Code.Ldloca || instr.OpCode.Code == Code.Ldloca_S) && GetLocalIndex(instr) is int index && stored.Contains(index))
					readsTemporary = true;
				end++;
			} while (end < flow.Count && flow.StackDepth[end] != 0);
			return readsTemporary ? end : runStart;
		}

		static int? GetLocalIndex(Instruction instr) {
			switch (instr.OpCode.Code) {
			case Code.Ldloc_0: case Code.Stloc_0: return 0;
			case Code.Ldloc_1: case Code.Stloc_1: return 1;
			case Code.Ldloc_2: case Code.Stloc_2: return 2;
			case Code.Ldloc_3: case Code.Stloc_3: return 3;
			}
			return (instr.Operand as Local)?.Index;
		}

		static void RebuildOwners(List<(int start, int end, TextSpan text)> spans, int[] owner) {
			spans.Sort((a, b) => a.start.CompareTo(b.start));
			for (int i = 0; i < owner.Length; i++)
				owner[i] = -1;
			for (int si = 0; si < spans.Count; si++) {
				for (int i = spans[si].start; i < spans[si].end; i++)
					owner[i] = si;
			}
		}

		/// <summary>
		/// ldarg.0; ldc.i4.s -2; stfld &lt;&gt;1__state (the C# compiler's 'finished' state)
		/// </summary>
		static bool IsFinishedStateStore(MethodFlow flow, int i) {
			var instrs = flow.Instructions;
			return instrs[i].OpCode.Code == Code.Ldarg_0 &&
				instrs[i + 1].IsLdcI4() && instrs[i + 1].GetLdcI4Value() == -2 &&
				instrs[i + 2].OpCode.Code == Code.Stfld && instrs[i + 2].Operand is IField field && field.Name == "<>1__state";
		}

		static bool IsEpilogueInstruction(Instruction instr) =>
			instr.OpCode.Code == Code.Nop || instr.IsLdloc() || instr.IsLdarg();

		static bool IsJumpOnly(MethodFlow flow, int start, int end) {
			for (int i = start; i < end; i++) {
				switch (flow.Instructions[i].OpCode.Code) {
				case Code.Nop:
				case Code.Br:
				case Code.Br_S:
				case Code.Leave:
				case Code.Leave_S:
				// They end a filter or finally block (reached from every path, e.g. a filter's type check failing)
				case Code.Endfilter:
				case Code.Endfinally:
					break;
				default:
					return false;
				}
			}
			return true;
		}
	}

	/// <summary>
	/// Instruction offsets, branch sources, evaluation stack depths and exception handling blocks of a method body.
	/// Computed without modifying the body (it's shared with the UI).
	/// </summary>
	sealed class MethodFlow {
		public IList<Instruction> Instructions { get; }
		public int Count => Instructions.Count;
		/// <summary>Offset of each instruction, plus the code size at index <see cref="Count"/></summary>
		public uint[] Offsets { get; }
		/// <summary>Stack depth before each instruction, -1 if it's unreachable</summary>
		public int[] StackDepth { get; }
		/// <summary>Innermost exception handling block (try, filter or handler) of each instruction, -1 if none</summary>
		public int[] Block { get; }
		public bool[] IsHandlerStart { get; }

		/// <summary>true if the instruction is in a catch/finally/fault handler or filter (its innermost block)</summary>
		public bool IsInHandler(int index) => Block[index] >= 0 && Block[index] % 3 != 0;
		readonly List<int>?[] branchSources;
		readonly Dictionary<uint, int> indexes;
		readonly Dictionary<Instruction, int> indexOf;

		MethodFlow(IList<Instruction> instructions, uint[] offsets, int[] stackDepth, int[] block, bool[] isHandlerStart, List<int>?[] branchSources, Dictionary<uint, int> indexes, Dictionary<Instruction, int> indexOf) {
			Instructions = instructions;
			Offsets = offsets;
			StackDepth = stackDepth;
			Block = block;
			IsHandlerStart = isHandlerStart;
			this.branchSources = branchSources;
			this.indexes = indexes;
			this.indexOf = indexOf;
		}

		public bool TryGetIndex(uint offset, out int index) => indexes.TryGetValue(offset, out index);

		public ILSpan ToILSpan(int start, int end) => ILSpan.FromBounds(Offsets[start], Offsets[end]);

		/// <summary>Instructions that branch to an instruction (not counting fall through)</summary>
		public IEnumerable<int> BranchSources(int index) => (IEnumerable<int>?)branchSources[index] ?? Array.Empty<int>();

		/// <summary>Instructions a branch instruction branches to (not counting fall through)</summary>
		public IEnumerable<int> BranchTargets(int index) {
			switch (Instructions[index].Operand) {
			case Instruction target:
				if (indexOf.TryGetValue(target, out int t))
					yield return t;
				break;
			case IList<Instruction> targets:
				foreach (var target2 in targets) {
					if (target2 is not null && indexOf.TryGetValue(target2, out int t2))
						yield return t2;
				}
				break;
			}
		}

		public static MethodFlow? TryCreate(MethodDef method) {
			var body = method.Body;
			var instrs = body.Instructions;
			int count = instrs.Count;
			var offsets = new uint[count + 1];
			var indexOf = new Dictionary<Instruction, int>(count);
			var indexes = new Dictionary<uint, int>(count + 1);
			uint offset = 0;
			for (int i = 0; i < count; i++) {
				var instr = instrs[i];
				if (instr is null || indexOf.ContainsKey(instr))
					return null;
				indexOf.Add(instr, i);
				offsets[i] = offset;
				indexes[offset] = i;
				offset += (uint)instr.GetSize();
			}
			offsets[count] = offset;
			indexes[offset] = count;

			var branchSources = new List<int>?[count + 1];
			for (int i = 0; i < count; i++) {
				switch (instrs[i].Operand) {
				case Instruction target:
					if (!indexOf.TryGetValue(target, out int t))
						return null;
					(branchSources[t] ??= new List<int>()).Add(i);
					break;
				case IList<Instruction> targets:
					foreach (var target2 in targets) {
						if (target2 is null || !indexOf.TryGetValue(target2, out int t2))
							return null;
						(branchSources[t2] ??= new List<int>()).Add(i);
					}
					break;
				}
			}

			int IndexOrEnd(Instruction? instr) => instr is null ? count : indexOf.TryGetValue(instr, out int index) ? index : -1;
			var block = new int[count];
			var blockSize = new int[count];
			for (int i = 0; i < count; i++) {
				block[i] = -1;
				blockSize[i] = int.MaxValue;
			}
			var isHandlerStart = new bool[count + 1];
			var handlers = body.ExceptionHandlers;
			for (int h = 0; h < handlers.Count; h++) {
				var eh = handlers[h];
				int tryStart = IndexOrEnd(eh.TryStart), tryEnd = IndexOrEnd(eh.TryEnd);
				int handlerStart = IndexOrEnd(eh.HandlerStart), handlerEnd = IndexOrEnd(eh.HandlerEnd);
				if (tryStart < 0 || tryEnd < 0 || handlerStart < 0 || handlerEnd < 0 || eh.TryStart is null || eh.HandlerStart is null)
					return null;
				SetBlock(block, blockSize, tryStart, tryEnd, h * 3);
				SetBlock(block, blockSize, handlerStart, handlerEnd, h * 3 + 1);
				isHandlerStart[handlerStart] = true;
				if (eh.HandlerType == ExceptionHandlerType.Filter) {
					int filterStart = IndexOrEnd(eh.FilterStart);
					if (filterStart < 0 || eh.FilterStart is null)
						return null;
					// The filter ends where the handler starts
					SetBlock(block, blockSize, filterStart, handlerStart, h * 3 + 2);
					isHandlerStart[filterStart] = true;
				}
			}

			var depth = CalculateStackDepths(method, instrs, indexOf, handlers);
			if (depth is null)
				return null;
			return new MethodFlow(instrs, offsets, depth, block, isHandlerStart, branchSources, indexes, indexOf);
		}

		static void SetBlock(int[] block, int[] blockSize, int start, int end, int id) {
			int size = end - start;
			for (int i = start; i < end && i < block.Length; i++) {
				// Innermost = smallest block
				if (size < blockSize[i]) {
					block[i] = id;
					blockSize[i] = size;
				}
			}
		}

		static int[]? CalculateStackDepths(MethodDef method, IList<Instruction> instrs, Dictionary<Instruction, int> indexOf, IList<ExceptionHandler> handlers) {
			int count = instrs.Count;
			var depth = new int[count];
			for (int i = 0; i < count; i++)
				depth[i] = -1;
			bool hasReturnValue = method.MethodSig?.RetType?.RemovePinnedAndModifiers().GetElementType() is ElementType type && type != ElementType.Void;
			var work = new Stack<(int index, int depth)>();
			work.Push((0, 0));
			foreach (var eh in handlers) {
				if (eh.HandlerStart is not null && indexOf.TryGetValue(eh.HandlerStart, out int h))
					work.Push((h, eh.HandlerType == ExceptionHandlerType.Catch || eh.HandlerType == ExceptionHandlerType.Filter ? 1 : 0));
				if (eh.FilterStart is not null && indexOf.TryGetValue(eh.FilterStart, out int f))
					work.Push((f, 1));
			}
			while (work.Count != 0) {
				var (index, d) = work.Pop();
				if (index >= count)
					continue;
				if (depth[index] >= 0) {
					// Valid IL has one stack depth per instruction
					if (depth[index] != d)
						return null;
					continue;
				}
				depth[index] = d;
				var instr = instrs[index];
				instr.CalculateStackUsage(hasReturnValue, out int pushes, out int pops);
				int after = pops == -1 ? 0 : d - pops + pushes;
				if (after < 0)
					return null;
				switch (instr.OpCode.FlowControl) {
				case FlowControl.Branch:
					if (instr.Operand is Instruction target && indexOf.TryGetValue(target, out int t))
						work.Push((t, instr.OpCode.Code == Code.Leave || instr.OpCode.Code == Code.Leave_S ? 0 : after));
					continue;
				case FlowControl.Cond_Branch:
					if (instr.Operand is Instruction condTarget && indexOf.TryGetValue(condTarget, out int ct))
						work.Push((ct, after));
					else if (instr.Operand is IList<Instruction> targets) {
						foreach (var t2 in targets) {
							if (indexOf.TryGetValue(t2, out int ti))
								work.Push((ti, after));
						}
					}
					break;
				case FlowControl.Return:
				case FlowControl.Throw:
					continue;
				}
				// endfinally/endfilter don't fall through
				if (instr.OpCode.Code == Code.Endfinally || instr.OpCode.Code == Code.Endfilter)
					continue;
				work.Push((index + 1, after));
			}
			return depth;
		}
	}
}
