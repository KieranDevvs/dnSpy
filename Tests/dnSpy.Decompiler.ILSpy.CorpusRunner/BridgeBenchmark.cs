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
using System.Diagnostics;
using System.IO;
using dnlib.DotNet;
using dnSpy.Decompiler.ILSpy.Core.Bridge;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	/// <summary>
	/// bridge-bench ASSEMBLY...
	/// Time to bridge each assembly via the original image and via a dnlib rewrite (what every edited module costs)
	/// </summary>
	static class BridgeBenchmark {
		public static int Run(Options options) {
			Console.WriteLine("| Assembly | Size | Original image (ms) | Normalized / rewrite (ms) | Rewritten size |");
			Console.WriteLine("|---|---|---|---|---|");
			foreach (var file in options.Paths) {
				var original = Measure(file, alwaysNormalize: false, out var kind1, out _);
				var normalized = Measure(file, alwaysNormalize: true, out _, out long size);
				Console.WriteLine($"| {Path.GetFileName(file)} | {new FileInfo(file).Length / 1024} KB | {original:0} ({kind1}) | {normalized:0} | {size / 1024} KB |");
			}
			return 0;
		}

		static double Measure(string file, bool alwaysNormalize, out BridgeImageKind kind, out long size) {
			double best = double.MaxValue;
			kind = default;
			size = 0;
			// Best of 3, each with a freshly loaded module (dnlib loads lazily, so the rewrite also pays for reading)
			for (int i = 0; i < 3; i++) {
				var module = ModuleDefMD.Load(file, AssemblyRunner.CreateModuleContext(file));
				var sw = Stopwatch.StartNew();
				using var bridged = ModuleBridge.Create(module, isModified: false, new ModuleBridgeOptions { AlwaysNormalize = alwaysNormalize });
				best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
				kind = bridged.ImageKind;
				if (bridged.File is ICSharpCode.Decompiler.Metadata.PEFile pe)
					size = pe.Reader.GetEntireImage().Length;
			}
			return best;
		}
	}
}
