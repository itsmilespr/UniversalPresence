# Universal Presence — agent instructions

## Commits and attribution

- Do **not** add `Co-Authored-By: Codebuff`, `Generated with Codebuff`, or any other
  AI attribution trailer to commit messages. The project's author is the only contributor
  of record.
- Keep commit messages short and in the repository's existing style (imperative mood,
  English or Spanish, no marketing tone).
- Do not rewrite published history or force-push without explicit authorization.

## Build and tests

- Build: `dotnet build UniversalPresence.csproj -c Release`
- Tests: `dotnet test UniversalPresence.Tests/UniversalPresence.Tests.csproj -c Release`
- The app has no NuGet dependencies; do not add packages to the main project.
- The test project may use NuGet (xunit, etc.).

## Conventions

- Identifiers and code comments in English; user-facing strings in Spanish (UI) or the
  configured language (presence text).
- No unrequested features, refactors, or new files beyond what the task requires.
