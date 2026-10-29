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
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.Upstream.Bridge {
	/// <summary>
	/// Which image the upstream decompiler reads
	/// </summary>
	enum BridgeImageKind {
		/// <summary>
		/// The module's original bytes, tokens are identical to dnlib's tokens
		/// </summary>
		Original,

		/// <summary>
		/// An image written by dnlib from the in-memory module (edited, dynamic, or anomalous metadata)
		/// </summary>
		Normalized,
	}

	sealed class ModuleBridgeOptions {
		/// <summary>
		/// Always let dnlib rewrite the module, even if the original image looks safe to read
		/// </summary>
		public bool AlwaysNormalize { get; set; }
	}

	/// <summary>
	/// A dnlib module made readable by the upstream decompiler
	/// </summary>
	sealed class BridgedModule : IDisposable {
		public ModuleDef Module { get; }
		public MetadataFile File { get; }
		public ITokenMap Tokens { get; }
		public BridgeImageKind ImageKind { get; }

		/// <summary>
		/// Why the module was normalized (empty if <see cref="ImageKind"/> is <see cref="BridgeImageKind.Original"/>)
		/// </summary>
		public IReadOnlyList<string> NormalizeReasons { get; }

		/// <summary>
		/// Time it took to check and (if needed) rewrite the module
		/// </summary>
		public TimeSpan CreationTime { get; }

		public BridgedModule(ModuleDef module, MetadataFile file, ITokenMap tokens, BridgeImageKind imageKind, IReadOnlyList<string> normalizeReasons, TimeSpan creationTime) {
			Module = module;
			File = file;
			Tokens = tokens;
			ImageKind = imageKind;
			NormalizeReasons = normalizeReasons;
			CreationTime = creationTime;
		}

		public void Dispose() => File.Dispose();
	}

	sealed class ModuleBridgeException : Exception {
		public ModuleBridgeException(string message, Exception? innerException) : base(message, innerException) { }
	}

	static class ModuleBridge {
		/// <summary>
		/// Creates an image of <paramref name="module"/> that the upstream decompiler can read
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="isModified">true if the module has been edited in memory since it was loaded</param>
		/// <param name="options">Options</param>
		public static BridgedModule Create(ModuleDef module, bool isModified, ModuleBridgeOptions options) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (options is null)
				throw new ArgumentNullException(nameof(options));
			var sw = Stopwatch.StartNew();
			var reasons = new List<string>();
			if (options.AlwaysNormalize)
				reasons.Add("Normalization is always enabled");
			if (isModified)
				reasons.Add("Module was modified in memory");
			var mdModule = module as ModuleDefMD;
			if (mdModule is null)
				reasons.Add("Module wasn't read from metadata (created or emitted in memory)");

			if (reasons.Count == 0 && mdModule is not null) {
				reasons.AddRange(MetadataAnomalyDetector.GetStructuralAnomalies(mdModule));
				if (reasons.Count == 0) {
					PEFile? file = null;
					try {
						file = OpenOriginal(mdModule);
						reasons.AddRange(MetadataAnomalyDetector.CompareWithDnlib(mdModule, file.Metadata));
					}
					catch (Exception ex) when (IsImageReadException(ex)) {
						reasons.Add("System.Reflection.Metadata can't read the original image: " + ex.Message);
					}
					if (reasons.Count == 0 && file is not null)
						return new BridgedModule(module, file, new IdentityTokenMap(module), BridgeImageKind.Original, Array.Empty<string>(), sw.Elapsed);
					file?.Dispose();
				}
			}

			return Normalize(module, reasons, sw);
		}

		static BridgedModule Normalize(ModuleDef module, List<string> reasons, Stopwatch sw) {
			Exception? firstError = null;
			// Preserving rids keeps tokens stable (same as what dnSpy shows), but it can fail on some
			// obfuscated modules. Fall back to fresh rids; the token map works either way.
			foreach (bool preserveRids in new[] { true, false }) {
				try {
					var (bytes, metadata) = Write(module, preserveRids);
					var map = RewriteTokenMap.Create(module, metadata);
					var file = new PEFile(GetFileName(module), new MemoryStream(bytes, writable: false), PEStreamOptions.PrefetchEntireImage);
					if (!preserveRids)
						reasons.Add("Rids couldn't be preserved: " + firstError?.Message);
					return new BridgedModule(module, file, map, BridgeImageKind.Normalized, reasons.ToArray(), sw.Elapsed);
				}
				catch (Exception ex) when (ex is not OutOfMemoryException) {
					firstError ??= ex;
				}
			}
			throw new ModuleBridgeException($"dnlib couldn't write a decompilable image of module '{module.Name}'", firstError);
		}

		static (byte[] bytes, Metadata metadata) Write(ModuleDef module, bool preserveRids) {
			var options = new ModuleWriterOptions(module) {
				// Never fail because of an invalid member, we'd rather decompile the rest
				Logger = DummyLogger.NoThrowInstance,
				WritePdb = false,
				StrongNameKey = null,
				StrongNamePublicKey = null,
				DelaySign = false,
			};
			options.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
			if (preserveRids)
				options.MetadataOptions.Flags |= MetadataFlags.PreserveRids;
			var writer = new ModuleWriter(module, options);
			var stream = new MemoryStream();
			writer.Write(stream);
			return (stream.ToArray(), writer.Metadata);
		}

		static PEFile OpenOriginal(ModuleDefMD module) {
			var peImage = module.Metadata.PEImage;
			var bytes = peImage.CreateReader().ToArray();
			var streamOptions = PEStreamOptions.PrefetchEntireImage;
			// Modules loaded from a process' memory use the memory layout
			if (!peImage.IsFileImageLayout)
				streamOptions |= PEStreamOptions.IsLoadedImage;
			return new PEFile(GetFileName(module), new MemoryStream(bytes, writable: false), streamOptions);
		}

		static string GetFileName(ModuleDef module) {
			if (!string.IsNullOrEmpty(module.Location))
				return module.Location;
			var name = UTF8String.ToSystemStringOrEmpty(module.Name);
			return name.Length != 0 ? name : "module.dll";
		}

		static bool IsImageReadException(Exception ex) =>
			ex is BadImageFormatException || ex is InvalidOperationException || ex is ArgumentException ||
			ex is IndexOutOfRangeException || ex is NotSupportedException || ex is OverflowException ||
			ex is IOException || ex is MetadataFileNotSupportedException;
	}
}
