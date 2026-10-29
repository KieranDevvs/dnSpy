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
using dnSpy.Decompiler.Upstream.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.Upstream.Tests {
	/// <summary>
	/// Checks the debug info the debugger consumes (statements, locals, async stepping) against the method's IL
	/// and against what the C# compiler wrote to its PDB for the same IL.
	/// </summary>
	public class DebugInfoTests {
		readonly ITestOutputHelper log;

		public DebugInfoTests(ITestOutputHelper log) => this.log = log;

		public static IEnumerable<object[]> Programs() {
			foreach (var program in new[] { nameof(TestPrograms.ControlFlow), nameof(TestPrograms.StateMachines) }) {
				yield return new object[] { program, false };
				yield return new object[] { program, true };
			}
		}

		static string GetSource(string program) =>
			program switch {
				nameof(TestPrograms.ControlFlow) => TestPrograms.ControlFlow,
				nameof(TestPrograms.StateMachines) => TestPrograms.StateMachines,
				_ => throw new System.ArgumentException(program),
			};

		[Theory]
		[MemberData(nameof(Programs))]
		public void DebugInfo_IsConsistentWithIL_AndTheCompilersPdb(string program, bool optimize) {
			var compiled = TestCompiler.Compile(program + (optimize ? "Release" : "Debug"), GetSource(program), optimize);
			var module = compiled.Load();
			var pdb = PdbGroundTruth.Read(compiled.Pdb);
			var decompiler = TestDecompiler.Create();

			var reports = new List<DebugInfoReport>();
			var methodsWithDebugInfo = new HashSet<uint>();
			foreach (var type in module.Types.Where(t => !t.IsGlobalModuleType)) {
				var result = decompiler.DecompileType(type);
				foreach (var info in result.MethodDebugInfos) {
					methodsWithDebugInfo.Add(info.Method.MDToken.Raw);
					pdb.TryGetValue((int)info.Method.MDToken.Raw, out var pdbInfo);
					reports.Add(DebugInfoValidator.Validate(info, result.Text, pdbInfo));
				}
			}

			foreach (var r in reports)
				log.WriteLine(r.ToString());
			foreach (var r in reports) {
				foreach (var e in r.Errors)
					log.WriteLine($"ERROR {r.Method.Name}: {e}");
			}

			// Every method the compiler wrote sequence points for must get debug info, else the debugger can't
			// map it to decompiled code at all
			var missing = pdb.Values.Where(p => p.SequencePointOffsets.Count != 0 && !methodsWithDebugInfo.Contains((uint)p.Token))
				.Select(p => module.ResolveToken(p.Token)?.ToString()).ToList();
			foreach (var m in missing)
				log.WriteLine("No debug info for " + m);

			int pdbSPs = reports.Sum(r => r.PdbSequencePoints);
			int pdbSPsMatched = reports.Sum(r => r.PdbSequencePointsMatched);
			int instructions = reports.Sum(r => r.Instructions);
			int covered = reports.Sum(r => r.CoveredInstructions);
			int pdbLocals = reports.Sum(r => r.PdbLocals);
			int pdbLocalsMapped = reports.Sum(r => r.PdbLocalsMapped);
			log.WriteLine($"TOTAL {program} {(optimize ? "Release" : "Debug")}: methods={reports.Count}, IL coverage={Percent(covered, instructions)}, " +
				$"breakpoint locations shared with the compiler={Percent(pdbSPsMatched, pdbSPs)}, compiler locals mapped={Percent(pdbLocalsMapped, pdbLocals)}, " +
				$"statements starting on a non-empty stack={reports.Sum(r => r.NonEmptyStackStarts)}");

			Assert.Empty(missing);
			Assert.Empty(reports.SelectMany(r => r.Errors));
		}

		static string Percent(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:0.0}% ({a}/{b})";
	}
}
