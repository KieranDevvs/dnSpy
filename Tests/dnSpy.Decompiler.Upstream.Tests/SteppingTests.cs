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
using dnSpy.Decompiler.Upstream.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.Upstream.Tests {
	/// <summary>
	/// Runs the test programs, records every IL instruction they execute, and replays what dnSpy's debugger would
	/// do with the decompiled code's debug info: where step over (F10) stops, when breakpoints fire, and which
	/// statement a frame shows while it's in a call. The compiler's own PDB is the reference.
	/// </summary>
	public class SteppingTests {
		readonly ITestOutputHelper log;

		public SteppingTests(ITestOutputHelper log) => this.log = log;

		const string ControlFlowDriver = """

			public static class Driver {
				public static void Run() {
					var c = new ControlFlow();
					c.Loops(new[] { 1, 20, 3 });
					c.Loops(new int[0]);
					c.Exceptions("text");
					try { c.Exceptions(null!); } catch (ArgumentNullException) { }
					c.Switch(5); c.Switch(-1); c.Switch(0); c.Switch("s"); c.Switch(null!); c.Switch(2.0);
					c.SwitchStatement(1); c.SwitchStatement(2); c.SwitchStatement(7);
					c.Lambdas(new List<int> { 3, 1, 2 });
					c.Enumerators(new List<int> { 1, 2 }, new[] { "ab", "", "c" });
					c.Enumerators(new List<int>(), new[] { "x" });
					c.Conditional(new[] { 4 }, null); c.Conditional(new int[0], "t");
					c.Locking(new object(), new List<string>());
				}
			}
			""";

		const string StateMachinesDriver = """

			public static class Driver {
				public static void Run() {
					var s = new StateMachines();
					s.Compute(3).GetAwaiter().GetResult();
					s.Fire();
					System.Threading.Thread.Sleep(100);
					foreach (var n in s.Numbers(5)) { }
					s.AwaitRemaining(2, default).GetAwaiter().GetResult();
					Consume(s.Stream(3)).GetAwaiter().GetResult();
				}

				static async Task Consume(IAsyncEnumerable<int> e) {
					await foreach (var x in e) { }
				}
			}
			""";

		const string Playground = "DebuggerPlayground";

		public static IEnumerable<object[]> Programs() {
			foreach (var program in new[] { nameof(TestPrograms.ControlFlow), nameof(TestPrograms.StateMachines), Playground }) {
				yield return new object[] { program, false };
				yield return new object[] { program, true };
			}
		}

		sealed class EngineResult {
			public string Engine { get; }
			public int Stops;
			public int ComparedStops;
			public List<string> WrongStops { get; } = new List<string>();
			public List<string> BreakpointErrors { get; } = new List<string>();
			public int CallsWithoutStatement;
			public int AwaitsFollowed;
			public HashSet<string> CallSitesWithoutStatement { get; } = new HashSet<string>();
			public EngineResult(string engine) => Engine = engine;
		}

		[Theory]
		[MemberData(nameof(Programs))]
		public void Debugger_StopsAtTheRightCode(string program, bool optimize) {
			string source;
			string entryType = "Driver", entryMethod = "Run";
			if (program == Playground) {
				// Tests/DebuggerPlayground: the scenarios of the manual debugger checklist
				source = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "DebuggerPlayground.cs"));
				(entryType, entryMethod) = ("DebuggerPlayground.Program", "Main");
			}
			else {
				var programSource = program == nameof(TestPrograms.ControlFlow) ? TestPrograms.ControlFlow : TestPrograms.StateMachines;
				source = programSource + (program == nameof(TestPrograms.ControlFlow) ? ControlFlowDriver : StateMachinesDriver);
			}
			var compiled = TestCompiler.Compile("Stepping" + program + (optimize ? "Release" : "Debug"), source, optimize);
			var pdb = PdbGroundTruth.Read(compiled.Pdb);
			var trace = ExecutionTrace.Run(compiled.Image, entryType, entryMethod);
			var sourceLines = source.Split('\n');

			var module = compiled.Load();
			var types = module.Types.Where(t => !t.IsGlobalModuleType && t.Name != "Driver" && t.Name != "Program").ToList();
			var methods = types.SelectMany(t => t.GetTypes().Prepend(t)).SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();
			var loopingToStart = new HashSet<uint>(methods.Where(m => m.Body.Instructions.Any(i => i.Operand is Instruction t && t == m.Body.Instructions[0] ||
				i.Operand is IList<Instruction> ts && ts.Contains(m.Body.Instructions[0]))).Select(m => m.MDToken.Raw));
			var frames = ExecutionTrace.GetFrames(trace, loopingToStart);
			var userMethods = new HashSet<uint>(methods.Select(m => m.MDToken.Raw));
			log.WriteLine($"{trace.Count} instructions executed, {frames.Count(f => userMethods.Contains(f.Token))} calls of the program's methods");

			var pdbModels = new Dictionary<uint, StepModel>();
			foreach (var m in methods) {
				if (pdb.TryGetValue((int)m.MDToken.Raw, out var p) && p.SequencePointOffsets.Count != 0)
					pdbModels[m.MDToken.Raw] = StepModel.FromPdb(m, p, sourceLines);
			}

			var results = new List<EngineResult>();
			var language = LanguageRunner.Create();
			foreach (var (engine, decompile) in new (string, Func<TypeDef, (string, IReadOnlyList<MethodDebugInfo>)>)[] {
				("upstream", t => LanguageRunner.DecompileType(language, t)),
				("legacy", LegacyDecompiler.DecompileType),
			}) {
				var engineModule = compiled.Load();
				var models = new Dictionary<uint, StepModel>();
				var infos = new Dictionary<uint, MethodDebugInfo>();
				foreach (var type in engineModule.Types.Where(t => !t.IsGlobalModuleType && t.Name != "Driver" && t.Name != "Program")) {
					var (text, typeInfos) = decompile(type);
					foreach (var info in typeInfos) {
						uint token = info.Method.MDToken.Raw;
						if (info.Statements.Length == 0 || (infos.TryGetValue(token, out var other) && other.Statements.Length >= info.Statements.Length))
							continue;
						infos[token] = info;
						models[token] = StepModel.FromDebugInfo(info, text);
					}
				}
				results.Add(Check(engine, trace, frames, models, infos, pdbModels, pdb, userMethods));
			}

			// The reference: stepping through the original source with the compiler's PDB
			var reference = new StepSimulator(trace, frames, pdbModels);
			int referenceStops = 0;
			var seen = new HashSet<TraceFrame>();
			var loggedMethods = new HashSet<uint>();
			foreach (var frame in frames) {
				if (!userMethods.Contains(frame.Token) || seen.Contains(frame))
					continue;
				var stops = reference.StepOverFrame(frame, seen);
				referenceStops += stops.Count;
				if (stops.Count > 1 && loggedMethods.Add(frame.Token))
					LogStops("source + compiler PDB", pdbModels, trace, stops);
			}

			var referenceCalls = CountCallsWithoutStatement(trace, frames, pdbModels);
			log.WriteLine($"{program} ({(optimize ? "Release" : "Debug")}): step over stops with the compiler's PDB: {referenceStops}, awaits stepped over: {reference.AwaitsFollowed}, " +
				$"calls without a current statement: {referenceCalls.count} at {referenceCalls.sites.Count} call sites");
			foreach (var c in referenceCalls.sites.Take(100))
				log.WriteLine($"    [source + compiler PDB] NO STATEMENT {c}");
			foreach (var r in results) {
				log.WriteLine($"  {r.Engine,-9} stops={r.Stops} (compared with the source: {r.ComparedStops}), awaits stepped over={r.AwaitsFollowed}, wrong stops={r.WrongStops.Count}, " +
					$"breakpoint errors={r.BreakpointErrors.Count}, calls without a current statement={r.CallsWithoutStatement} at {r.CallSitesWithoutStatement.Count} call sites");
				foreach (var w in r.WrongStops.Distinct().Take(20))
					log.WriteLine($"    [{r.Engine}] WRONG STOP {w}");
				foreach (var e in r.BreakpointErrors.Distinct().Take(20))
					log.WriteLine($"    [{r.Engine}] BREAKPOINT {e}");
				foreach (var c in r.CallSitesWithoutStatement.Take(100))
					log.WriteLine($"    [{r.Engine}] NO STATEMENT {c}");
			}

			var upstream = results.Single(a => a.Engine == "upstream");
			Assert.True(upstream.Stops > 10);
			// F10 never shows a statement other than the one that runs
			Assert.Empty(upstream.WrongStops);
			// Breakpoints fire exactly when their statement runs
			Assert.Empty(upstream.BreakpointErrors);
			// Stepping over an await continues after it like with the compiler's PDB
			Assert.Equal(reference.AwaitsFollowed, upstream.AwaitsFollowed);
			// A frame that's in a call shows its statement, except where the compiler hides the call too (state
			// machine and using/lock/foreach infrastructure). Known exceptions: an implicit base constructor call, a
			// conditional expression arm that upstream gave to an if statement, a loop condition's call.
			var missing = upstream.CallSitesWithoutStatement.Except(referenceCalls.sites).ToList();
			foreach (var site in missing)
				log.WriteLine($"  [upstream] NO STATEMENT (the compiler's PDB has one) {site}");
			Assert.True(missing.Count <= 4, $"{missing.Count} call sites without a current statement where the compiler's PDB has one");
		}

		EngineResult Check(string engine, List<TraceEvent> trace, List<TraceFrame> frames, Dictionary<uint, StepModel> models,
			Dictionary<uint, MethodDebugInfo> infos, Dictionary<uint, StepModel> pdbModels, Dictionary<int, PdbMethodInfo> pdb, HashSet<uint> userMethods) {
			var result = new EngineResult(engine);
			var simulator = new StepSimulator(trace, frames, models);
			var continuations = new HashSet<TraceFrame>();
			var loggedMethods = new HashSet<uint>();
			foreach (var frame in frames) {
				if (!userMethods.Contains(frame.Token) || continuations.Contains(frame) || !models.ContainsKey(frame.Token))
					continue;
				var stops = simulator.StepOverFrame(frame, continuations);
				result.Stops += stops.Count;
				if (stops.Count > 1 && loggedMethods.Add(frame.Token))
					LogStops(engine, models, trace, stops);
				foreach (var (f, stop) in stops) {
					var ev = trace[stop.Event];
					// The source statement the debugger shows at the same point of the execution: the one it can stop
					// at next (e.g. a nop the decompiler attributes to the next statement and the compiler to the
					// previous one: the compiler's debugger never stops there but at the next statement)
					if (!pdbModels.TryGetValue(ev.Token, out var pdbModel))
						continue;
					int? sourceStatementAt = null;
					for (int i = f.Events.IndexOf(stop.Event); i >= 0 && i < f.Events.Count; i++) {
						uint offset = trace[f.Events[i]].Offset;
						if (pdbModel.Boundaries.Contains(offset) && pdbModel.StatementAt(offset) is int ss) {
							sourceStatementAt = ss;
							break;
						}
					}
					if (sourceStatementAt is not int sourceStatement)
						continue;
					var original = pdbModel.GetText(sourceStatement);
					var decompiled = models[f.Token].GetText(stop.Statement);
					var same = DebugInfoValidator.IsSameCode(original, decompiled, DebugInfoValidator.GetVariableNames(infos[ev.Token], pdb[(int)ev.Token]));
					if (same is null)
						continue;
					result.ComparedStops++;
					if (same == false)
						result.WrongStops.Add($"{models[f.Token].Method.Name} IL_{ev.Offset:X4}: source `{Oneline(original)}`, decompiled `{Oneline(decompiled)}`");
				}
			}

			result.AwaitsFollowed = simulator.AwaitsFollowed;

			// Breakpoints and the statement of a frame that's in a call
			foreach (var frame in frames) {
				if (!models.TryGetValue(frame.Token, out var model))
					continue;
				int? previous = null;
				for (int i = 0; i < frame.Events.Count; i++) {
					var ev = trace[frame.Events[i]];
					var statement = model.StatementAt(ev.Offset);
					var instr = model.InstructionAt(ev.Offset);
					if (statement is int s && s != previous) {
						// The breakpoint is on the first instruction of each IL span of the statement
						if (!model.GetStarts(s).Contains(ev.Offset))
							result.BreakpointErrors.Add($"{model.Method.Name}: `{Oneline(model.GetText(s))}` entered at IL_{ev.Offset:X4} without hitting its breakpoint");
					}
					if (statement is int s2 && model.GetStarts(s2).Contains(ev.Offset) && s2 != previous) {
						// It fired: the statement must do something, not only jump away
						bool ran = false;
						for (int j = i; j < frame.Events.Count && model.StatementAt(trace[frame.Events[j]].Offset) == s2; j++) {
							if (!IsJump(model.InstructionAt(trace[frame.Events[j]].Offset)))
								ran = true;
						}
						bool canRun = model.GetSpans(s2).Any(sp => model.Method.Body.Instructions.Any(x => x.Offset >= sp.start && x.Offset < sp.end && !IsJump(x)));
						if (!ran && canRun)
							result.BreakpointErrors.Add($"{model.Method.Name}: breakpoint on `{Oneline(model.GetText(s2))}` (IL_{ev.Offset:X4}) fired but the statement didn't run");
					}
					if (instr is not null && (instr.OpCode.FlowControl == FlowControl.Call) && statement is null) {
						result.CallsWithoutStatement++;
						result.CallSitesWithoutStatement.Add($"{model.Method.Name} IL_{ev.Offset:X4} {instr}");
					}
					previous = statement;
				}
			}
			return result;
		}

		static (int count, HashSet<string> sites) CountCallsWithoutStatement(List<TraceEvent> trace, List<TraceFrame> frames, Dictionary<uint, StepModel> models) {
			int count = 0;
			var sites = new HashSet<string>();
			foreach (var frame in frames) {
				if (!models.TryGetValue(frame.Token, out var model))
					continue;
				foreach (int e in frame.Events) {
					var ev = trace[e];
					if (model.InstructionAt(ev.Offset) is Instruction instr && instr.OpCode.FlowControl == FlowControl.Call && model.StatementAt(ev.Offset) is null) {
						count++;
						sites.Add($"{model.Method.Name} IL_{ev.Offset:X4} {instr}");
					}
				}
			}
			return (count, sites);
		}

		void LogStops(string engine, Dictionary<uint, StepModel> models, List<TraceEvent> trace, List<(TraceFrame frame, Stop stop)> stops) {
			log.WriteLine($"[{engine}] F10 through {models[stops[0].frame.Token].Method.FullName}:");
			foreach (var (f, stop) in stops)
				log.WriteLine($"    frame {f.Id} IL_{trace[stop.Event].Offset:X4} {Oneline(models[f.Token].GetText(stop.Statement))}");
		}

		static bool IsJump(Instruction? instr) =>
			instr is null || instr.OpCode.Code is Code.Nop or Code.Br or Code.Br_S or Code.Leave or Code.Leave_S or Code.Endfilter or Code.Endfinally;

		static string Oneline(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
	}
}
