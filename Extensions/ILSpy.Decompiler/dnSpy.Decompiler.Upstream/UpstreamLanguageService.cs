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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Settings;
using dnSpy.Decompiler.ILSpy.Core.Mixed;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.Upstream.Settings;

namespace dnSpy.Decompiler.Upstream {
	/// <summary>
	/// Owns the decompiler instance and its settings, which are saved in dnSpy's settings file
	/// </summary>
	[Export]
	sealed class UpstreamLanguageService {
		static readonly Guid SETTINGS_GUID = new Guid("2E5E8E5F-5B1B-4C61-9C2C-7A2B8E3C4D11");

		readonly ISettingsService settingsService;
		bool disableSave;

		public UpstreamDecompilerSettings Settings { get; }
		public UpstreamCSharpLanguage Language { get; }
#if DEBUG
		/// <summary>
		/// The ILAst views (debug builds)
		/// </summary>
		public IReadOnlyList<ILAst.UpstreamILAstLanguage> ILAstLanguages { get; }
#endif

		[ImportingConstructor]
		UpstreamLanguageService(ISettingsService settingsService) {
			this.settingsService = settingsService;
			Settings = new UpstreamDecompilerSettings();
			Load();
			Settings.VersionChanged += (s, e) => Save();
			Language = new UpstreamCSharpLanguage(Settings);
#if DEBUG
			ILAstLanguages = ILAst.UpstreamILAstLanguage.Create(Language).ToArray();
#endif
		}

		void Load() {
			disableSave = true;
			try {
				var section = settingsService.GetOrCreateSection(SETTINGS_GUID);
				foreach (var info in UpstreamDecompilerSettings.AllSettings) {
					if (section.Attribute<bool?>(info.Name) is bool value)
						info.SetValue(Settings, value);
				}
			}
			finally {
				disableSave = false;
			}
		}

		void Save() {
			if (disableSave)
				return;
			// Only the settings the user changed: a later version of the upstream decompiler can change defaults
			var section = settingsService.RecreateSection(SETTINGS_GUID);
			foreach (var info in UpstreamDecompilerSettings.AllSettings) {
				bool value = info.GetValue(Settings);
				if (value != info.DefaultValue)
					section.Attribute(info.Name, value);
			}
		}

		/// <summary>
		/// Visible tabs that show this decompiler's output
		/// </summary>
		public IDocumentTab[] GetTabs(IDocumentTabService documentTabService) =>
			documentTabService.VisibleFirstTabs.Where(t => (t.Content as IDecompilerTabContent)?.Decompiler == Language).ToArray();

		/// <summary>
		/// Visible tabs that show this decompiler's output of a module in <paramref name="documents"/> or of a
		/// module that references one of them
		/// </summary>
		public IDocumentTab[] GetTabs(IDocumentTabService documentTabService, IDsDocument[] documents) {
			var docs = new HashSet<IDsDocument>(documents);
			var asmNames = new HashSet<string>(documents.Select(a => a.AssemblyDef?.Name.String).OfType<string>(), StringComparer.OrdinalIgnoreCase);
			return GetTabs(documentTabService).Where(tab => tab.Content.Nodes.Any(node => {
				var document = node.GetDocumentNode()?.Document;
				if (document is null)
					return false;
				if (docs.Contains(document))
					return true;
				var module = document.ModuleDef;
				return module is not null && module.GetAssemblyRefs().Any(a => asmNames.Contains(a.Name.String));
			})).ToArray();
		}
	}

	[Export(typeof(IDecompilerCreator))]
	sealed class UpstreamDecompilerCreator : IDecompilerCreator {
		readonly UpstreamLanguageService service;
		readonly DecompilerSettingsService? decompilerSettingsService;

		// The IL settings are exported by dnSpy's other decompiler extension: without it, there's no "IL with C#"
		[ImportingConstructor]
		UpstreamDecompilerCreator(UpstreamLanguageService service, [Import(AllowDefault = true)] DecompilerSettingsService? decompilerSettingsService) {
			this.service = service;
			this.decompilerSettingsService = decompilerSettingsService;
		}

		public IEnumerable<IDecompiler> Create() {
			yield return service.Language;
			// "IL with C# (ILSpy 11.2)": dnSpy's IL with this decompiler's C# code
			if (decompilerSettingsService is not null)
				yield return ILWithCSharpDecompiler.CreateUpstream(decompilerSettingsService.ILDecompilerSettings, service.Language);
#if DEBUG
			foreach (var ilast in service.ILAstLanguages)
				yield return ilast;
#endif
		}
	}

	/// <summary>
	/// Tells the decompiler about edited, added and removed modules
	/// </summary>
	[ExportAutoLoaded]
	sealed class DocumentListener : IAutoLoaded {
		readonly UpstreamLanguageService service;
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		DocumentListener(UpstreamLanguageService service, IDocumentTabService documentTabService) {
			this.service = service;
			this.documentTabService = documentTabService;
			documentTabService.DocumentModified += DocumentTabService_DocumentModified;
			documentTabService.DocumentCollectionChanged += DocumentTabService_DocumentCollectionChanged;
		}

		void DocumentTabService_DocumentModified(object? sender, DocumentModifiedEventArgs e) {
			var modules = e.Documents.Select(a => a.ModuleDef).OfType<ModuleDef>().Distinct().ToArray();
			if (modules.Length == 0)
				return;
			service.Language.OnModulesModified(modules);
			// dnSpy refreshed the tabs before raising the event, so they may have used the old image
			var tabs = service.GetTabs(documentTabService, e.Documents);
			if (tabs.Length != 0)
				documentTabService.Refresh(tabs);
		}

		void DocumentTabService_DocumentCollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			switch (e.Type) {
			case NotifyDocumentCollectionType.Add:
				service.Language.OnModulesAdded();
				break;
			case NotifyDocumentCollectionType.Remove:
				service.Language.OnModulesRemoved(e.Documents.Select(a => a.ModuleDef).OfType<ModuleDef>());
				break;
			case NotifyDocumentCollectionType.Clear:
				service.Language.OnAllModulesRemoved();
				break;
			}
		}
	}

	/// <summary>
	/// "Reload all" doesn't raise the collection events for documents that are reloaded
	/// </summary>
	[ExportDocumentListListener]
	sealed class UpstreamDocumentListListener : IDocumentListListener {
		readonly UpstreamLanguageService service;

		[ImportingConstructor]
		UpstreamDocumentListListener(UpstreamLanguageService service) => this.service = service;

		public bool CanLoad => true;
		public bool CanReload => true;
		public void BeforeLoad(bool isReload) { }
		public void AfterLoad(bool isReload) => service.Language.OnAllModulesRemoved();
		public bool CheckCanLoad(bool isReload) => true;
	}
}
