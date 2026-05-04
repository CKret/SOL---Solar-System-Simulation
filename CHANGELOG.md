# Changelog

This changelog is derived from the project's git commit messages and is listed newest first.

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