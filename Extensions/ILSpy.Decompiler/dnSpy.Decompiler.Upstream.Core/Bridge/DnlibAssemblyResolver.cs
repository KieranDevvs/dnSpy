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
using System.Threading.Tasks;
using dnlib.DotNet;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.Upstream.Bridge {
	/// <summary>
	/// Resolves assemblies for the upstream decompiler with dnlib's resolver (in dnSpy: the documents in the
	/// assembly explorer), so references resolve to exactly the modules the user sees.
	/// </summary>
	sealed class DnlibAssemblyResolver : ICSharpCode.Decompiler.Metadata.IAssemblyResolver {
		readonly ModuleBridgeCache cache;
		readonly ModuleDef sourceModule;

		public DnlibAssemblyResolver(ModuleBridgeCache cache, ModuleDef sourceModule) {
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.sourceModule = sourceModule ?? throw new ArgumentNullException(nameof(sourceModule));
		}

		public MetadataFile? Resolve(IAssemblyReference reference) {
			var asmRef = new AssemblyRefUser(reference.Name, reference.Version ?? new Version(0, 0, 0, 0),
				reference.PublicKeyToken is byte[] pkt && pkt.Length != 0 ? new PublicKeyToken(pkt) : null,
				reference.Culture ?? string.Empty);
			AssemblyDef? asm;
			try {
				asm = sourceModule.Context.AssemblyResolver.Resolve(asmRef, sourceModule);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException) {
				return null;
			}
			return TryBridge(asm?.ManifestModule);
		}

		public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) {
			var asm = cache.Find(mainModule)?.Module.Assembly;
			if (asm is null)
				return null;
			foreach (var module in asm.Modules) {
				if (StringComparer.OrdinalIgnoreCase.Equals(UTF8String.ToSystemStringOrEmpty(module.Name), moduleName))
					return TryBridge(module);
			}
			return null;
		}

		public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

		public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

		public IDisposable? BeginSnapshot() => null;

		MetadataFile? TryBridge(ModuleDef? module) {
			if (module is null)
				return null;
			try {
				return cache.Get(module).File;
			}
			catch (ModuleBridgeException) {
				// Decompile anyway, the types in this module will be unresolved
				return null;
			}
		}
	}
}
