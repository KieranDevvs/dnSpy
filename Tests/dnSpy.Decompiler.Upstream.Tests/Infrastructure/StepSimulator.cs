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
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.Upstream.Tests.Infrastructure {
	/// <summary>
	/// What the debugger knows about one method: its statements (IL spans and text), where the JIT lets it stop,
	/// and the async step info
	/// </summary>
	sealed class StepModel {
		public uint Token { get; }
		public MethodDef Method { get; }
		readonly List<(uint start, uint end, int statement)> spans = new List<(uint, uint, int)>();
		readonly List<string> texts = new List<string>();
		readonly Dictionary<string, int> statementIds = new Dictionary<string, int>();
		public HashSet<uint> Boundaries { get; } = new HashSet<uint>();
		public List<(uint yieldOffset, uint resumeOffset)> Awaits { get; } = new List<(uint, uint)>();
		readonly Dictionary<uint, Instruction> instructions;

		public StepModel(MethodDef method) {
			Method = method;
			Token = method.MDToken.Raw;
			method.Body.UpdateInstructionOffsets();
			instructions = method.Body.Instructions.ToDictionary(a => a.Offset);
		}

		/// <param name="key">Identifies the statement (its text span or source location): spans with the same key
		/// are the same statement</param>
		public void AddStatement(uint start, uint end, string key, string text) {
			if (!statementIds.TryGetValue(key, out int id)) {
				id = texts.Count;
				texts.Add(text);
				statementIds.Add(key, id);
			}
			spans.Add((start, end, id));
		}

		public int StatementCount => texts.Count;
		public string GetText(int statement) => texts[statement];
		public IEnumerable<uint> GetStarts(int statement) => spans.Where(a => a.statement == statement).Select(a => a.start);
		public IEnumerable<(uint start, uint end)> GetSpans(int statement) => spans.Where(a => a.statement == statement).Select(a => (a.start, a.end));

		public int? StatementAt(uint offset) {
			foreach (var (start, end, statement) in spans) {
				if (start <= offset && offset < end)
					return statement;
			}
			return null;
		}

		public Instruction? InstructionAt(uint offset) => instructions.TryGetValue(offset, out var instr) ? instr : null;

		/// <summary>
		/// dnSpy treats a hidden range right after a statement as part of it when it looks for awaits
		/// (DbgEngineStepperImpl.GetHiddenRanges)
		/// </summary>
		public bool IsAwaitOfStatement(uint yieldOffset, int statement) {
			if (StatementAt(yieldOffset) == statement)
				return true;
			if (StatementAt(yieldOffset) is not null)
				return false;
			// The hidden range containing the yield must start where one of the statement's spans ends
			uint start = yieldOffset;
			while (start > 0 && StatementAt(start - 1) is null && start - 1 >= 0)
				start--;
			return GetSpans(statement).Any(a => a.end == start);
		}

		/// <summary>
		/// The JIT's default boundaries (no PDB): where the evaluation stack is empty, nops, and the method start
		/// </summary>
		public void AddDefaultBoundaries() {
			Boundaries.Add(0);
			foreach (var (offset, depth) in StackDepthCalculator.Calculate(Method)) {
				if (depth == 0)
					Boundaries.Add(offset);
			}
			foreach (var instr in Method.Body.Instructions) {
				if (instr.OpCode.Code == Code.Nop)
					Boundaries.Add(instr.Offset);
			}
		}

		public static StepModel FromDebugInfo(MethodDebugInfo info, string text) {
			var model = new StepModel(info.Method);
			foreach (var s in info.Statements) {
				var t = text.Substring(s.TextSpan.Start, s.TextSpan.Length);
				model.AddStatement(s.ILSpan.Start, s.ILSpan.End, s.TextSpan.ToString(), t);
			}
			model.AddDefaultBoundaries();
			foreach (var a in info.AsyncInfo?.StepInfos ?? System.Array.Empty<AsyncStepInfo>()) {
				if (a.ResumeMethod == info.Method)
					model.Awaits.Add((a.YieldOffset, a.ResumeOffset));
			}
			return model;
		}

		public static StepModel FromPdb(MethodDef method, PdbMethodInfo pdb, string[] sourceLines) {
			var model = new StepModel(method);
			uint codeSize = (uint)method.Body.GetCodeSize();
			var points = pdb.SequencePointOffsets.Select(o => (offset: o, hidden: false))
				.Concat(pdb.HiddenSequencePointOffsets.Select(o => (offset: o, hidden: true))).OrderBy(p => p.offset).ToList();
			for (int i = 0; i < points.Count; i++) {
				// With a PDB the JIT can stop at each sequence point
				model.Boundaries.Add((uint)points[i].offset);
				if (points[i].hidden)
					continue;
				uint start = (uint)points[i].offset;
				uint end = i + 1 < points.Count ? (uint)points[i + 1].offset : codeSize;
				if (end <= start)
					continue;
				var loc = pdb.SourceLocations[points[i].offset];
				model.AddStatement(start, end, loc.ToString(), GetSourceText(sourceLines, loc));
			}
			model.Boundaries.Add(0);
			foreach (var (yieldOffset, resumeOffset) in pdb.Awaits)
				model.Awaits.Add(((uint)yieldOffset, (uint)resumeOffset));
			return model;
		}

		static string GetSourceText(string[] lines, (int startLine, int startColumn, int endLine, int endColumn) loc) {
			if (loc.startLine == loc.endLine)
				return lines[loc.startLine - 1].Substring(loc.startColumn - 1, loc.endColumn - loc.startColumn);
			var parts = new List<string> { lines[loc.startLine - 1].Substring(loc.startColumn - 1) };
			for (int l = loc.startLine; l < loc.endLine - 1; l++)
				parts.Add(lines[l]);
			parts.Add(lines[loc.endLine - 1].Substring(0, loc.endColumn - 1));
			return string.Join("\n", parts);
		}
	}

	/// <summary>
	/// A place where a step stopped
	/// </summary>
	readonly struct Stop {
		public readonly int Event;
		public readonly int Statement;
		public Stop(int @event, int statement) {
			Event = @event;
			Statement = statement;
		}
	}

	/// <summary>
	/// Replays dnSpy's stepper over an execution trace: where would pressing F10 (step over) repeatedly stop?
	/// Mirrors DbgEngineStepperImpl: the step range is the current statement's IL spans plus all IL without a
	/// statement; it stops at the first JIT boundary outside of it. An await in the step range sets a breakpoint
	/// at its resume offset, and the step continues there.
	/// </summary>
	sealed class StepSimulator {
		readonly List<TraceEvent> events;
		readonly List<TraceFrame> frames;
		readonly Dictionary<uint, StepModel> models;

		/// <summary>
		/// Number of times a step over an await continued in the frame that resumed it
		/// </summary>
		public int AwaitsFollowed { get; private set; }

		public StepSimulator(List<TraceEvent> events, List<TraceFrame> frames, Dictionary<uint, StepModel> models) {
			this.events = events;
			this.frames = frames;
			this.models = models;
		}

		/// <summary>
		/// Steps over from the first statement of a frame until the method returns. Follows awaits into the frames
		/// that resume them (returned in <paramref name="continuationFrames"/>).
		/// </summary>
		public List<(TraceFrame frame, Stop stop)> StepOverFrame(TraceFrame frame, HashSet<TraceFrame> continuationFrames) {
			var stops = new List<(TraceFrame, Stop)>();
			if (!models.TryGetValue(frame.Token, out var model))
				return stops;
			int pos = FirstStop(model, frame, 0, currentStatement: null);
			while (pos >= 0) {
				int e = frame.Events[pos];
				int statement = model.StatementAt(events[e].Offset)!.Value;
				stops.Add((frame, new Stop(e, statement)));
				int next = -1;
				for (int i = pos + 1; i < frame.Events.Count; i++) {
					var ev = events[frame.Events[i]];
					// Suspended at an await of this statement: continue where it resumes
					var await = model.Awaits.FirstOrDefault(a => a.yieldOffset == ev.Offset);
					if (model.Awaits.Any(a => a.yieldOffset == ev.Offset) && model.IsAwaitOfStatement(ev.Offset, statement)) {
						var resumed = FindResume(frame, frame.Events[i], await.resumeOffset);
						if (resumed is null)
							return stops;
						continuationFrames.Add(resumed.Value.frame);
						AwaitsFollowed++;
						frame = resumed.Value.frame;
						// Stopped at the resume breakpoint (hidden code): step over hidden code to the next statement
						next = FirstStop(model, frame, resumed.Value.position, currentStatement: null);
						break;
					}
					if (!model.Boundaries.Contains(ev.Offset))
						continue;
					var s = model.StatementAt(ev.Offset);
					if (s is not null && s != statement) {
						next = i;
						break;
					}
				}
				pos = next;
			}
			return stops;
		}

		int FirstStop(StepModel model, TraceFrame frame, int from, int? currentStatement) {
			for (int i = from; i < frame.Events.Count; i++) {
				var ev = events[frame.Events[i]];
				if (model.Boundaries.Contains(ev.Offset) && model.StatementAt(ev.Offset) is int s && s != currentStatement)
					return i;
			}
			return -1;
		}

		(TraceFrame frame, int position)? FindResume(TraceFrame suspended, int afterEvent, uint resumeOffset) {
			foreach (var f in frames) {
				if (f == suspended || f.Token != suspended.Token || f.Events[f.Events.Count - 1] < afterEvent)
					continue;
				for (int i = 0; i < f.Events.Count; i++) {
					if (f.Events[i] > afterEvent && events[f.Events[i]].Offset == resumeOffset)
						return (f, i);
				}
			}
			return null;
		}
	}
}
