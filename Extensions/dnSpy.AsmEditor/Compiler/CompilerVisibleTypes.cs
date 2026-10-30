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
using dnlib.DotNet;

namespace dnSpy.AsmEditor.Compiler {
	/// <summary>
	/// Which of the edited module's types the edited code sees, and which types of other references would clash with them
	/// </summary>
	static class CompilerVisibleTypes {
		const string EmbeddedAttribute = "Microsoft.CodeAnalysis.EmbeddedAttribute";
		const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

		/// <summary>
		/// A type marked [Microsoft.CodeAnalysis.Embedded] by a source generator (e.g. ILSpy's [DecompilerAstNode]). The
		/// compiler doesn't see embedded types of other assemblies, but the decompiled code uses them. (The types the
		/// compiler embeds itself, e.g. NullableAttribute, are also [CompilerGenerated]; it adds them again when needed.)
		/// </summary>
		public static bool IsEmbeddedByGenerator(TypeDef type) =>
			type.FullName != EmbeddedAttribute &&
			type.CustomAttributes.IsDefined(EmbeddedAttribute) &&
			!type.CustomAttributes.IsDefined(CompilerGeneratedAttribute);

		static bool IsEmbeddedByCompiler(TypeDef type) =>
			type.FullName == EmbeddedAttribute ||
			(type.CustomAttributes.IsDefined(EmbeddedAttribute) && type.CustomAttributes.IsDefined(CompilerGeneratedAttribute));

		/// <summary>
		/// Full names of the edited module's top-level types that the edited code sees (it gets access to internal types)
		/// </summary>
		public static HashSet<string> GetTypeNames(ModuleDef editedModule) {
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var type in editedModule.Types) {
				if (type.IsGlobalModuleType || type.Name.StartsWith("<") || IsEmbeddedByCompiler(type))
					continue;
				names.Add(type.FullName);
			}
			return names;
		}

		/// <summary>
		/// Public top-level types of <paramref name="module"/> that the edited module also defines, typically its own
		/// internal copy of a type that's newer than the framework it targets (NotNullWhenAttribute, IsExternalInit,
		/// System.Index). The compiler would see both and report them as ambiguous (CS0433); the original compiler
		/// used the module's own type.
		/// </summary>
		public static List<TypeDef> GetClashingTypes(ModuleDef module, HashSet<string> editedModuleTypeNames) {
			var types = new List<TypeDef>();
			if (editedModuleTypeNames.Count == 0)
				return types;
			foreach (var type in module.Types) {
				if (!type.IsPublic)
					continue;
				var fullName = type.FullName;
				if (editedModuleTypeNames.Contains(fullName) && !compilerRequiredTypes.Contains(fullName))
					types.Add(type);
			}
			return types;
		}

		/// <summary>
		/// The compiler's special types (and a few that every compilation needs): never hidden, even if a module defines
		/// its own
		/// </summary>
		static readonly HashSet<string> compilerRequiredTypes = new HashSet<string>(StringComparer.Ordinal) {
			"System.ArgIterator",
			"System.Array",
			"System.AsyncCallback",
			"System.Attribute",
			"System.Boolean",
			"System.Byte",
			"System.Char",
			"System.Collections.Generic.ICollection`1",
			"System.Collections.Generic.IEnumerable`1",
			"System.Collections.Generic.IEnumerator`1",
			"System.Collections.Generic.IList`1",
			"System.Collections.Generic.IReadOnlyCollection`1",
			"System.Collections.Generic.IReadOnlyList`1",
			"System.Collections.IEnumerable",
			"System.Collections.IEnumerator",
			"System.DateTime",
			"System.Decimal",
			"System.Delegate",
			"System.Double",
			"System.Enum",
			"System.Exception",
			"System.IAsyncResult",
			"System.IDisposable",
			"System.Int16",
			"System.Int32",
			"System.Int64",
			"System.IntPtr",
			"System.MulticastDelegate",
			"System.Nullable`1",
			"System.Object",
			"System.ReadOnlySpan`1",
			"System.Runtime.CompilerServices.InlineArrayAttribute",
			"System.Runtime.CompilerServices.IsVolatile",
			"System.Runtime.CompilerServices.PreserveBaseOverridesAttribute",
			"System.Runtime.CompilerServices.RuntimeFeature",
			"System.RuntimeArgumentHandle",
			"System.RuntimeFieldHandle",
			"System.RuntimeMethodHandle",
			"System.RuntimeTypeHandle",
			"System.SByte",
			"System.Single",
			"System.Span`1",
			"System.String",
			"System.Type",
			"System.TypedReference",
			"System.UInt16",
			"System.UInt32",
			"System.UInt64",
			"System.UIntPtr",
			"System.ValueType",
			"System.Void",
		};
	}
}
