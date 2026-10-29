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
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Upstream.DebugInfo;
using dnSpy.Decompiler.Upstream.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.Upstream.Tests {
	/// <summary>
	/// The statement shaper fixes upstream's IL spans. It must leave well formed statements alone, so that it
	/// keeps working when upstream's sequence points get better: the C# compiler's own sequence points must pass
	/// through with the same breakpoint locations and IL. The only change allowed is hiding a jump (br, leave,
	/// endfilter...) that other code also jumps to, e.g. the leave shared by a foreach's 'in' and a 'break'.
	/// </summary>
	public class StatementShaperTests {
		readonly ITestOutputHelper log;

		public StatementShaperTests(ITestOutputHelper log) => this.log = log;

		public static IEnumerable<object[]> Programs() {
			foreach (var program in new[] { nameof(TestPrograms.ControlFlow), nameof(TestPrograms.StateMachines) }) {
				yield return new object[] { program, false };
				yield return new object[] { program, true };
			}
		}

		static readonly Regex controlFlow = new Regex(@"^(if|while|for|foreach|switch|do|when|catch|using|lock|fixed|in|case|else|try|finally|await)\b|^[{}]");
		static readonly Regex jump = new Regex(@"^(break|continue|goto|yield\s+break)\b");

		[Theory]
		[MemberData(nameof(Programs))]
		public void CompilerSequencePoints_PassThroughUnchanged(string program, bool optimize) {
			var source = program == nameof(TestPrograms.ControlFlow) ? TestPrograms.ControlFlow : TestPrograms.StateMachines;
			var compiled = TestCompiler.Compile(program + (optimize ? "Release" : "Debug"), source, optimize);
			var pdb = PdbGroundTruth.Read(compiled.Pdb);
			var module = compiled.Load();
			var lines = source.Split('\n');
			int checkedMethods = 0;
			foreach (var info in pdb.Values) {
				if (info.SequencePointOffsets.Count == 0 || module.ResolveToken(info.Token) is not MethodDef method || method.Body is null)
					continue;
				uint codeSize = (uint)method.Body.GetCodeSize();
				var points = info.SequencePointOffsets.Select(o => (offset: o, hidden: false))
					.Concat(info.HiddenSequencePointOffsets.Select(o => (offset: o, hidden: true))).OrderBy(p => p.offset).ToList();
				var statements = new List<SourceStatement>();
				var kinds = new Dictionary<TextSpan, StatementKind>();
				for (int i = 0; i < points.Count; i++) {
					if (points[i].hidden)
						continue;
					uint start = (uint)points[i].offset;
					uint end = i + 1 < points.Count ? (uint)points[i + 1].offset : codeSize;
					if (end <= start)
						continue;
					// A fake text span per source location: same location = same statement
					var loc = info.SourceLocations[points[i].offset];
					var text = TextSpan.FromBounds(loc.startLine * 1000 + loc.startColumn, loc.endLine * 1000 + loc.endColumn);
					statements.Add(new SourceStatement(ILSpan.FromBounds(start, end), text));
					kinds[text] = GetKind(lines, loc);
				}
				var visibleLocals = new HashSet<int>(method.Body.Variables.Select(a => a.Index));
				var context = new ShapeContext(span => kinds.TryGetValue(span, out var kind) ? kind : StatementKind.Other, visibleLocals, closingBrace: null, isStateMachine: info.KickoffMethodToken != 0);
				var shaped = StatementShaper.Shape(statements, method, context);

				var expected = statements.Select(a => a.ToString()).OrderBy(a => a).ToArray();
				var actual = shaped.Select(a => a.ToString()).OrderBy(a => a).ToArray();
				if (!expected.SequenceEqual(actual)) {
					log.WriteLine($"{method.FullName}:");
					log.WriteLine("  compiler: " + string.Join(" ", expected));
					log.WriteLine("  shaped:   " + string.Join(" ", actual));
				}
				// Same breakpoint locations
				foreach (var s in statements)
					Assert.Contains(shaped, a => a.TextSpan == s.TextSpan && a.ILSpan.Start == s.ILSpan.Start);
				// No IL moved to another statement or added
				foreach (var a in shaped)
					Assert.Contains(statements, s => s.TextSpan == a.TextSpan && s.ILSpan.Start <= a.ILSpan.Start && a.ILSpan.End <= s.ILSpan.End);
				// Only jumps hidden
				method.Body.UpdateInstructionOffsets();
				foreach (var instr in method.Body.Instructions) {
					bool before = statements.Any(s => s.ILSpan.Start <= instr.Offset && instr.Offset < s.ILSpan.End);
					bool after = shaped.Any(s => s.ILSpan.Start <= instr.Offset && instr.Offset < s.ILSpan.End);
					if (before && !after)
						Assert.Contains(instr.OpCode.Code, new[] { dnlib.DotNet.Emit.Code.Br, dnlib.DotNet.Emit.Code.Br_S, dnlib.DotNet.Emit.Code.Leave, dnlib.DotNet.Emit.Code.Leave_S, dnlib.DotNet.Emit.Code.Endfilter, dnlib.DotNet.Emit.Code.Endfinally, dnlib.DotNet.Emit.Code.Nop });
				}
				checkedMethods++;
			}
			Assert.True(checkedMethods > 5);
		}

		static StatementKind GetKind(string[] lines, (int startLine, int startColumn, int endLine, int endColumn) loc) {
			var line = lines[loc.startLine - 1];
			var text = line.Substring(loc.startColumn - 1).TrimStart();
			if (jump.IsMatch(text))
				return StatementKind.Jump;
			if (controlFlow.IsMatch(text))
				return StatementKind.Other;
			// The condition and iterator of a for loop, and foreach's variable and collection, aren't statements
			var before = line.Substring(0, loc.startColumn - 1).TrimStart();
			if (before.StartsWith("for", StringComparison.Ordinal) || before.StartsWith("foreach", StringComparison.Ordinal))
				return StatementKind.Other;
			// Expression bodies of lambdas and members (=> x) can be anything
			if (before.EndsWith("=>", StringComparison.Ordinal))
				return StatementKind.Other;
			return StatementKind.Simple;
		}
	}
}
