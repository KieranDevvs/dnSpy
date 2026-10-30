# Windows checks

`Run-WindowsChecks.ps1` runs the checks of the upstream ILSpy decompiler work that need Windows, and puts the
results into one zip file. Results so far were from Linux, where the WPF UI, MSBuild for WPF/WinForms projects, the
GAC and .NET Framework don't exist.

## Running it

Use a PowerShell with the .NET 10 SDK, for example "Developer PowerShell for VS" (MSBuild from Visual Studio is used
when it's installed; dnSpy's COM references need it). In the repository:

```
powershell -ExecutionPolicy Bypass -File Tests\WindowsChecks\Run-WindowsChecks.ps1
```

It takes about an hour. Leave the desktop alone during the last step: it starts dnSpy a few times and takes
screenshots. When it's done, it prints the path of `Tests\WindowsChecks\out\WindowsChecks-<date-time>.zip`; send
that file. `summary.md` in it has the results.

Nothing outside the repository changes: dnSpy is started with its own settings file, and exported projects are built
in a temporary folder that's deleted afterwards.

Steps (`-Skip Build,Corpus` or `-Only Samples` to run some of them; `-Skip Build` needs a Debug and Release build):

| Step | What |
|---|---|
| Build | `dnSpy.sln` Debug and Release (.NET Framework 4.8 and .NET 10), the test projects, `DebuggerPlayground` |
| Tests | The headless test suite (`Tests\dnSpy.Decompiler.Upstream.Tests`) on Windows |
| Baml | BAML to XAML of dnSpy's own assemblies, the WPF framework (.NET 10) and .NET Framework WPF with its themes |
| Corpus | Decompiles 13 .NET Framework 4.8 assemblies (mscorlib, WinForms, WPF, ...) with both engines (`CorpusRunner`) |
| Samples | `Samples\*`: a console, a WPF, a WinForms and a .NET Framework app. Each is exported with `dnSpy.Console` (its .NET 10 and its .NET Framework build, both C# engines; SDK-style, and a classic project for .NET Framework), the exported project is built with MSBuild and run, and must print the same as the original |
| Screenshots | Starts dnSpy (Debug, .NET 10 and .NET Framework) on "C# (ILSpy 11.2)", the legacy C#, "IL with C#", ILAst with the stepper and typed IL, and takes screenshots |

## Still manual

- The debugger: `Tests\DebuggerPlayground\README.md`.
- The edit dialogs and the rest of the UI: the checklists in the project (`claude/phase1-ui-test-checklist.md`,
  `claude/phase3-edit-test-checklist.md`, `claude/phase4-views-test-checklist.md`).
