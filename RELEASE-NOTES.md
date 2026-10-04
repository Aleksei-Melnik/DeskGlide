ScreenCapture 0.7.4 fixes delayed or unresponsive Settings opening from the tray.

- Settings appears before audio-device discovery. A single background enumeration populates all audio selectors, including Discord, instead of repeatedly calling audio drivers on the UI thread.
- Slow or unavailable audio devices no longer block the window. Saved selections remain available while loading or after a timeout, and an explicit refresh retries discovery.
- Background results preserve edits made while devices are loading. Closing Settings safely cancels its wait; reopening reuses an in-progress driver query.
- Tray activation is queued until the popup closes. Repeated clicks restore the same window.
- Display selection uses the existing Windows monitor list; diagnostics loads only when requested, outside the UI thread.
- Sleep protection from 0.7.3 remains enabled by default. Capture, NDI, recording, KVM transport and camera formats are unchanged.

Validation: UI tests exercised pending/failed device discovery, saving during loading, preserving edits after completion, closing before completion, and repeated activation. Local Settings opening took 565 ms with discovery deliberately unfinished. UI, feature and update tests passed. The reported intermittent failure still needs confirmation on the affected PC after updating.
