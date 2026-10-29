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
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Upstream.Bridge;
using dnSpy.Decompiler.Upstream.Output;
using ICSharpCode.Decompiler.IL;

namespace dnSpy.Decompiler.Upstream.DebugInfo {
	/// <summary>
	/// Creates the objects that represent upstream variables, both in the text (references, so all uses of a
	/// variable are highlighted together) and in the debug info (locals the debugger shows). One instance is
	/// used for one decompilation so both use the same objects.
	/// </summary>
	sealed class SourceVariableTable {
		readonly ModuleBridgeCache cache;
		readonly TypeSigConverter typeSigConverter;
		readonly Dictionary<ILVariable, SourceLocal> locals = new Dictionary<ILVariable, SourceLocal>();
		readonly Dictionary<ILVariable, object> textReferences = new Dictionary<ILVariable, object>();

		public SourceVariableTable(ModuleBridgeCache cache, TypeSigConverter typeSigConverter) {
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.typeSigConverter = typeSigConverter ?? throw new ArgumentNullException(nameof(typeSigConverter));
		}

		/// <summary>
		/// Gets the object used as the reference of a variable in the text: the dnlib <see cref="Parameter"/> of
		/// parameters (what dnSpy's other decompilers use) and a <see cref="SourceLocal"/> for everything else
		/// </summary>
		public object GetTextReference(ILVariable variable) {
			if (textReferences.TryGetValue(variable, out var reference))
				return reference;
			reference = TryGetParameter(variable) ?? (object)GetLocal(variable);
			textReferences.Add(variable, reference);
			return reference;
		}

		Parameter? TryGetParameter(ILVariable variable) {
			if (variable.Kind != VariableKind.Parameter || !(variable.Index is int index))
				return null;
			if (!(variable.Function?.Method is ICSharpCode.Decompiler.TypeSystem.IMethod method) || !(cache.ToDnlib(method) is MethodDef md))
				return null;
			// Upstream: -1 is the hidden 'this' parameter. dnlib: Parameters includes a hidden 'this' parameter at
			// index 0 only if the signature has one, see MethodSigIndexBase.
			int dnlibIndex = index + md.Parameters.MethodSigIndexBase;
			if ((uint)dnlibIndex >= (uint)md.Parameters.Count)
				return null;
			return md.Parameters[dnlibIndex];
		}

		/// <summary>
		/// Gets the local of a variable (not a parameter)
		/// </summary>
		public SourceLocal GetLocal(ILVariable variable) {
			if (locals.TryGetValue(variable, out var local))
				return local;
			var flags = SourceVariableFlags.None;
			if (variable.IsRefReadOnly)
				flags |= SourceVariableFlags.ReadOnlyReference;
			var name = variable.Name ?? string.Empty;
			// SourceLocal derives DecompilerGenerated itself (no IL local and no hoisted field)
			if (GetHoistedField(variable) is FieldDef hoistedField)
				local = new SourceLocal(null, name, hoistedField, flags);
			else if (GetILLocal(variable) is Local ilLocal)
				local = new SourceLocal(ilLocal, name, ilLocal.Type, flags);
			else
				local = new SourceLocal(null, name, typeSigConverter.ToTypeSig(variable.Type), flags);
			locals.Add(variable, local);
			return local;
		}

		/// <summary>
		/// Gets the field of the state machine that stores a variable of an async/iterator method
		/// </summary>
		public FieldDef? GetHoistedField(ILVariable variable) =>
			variable.StateMachineField is null ? null : cache.ToDnlib(variable.StateMachineField) as FieldDef;

		/// <summary>
		/// Gets the IL local (slot) of a variable or null if it doesn't use one
		/// </summary>
		public Local? GetILLocal(ILVariable variable) {
			if (!(variable.Index is int index) || !IsILLocal(variable.Kind))
				return null;
			// IL offsets and locals belong to the method whose body was decompiled: MoveNext() for async/iterator methods
			var function = variable.Function;
			var method = function?.MoveNextMethod ?? function?.Method;
			var bodyMethod = method is null ? null : cache.ToDnlib(method) as MethodDef;
			var body = bodyMethod?.Body;
			if (body is null || (uint)index >= (uint)body.Variables.Count)
				return null;
			return body.Variables[index];
		}

		static bool IsILLocal(VariableKind kind) =>
			kind switch {
				VariableKind.Local or VariableKind.PinnedLocal or VariableKind.PinnedRegionLocal or VariableKind.UsingLocal or
				VariableKind.ForeachLocal or VariableKind.InitializerTarget or VariableKind.ExceptionLocal or VariableKind.DisplayClassLocal or
				VariableKind.PatternLocal => true,
				_ => false,
			};
	}
}
