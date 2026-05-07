# Changelog

This changelog is derived from the project's git commit messages and is listed newest first.

## 2026-05-08

### Fixed
- `4b9f6bc` Ephemeris sample importer no longer fails on startup when `sol_user` lacks `VIEW DEFINITION`: removed `EnsureEphemerisMergeIndexAsync` which used `OBJECT_ID()` (returns `NULL` without that permission), causing `CREATE INDEX` to run unconditionally and error.
- `4b9f6bc` Added missing `IX_EphemerisSamples_BodyId_SampleJd` index to `001_initial_schema.sql` where it belongs.
- `4b9f6bc` Reduced `import-samples` JPL Horizons concurrency from 5 to 2 and added a 1 s delay between chunk fetches to avoid rate limiting.
- `4b9f6bc` Body catalog sync phase now logs per-body progress so `import-samples` no longer appears stalled during startup.

### Added
- `60d4552` Incremental body-batch API: `GET /api/bodies/batch` now serves keyset-paginated H-band slices so the frontend can load minor-planet metadata in smaller background batches instead of re-requesting the full `h_max` range each time.
- `60d4552` Body catalog is now merged incrementally into the existing frontend catalog during background warm-up instead of replacing it.

### Changed
- `60d4552` `import-samples` now syncs the authoritative body catalog before fetching Horizons chunks, so curated bodies are refreshed automatically at import start.
- `65b40ea` Timeline hardpoints were streamlined for the usable simulation range: removed deep-time/far-future entries and added an `SL9 IMPACTS` hardpoint; timeline slider range expanded from ±500k years to ±4 billion years.
- `221d29d` Ephemeris window requests now adapt payload size by fetch stage and current H-filtered body count, with coarser decade-stage sampling to reduce wide-window memory pressure.
- `221d29d` Kepler mode now refreshes anchor offsets on timeline interactions while keeping ephemeris bulk-window refetch disabled.

### Fixed
- `60d4552` Ephemeris sample imports now stream rows directly into `SqlBulkCopy` instead of staging them fully in memory first.
- `1773910` Fixed Shoemaker-Levy 9 fragment impacts (staggered impact sequence, nucleus hidden during fragment phase, fragments are individually focusable).
- `65b40ea` Ephemeris sample cache now prunes stale data outside the active window margin before each progressive fetch, reducing long-session browser memory growth.
- `221d29d` Stale in-flight ephemeris fetches are now aborted on superseding requests/clears, preventing late high-volume responses from re-inflating memory after filter or mode changes.
- `221d29d` Timeline display slider clamping now follows the configured slider `min`/`max` range instead of a hardcoded ±500k year clamp.

## 2026-05-07

### Fixed
- `2592401` Realtime mode now stays aligned to wall-clock elapsed time even when Chromium throttles `requestAnimationFrame` for unfocused, occluded, or minimized windows.
- `44872f0` Fixed orbit instability caused by insufficient ephemeris cadence by falling back to Kepler when sampled data is too sparse for fast-moving objects.

### Changed
- `44872f0` Updated ephemeris runtime behavior in `js/ephemeris.js` and `js/solar_system.js` to improve fallback handling for undersampled trajectories.

## 2026-05-06

### Added
- `83b77ec` Ephemeris mode toggle (**KEPLER MODE** / **EPHEMERIS ON**) switches between fast analytical orbits and high-precision pre-computed state vectors from the database.
- `83b77ec` Progressive 4-stage ephemeris fetch (±1 day → ±1 month → ±1 year → ±10 years): present-day positions load almost immediately and the cache broadens in the background.
- `83b77ec` **EPH OBJECTS** slider (100–8,000): controls how many minor planets are fetched from the database and rendered as a real-position point-particle cloud in the scene. Bodies are sorted by absolute magnitude so the brightest/largest objects appear first.
- `83b77ec` `getCacheVersion()` and `getCachedBodyIds()` added to the ephemeris module so the animation loop can react to new cache data and drive the particle system.
- `83b77ec` Moon glow dots: each moon now has a `THREE.Points` child rendered at a fixed 3 px regardless of zoom, keeping sub-pixel moons visible in real-size mode.
- `83b77ec` Per-frame orbit line pin: `pinOrbitLineGeometry()` writes the planet's exact current scene position directly into the two shared midpoint vertices of the orbit line's `BufferGeometry` every frame, eliminating the visible drift-and-jump cycle between full refreshes.
- `83b77ec` Cache version tracking in the animation loop: any completed ephemeris fetch stage immediately triggers an orbit line refresh and a particle cloud rebuild.
- `dfed319` Added ephemeris prefetch during the intro sequence.
- `dfed319` Added proactive ephemeris prefetch when simulation time approaches the edge of the cached window.
- `dfed319` Added updated texture set for Sun, planets, Moon, and dwarf planets, including separate Earth day/night maps.
- `dfed319` Added Shoemaker-Levy 9 fragment rendering updates.

### Changed
- `83b77ec` All `fetchWindow` calls now pass `h_max = 25` so MPCORB minor planets are included in the ephemeris fetch alongside authoritative bodies.
- `83b77ec` EPH OBJECTS slider change now triggers a cache clear and refetch unconditionally (previously required already being in ephemeris mode).
- `83b77ec` Free-mode left-drag rotation direction corrected: drag axes are no longer inverted when no object is focused. Focused-orbit drag direction is unchanged.
- `83b77ec` Right-click roll direction corrected when an object is focused.
- `83b77ec` EPH OBJECTS slider value label moved to the left of the slider track; slider CSS fixed to prevent overflow outside the panel.
- `dfed319` Bodies slider behavior updated around `H_AbsMag`, including showing simulated-body count for the selected threshold.
- `dfed319` Simulated asteroid belt made feature-flag controlled now that ephemeris object rendering can represent dense real populations.

### Fixed
- `83b77ec` Backend SQL filter (`GetBulkSamplesAsync`): when `h_max` is provided, non-MPCORB bodies (planets, moons, etc.) are now always included regardless of their `H_AbsMag` value; previously bodies with a NULL absolute magnitude were excluded.
- `83b77ec` Orbit lines in ephemeris mode no longer revert to Kepler geometry after the initial fetch stage completes.
- `83b77ec` Orbit lines in real-size mode no longer visibly miss planet positions due to polyline chord deviation.

### Documentation
- `98a1526` README updated: project layout, endpoints, ephemeris mode section, EPH OBJECTS slider, texture upgrade backlog, ephemeris epoch coverage (1600–2500 AD for most bodies), orbit line pinning accuracy note, and `js/ephemeris.js` added to main files.

### Maintenance
- `83b77ec` `_publish/` added to `.gitignore`.

## 2026-05-04

### Added
- `9fc8921` Expanded body catalog to ~1.5 million objects via MPCORB full import.
- `9fc8921` Ephemeris import now supports an `h_max` magnitude cutoff to limit which bodies receive pre-computed state vectors (e.g. `import-samples 15` fetches data for ~83k objects with H ≤ 15 or no magnitude).
- `9fc8921` Ephemeris import is now resumable: every fetched chunk is logged in `EphemerisImportLog`; interrupted runs skip already-completed chunks and bodies on restart.
- `9fc8921` `CompletedEphemeris` flag on `Bodies` marks bodies whose full Horizons date range is fully logged, allowing future runs to skip them instantly.
- `9fc8921` Epoch-range clipping: each body's Horizons request is clipped to its stored `EphemerisMinJD`/`EphemerisMaxJD` so Horizons never returns empty data for out-of-range windows.
- `94f9883` Initial ephemeris import pipeline and expansion to 41k+ objects.
- `1a8fee6` Initial Ephemeris API and SQL Server schema (`dbo.Bodies`, `dbo.EphemerisSamples`).

### Changed
- `9fc8921` All ephemeris dates migrated to Julian Day Numbers (FLOAT) to support BC dates (BC 9999 – AD 9999) without calendar-system constraints.
- `9fc8921` Schema consolidated into a single `001_initial_schema.sql` migration.
- `9fc8921` `import-samples` command signature updated: `import-samples [h_max] [startUtc] [endUtc] [step]`.

### Fixed
- `9fc8921` Fixed DB schema collation conflict between `tempdb` and `sol_ephemeris` on staging tables.
- `9fc8921` Fixed duplicate slug collision during MPCORB full import.
- `9fc8921` Fixed Horizons timestamp parser to handle Julian-calendar dates (e.g. Feb 29 in years that are not Gregorian leap years) by parsing components manually instead of using `DateTime.ParseExact`.
- `9fc8921` Fixed search losing focus on click.

### Documentation
- `2c11a24` Updated README to reflect current schema, import commands, and data sources.

## 2026-04-25

### Added
- `29eca24` Added missing comets in the list. Fixed so that SL9 does not exist after impact with Jupiter in July 1994.
- `3eb12f0` Added keyboard shortcuts to Realtime and Real Size buttons.
- `561648d` Added `REAL SIZE` mode.
- `9f9dcb0` Added the realtime button.

### Fixed
- `63631db` Fixed SL9 to impact Jupiter.
- `5c284f6` Fixed comet naming.
- `bb6c5c3` Fixed additional button layout issues in smaller resolutions.
- `049f12a` Fixed Earth's meridian alignment and the Moon phase.
- `b17bb7a` Fixed timestep and object button layout across different desktop heights.
- `4a8c171` Fixed the mobile search click issue that returned to solar mode.
- `deb34e9` Fixed the UI becoming unresponsive immediately after the intro ended, including text-selection issues.
- `a199682` Fixed planet orientation in Vortex mode.

### Documentation
- `f0bfd2a` Updated the README and added the changelog.

## 2026-04-24

### Added
- `683bca2` Added orbital velocity.
- `efbb408` Added the initial working system.
- `65106ee` Initial commit.

### Changed
- `d0a78ee` Removed the fullscreen button from desktop view.

### Fixed
- `2e1e148` Fixed retrograde spin direction caused by a sign error.
- `1e799c3` Fixed Hunter/Orion button snap drift on repeated clicks.

### Documentation
- `36b2650` Added mention of Earth's cloud system to the README.