# JSON editor reliability and bounded search work

> Historical first-stage measurements: the later incremental document work supersedes the full-string-per-key implementation described below. See [JSON_EDITOR_INCREMENTAL_DOCUMENT.md](JSON_EDITOR_INCREMENTAL_DOCUMENT.md) for current architecture, final measurements and limits.

## Changes

- Cut used to delete selected text even when no clipboard was available. Reproduced on the real editor: `abcdef`, selection `bcd`, cut without clipboard produced `aef`. Cut now requires successful clipboard write.
- Copy/cut/paste use the editor's owning top-level clipboard. Delayed cut/paste only change text if the same text instance, caret, selection, editable state and attached clipboard still apply. Changed context cancels the edit; a completed copy may still have placed the original selection on the clipboard.
- Clipboard shortcuts are handled before awaiting; system clipboard failures are caught at the UI event boundary and debug-logged, preserving content. There is currently no user-facing clipboard failure notification.
- Ctrl+A is synchronous instead of posting via a thread-pool task, preserving ordering with subsequent typing.
- Public Undo/Redo respect read-only, in addition to keyboard guards.
- Regex matching has a 100ms timeout and clears partial results on timeout. Existing ERR search indicator is reused. This is a regex matching timeout, not a guaranteed end-to-end budget for all searches or all match enumeration.
- Search highlight drawing binary-searches ordered non-overlapping matches to skip the offscreen prefix, then renders visible matches. Full match storage and search semantics are unchanged.

## Verification

Real assembly probe: `dotnet run --project scripts/memory/PageLifetimeProbe -c Release -- --editor`.

The first test failed on the original implementation with `Cut deleted text despite unavailable clipboard: aef`. Subsequent real-control tests passed for unavailable clipboard, replace/undo/redo, readonly undo/redo, select-all followed immediately by typing, pathological regex (~102–103ms), and valid search recovery.

Delayed clipboard test doubles replace only the headless window's clipboard feature. They invoke real cut/paste code and cover 12 combinations: cut and paste each with successful completion, selection change, document replacement, becoming readonly, detachment and clipboard failure. Every case passed. Reflection/platform substitution is confined to the diagnostic executable, not the production GUI.

Final supplemental checks passed for literal search count/previous/next wrapping/close, emoji backspace and undo/redo. Desktop light/dark rendering at 700/900 widths passed; all **377 existing GUI tests passed**. Pathological regex returned ERR in 107ms in the final run. Raw final output: `artifacts/json-editor-refresh/editor-final-verification.log`. The screenshot harness reports one obsolete `Bitmap.Save` API warning; product compilation and tests succeeded.

These checks do not amount to exhaustive OS clipboard or input-method certification. No native clipboard contention soak, Windows IME composition test, screen-reader test, or real GPU frame-time measurement was performed.

## Context-aware newline indentation

The initial newline implementation scanned the whole document to infer indentation. That implementation has been replaced. Reference reviewed: [AvaloniaEdit DefaultIndentationStrategy](https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/src/AvaloniaEdit/Indentation/DefaultIndentationStrategy.cs), which uses document line offsets and copies local leading whitespace. Our implementation is independent and additionally expands JSON bracket pairs.

Production Enter passes its existing line index and binary-searched current line to `JsonIndentation.Create`. Inference checks at most 32 lines on each side (65 total), compares whitespace with spans, and allocates only result strings. String/bracket inspection is restricted to the current physical line (JSON strings cannot legally contain raw newlines), with a 4096-character scan limit. Leading whitespace and whitespace before a paired closer are bounded at 256 characters. Beyond these limits the editor conservatively inserts a newline with the bounded leading indentation and skips bracket expansion. It does not promise perfect indentation inference for malformed JSON, extreme indentation or inconsistent styles.

Verified two/four-space and tab contexts, LF/CRLF, string/escaped quote brackets, selected-text replacement, inner caret placement, one-step undo/redo and readonly Enter. An instrumented million-entry line collection rejects enumeration and asserts no more than 70 indexed reads. A two-million-character single line verifies the fallback. **391 tests and the actual-editor interaction probe passed.**

Focused timing (existing line index built outside measurement, same code path, 5 batches × 10,000 calls after warmup; includes reflection overhead):

| Document lines | Characters | Median indentation time | Allocation/call |
|---|---:|---:|---:|
| 1,003 | 15,020 | 0.001547 ms | 224 bytes |
| 100,003 | 1,500,020 | 0.001447 ms | 224 bytes |
| 1,000,003 | 15,000,020 | 0.001483 ms | 224 bytes |

This demonstrates bounded **indentation computation**, not constant-time whole-document editing. The existing string replacement and full line-index reconstruction remain as described above. Reproduce with `--indent-performance`; raw evidence: `artifacts/json-editor-refresh/bounded-indentation-verification.log`.

## Measured performance and limits

Same Release/headless harness against archived HEAD before these editor changes and current source. Actual `DrawSearchMatches` method, viewport at document end, 100 calls per batch, one warmup batch plus five measured batches. Median batch time per draw below. One process per version: this is a focused microbenchmark, not a statistically robust end-user speed claim.

| JSON workload | Before highlight ms/pass | After highlight ms/pass |
|---|---:|---:|
| 1,000 repeated object lines, ~36k chars | 0.093 | 0.143 |
| 100,000 lines, ~3.6m chars, ~200k matches | 0.230 | 0.136 |

The large-match-list path eliminates a linear prefix walk and saved ~0.094ms in this run. Small-document timing varied unfavorably; there is no claim of universal or perceptible speedup. No changes to rendering fidelity, text content or match counts.

Typing without active search, separately measured for 20 characters:

- ~36k chars: before 0.122ms/key, after 0.117ms/key; both ~0.069 MiB allocated/key.
- ~3.6m chars: before 5.116ms/key, after 4.474ms/key; both ~6.867 MiB allocated/key.

Typing allocation is unchanged. These timing differences are not attributed to this patch. Each edit still reconstructs a full string and line index; active searches still recompute synchronously after edits. Very large documents and match-heavy searches may stall. Replacing document storage or adding asynchronous search requires a separate design and verification effort; neither was smuggled into this bounded fix.

Reproduce performance: `dotnet run --project scripts/memory/PageLifetimeProbe -c Release -- --editor-performance`. Raw benchmark output is retained locally under `artifacts/json-editor-refresh/editor-performance.log`.

UI styling changes (rounded themed editor card and toolbar) are separate from the reliability fixes; no new GC, working-set trimming, rendering downgrade, document truncation or reduced data limits were introduced.
