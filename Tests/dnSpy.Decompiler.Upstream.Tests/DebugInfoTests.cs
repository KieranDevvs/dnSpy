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
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Upstream.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.Upstream.Tests {
	/// <summary>
	/// Checks the debug info the debugger consumes (statements, locals, async stepping) against the method's IL
	/// and against what the C# compiler wrote to its PDB for the same IL, for the upstream engine and (as a
	/// baseline) the legacy engine.
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
				_ => throw new ArgumentException(program),
			};

		sealed class EngineRun {
			public string Engine { get; }
			public List<DebugInfoReport> Reports { get; } = new List<DebugInfoReport>();
			public List<string> MissingDebugInfo { get; } = new List<string>();
			public List<string> Exceptions { get; } = new List<string>();
			public EngineRun(string engine) => Engine = engine;

			public string Summary() {
				int Sum(Func<DebugInfoReport, int> f) => Reports.Sum(f);
				return $"{Engine,-9} errors={Sum(r => r.Errors.Count),3}  missing={MissingDebugInfo.Count,2}  exceptions={Exceptions.Count}  " +
					$"user-code covered={Percent(Sum(r => r.PdbUserCodeInstructionsCovered), Sum(r => r.PdbUserCodeInstructions)),-17}" +
					$"compiler breakpoint locations={Percent(Sum(r => r.PdbSequencePointsMatched), Sum(r => r.PdbSequencePoints)),-17}" +
					$"same code at shared breakpoints={Percent(Sum(r => r.SharedLocationsSameCode), Sum(r => r.SharedLocations)),-17}" +
					$"locals mapped={Percent(Sum(r => r.PdbLocalsMapped), Sum(r => r.PdbLocals)),-17}" +
					$"non-empty-stack starts={Sum(r => r.NonEmptyStackStarts)}";
			}
		}

		static EngineRun Run(string engine, ModuleDefMD module, Dictionary<int, PdbMethodInfo> pdb, string source, Func<TypeDef, (string text, IReadOnlyList<MethodDebugInfo> infos)> decompile) {
			var run = new EngineRun(engine);
			var methodsWithDebugInfo = new HashSet<uint>();
			foreach (var type in module.Types.Where(t => !t.IsGlobalModuleType)) {
				(string text, IReadOnlyList<MethodDebugInfo> infos) result;
				try {
					result = decompile(type);
				}
				catch (Exception ex) {
					run.Exceptions.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}");
					continue;
				}
				foreach (var info in result.infos) {
					methodsWithDebugInfo.Add(info.Method.MDToken.Raw);
					pdb.TryGetValue((int)info.Method.MDToken.Raw, out var pdbInfo);
					run.Reports.Add(DebugInfoValidator.Validate(info, result.text, pdbInfo, source));
				}
			}
			// Every method the compiler wrote sequence points for must get debug info, else the debugger can't
			// map it to decompiled code at all
			foreach (var p in pdb.Values) {
				if (p.SequencePointOffsets.Count != 0 && !methodsWithDebugInfo.Contains((uint)p.Token))
					run.MissingDebugInfo.Add(module.ResolveToken(p.Token)?.ToString() ?? $"0x{p.Token:X8}");
			}
			return run;
		}

		void Log(EngineRun run) {
			foreach (var r in run.Reports) {
				log.WriteLine($"  [{run.Engine}] {r}");
				foreach (var e in r.Errors)
					log.WriteLine($"  [{run.Engine}] ERROR {r.Method.Name}: {e}");
				var body = r.Method.Body;
				foreach (var offset in r.UncoveredUserCode) {
					var instr = body.Instructions.FirstOrDefault(i => i.Offset == offset);
					log.WriteLine($"  [{run.Engine}] UNCOVERED {r.Method.Name}: {instr}");
				}
				foreach (var w in r.WrongCode)
					log.WriteLine($"  [{run.Engine}] WRONG CODE {r.Method.Name}: {w}");
				foreach (var slot in r.UnmappedPdbLocals)
					log.WriteLine($"  [{run.Engine}] UNMAPPED LOCAL {r.Method.Name}: V_{slot} {body.Variables[slot].Type}");
			}
			foreach (var m in run.MissingDebugInfo)
				log.WriteLine($"  [{run.Engine}] No debug info for {m}");
			foreach (var e in run.Exceptions)
				log.WriteLine($"  [{run.Engine}] EXCEPTION {e}");
		}

		[Theory]
		[MemberData(nameof(Programs))]
		public void DebugInfo_IsConsistentWithIL_AndTheCompilersPdb(string program, bool optimize) {
			var compiled = TestCompiler.Compile(program + (optimize ? "Release" : "Debug"), GetSource(program), optimize);
			var pdb = PdbGroundTruth.Read(compiled.Pdb);

			var decompiler = TestDecompiler.Create();
			var source = GetSource(program);
			var upstream = Run("upstream", compiled.Load(), pdb, source, t => {
				var r = decompiler.DecompileType(t);
				return (r.Text, r.MethodDebugInfos);
			});
			var legacy = Run("legacy", compiled.Load(), pdb, source, LegacyDecompiler.DecompileType);

			Log(upstream);
			Log(legacy);
			log.WriteLine($"{program} ({(optimize ? "Release" : "Debug")})");
			log.WriteLine("  " + upstream.Summary());
			log.WriteLine("  " + legacy.Summary());

			Assert.Empty(upstream.Exceptions);
			Assert.Empty(upstream.MissingDebugInfo);
			Assert.Empty(upstream.Reports.SelectMany(r => r.Errors));
			// A statement at a compiler breakpoint location that's about different code means a breakpoint on it
			// would stop in the wrong place
			Assert.Empty(upstream.Reports.SelectMany(r => r.WrongCode));
		}

		static string Percent(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:0.0}% ({a}/{b})";
	}
}
