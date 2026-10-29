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
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.Upstream.Bridge {
	/// <summary>
	/// A copy of a bridged image in which some method bodies are 'ldnull; throw'. The upstream decompiler can only
	/// decompile a whole type, so Edit Method on a member of a big type would spend most of its time on code that
	/// is thrown away. Tokens, signatures and everything else stay the same.
	/// </summary>
	static class StubbedImage {
		// Tiny header (code size 2), ldnull, throw
		static readonly byte[] stubBody = { 0x0A, 0x14, 0x7A };

		/// <summary>
		/// Creates the image, or returns null if there isn't enough code to replace or the image can't be patched
		/// </summary>
		/// <param name="cache">Cache, the image is registered in it so its entities map back to dnlib</param>
		/// <param name="bridged">Image of the module</param>
		/// <param name="methods">Methods whose bodies are replaced</param>
		/// <param name="minSize">Min total size of the replaced bodies (smaller savings aren't worth a type system)</param>
		public static BridgedModule? TryCreate(ModuleBridgeCache cache, BridgedModule bridged, IEnumerable<MethodDef> methods, int minSize) {
			try {
				return TryCreateCore(cache, bridged, methods, minSize);
			}
			catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException) {
				return null;
			}
		}

		readonly struct Range {
			public readonly int Start, End;
			public readonly bool IsStub;
			public Range(int start, int end, bool isStub) {
				Start = start;
				End = end;
				IsStub = isStub;
			}
		}

		static BridgedModule? TryCreateCore(ModuleBridgeCache cache, BridgedModule bridged, IEnumerable<MethodDef> methods, int minSize) {
			if (bridged.File is not PEFile pe)
				return null;
			var reader = pe.Reader;
			if (!reader.IsEntireImageAvailable)
				return null;
			var metadata = pe.Metadata;

			var stubbed = new HashSet<MethodDefinitionHandle>();
			foreach (var method in methods) {
				int token = bridged.Tokens.GetToken(method);
				if ((token >> 24) == 0x06 && (token & 0x00FFFFFF) != 0)
					stubbed.Add(MetadataTokens.MethodDefinitionHandle(token & 0x00FFFFFF));
			}
			if (stubbed.Count == 0)
				return null;

			// Everything that has to stay as it is: other bodies, field data, PE and CLI data. Bodies that share an
			// RVA with a body that stays aren't replaced.
			var ranges = new List<Range>();
			var keptBodies = new HashSet<int>();
			int stubbedSize = 0;
			foreach (var handle in metadata.MethodDefinitions) {
				int rva = metadata.GetMethodDefinition(handle).RelativeVirtualAddress;
				if (rva == 0)
					continue;
				int size = GetBodySize(reader, rva);
				bool isStub = stubbed.Contains(handle) && size >= stubBody.Length;
				if (!isStub)
					keptBodies.Add(rva);
				ranges.Add(new Range(rva, rva + Math.Max(size, 1), isStub));
			}
			foreach (var handle in metadata.FieldDefinitions) {
				var field = metadata.GetFieldDefinition(handle);
				int rva = field.GetRelativeVirtualAddress();
				if (rva != 0)
					ranges.Add(new Range(rva, rva + Math.Max(GetFieldDataSize(metadata, field), 1), false));
			}
			AddDirectories(ranges, reader.PEHeaders);
			ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

			// A body is replaced if nothing else starts or ends in the bytes that are overwritten
			var patches = new HashSet<int>();
			int maxEndBefore = 0;
			for (int i = 0; i < ranges.Count; i++) {
				var range = ranges[i];
				if (range.IsStub && !keptBodies.Contains(range.Start) && maxEndBefore <= range.Start && !patches.Contains(range.Start)) {
					int patchEnd = range.Start + stubBody.Length;
					bool overlaps = false;
					for (int j = i + 1; j < ranges.Count && ranges[j].Start < patchEnd; j++) {
						if (!(ranges[j].IsStub && ranges[j].Start == range.Start)) {
							overlaps = true;
							break;
						}
					}
					if (!overlaps && RvaToOffset(reader, range.Start) >= 0) {
						patches.Add(range.Start);
						stubbedSize += range.End - range.Start;
					}
				}
				// Bodies at the same RVA are one body
				if (!(i + 1 < ranges.Count && ranges[i + 1].Start == range.Start && ranges[i + 1].IsStub && range.IsStub))
					maxEndBefore = Math.Max(maxEndBefore, range.End);
			}
			if (patches.Count == 0 || stubbedSize < minSize)
				return null;

			var image = reader.GetEntireImage();
			var bytes = new byte[image.Length];
			image.GetContent().CopyTo(bytes);
			foreach (int rva in patches) {
				int offset = RvaToOffset(reader, rva);
				if (offset < 0 || offset + stubBody.Length > bytes.Length)
					return null;
				Array.Copy(stubBody, 0, bytes, offset, stubBody.Length);
			}
			var options = PEStreamOptions.PrefetchEntireImage;
			if (reader.IsLoadedImage)
				options |= PEStreamOptions.IsLoadedImage;
			var file = new PEFile(pe.FileName, new MemoryStream(bytes, writable: false), options);
			return cache.AddImage(bridged, file);
		}

		static int GetBodySize(PEReader reader, int rva) {
			try {
				return reader.GetMethodBody(rva).Size;
			}
			catch (Exception ex) when (ex is BadImageFormatException || ex is ArgumentException || ex is InvalidOperationException) {
				return -1;
			}
		}

		/// <summary>
		/// Size of a field's initial data, or 0 if unknown
		/// </summary>
		static int GetFieldDataSize(MetadataReader metadata, FieldDefinition field) {
			try {
				var sig = metadata.GetBlobReader(field.Signature);
				if (sig.ReadSignatureHeader().Kind != SignatureKind.Field)
					return 0;
				var code = sig.ReadSignatureTypeCode();
				while (code == SignatureTypeCode.RequiredModifier || code == SignatureTypeCode.OptionalModifier) {
					sig.ReadTypeHandle();
					code = sig.ReadSignatureTypeCode();
				}
				switch (code) {
				case SignatureTypeCode.Boolean:
				case SignatureTypeCode.SByte:
				case SignatureTypeCode.Byte:
					return 1;
				case SignatureTypeCode.Char:
				case SignatureTypeCode.Int16:
				case SignatureTypeCode.UInt16:
					return 2;
				case SignatureTypeCode.Int32:
				case SignatureTypeCode.UInt32:
				case SignatureTypeCode.Single:
					return 4;
				case SignatureTypeCode.Int64:
				case SignatureTypeCode.UInt64:
				case SignatureTypeCode.Double:
				case SignatureTypeCode.IntPtr:
				case SignatureTypeCode.UIntPtr:
					return 8;
				case SignatureTypeCode.TypeHandle:
					var type = sig.ReadTypeHandle();
					if (type.Kind == HandleKind.TypeDefinition)
						return Math.Max(0, metadata.GetTypeDefinition((TypeDefinitionHandle)type).GetLayout().Size);
					return 0;
				default:
					return 0;
				}
			}
			catch (BadImageFormatException) {
				return 0;
			}
		}

		static void AddDirectories(List<Range> ranges, PEHeaders headers) {
			void Add(DirectoryEntry entry) {
				if (entry.RelativeVirtualAddress > 0 && entry.Size > 0)
					ranges.Add(new Range(entry.RelativeVirtualAddress, entry.RelativeVirtualAddress + entry.Size, false));
			}
			if (headers.PEHeader is PEHeader pe) {
				Add(pe.ExportTableDirectory);
				Add(pe.ImportTableDirectory);
				Add(pe.ResourceTableDirectory);
				Add(pe.ExceptionTableDirectory);
				Add(pe.BaseRelocationTableDirectory);
				Add(pe.DebugTableDirectory);
				Add(pe.CopyrightTableDirectory);
				Add(pe.GlobalPointerTableDirectory);
				Add(pe.ThreadLocalStorageTableDirectory);
				Add(pe.LoadConfigTableDirectory);
				Add(pe.BoundImportTableDirectory);
				Add(pe.ImportAddressTableDirectory);
				Add(pe.DelayImportTableDirectory);
				Add(pe.CorHeaderTableDirectory);
			}
			if (headers.CorHeader is CorHeader cor) {
				Add(cor.MetadataDirectory);
				Add(cor.ResourcesDirectory);
				Add(cor.StrongNameSignatureDirectory);
				Add(cor.CodeManagerTableDirectory);
				Add(cor.VtableFixupsDirectory);
				Add(cor.ExportAddressTableJumpsDirectory);
				Add(cor.ManagedNativeHeaderDirectory);
			}
		}

		static int RvaToOffset(PEReader reader, int rva) {
			if (reader.IsLoadedImage)
				return rva;
			var headers = reader.PEHeaders;
			int index = headers.GetContainingSectionIndex(rva);
			if (index < 0)
				return -1;
			var section = headers.SectionHeaders[index];
			int delta = rva - section.VirtualAddress;
			if (delta < 0 || delta + stubBody.Length > section.SizeOfRawData)
				return -1;
			return section.PointerToRawData + delta;
		}
	}
}
