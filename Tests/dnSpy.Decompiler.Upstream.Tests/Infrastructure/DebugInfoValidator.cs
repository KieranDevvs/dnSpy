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

using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.Upstream.Tests.Infrastructure {
	/// <summary>
	/// Result of checking one method's debug info
	/// </summary>
	sealed class DebugInfoReport {
		public MethodDef Method { get; }

		/// <summary>
		/// Hard errors: debug info the debugger can't use correctly
		/// </summary>
		public List<string> Errors { get; } = new List<string>();

		/// <summary>
		/// Statements starting where the IL evaluation stack isn't empty. Legal, but a breakpoint there stops in the
		/// middle of an expression.
		/// </summary>
		public int NonEmptyStackStarts { get; set; }

		public int Instructions { get; set; }
		public int CoveredInstructions { get; set; }

		/// <summary>
		/// Non-hidden sequence points in the compiler's PDB, and how many of them are also the start of a decompiled statement
		/// </summary>
		public int PdbSequencePoints { get; set; }
		public int PdbSequencePointsMatched { get; set; }

		public int PdbLocals { get; set; }
		public int PdbLocalsMapped { get; set; }

		public DebugInfoReport(MethodDef method) => Method = method;

		public override string ToString() =>
			$"{Method.FullName}: errors={Errors.Count}, coverage={CoveredInstructions}/{Instructions}, " +
			$"pdbSPs={PdbSequencePointsMatched}/{PdbSequencePoints}, pdbLocals={PdbLocalsMapped}/{PdbLocals}, nonEmptyStackStarts={NonEmptyStackStarts}";
	}

	/// <summary>
	/// Checks the debug info the debugger consumes (<see cref="MethodDebugInfo"/>) against the method's IL and,
	/// when available, against the compiler's PDB.
	/// </summary>
	static class DebugInfoValidator {
		public static DebugInfoReport Validate(MethodDebugInfo info, string text, PdbMethodInfo? pdb) {
			var report = new DebugInfoReport(info.Method);
			var body = info.Method.Body;
			if (body is null) {
				report.Errors.Add("Method has no body");
				return report;
			}
			body.UpdateInstructionOffsets();
			var offsets = new HashSet<uint>(body.Instructions.Select(i => i.Offset));
			uint codeSize = (uint)body.GetCodeSize();
			var stackDepths = StackDepthCalculator.Calculate(info.Method);

			var ilOwner = new Dictionary<uint, TextSpan>();
			foreach (var statement in info.Statements) {
				var il = statement.ILSpan;
				var ts = statement.TextSpan;
				if (!offsets.Contains(il.Start))
					report.Errors.Add($"Statement {statement} starts inside an instruction");
				if (il.End != codeSize && !offsets.Contains(il.End))
					report.Errors.Add($"Statement {statement} ends inside an instruction");
				if (il.IsEmpty || il.End > codeSize)
					report.Errors.Add($"Statement {statement} has an invalid IL span (code size 0x{codeSize:X})");
				if (ts.Start < 0 || ts.End > text.Length || ts.IsEmpty)
					report.Errors.Add($"Statement {statement} has an invalid text span (text length {text.Length})");
				else if (string.IsNullOrWhiteSpace(text.Substring(ts.Start, ts.Length)))
					report.Errors.Add($"Statement {statement} only covers whitespace");
				if (info.HasSpan && (ts.Start < info.Span.Start || ts.End > info.Span.End))
					report.Errors.Add($"Statement {statement} is outside the method's text span {info.Span}");
				if (stackDepths.TryGetValue(il.Start, out int depth) && depth != 0)
					report.NonEmptyStackStarts++;

				foreach (var instr in body.Instructions) {
					if (instr.Offset < il.Start || instr.Offset >= il.End)
						continue;
					if (ilOwner.TryGetValue(instr.Offset, out var other) && other != ts)
						report.Errors.Add($"IL_{instr.Offset:X4} belongs to two different statements: {other} and {ts}");
					ilOwner[instr.Offset] = ts;
				}
			}

			report.Instructions = stackDepths.Count;
			report.CoveredInstructions = stackDepths.Keys.Count(o => ilOwner.ContainsKey(o));

			if (pdb is not null) {
				var starts = new HashSet<uint>(info.Statements.Select(s => s.ILSpan.Start));
				report.PdbSequencePoints = pdb.SequencePointOffsets.Count;
				report.PdbSequencePointsMatched = pdb.SequencePointOffsets.Count(o => starts.Contains((uint)o));

				var mappedSlots = new HashSet<int>(GetLocals(info.Scope).Where(l => l.Local is not null).Select(l => l.Local!.Index));
				report.PdbLocals = pdb.LocalNames.Count;
				report.PdbLocalsMapped = pdb.LocalNames.Keys.Count(slot => mappedSlots.Contains(slot));

				if (pdb.Awaits.Count != 0) {
					var expected = pdb.Awaits.Select(a => ((uint)a.yieldOffset, (uint)a.resumeOffset)).OrderBy(a => a).ToList();
					var actual = (info.AsyncInfo?.StepInfos ?? System.Array.Empty<AsyncStepInfo>()).Select(a => (a.YieldOffset, a.ResumeOffset)).OrderBy(a => a).ToList();
					if (!expected.SequenceEqual(actual))
						report.Errors.Add($"Async step info differs from the compiler's PDB: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
				}
			}
			return report;
		}

		static IEnumerable<SourceLocal> GetLocals(MethodDebugScope scope) {
			foreach (var local in scope.Locals)
				yield return local;
			foreach (var child in scope.Scopes) {
				foreach (var local in GetLocals(child))
					yield return local;
			}
		}
	}

	/// <summary>
	/// IL evaluation stack depth at each reachable instruction
	/// </summary>
	static class StackDepthCalculator {
		public static Dictionary<uint, int> Calculate(MethodDef method) {
			var body = method.Body;
			var result = new Dictionary<uint, int>();
			var work = new Stack<(Instruction instr, int depth)>();
			bool hasReturnValue = method.MethodSig.RetType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void;
			if (body.Instructions.Count != 0)
				work.Push((body.Instructions[0], 0));
			foreach (var eh in body.ExceptionHandlers) {
				if (eh.HandlerStart is not null)
					work.Push((eh.HandlerStart, eh.IsCatch || eh.IsFilter ? 1 : 0));
				if (eh.FilterStart is not null)
					work.Push((eh.FilterStart, 1));
			}
			var indexes = new Dictionary<Instruction, int>();
			for (int i = 0; i < body.Instructions.Count; i++)
				indexes[body.Instructions[i]] = i;

			while (work.Count != 0) {
				var (instr, depth) = work.Pop();
				if (result.ContainsKey(instr.Offset))
					continue;
				result[instr.Offset] = depth;
				instr.CalculateStackUsage(hasReturnValue, out int pushes, out int pops);
				int after = pops == -1 ? 0 : depth - pops + pushes;
				if (after < 0)
					after = 0;
				switch (instr.OpCode.FlowControl) {
				case FlowControl.Branch:
					if (instr.Operand is Instruction target)
						work.Push((target, instr.OpCode.Code == Code.Leave || instr.OpCode.Code == Code.Leave_S ? 0 : after));
					continue;
				case FlowControl.Cond_Branch:
					if (instr.Operand is Instruction condTarget)
						work.Push((condTarget, after));
					else if (instr.Operand is IList<Instruction> targets) {
						foreach (var t in targets)
							work.Push((t, after));
					}
					break;
				case FlowControl.Return:
				case FlowControl.Throw:
					continue;
				}
				if (indexes.TryGetValue(instr, out int index) && index + 1 < body.Instructions.Count)
					work.Push((body.Instructions[index + 1], after));
			}
			return result;
		}
	}
}
