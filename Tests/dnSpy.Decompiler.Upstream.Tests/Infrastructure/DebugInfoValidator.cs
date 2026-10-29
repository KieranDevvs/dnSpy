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

		/// <summary>
		/// Instructions in the compiler's non-hidden sequence point regions (user code), and how many of them are
		/// covered by a decompiled statement. Uncovered user code means stepping lands where there's no source.
		/// </summary>
		public int PdbUserCodeInstructions { get; set; }
		public int PdbUserCodeInstructionsCovered { get; set; }
		public List<uint> UncoveredUserCode { get; } = new List<uint>();
		public List<int> UnmappedPdbLocals { get; } = new List<int>();

		/// <summary>
		/// Decompiled statements that start at a compiler breakpoint location, and how many of them are about the
		/// same code as the original source statement there (shared identifiers). A statement that doesn't is mapped
		/// to the wrong IL: a breakpoint on it would stop somewhere else.
		/// </summary>
		public int SharedLocations { get; set; }
		public int SharedLocationsSameCode { get; set; }
		public List<string> WrongCode { get; } = new List<string>();

		public int PdbLocals { get; set; }
		public int PdbLocalsMapped { get; set; }

		public DebugInfoReport(MethodDef method) => Method = method;

		public override string ToString() =>
			$"{Method.FullName}: errors={Errors.Count}, coverage={CoveredInstructions}/{Instructions}, " +
			$"pdbSPs={PdbSequencePointsMatched}/{PdbSequencePoints}, sameCode={SharedLocationsSameCode}/{SharedLocations}, userCodeCoverage={PdbUserCodeInstructionsCovered}/{PdbUserCodeInstructions}, pdbLocals={PdbLocalsMapped}/{PdbLocals}, nonEmptyStackStarts={NonEmptyStackStarts}";
	}

	/// <summary>
	/// Checks the debug info the debugger consumes (<see cref="MethodDebugInfo"/>) against the method's IL and,
	/// when available, against the compiler's PDB.
	/// </summary>
	static class DebugInfoValidator {
		public static DebugInfoReport Validate(MethodDebugInfo info, string text, PdbMethodInfo? pdb, string? originalSource = null) {
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

				if (originalSource is not null) {
					var lineStarts = GetLineStarts(originalSource);
					var variableNames = GetVariableNames(info, pdb);
					foreach (var statement in info.Statements) {
						if (!pdb.SourceLocations.TryGetValue((int)statement.ILSpan.Start, out var loc))
							continue;
						var original = Substring(originalSource, lineStarts, loc);
						var decompiled = text.Substring(statement.TextSpan.Start, statement.TextSpan.Length);
						var same = IsSameCode(original, decompiled, variableNames);
						if (same is null)
							continue;
						report.SharedLocations++;
						if (same.Value)
							report.SharedLocationsSameCode++;
						else
							report.WrongCode.Add($"IL_{statement.ILSpan.Start:X4}: source `{original.Trim()}` vs decompiled `{decompiled.Trim()}`");
					}
				}

				var allPoints = pdb.SequencePointOffsets.Select(o => (offset: o, hidden: false))
					.Concat(pdb.HiddenSequencePointOffsets.Select(o => (offset: o, hidden: true))).OrderBy(p => p.offset).ToList();
				for (int i = 0; i < allPoints.Count; i++) {
					if (allPoints[i].hidden)
						continue;
					uint start = (uint)allPoints[i].offset;
					uint end = i + 1 < allPoints.Count ? (uint)allPoints[i + 1].offset : codeSize;
					foreach (var offset in stackDepths.Keys) {
						if (offset < start || offset >= end)
							continue;
						report.PdbUserCodeInstructions++;
						if (ilOwner.ContainsKey(offset))
							report.PdbUserCodeInstructionsCovered++;
						else
							report.UncoveredUserCode.Add(offset);
					}
				}

				var mappedSlots = new HashSet<int>(GetLocals(info.Scope).Where(l => l.Local is not null).Select(l => l.Local!.Index));
				report.PdbLocals = pdb.LocalNames.Count;
				report.PdbLocalsMapped = pdb.LocalNames.Keys.Count(slot => mappedSlots.Contains(slot));
				report.UnmappedPdbLocals.AddRange(pdb.LocalNames.Keys.Where(slot => !mappedSlots.Contains(slot)));

				if (pdb.Awaits.Count != 0) {
					var expected = pdb.Awaits.Select(a => ((uint)a.yieldOffset, (uint)a.resumeOffset)).OrderBy(a => a).ToList();
					var actual = (info.AsyncInfo?.StepInfos ?? System.Array.Empty<AsyncStepInfo>()).Select(a => (a.YieldOffset, a.ResumeOffset)).OrderBy(a => a).ToList();
					if (!expected.SequenceEqual(actual))
						report.Errors.Add($"Async step info differs from the compiler's PDB: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
				}
			}
			return report;
		}

		static List<int> GetLineStarts(string text) {
			var list = new List<int> { 0 };
			for (int i = 0; i < text.Length; i++) {
				if (text[i] == '\n')
					list.Add(i + 1);
			}
			return list;
		}

		static string Substring(string text, List<int> lineStarts, (int startLine, int startColumn, int endLine, int endColumn) loc) {
			int start = lineStarts[loc.startLine - 1] + loc.startColumn - 1;
			int end = lineStarts[loc.endLine - 1] + loc.endColumn - 1;
			return text.Substring(start, System.Math.Max(0, end - start));
		}

		/// <summary>
		/// Names of locals and parameters in the original source and in the decompiled code. The decompiler renames
		/// locals (it doesn't get the PDB), so these names aren't used to compare statements.
		/// </summary>
		static HashSet<string> GetVariableNames(MethodDebugInfo info, PdbMethodInfo pdb) {
			var names = new HashSet<string>(pdb.LocalNames.Values);
			foreach (var method in new[] { info.Method, info.KickoffMethod }) {
				if (method is null)
					continue;
				foreach (var p in method.Parameters)
					names.Add(p.Name);
			}
			foreach (var l in GetLocals(info.Scope))
				names.Add(l.Name);
			foreach (var p in info.Parameters)
				names.Add(p.Name);
			// Hoisted locals (<name>5__N) and captured variables live in compiler generated types
			for (var type = info.Method.DeclaringType; type is not null; type = type.DeclaringType) {
				foreach (var nested in type.NestedTypes.Append(type)) {
					if (!nested.Name.String.Contains("<"))
						continue;
					foreach (var field in nested.Fields) {
						var m = System.Text.RegularExpressions.Regex.Match(field.Name.String, @"^<([A-Za-z_][A-Za-z0-9_]*)>5__\d+$");
						names.Add(m.Success ? m.Groups[1].Value : field.Name.String);
					}
				}
			}
			return names;
		}

		/// <summary>
		/// Are the original and decompiled statements about the same code? They are if they share a member name or
		/// literal. Returns null if there's nothing to compare: braces (the decompiler has no brace statements, so it
		/// attributes a brace's nop to the next statement) or statements that only use variables.
		/// </summary>
		static bool? IsSameCode(string original, string decompiled, HashSet<string> variableNames) {
			if (IsBrace(original) || IsBrace(decompiled))
				return null;
			var a = Words(original, variableNames);
			var b = Words(decompiled, variableNames);
			if (a.Count == 0 || b.Count == 0)
				return null;
			return a.Overlaps(b);
		}

		static bool IsBrace(string s) => s.Trim() is "{" or "}";

		static readonly HashSet<string> keywords = new HashSet<string> { "var", "int", "string", "object", "bool", "return", "new", "this", "base", "in", "is", "when", "if", "else", "for", "foreach", "while", "do", "using", "try", "catch", "finally", "throw", "await", "yield", "break", "continue", "lock", "checked", "switch", "case", "default", "null", "true", "false", "async" };

		static HashSet<string> Words(string s, HashSet<string> variableNames) {
			var set = new HashSet<string>();
			foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"[A-Za-z_][A-Za-z0-9_]*|\d+|""[^""]*""")) {
				if (!keywords.Contains(m.Value) && !variableNames.Contains(m.Value))
					set.Add(m.Value);
			}
			return set;
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
