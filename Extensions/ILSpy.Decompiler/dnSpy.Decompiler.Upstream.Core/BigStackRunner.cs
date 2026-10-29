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
using System.Runtime.ExceptionServices;
using System.Threading;

namespace dnSpy.Decompiler.Upstream {
	/// <summary>
	/// Runs code on a thread with a big stack. The upstream decompiler is recursive and deeply nested code (common
	/// in obfuscated assemblies) overflows the default 1MB stack, which kills the process.
	/// </summary>
	static class BigStackRunner {
		// Only reserved, it's committed as it's used
		static int StackSize => IntPtr.Size == 8 ? 256 * 1024 * 1024 : 64 * 1024 * 1024;

		public static void Run(Action action) {
			if (action is null)
				throw new ArgumentNullException(nameof(action));
			ExceptionDispatchInfo? error = null;
			var culture = Thread.CurrentThread.CurrentCulture;
			var uiCulture = Thread.CurrentThread.CurrentUICulture;
			var thread = new Thread(() => {
				try {
					action();
				}
				catch (Exception ex) {
					error = ExceptionDispatchInfo.Capture(ex);
				}
			}, StackSize) {
				IsBackground = true,
				Name = "Upstream C# decompiler",
				CurrentCulture = culture,
				CurrentUICulture = uiCulture,
			};
			thread.Start();
			thread.Join();
			error?.Throw();
		}
	}
}
