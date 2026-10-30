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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Text;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Decompiler.Upstream {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		public IEnumerable<string> MergedResourceDictionaries => Array.Empty<string>();

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "C# decompiler using the upstream ILSpy engine (ICSharpCode.Decompiler)",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) { }
	}

	static class ContentTypeDefinitions {
#pragma warning disable CS0169
		// The C# language uses "C# ILSpy" (dnSpy.Decompiler.ILSpy's content type definitions)
#if DEBUG
		[Export]
		[Name(ILAst.UpstreamILAstLanguage.ContentTypeName)]
		[BaseDefinition(ContentTypes.DecompiledCode)]
		static readonly ContentTypeDefinition? ILAstUpstreamContentTypeDefinition;
#endif
#pragma warning restore CS0169
	}
}
