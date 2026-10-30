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
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace dnSpy.Decompiler.Upstream.Tests.Infrastructure {
	/// <summary>
	/// Compares the nullable annotations of two images of an assembly the way the C# compiler reads them: the types and
	/// members that are in both must have the same annotations ('?', not annotated, or oblivious)
	/// </summary>
	static class NullabilityOracle {
		/// <param name="original">Original image</param>
		/// <param name="edited">Edited image</param>
		/// <param name="topLevelType">Only compare this type (metadata name) and its nested types, or null to compare all types</param>
		public static List<string> Compare(byte[] original, byte[] edited, string? topLevelType = null) {
			var before = Describe(original, topLevelType);
			var after = Describe(edited, topLevelType);
			var differences = new List<string>();
			foreach (var kv in before) {
				if (after.TryGetValue(kv.Key, out var description) && description != kv.Value)
					differences.Add($"{kv.Key}: {kv.Value} -> {description}");
			}
			return differences;
		}

		static Dictionary<string, string> Describe(byte[] image, string? topLevelType) {
			var reference = MetadataReference.CreateFromImage(image);
			var compilation = CSharpCompilation.Create("NullabilityOracle", references: TestCompiler.References.Append(reference));
			var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
			var result = new Dictionary<string, string>(StringComparer.Ordinal);
			var types = topLevelType is null ? GetTypes(assembly.GlobalNamespace) :
				assembly.GetTypeByMetadataName(topLevelType) is INamedTypeSymbol t ? GetTypes(t) : Enumerable.Empty<INamedTypeSymbol>();
			foreach (var type in types) {
				var typeKey = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				var sb = new StringBuilder();
				if (type.BaseType is INamedTypeSymbol baseType)
					sb.Append("base ").Append(Describe(baseType)).Append("; ");
				// (The order of the interfaces doesn't matter: metadata sorts them, the compiled code has the source order)
				foreach (var iface in type.Interfaces.Select(Describe).OrderBy(a => a, StringComparer.Ordinal))
					sb.Append("interface ").Append(iface).Append("; ");
				AppendConstraints(sb, type.TypeParameters);
				result[typeKey] = sb.ToString();
				foreach (var member in type.GetMembers()) {
					if (member.IsImplicitlyDeclared && member is not IMethodSymbol { MethodKind: MethodKind.Constructor })
						continue;
					var (key, description) = member switch {
						IFieldSymbol f => ($"field {f.Name}", Describe(f.Type)),
						IPropertySymbol p => ($"property {p.Name}({string.Join(",", p.Parameters.Select(a => Key(a.Type)))})", Describe(p.Type) + "(" + string.Join(", ", p.Parameters.Select(a => Describe(a.Type))) + ")"),
						IEventSymbol e => ($"event {e.Name}", Describe(e.Type)),
						IMethodSymbol m => ($"method {m.Name}`{m.Arity}({string.Join(",", m.Parameters.Select(a => a.RefKind + Key(a.Type)))})", DescribeMethod(m)),
						_ => (null, null),
					};
					if (key is not null)
						result[typeKey + " " + key] = description!;
				}
			}
			return result;
		}

		static IEnumerable<INamedTypeSymbol> GetTypes(INamespaceSymbol ns) {
			foreach (var type in ns.GetTypeMembers()) {
				foreach (var t in GetTypes(type))
					yield return t;
			}
			foreach (var child in ns.GetNamespaceMembers()) {
				foreach (var t in GetTypes(child))
					yield return t;
			}
		}

		static IEnumerable<INamedTypeSymbol> GetTypes(INamedTypeSymbol type) {
			yield return type;
			foreach (var nested in type.GetTypeMembers()) {
				foreach (var t in GetTypes(nested))
					yield return t;
			}
		}

		static string DescribeMethod(IMethodSymbol method) {
			var sb = new StringBuilder();
			sb.Append(Describe(method.ReturnType)).Append(" (");
			sb.Append(string.Join(", ", method.Parameters.Select(a => Describe(a.Type))));
			sb.Append(") ");
			AppendConstraints(sb, method.TypeParameters);
			return sb.ToString();
		}

		static void AppendConstraints(StringBuilder sb, IEnumerable<ITypeParameterSymbol> typeParameters) {
			foreach (var tp in typeParameters) {
				sb.Append("where ").Append(tp.Name).Append(':');
				if (tp.HasReferenceTypeConstraint)
					sb.Append("class").Append(Annotation(tp.ReferenceTypeConstraintNullableAnnotation)).Append(',');
				if (tp.HasNotNullConstraint)
					sb.Append("notnull,");
				foreach (var (constraint, annotation) in tp.ConstraintTypes.Zip(tp.ConstraintNullableAnnotations))
					sb.Append(Describe(constraint)).Append(',');
				sb.Append(' ');
			}
		}

		// Type without the annotations (to match members)
		static string Key(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		/// <summary>
		/// The type with the annotation of every reference type in it: '?' annotated, '!' not annotated, '~' oblivious
		/// </summary>
		static string Describe(ITypeSymbol type) {
			switch (type) {
			case IArrayTypeSymbol array:
				return Describe(array.ElementType) + "[]" + Annotation(type.NullableAnnotation);
			case IPointerTypeSymbol pointer:
				return Describe(pointer.PointedAtType) + "*";
			case INamedTypeSymbol named:
				var name = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				int lt = name.IndexOf('<');
				if (lt >= 0)
					name = name.Substring(0, lt);
				var args = named.IsTupleType ? named.TupleElements.Select(a => Describe(a.Type)) : named.TypeArguments.Select(Describe);
				var text = named.Arity == 0 ? name : name + "<" + string.Join(",", args) + ">";
				return text + (type.IsValueType ? "" : Annotation(type.NullableAnnotation));
			case ITypeParameterSymbol tp:
				return tp.Name + Annotation(type.NullableAnnotation);
			default:
				return type.ToDisplayString() + Annotation(type.NullableAnnotation);
			}
		}

		static string Annotation(NullableAnnotation annotation) => annotation switch {
			NullableAnnotation.Annotated => "?",
			NullableAnnotation.NotAnnotated => "!",
			_ => "~",
		};
	}
}
