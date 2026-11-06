# Page lifetime and list-allocation regression probe

This small executable references the **real GUI assembly**, not copies or stubs of its page view models. It uses Avalonia's headless backend and never starts the proxy/kernel.

```powershell
# Verify that all four disposable page types are collectible after 20 lifecycles.
# Also checks active-page language switching and repeated Dispose.
dotnet run --project scripts/memory/PageLifetimeProbe -c Release -- --verify

# Verify viewport-bounded cards, keyboard traversal, recycling, action bindings and reloads.
dotnet run --project scripts/memory/PageLifetimeProbe -c Release -- --profiles --verify
```

Omit `--verify` to record a faulty baseline without a failing exit code. A failed assertion/exception still fails the process. `--profiles` creates isolated portable preferences **inside its own build output**, never in the user's application data directory.

## What the numbers mean

- The lifetime check deliberately forces collection **in the diagnostic process only**. Surviving weak references plus localization-event targets identify retained objects. This is not a proposed GC policy or a working-set benchmark.
- The list check constructs the real `ProfilesView`, loads application styles and bindings, warms the framework, then measures 0/8/100/500 metadata rows. `AllocatedBytes` is UI-thread managed allocation during construction/layout, not live heap, private bytes or native memory.
- After measuring, it traverses multiple viewports with Tab/Shift+Tab, scrolls top/bottom repeatedly, checks edit/update/delete command parameters and row-specific enabled/visibility states, resizes, switches language/theme, and removes/clears/repopulates the list. These checks exercise the UI bindings; they do not execute destructive commands on real user profiles.
- Keep the profile list's local keyboard boundary handler: default Tab enumeration cannot see unrealized rows. The handler realizes only the adjacent target row and preserves ordinary within-row navigation, disabled/hidden controls, and leaving the list at either end. Increasing a cache to hide this defect is not an accessibility fix.
- Headless rendering is for deterministic control counts and interaction checks. It does **not** measure production GPU rendering or NativeAOT performance.
- A closed view can remain alive until pending UI/animation callbacks finish. The probe records the immediate state, pumps the dispatcher for 600 ms, then asserts eventual collection. The observed before/after callback distinction is diagnostic-only; no application timers or GC policy were changed.

## NativeAOT process measurements

Publish two disposable builds under `artifacts/memory-lifecycle/before` and `after` using the same flags from `scripts/build/build-portable-win-x64.ps1`. Then, on Windows with no existing carton instance:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/memory/mem-page-lifecycle.ps1 -ProfileCount 100 -Runs 3 -Cycles 3
```

The script seeds synthetic local metadata/configs, uses real UI Automation navigation, alternates fresh-process run order, and records executable hashes, visible state, total/private working sets and private committed bytes. It never stops an already-running user instance and refuses publish roots outside this repository's `artifacts` directory.

`NavigationCpuMs` includes selection and a fixed 400 ms settling interval; it is CPU work, **not first-frame latency**. `CpuOneCorePercent` samples the subsequent idle second. `NavigationElapsedMs` includes that fixed delay and is not a page-speed benchmark.

Existing application startup/unload/minimize GC and working-set trimming remain unchanged. The harness introduces none. Restored/idle samples can still be influenced by that existing policy; do not use their low working-set values as evidence of reduced live memory. Compare active-page results together with realized-control/managed-allocation evidence instead.
