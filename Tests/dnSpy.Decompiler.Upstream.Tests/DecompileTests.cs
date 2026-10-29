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
using dnlib.DotNet;
using dnSpy.Decompiler.Upstream.Output;
using dnSpy.Decompiler.Upstream.Tests.Infrastructure;
using ICSharpCode.Decompiler.TypeSystem;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.Upstream.Tests {
	public class DecompileTests {
		readonly ITestOutputHelper log;

		public DecompileTests(ITestOutputHelper log) => this.log = log;

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void References_MapBackToTheRightDnlibDefinitions(bool normalize) {
			var module = TestCompiler.Compile(nameof(References_MapBackToTheRightDnlibDefinitions) + normalize, TestPrograms.ControlFlow).Load();
			var decompiler = TestDecompiler.Create(alwaysNormalize: normalize);
			var result = decompiler.DecompileType(module.Find("ControlFlow", false));

			int mapped = 0;
			var mismatches = new List<string>();
			foreach (var reference in result.References) {
				if (reference.Target is not IEntity entity)
					continue;
				var dnlib = decompiler.Cache.ToDnlib(entity);
				if (dnlib is null)
					continue; // defined in an assembly that isn't bridged or not a definition
				mapped++;
				var text = result.Text.Substring(reference.Start, reference.Length);
				if (!NameMatches(dnlib, text))
					mismatches.Add($"'{text}' -> {dnlib}");
			}
			log.WriteLine($"{mapped} references mapped to dnlib definitions");
			foreach (var m in mismatches)
				log.WriteLine("Mismatch: " + m);
			Assert.True(mapped > 20, $"Only {mapped} references were mapped");
			Assert.Empty(mismatches);
		}

		static readonly Dictionary<string, string> keywordTypes = new Dictionary<string, string> {
			["object"] = "System.Object", ["string"] = "System.String", ["bool"] = "System.Boolean", ["char"] = "System.Char",
			["sbyte"] = "System.SByte", ["byte"] = "System.Byte", ["short"] = "System.Int16", ["ushort"] = "System.UInt16",
			["int"] = "System.Int32", ["uint"] = "System.UInt32", ["long"] = "System.Int64", ["ulong"] = "System.UInt64",
			["float"] = "System.Single", ["double"] = "System.Double", ["decimal"] = "System.Decimal", ["void"] = "System.Void",
			["nint"] = "System.IntPtr", ["nuint"] = "System.UIntPtr",
		};

		/// <summary>
		/// Does the referenced dnlib definition match the text? Follows upstream's reference conventions: keywords
		/// reference their type, an invocation's parentheses reference the invoked method, local functions use their
		/// source name.
		/// </summary>
		static bool NameMatches(IMDTokenProvider dnlib, string text) {
			switch (dnlib) {
			case TypeDef type:
				// Nullable annotation (string?)
				if (text == "?")
					return true;
				if (keywordTypes.TryGetValue(text, out var fullName))
					return type.FullName == fullName;
				var name = type.Name.String;
				int tick = name.IndexOf('`');
				return (tick >= 0 ? name.Substring(0, tick) : name) == text;
			case MethodDef method:
				if (text is "(" or ")" or "[" or "]")
					return true;
				// Operators reference the method that implements them (string + string is String.Concat)
				if (text.Length <= 3 && !char.IsLetter(text[0]))
					return method.IsStatic;
				if (method.IsConstructor)
					return method.DeclaringType.Name.String.StartsWith(text) || text == "base" || text == "this";
				var methodName = method.Name.String;
				// Local function: <Outer>g__Name|N
				int g = methodName.IndexOf(">g__");
				if (g >= 0) {
					int bar = methodName.IndexOf('|', g);
					methodName = methodName.Substring(g + 4, (bar < 0 ? methodName.Length : bar) - (g + 4));
				}
				return methodName == text;
			case IMemberDef member:
				return member.Name.String == text;
			default:
				return true;
			}
		}

		[Fact]
		public void ModernCSharp_IsDecompiledWithModernSyntax() {
			var module = TestCompiler.Compile(nameof(ModernCSharp_IsDecompiledWithModernSyntax), TestPrograms.ModernCSharp).Load();
			var decompiler = TestDecompiler.Create();
			var text = string.Join("\n", module.Types.Where(t => t.Namespace == "Modern").Select(t => decompiler.DecompileType(t).Text));
			log.WriteLine(text);

			Assert.Contains("class Point(int x, int y)", text);           // C# 12 primary constructor
			Assert.Contains("extension(string s)", text);                 // C# 14 extension block
			Assert.Contains("field = value.Trim();", text);               // C# 14 field keyword
			Assert.Contains("required string Name", text);                // C# 11 required member
			Assert.Contains("record Person(string First, string Last)", text);
			Assert.Contains("[InlineArray(4)]", text);                    // C# 12 inline array
			Assert.Contains("public ref int Value;", text);               // C# 11 ref field
			Assert.Contains("\"hello\"u8", text);                         // C# 11 UTF-8 literal
		}
	}
}
