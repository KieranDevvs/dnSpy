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

namespace dnSpy.Decompiler.Upstream.Tests {
	/// <summary>
	/// Source of the programs the tests compile with Roslyn and then decompile
	/// </summary>
	static class TestPrograms {
		public const string ControlFlow = """
			using System;
			using System.Collections.Generic;
			using System.IO;

			public class ControlFlow {
				public int Loops(int[] values) {
					int sum = 0;
					for (int i = 0; i < values.Length; i++) {
						if (values[i] > 10)
							continue;
						sum += values[i];
					}
					foreach (var v in values)
						sum -= v;
					while (sum < 100)
						sum *= 2;
					do {
						sum--;
					} while (sum > 1000);
					return sum;
				}

				public string Exceptions(string text) {
					try {
						using var reader = new StringReader(text);
						return reader.ReadToEnd();
					}
					catch (IOException ex) when (ex.HResult != 0) {
						return ex.Message;
					}
					finally {
						Console.WriteLine("done");
					}
				}

				public string Switch(object o) => o switch {
					int i when i > 0 => "positive",
					int => "int",
					string s => s,
					null => "null",
					_ => "other",
				};

				public int SwitchStatement(int value) {
					switch (value) {
					case 1:
						return 10;
					case 2:
					case 3:
						value *= 3;
						break;
					default:
						value = -value;
						break;
					}
					return value;
				}

				public int Lambdas(List<int> list) {
					int captured = 3;
					Func<int, int> f = x => x * captured;
					int Local(int y) => y + captured;
					list.Sort((a, b) => b.CompareTo(a));
					return f(Local(list.Count));
				}

				public void Locking(object gate, List<string> log) {
					lock (gate) {
						log.Add("in");
					}
					checked {
						int big = int.MaxValue - 1;
						log.Add((big + 1).ToString());
					}
				}
			}
			""";

		public const string StateMachines = """
			using System;
			using System.Collections.Generic;
			using System.Threading.Tasks;

			public class StateMachines {
				public async Task<int> Compute(int x) {
					int a = await Task.FromResult(x);
					await Task.Delay(1);
					try {
						a += await Other(a);
					}
					catch (InvalidOperationException) {
						a = -1;
					}
					return a;
				}

				async Task<int> Other(int v) {
					await Task.Yield();
					return v * 2;
				}

				public async void Fire() {
					await Task.Delay(1);
					Console.WriteLine("fired");
				}

				public IEnumerable<int> Numbers(int n) {
					for (int i = 0; i < n; i++) {
						if (i % 2 == 0)
							yield return i;
					}
				}

				// Same shape as Newtonsoft.Json's JsonWriter.InternalWriteEndAsync/AwaitRemaining, where upstream
				// produced sequence points that start inside an instruction
				int levels;
				bool property;
				Task WriteNullAsync(System.Threading.CancellationToken ct) => Task.CompletedTask;
				Task WriteEndAsync(int token, System.Threading.CancellationToken ct) => Task.CompletedTask;
				int Pop() => levels--;
				void UpdateCurrentState() => property = !property;

				public async Task AwaitRemaining(int levelsToComplete, System.Threading.CancellationToken cancellationToken) {
					while (levelsToComplete-- > 0) {
						int token = Pop();
						if (property) {
							await WriteNullAsync(cancellationToken).ConfigureAwait(false);
						}
						await WriteEndAsync(token, cancellationToken).ConfigureAwait(false);
						UpdateCurrentState();
					}
				}

				public async IAsyncEnumerable<int> Stream(int n) {
					for (int i = 0; i < n; i++) {
						await Task.Yield();
						yield return i;
					}
				}
			}
			""";

		/// <summary>
		/// Every kind of definition the token maps must handle
		/// </summary>
		public const string Members = """
			using System;
			using System.Collections.Generic;

			public class Container<T> where T : class {
				public event EventHandler? Changed;
				public T? Value { get; set; }
				public int this[int index] => index * 2;
				public U Convert<U>(T value, Func<T, U> converter) where U : struct => converter(value);
				public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
				public class Nested<V> {
					public V? Item;
				}
			}

			public static class Constants {
				public static int Answer() => 42;
			}
			""";

		/// <summary>
		/// C# 11-14 features the legacy decompiler doesn't understand
		/// </summary>
		public const string ModernCSharp = """"
			using System;
			using System.Collections.Generic;
			using System.Runtime.CompilerServices;

			namespace Modern {
				public class Point(int x, int y) {
					public int X => x;
					public int Y => y;
				}

				public static class StringExtensions {
					extension(string s) {
						public bool IsShort => s.Length < 5;
						public string Twice() => s + s;
					}
				}

				public class FieldKeyword {
					public string Name {
						get;
						set => field = value.Trim();
					} = "";
				}

				public class Required {
					public required string Name { get; init; }
				}

				public record Person(string First, string Last);

				[InlineArray(4)]
				public struct Buffer4 {
					private int element;
				}

				public ref struct RefHolder {
					public ref int Value;
				}

				public class Patterns {
					public bool IsSmall(int[] values) => values is [_, _] or [];
					public string Describe(object o) => o is Person { First: var first } ? first : "?";
					public ReadOnlySpan<byte> Utf8() => "hello"u8;
					public string Raw() => $$"""{"x": {{1 + 1}}}""";
				}
			}
			"""";
	}
}
