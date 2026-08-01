# Agent Guidelines

These instructions apply to Codex and other coding agents working in this repo.

## Scope
- This repo is the **Sentry Deck** WPF desktop app (XAML + C#).
- Media playback uses Flyleaf; logging uses `Serilog`.

## Architecture
- **`SentryDeck.Data`** — pure domain logic: TeslaCam storage models (`Cam*`) and playback types (`ClipPlaylist`, `ClipTimeline`, `ICameraPlayer`, …). It has no WPF, Flyleaf, or UI dependencies — keep it that way so this logic stays unit-testable.
- **`SentryDeck`** — the WPF app: views (`*.xaml` + thin `*.xaml.cs`), view-models (`*ViewModel.cs`), and services (`Services/`).
- Keep view-models free of WPF control references. A view-model exposes state and commands; the view binds to them. Anything that must touch a named control, the dispatcher, or the visual tree (focus, control re-parenting, window lifecycle) belongs in the view's code-behind, not the view-model.

## Where new code goes
- New feature logic, state, and commands go in a **view-model**, not in window/view code-behind. Code-behind is for view-only plumbing.
- Don't reach for the largest existing file by default. If a view-model is growing unwieldy, split it by feature rather than letting one file own everything.
- Keep code-behind small: if a `*.xaml.cs` is pushing past ~150 lines or holds non-view logic, that logic probably belongs in a view-model.

## XAML
- Keep XAML readable: group related properties and follow `Settings.XamlStyler`.
- Use existing resources/styles before adding new ones.
- Prefer bindings and converters over code-behind for UI state.
- Avoid introducing third-party UI frameworks unless explicitly requested.

## C#
- Follow `.editorconfig` naming and formatting rules.
- Use `ObservableProperty`/`RelayCommand` (CommunityToolkit.Mvvm) for view-model state and commands.

## Comments
- Break comment lines at sentence boundaries, one sentence per line, however long the line gets. Never wrap a sentence at a column width: editing one word would reflow every line after it and bury the real change in the diff.
- This applies to every file type, including YAML in `.github/`, not just C#.
- Don't use em dashes. A colon, semicolon, comma, or a second sentence reads the same and survives copy/paste.
- Explain why the behavior matters, not what the code does.

## WPF-specific considerations
- Be mindful of dispatcher usage when touching UI from background tasks.
- Prefer `INotifyPropertyChanged` patterns over manual UI updates.
- Use `IValueConverter`/`IMultiValueConverter` for presentation logic.

## Testing/Validation
- Always run `dotnet format`.
- Tests live in `SentryDeck.Tests` and use xUnit/Shouldly.
- Name tests `Subject_Scenario_Expectation` on a `public sealed class` with instance methods.
- Add tests for new view-model and domain logic — view-models are plain objects that can be constructed directly in tests (see `MainWindowViewModelTests`).
- If you change UI behavior, mention how to verify it (e.g., which view to open, what to click).
