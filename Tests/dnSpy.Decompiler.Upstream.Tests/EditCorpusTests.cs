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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Upstream.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.Upstream.Tests {
	/// <summary>
	/// Edit Method / Edit Class without changes on real assemblies (opt-in: EDIT_CORPUS=directory with the assemblies
	/// and their references, EDIT_CORPUS_SAMPLES=methods per assembly). The code can't be run, so it measures how often
	/// the code compiles, imports and the module can be saved.
	/// </summary>
	public class EditCorpusTests {
		readonly ITestOutputHelper log;
		public EditCorpusTests(ITestOutputHelper log) => this.log = log;

		static ModuleDefMD Load(string file) {
			var context = new ModuleContext();
			var resolver = new AssemblyResolver(context) { FindExactMatch = false, EnableFrameworkRedirect = false };
			resolver.PreSearchPaths.Add(Path.GetDirectoryName(file)!);
			resolver.PreSearchPaths.Add(RuntimeEnvironment.GetRuntimeDirectory());
			context.AssemblyResolver = resolver;
			context.Resolver = new Resolver(resolver);
			return ModuleDefMD.Load(file, context);
		}

		sealed class Stats {
			public int Total, Ok;
			public Dictionary<string, int> Categories = new Dictionary<string, int>();
			public Dictionary<string, int> Errors = new Dictionary<string, int>();
			public List<string> Examples = new List<string>();
			public List<double> EditSeconds = new List<double>();
			public double Seconds;

			public void Add(string category, IEnumerable<string> errors, string example, double seconds) {
				Total++;
				EditSeconds.Add(seconds);
				if (category == "ok") {
					Ok++;
					return;
				}
				Categories[category] = Categories.GetValueOrDefault(category) + 1;
				foreach (var e in errors.Distinct())
					Errors[e] = Errors.GetValueOrDefault(e) + 1;
				if (Examples.Count < 8)
					Examples.Add(example);
			}
		}

		[Fact]
		public void EditMethodAndClass_RealWorldAssemblies() {
			var dir = Environment.GetEnvironmentVariable("EDIT_CORPUS");
			if (dir is null)
				return;
			int methodSamples = int.TryParse(Environment.GetEnvironmentVariable("EDIT_CORPUS_SAMPLES"), out int n) ? n : 30;
			int typeSamples = Math.Max(1, methodSamples / 4);
			var engines = (Environment.GetEnvironmentVariable("EDIT_ENGINE") ?? "upstream,legacy").Split(',');
			// Optional file that gets each edit before it starts, to find the edit if the test host crashes
			var progress = Environment.GetEnvironmentVariable("EDIT_CORPUS_PROGRESS");
			// The legacy engine is a debug build here: its assertions would throw in the test host, not in dnSpy
			System.Diagnostics.Trace.Listeners.Clear();
			var totals = engines.ToDictionary(e => e, e => new Stats());
			foreach (var file in Directory.GetFiles(dir, "*.dll").OrderBy(a => a, StringComparer.Ordinal)) {
				var module = Load(file);
				var upstream = LanguageRunner.Create();
				// string.GetHashCode() is randomized per process: use a stable seed so a run can be repeated
				var random = new Random(StableHash(Path.GetFileName(file)));
				var methods = module.GetTypes()
					.Where(t => !t.IsGlobalModuleType && IsVisible(upstream, t))
					.SelectMany(t => t.Methods.Where(m => m.HasBody && upstream.ShowMember(m)))
					.Select(m => m.MDToken.Raw).OrderBy(_ => random.Next()).Take(methodSamples).ToList();
				var types = module.Types.Where(t => !t.IsGlobalModuleType && upstream.ShowMember(t) && !t.Name.String.StartsWith("<", StringComparison.Ordinal))
					.Select(t => t.MDToken.Raw).OrderBy(_ => random.Next()).Take(typeSamples).ToList();
				foreach (var engine in engines) {
					var stats = new Stats();
					var watch = System.Diagnostics.Stopwatch.StartNew();
					foreach (var (kind, tokens) in new[] { (EditKind.EditMethod, methods), (EditKind.EditClass, types) }) {
						foreach (var token in tokens) {
							var decompiler = engine == "legacy" ? LegacyDecompiler.CSharp : LanguageRunner.Create();
							if (progress is not null)
								File.AppendAllText(progress, $"{engine} {kind} {Path.GetFileName(file)} 0x{token:X8}{Environment.NewLine}");
							var editWatch = System.Diagnostics.Stopwatch.StartNew();
							var (category, errors, example) = Edit(decompiler, kind, file, token);
							double editSeconds = editWatch.Elapsed.TotalSeconds;
							stats.Add(category, errors, example, editSeconds);
							totals[engine].Add(category, errors, example, editSeconds);
						}
					}
					stats.Seconds = watch.Elapsed.TotalSeconds;
					totals[engine].Seconds += stats.Seconds;
					Report($"{engine} {Path.GetFileName(file)}", stats, examples: false);
				}
			}
			foreach (var engine in engines)
				Report($"{engine} TOTAL", totals[engine], examples: true);
		}

		void Report(string title, Stats stats, bool examples) {
			var times = stats.EditSeconds.OrderBy(a => a).ToList();
			string timing = times.Count == 0 ? "" : $", per edit: median {times[times.Count / 2]:F1} s, max {times[^1]:F1} s";
			log.WriteLine($"{title}: {stats.Ok}/{stats.Total} OK ({stats.Seconds:F0} s{timing})" + (stats.Categories.Count == 0 ? "" : ", " +
				string.Join(", ", stats.Categories.OrderByDescending(a => a.Value).Select(a => $"{a.Key} {a.Value}"))));
			if (!examples)
				return;
			log.WriteLine("  most common errors (edits with them): " + string.Join(", ", stats.Errors.OrderByDescending(a => a.Value).Take(15).Select(a => $"{a.Key} {a.Value}")));
			foreach (var e in stats.Examples)
				log.WriteLine("  " + e);
		}

		static (string category, IEnumerable<string> errors, string example) Edit(IDecompiler decompiler, EditKind kind, string file, uint token) {
			var module = Load(file);
			try {
				var member = (IMemberDef)module.ResolveToken(token);
				var result = EditPipeline.Decompile(decompiler, kind, module, member);
				if (result.DecompileError is not null)
					return ("decompile", new[] { "decompile" }, result.Describe());
				if (!EditPipeline.CompileAndImport(result, module, member)) {
					if (result.CompileErrors.Count != 0)
						return ("compile", result.CompileErrors.Select(a => a.Id), result.Describe(3));
					return ("import", result.ImportErrors.Select(a => a.Split(' ')[0]), result.Describe(3));
				}
				try {
					module.Write(new MemoryStream());
				}
				catch (Exception ex) {
					return ("write", new[] { "write" }, $"{kind} {result.Target}: {ex.Message}");
				}
				return ("ok", Array.Empty<string>(), "");
			}
			catch (Exception ex) {
				return ("exception", new[] { ex.GetType().Name }, $"{kind} 0x{token:X8}: {ex}");
			}
			finally {
				(decompiler as UpstreamCSharpLanguage)?.OnAllModulesRemoved();
				module.Dispose();
			}
		}

		static int StableHash(string s) {
			uint hash = 2166136261;
			foreach (var c in s)
				hash = (hash ^ c) * 16777619;
			return (int)(hash & 0x7FFFFFFF);
		}

		static bool IsVisible(IDecompiler decompiler, TypeDef type) {
			for (var t = type; t is not null; t = t.DeclaringType) {
				if (!decompiler.ShowMember(t))
					return false;
			}
			return true;
		}
	}
}
