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
using System.Reflection;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.Upstream.Tests.Infrastructure {
	/// <summary>
	/// The legacy (ILSpy 2.x fork) C# decompiler, loaded the same way dnSpy.Console loads it. Used as the baseline
	/// the upstream engine's debug info is compared against. Remove when the legacy engine is removed.
	/// </summary>
	static class LegacyDecompiler {
		static readonly Lazy<IDecompiler> csharp = new Lazy<IDecompiler>(() => {
			var asm = Assembly.Load("dnSpy.Decompiler.ILSpy.Core");
			foreach (var type in asm.GetTypes()) {
				if (type.IsAbstract || type.IsInterface || !typeof(IDecompilerProvider).IsAssignableFrom(type))
					continue;
				var provider = (IDecompilerProvider)Activator.CreateInstance(type)!;
				var decompiler = provider.Create().FirstOrDefault(d => d.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY);
				if (decompiler is not null)
					return decompiler;
			}
			throw new InvalidOperationException("Legacy C# decompiler not found");
		});

		public static IDecompiler CSharp => csharp.Value;

		public static (string text, IReadOnlyList<MethodDebugInfo> debugInfos) DecompileType(TypeDef type) => DecompileType(type, default);

		public static (string text, IReadOnlyList<MethodDebugInfo> debugInfos) DecompileType(TypeDef type, System.Threading.CancellationToken cancellationToken) {
			var output = new CapturingOutput();
			var ctx = new DecompilationContext { CalculateILSpans = true, CancellationToken = cancellationToken };
			csharp.Value.Decompile(type, output, ctx);
			return (output.ToString(), output.DebugInfos);
		}

		sealed class CapturingOutput : StringBuilderDecompilerOutput, IDecompilerOutput {
			public List<MethodDebugInfo> DebugInfos { get; } = new List<MethodDebugInfo>();

			bool IDecompilerOutput.UsesCustomData => true;

			void IDecompilerOutput.AddCustomData<TData>(string id, TData data) {
				if (id == PredefinedCustomDataIds.DebugInfo && data is MethodDebugInfo info)
					DebugInfos.Add(info);
			}
		}
	}
}
