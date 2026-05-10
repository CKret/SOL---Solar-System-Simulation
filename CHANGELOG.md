# Changelog

This changelog is derived from the project's git commit messages and is listed newest first.

## 2026-05-10 (later)

### Fixed
- `80201a7` Fixed mobile tap-to-focus: `e.preventDefault()` on touchstart was blocking the browser's synthetic click event, so tapping objects never triggered selection. Selection logic extracted into `handleSceneClick(cx, cy)` and called from both the desktop click handler and a new tap detector in touchend (single finger, movement < 8 px).
- `80201a7` Fixed two-finger rotation direction in geo-lock mode.

### Added
- `80201a7` Two-finger twist gesture now rotates the camera around its Z-axis (roll): in geo-lock mode it rotates `geoLockLocalUp` around the camera direction; in all other modes it drives `cameraRoll`. Pinch-zoom continues to work simultaneously.

## 2026-05-10

### Fixed
- `6e8ce46` Fixed planet axial tilt and ring plane orientation for all planets: replaced the incorrect obliquity/longitude lookup tables with IAU WGCCRE J2000 pole RA/Dec values and a proper equatorial-to-ecliptic coordinate conversion (ε = 23.4393°). Saturn's ring plane was previously off by ~17.7°; it now matches JPL Horizons geometry, allowing accurate reproduction of probe viewpoints such as the Cassini March 2006 nearly edge-on ring photo.
- `6e8ce46` Removed probe-proximity planet fade: planets no longer become semi-transparent when a space probe passes nearby.

### Added
- `6e8ce46` Look-at dropdown replaces the single "Look at Sun" button: camera can now be locked to point toward the Sun or any of the eight planets while keeping focus on the current body.
- `6e8ce46` Import command `import-samples` now accepts `--bodies=slug1,slug2,...`, `--bodyIds=id1,id2,...`, and `--skip-sync` flags for targeted dense re-imports without re-running the full authoritative catalog sync.
- `6e8ce46` Hourly encounter windows registered for all tracked probes (Cassini, Pioneer 10 & 11, New Horizons, Juno, Parker Solar Probe, BepiColombo, Galileo, MESSENGER, Dawn, Rosetta, OSIRIS-REx) in addition to the existing Voyager windows, so planetary flybys and orbit insertions are imported at 1-hour resolution automatically.

### Changed
- `6e8ce46` Camera zoom and pan offsets reset when switching focus to a new object so the view starts clean rather than inheriting the previous body's zoom level.
- `6e8ce46` Zoom range extended to ±2,000,000 scene units; wheel handler now updates both `camR` and `targetR` immediately so the change takes effect without a lerp delay.

## 2026-05-09

### Fixed
- `86cba79` Fixed ephemeris importer re-downloading already-imported chunks: replaced exact floating-point chunk-key matching with a tolerance-based comparison (±2 JD) so minor boundary differences no longer cause redundant Horizons fetches and no-op inserts.
- `a207c1b` Fixed rendering blackout at extreme simulation times (e.g. 183,469 BC / 208,676 AD): malformed ISO UTC strings from JavaScript `Date` overflow were being sent as `centerUtc` to the API, causing HTTP 400 errors that prevented Kepler fallback from activating.
- `e93de14` Replaced `Date` overflow detection with an explicit ephemeris range guard (BC 9999–AD 9999): fetch functions now skip API calls cleanly for out-of-range times and fall back to pure Kepler propagation, with no wasted network attempts.

## 2026-05-09 (earlier)

### Fixed
- `6f3f1e3` Fixed Kepler orbit line behavior so the line geometry stays aligned with the current moon period source instead of jumping in Kepler mode.

### Changed
- `6f3f1e3` Renamed the small-body magnitude slider label for the H-absolute-magnitude threshold control.

### Documentation
- `6f3f1e3` README wording was updated to match the current small-bodies label and behavior.

### Added
- `22329a5` Added orbit-extrema details to the object info panel: perihelion/aphelion plus ETA for heliocentric bodies, and periapsis/apoapsis plus ETA for moons.
- `22329a5` Added targeted per-body ephemeris range fetching so long-period authoritative bodies can extend beyond the baseline cached window without expanding the global fetch for the full catalog.

### Changed
- `22329a5` Ephemeris mode now switches on immediately while the progressive cache warm-up and any long-period targeted extensions continue in the background.
- `22329a5` Free-view left-drag is now inverted on both axes when no object is focused.
- `22329a5` The info panel now scrolls to accommodate the added orbit-extrema rows, and the redundant in-panel `ESC clear focus` hint was removed.

### Fixed
- `22329a5` Fixed long-period moon orbit rendering by deriving moon periods from ephemeris-aware metadata, sampling against the full valid moon/parent overlap window, and avoiding the malformed hybrid orbit artifacts that affected bodies such as Neso.
- `22329a5` Fixed moon ephemeris local-position scaling so visual-size mode preserves the sampled parent-relative direction and distance correctly instead of distorting some irregular moon tracks.
- `22329a5` Fixed authoritative-body SQL filtering and ordering in the ephemeris backend so NULL-`H_AbsMag` curated bodies are included consistently and prioritized correctly ahead of H-filtered MPCORB results.
- `22329a5` Fixed ephemeris import completion detection so only successful logged chunks count toward completion, using chunk-range matching instead of a raw row count.

## 2026-05-08

### Fixed
- `c681108` Ephemeris importer now handles overlapping imports gracefully: PK violations (SQL error 2627) are caught and the chunk is marked as complete, preventing infinite retry loops when resuming partial runs or re-running the same H-range.
- `4b9f6bc` Ephemeris sample importer no longer fails on startup when `sol_user` lacks `VIEW DEFINITION`: removed `EnsureEphemerisMergeIndexAsync` which used `OBJECT_ID()` (returns `NULL` without that permission), causing `CREATE INDEX` to run unconditionally and error.
- `4b9f6bc` Added missing `IX_EphemerisSamples_BodyId_SampleJd` index to `001_initial_schema.sql` where it belongs.
- `4b9f6bc` Reduced `import-samples` JPL Horizons concurrency from 5 to 2 and added a 1 s delay between chunk fetches to avoid rate limiting.
- `4b9f6bc` Body catalog sync phase now logs per-body progress so `import-samples` no longer appears stalled during startup.

### Added
- `60d4552` Incremental body-batch API: `GET /api/bodies/batch` now serves keyset-paginated H-band slices for targeted range fetches; the backend endpoint is in place for future incremental loading scenarios.
- `60d4552` Body catalog merge now adds new entries without replacing already-cached bodies, so future incremental fetches can be appended safely.

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