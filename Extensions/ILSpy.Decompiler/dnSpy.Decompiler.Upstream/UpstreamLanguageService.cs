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
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Settings;
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

		[ImportingConstructor]
		UpstreamLanguageService(ISettingsService settingsService) {
			this.settingsService = settingsService;
			Settings = new UpstreamDecompilerSettings();
			Load();
			Settings.VersionChanged += (s, e) => Save();
			Language = new UpstreamCSharpLanguage(Settings);
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
	}

	[Export(typeof(IDecompilerCreator))]
	sealed class UpstreamDecompilerCreator : IDecompilerCreator {
		readonly UpstreamLanguageService service;

		[ImportingConstructor]
		UpstreamDecompilerCreator(UpstreamLanguageService service) => this.service = service;

		public IEnumerable<IDecompiler> Create() {
			yield return service.Language;
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
			// dnSpy refreshed the tabs before raising the event, so they may have used the old image. Edits are
			// rare, refreshing this decompiler's visible tabs again is cheap enough.
			var tabs = service.GetTabs(documentTabService);
			if (tabs.Length != 0)
				documentTabService.Refresh(tabs);
		}

		void DocumentTabService_DocumentCollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			switch (e.Type) {
			case NotifyDocumentCollectionType.Add:
				service.Language.OnModulesAdded();
				break;
			case NotifyDocumentCollectionType.Remove:
			case NotifyDocumentCollectionType.Clear:
				service.Language.OnModulesRemoved();
				break;
			}
		}
	}
}
