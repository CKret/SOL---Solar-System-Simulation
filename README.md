# SOL - Solar System Simulator

A browser-based 3D solar system and deep-time space visualizer built with Three.js. It combines planetary orbits, present-day Earth and Moon time calibration, dwarf planets, comets, a full fleet of space probes, responsive desktop/mobile controls, and a cinematic intro overlay into a single self-contained project.

## Run It

The full experience requires the ephemeris backend: an ASP.NET Core API backed by a SQL Server `sol_ephemeris` database populated via the import commands described in [Ephemeris API](#ephemeris-api). With the backend running, **EPHEMERIS ON** uses JPL-sourced trajectory samples when cached data exists and cadence is sufficient for that body's orbital period, and falls back to anchored Kepler propagation otherwise. The `SMALL BODIES H <= XX` slider controls the absolute-magnitude threshold used to load real minor-planet bodies from the database for the scene. The API base path is hardcoded to `/sol-api`, matching the production IIS layout.

The frontend also runs without the backend. Serve the project root with any static file server:

```bash
python -m http.server 8000
```

Open `http://localhost:8000/index.html`. Without the API the ephemeris system silently becomes a no-op: all bodies continue to move correctly using analytical Kepler orbits, but **EPHEMERIS ON** and the `SMALL BODIES H <= XX` catalog-threshold control are inactive.

Project layout (representative):

```text
.
├── backend/
│   └── Sol.Api/                  # ASP.NET Core ephemeris API
│       ├── Models/
│       ├── Services/             # Repository, importers, catalog reader
│       ├── sql/
│       │   └── 001_initial_schema.sql
│       ├── Program.cs
│       └── Sol.Api.csproj
├── favicon/
│   ├── android-chrome-192x192.png
│   ├── android-chrome-512x512.png
│   ├── apple-touch-icon.png
│   ├── favicon-16x16.png
│   ├── favicon-32x32.png
│   ├── favicon.ico
│   └── site.webmanifest
├── favicon.ico
├── index.html
├── js/
│   ├── solar_system.js
│   ├── ephemeris.js
│   ├── voyager_trajectories.js
│   └── three.min.js
├── textures/
│   └── ...
├── CHANGELOG.md
└── README.md
```

The frontend requires no build step. The backend is a standard .NET project — `dotnet run` or publish via `dotnet publish`.

## Ephemeris API

This repo includes an ASP.NET Core backend in [backend/Sol.Api](backend/Sol.Api) that serves pre-computed ephemeris data from SQL Server. The frontend can keep running as a static site; the API is an optional layer that provides high-precision positional data for supported bodies.

### Schema

The full schema is in [backend/Sol.Api/sql/001_initial_schema.sql](backend/Sol.Api/sql/001_initial_schema.sql). Key tables:

- `dbo.Bodies` — all known bodies: a curated authoritative set (planets, moons, comets, probes, etc.) plus ~1.5 million minor planets imported from the MPC Orbit Database. Stores orbital elements, absolute magnitude, JPL Horizons ID, and the valid ephemeris date range per body as Julian Day Numbers.
- `dbo.EphemerisSamples` — pre-fetched state vectors (position + velocity in AU and AU/day, Solar System Barycenter / Ecliptic J2000 frame) keyed by `(BodyId, SampleJd)`.
- `dbo.EphemerisImportLog` — chunk-level import log used to resume interrupted runs and skip already-fetched date windows.

All dates are stored as Julian Day Numbers (`FLOAT`) so the schema can represent dates from BC 9999 to AD 9999 without calendar-system constraints.

### Running the API

```bash
dotnet run --project backend/Sol.Api
```

By default the API listens on the standard ASP.NET Core development URLs.

### Configuration

Connection strings live in .NET user-secrets so credentials never enter tracked files:

```bash
dotnet user-secrets --project backend/Sol.Api set "ConnectionStrings:EphemerisDb" "Server=<host>;Database=sol_ephemeris;User ID=sol_reader;Password=<pw>;Encrypt=False;TrustServerCertificate=True;"
dotnet user-secrets --project backend/Sol.Api set "ConnectionStrings:EphemerisDbWrite" "Server=<host>;Database=sol_ephemeris;User ID=sol_user;Password=<pw>;Encrypt=False;TrustServerCertificate=True;"
```

The API uses `sol_reader` (read-only) at runtime. Import commands use `sol_user` (read-write). For IIS, set `ConnectionStrings__EphemerisDb` and `ConnectionStrings__EphemerisDbWrite` in the application environment instead of user-secrets.

### Endpoints

- `GET /api/health`
- `GET /api/bodies?h_max=<magnitude>&maxBodies=<count>` — returns active bodies with ephemeris data; `h_max` filters by absolute magnitude (omit for authoritative bodies only); `maxBodies` caps the result set
- `GET /api/bodies/batch?h_max=<magnitude>&h_min_exclusive=<magnitude>&take=<count>&afterBodyId=<id>` — keyset-paginated body batches for incremental H-band loading; used by the intro/background warm-up path so the frontend can add newly unlocked minor planets without re-downloading earlier bands
- `GET /api/bodies/{slug}` — single body by slug
- `GET /api/bodies/search?q=<text>&limit=<n>&namedOnly=<bool>` — full-text search used by the in-app search panel
- `GET /api/ephemeris/{bodyId}?startUtc=...&endUtc=...&limit=...` — samples for one body by numeric `BodyId`
- `GET /api/ephemeris/by-slug/{slug}?startUtc=...&endUtc=...&limit=...` — samples for one body by slug
- `GET /api/ephemeris/window?centerUtc=...&radiusDays=...&step=<days>&h_max=<magnitude>&maxBodies=<count>` — state vectors centered on a UTC date; used by the progressive 4-stage ephemeris fetch
- `GET /api/ephemeris/bulk?startUtc=...&endUtc=...&step=<days>&h_max=<magnitude>&maxBodies=<count>` — state vectors over an arbitrary date range

### Import Commands

**1. Authoritative bodies** — imports the curated body set (planets, moons, dwarf planets, comets, space probes) from JPL Horizons and the JPL Small-Body Database:

```bash
dotnet run --project backend/Sol.Api -- import-bodies
```

**2. Minor planets** — imports orbital elements from the MPC Orbit Database (MPCORB):

```bash
dotnet run --project backend/Sol.Api -- import-mpcorb          # ~1500 numbered objects (sample)
dotnet run --project backend/Sol.Api -- import-mpcorb full     # full ~1.5 million object catalog
```

**3. Ephemeris samples** — fetches state vectors from the JPL Horizons API for bodies that have a stored Horizons date range:

```bash
dotnet run --project backend/Sol.Api -- import-samples [--bodies=slug1,slug2,...] [--bodyIds=1,2,...] [--skip-sync] [h_max] [startUtc] [endUtc] [step]
```

- `--bodies`: comma-separated slug list for targeted imports (bypasses `CompletedEphemeris` filter).
- `--bodyIds`: comma-separated body ID list; same bypass behaviour as `--bodies`.
- `--skip-sync`: skip the authoritative catalog sync step (saves time on targeted re-imports).
- `h_max`: absolute magnitude cutoff — imports bodies where `H <= h_max` or `H IS NULL` (authoritative bodies). Omit to import all eligible bodies.
- `startUtc` / `endUtc`: optional batch window clipped to each body's stored Horizons range.
- `step`: sample rate — `daily`, `hourly`, `<n>h`, `<n>d`. Defaults to 1 day.

Before the sample import starts, the command re-syncs the authoritative body catalog (unless `--skip-sync` is given) so newly changed Horizons IDs, date ranges, and curated-body metadata are present before chunk fetches begin. For probe encounter windows (e.g. Cassini at Saturn, Voyager at Jupiter), the importer automatically switches to 1-hour sampling within the registered encounter interval and falls back to daily sampling outside it.

Example — import daily samples for all bodies brighter than H=15 (≈ 83,000 objects) over their full available date range:

```bash
dotnet run --project backend/Sol.Api -- import-samples 15
```

Example — re-import Cassini's trajectory at full daily resolution without re-syncing the catalog:

```bash
dotnet run --project backend/Sol.Api -- import-samples --bodies=cassini --skip-sync
```

Imports are resumable: each fetched chunk is logged in `dbo.EphemerisImportLog` and skipped on re-runs. A body is marked `CompletedEphemeris=1` once its entire stored date range is fully logged. The importer runs bodies in parallel, bulk-inserts samples through a streaming reader instead of staging a full in-memory `DataTable`, and creates the merge lookup index used by duplicate checks before the run so long imports do not degrade as sharply as the target table grows.

**4. Retry zero-sample chunks** — retries import log chunks where Horizons previously returned zero samples, with optional boundary shrinking on edge chunks:

```bash
dotnet run --project backend/Sol.Api -- import-retry-zeros [max_shrink_days]
```

For IIS hosting, publish the app and point the IIS site at the published output. SQL Server can remain on the same host.

## Current Feature Set

### Core simulation
- 8 planets with axial tilt, axial rotation, and elliptical orbits. Axial tilt orientations are derived from IAU WGCCRE J2000 pole RA/Dec values with a proper equatorial-to-ecliptic coordinate conversion, so ring planes and polar axes reflect the authoritative orientation rather than approximate lookup-table values.
- Earth includes a separate animated cloud layer with a dynamic procedural storm system.
- Curated moon tracking across Earth, Mars, Jupiter, Saturn, Uranus, and Neptune, with explicit moon spin handling: synchronous rotation for regular/tidally evolved moons, published spin periods for several irregular moons, and special handling for cases such as Hyperion's chaotic rotation. In real-size mode, sub-pixel moons remain visible as fixed-size glow dots.
- 9 dwarf planets: Ceres, Pluto, Eris, Makemake, Haumea, Sedna, Gonggong, Quaoar, and Orcus.
- 10 named comets: Halley's, Hale-Bopp, Hyakutake, Encke, 67P/Churyumov-Gerasimenko, Tempel 1, Wild 2, Shoemaker-Levy 9, NEOWISE, and Ikeya-Seki. Shoemaker-Levy 9 is additionally tracked as 21 individually focusable fragments (A through W) with a staggered impact sequence and visual nucleus-hiding during the fragment phase.
- 14 space probes with full ephemeris trajectories: Voyager 1, Voyager 2, Cassini, Pioneer 10, Pioneer 11, New Horizons, Juno, Parker Solar Probe, BepiColombo, Galileo, MESSENGER, Dawn, Rosetta, and OSIRIS-REx. Probe encounter windows (planetary flybys, orbit insertions, landing events) are registered with 1-hour sample resolution for precise trajectory rendering during close approaches.
- Database-backed minor-planet coverage from the 1.5M+ object MPCORB catalog, with the `SMALL BODIES H <= XX` slider controlling the active absolute-magnitude threshold for loaded asteroid/TNO bodies across the scene. A separate procedural Oort cloud remains as a distant background population.

### Ephemeris mode
- Toggle between **Kepler mode** (fast analytical orbits) and **Ephemeris mode** (high-precision positions from the pre-computed SQL Server database).
- In Ephemeris mode the frontend switches immediately and uses a progressive 4-stage fetch: ±1 day → ±1 month → ±1 year → ±10 years, so present-day positions are available almost immediately and longer-range cache builds in the background without blocking the toggle.
- After the baseline window is cached, the frontend can request targeted forward-only extensions for long-period authoritative bodies whose orbital periods exceed the current cached window. This keeps bodies such as distant irregular moons and long-period comets from dropping back to coarse analytical orbit lines unnecessarily.
- On startup, body metadata is loaded at the user's current `SMALL BODIES H <= XX` threshold via `/api/bodies`. The `/api/bodies/batch` endpoint supports keyset-paginated incremental loading for future use, but the active catalog is determined solely by the user's H slider setting.
- Cached ephemeris positions are evaluated with Hermite interpolation (position + velocity), then mapped into scene coordinates.
- Runtime selection is uniform across periodic bodies: use ephemeris where cached cadence is sufficient for the body's orbital period; otherwise fall back to Kepler (with anchors where available).
- Ephemeris coverage varies by body. Most objects have pre-computed samples spanning roughly **1600–2500 AD**; a few bodies have shorter or longer ranges depending on what JPL Horizons provides for that object.
- Orbit lines update automatically whenever new cache data arrives, and are continuously pinned to the body's exact current position each frame so they never visibly drift.
- Orbit-line color indicates what is currently in use: blue when a body is using ephemeris at the current time, grey when it is falling back to Kepler.
- The `SMALL BODIES H <= XX` slider controls the absolute-magnitude threshold used to load minor-planet bodies from the database. Raising the threshold includes progressively dimmer and more numerous catalog objects; lowering it restricts the scene to brighter bodies. Those loaded bodies remain part of the scene in both Kepler and Ephemeris mode, while their positions are rendered from cached ephemeris samples when available.

### Sky and time
- Bright-star catalog with spectral coloring and proper motion.
- Constellation lines that update with star movement.
- Persistent constellation name labels centered over visible constellations.
- Hover tooltips for bright stars and nearby constellation lines.
- Timeline scrubbing across deep time with landmark buttons such as Voyager launch, SL9 impacts, and major historical or astronomical milestones.
- Realtime mode locks the simulation to `1 s/s` and exposes a dedicated `NOW` hardpoint for live present-day viewing.
- Simulation startup initializes from the real current UTC date and time rather than a fixed preset date.
- Deterministic orbital positioning from simulation time rather than accumulated stepping.
- Earth day/night rotation is anchored to Greenwich sidereal time for present-day local-time accuracy.
- The Moon keeps a tuned tidal-lock texture orientation and a calibrated present-day orbital phase offset for more realistic realtime waxing/waning illumination.

## Tracked Objects

- Focusable tracked objects include the Sun, planets, moons, dwarf planets, named comets (including individual Shoemaker-Levy 9 fragments), and all 14 space probes.
- In addition to curated tracked objects, the ephemeris backend can stream a much larger minor-planet set from the database via the `SMALL BODIES H <= XX` threshold control, while the distant Oort cloud remains a separate procedural background population.

### Views and navigation
- `SOLAR SYSTEM` view for the standard orbital layout.
- `VORTEX` view for the solar system's helical galactic motion.
- Desktop object selection is available from the right-side `OBJECT` dropdown, while mobile retains a dedicated Objects sheet.
- Click-to-focus object inspection with an info panel that can be temporarily hidden without clearing focus.
- The info panel includes perihelion/aphelion data for heliocentric bodies, periapsis/apoapsis data for moons, and ETA readouts for the next extrema passage when that orbit model is available.
- Search box with keyboard navigation for fast lookup of objects and constellations.
- **Look-at** dropdown lets you lock the camera orientation toward the Sun or any planet while keeping focus on the current body — useful for viewing a probe's perspective of a nearby planet.

### UI and presentation
- Full-screen cinematic intro overlay in `index.html` with stars, flare, nebula, and animated `SOL` title treatment.
- Intro runs to completion before the main UI becomes interactive.
- Built-in help overlay and keyboard shortcut guide, opened from the bottom-left help button.
- Toggle buttons for realtime, real-size rendering, trails, orbits, constellations, look-at mode, and geo lock.
- **KEPLER MODE / EPHEMERIS ON** toggle switches between analytical orbits and database-backed high-precision positions.
- `SMALL BODIES H <= XX` slider (`H = 0–25`) sets the absolute-magnitude threshold for minor-planet bodies loaded into the scene in both Kepler and Ephemeris mode.
- Orion shortcut button (`HUNTER / ORION`) for quick sky focus.
- Responsive mobile UI with a bottom dock and dedicated Search, Objects, Time, and Controls sheets.
- Touch-safe mobile search, panel management, and object info behavior.
- Compact desktop timeline layout adapts for shorter viewports, including balanced time-step rows and a separate timeline button row for realtime/pause.
- Persistent top-right fullscreen toggle button using the browser fullscreen API where supported.

## Controls

### Mouse
- Left drag: orbit the focused object or current view. In unfocused free view, both drag axes are inverted.
- Right drag: roll focused view, or pan when no object is focused.
- Scroll: zoom in and out.
- Click: focus an object and open its info panel.
- Hover near bright stars or constellation lines: show sky tooltip labels.

### Touch / mobile
- Bottom dock buttons open Search, Objects, Time, and Controls sheets.
- Tap objects to focus them and open the info panel.
- Use the info panel's `Hide` control to dismiss it temporarily while keeping the current focus.
- Use the top-right fullscreen button to enter or exit fullscreen on supported browsers.

### Desktop UI
- Use the right-side `OBJECT` dropdown to focus planets, dwarf planets, comets, and space probes.
- Use the timeline panel for step buttons, landmark jumps, `REALTIME`, `PAUSE`, and the live `NOW` hardpoint.
- Use the right-side controls stack for `REAL SIZE`, orbit/trail/constellation toggles, look-at dropdown, and view-lock shortcuts.
- The built-in navigation/help pane lists the keyboard shortcuts, including `R` for realtime and `S` for real-size mode.

### Keyboard
- `/`: focus search.
- `Space`: pause or resume time.
- `R`: toggle realtime mode.
- `S`: toggle real-size mode.
- `O`: toggle orbits.
- `T`: toggle trails.
- `C`: toggle constellations.
- `L`: toggle look-at Sun (cycles the look-at dropdown between Sun and off).
- `G`: toggle geo lock.
- `H`: toggle help.
- `1`: switch to solar-system view.
- `2`: switch to vortex view.
- `Esc`: clear focus or close panels.

## Notes On Accuracy

- Planets, moons, dwarf planets, and comets are propagated from analytical orbital elements rather than hand-authored animation paths. The simulation advances mean anomaly as $M(t)=M_0+2\pi t/P$, solves Kepler's equation $M=E-e\sin E$ with a Newton-style iterative solver, converts eccentric anomaly $E$ to true anomaly, and then rotates the orbit into 3D ecliptic space using inclination $i$, ascending node $\Omega$, and argument/longitude terms derived from the source elements.
- Orbit shapes are true ellipses built from the semimajor axis and eccentricity, using $b=a\sqrt{1-e^2}$ for the semiminor axis and $c=ae$ for the focus offset.
- Major planets use time-varying secular elements rather than a single frozen J2000 orbit, which improves present-day alignment and probe flyby plausibility while keeping the model analytical and usable into the future.
- Planet axial tilt and ring plane orientation are computed from IAU WGCCRE J2000 pole RA/Dec values. Each pole unit vector is converted from equatorial to ecliptic J2000 coordinates using the standard J2000 obliquity (ε = 23.4393°) before being mapped into scene space. This gives physically correct ring-plane geometry for close-up views — for example, Saturn's rings appear nearly edge-on when the simulation time matches a known edge-on epoch.
- The remaining procedural Oort-cloud particles are given orbital parameters and periods from Kepler's third law, $P\propto a^{3/2}$, so that distant background population still evolves over time rather than staying static.
- Bright stars use catalog right ascension, declination, and proper motion in a J2000 frame. Their sky positions are advanced with linear proper-motion drift over simulation time, so constellations slowly deform across deep time instead of staying fixed.
- Earth's axial rotation for present-day viewing is anchored to Greenwich sidereal time, so realtime illumination now lines up much more closely with actual UTC-based local daylight.
- Moon orientation uses explicit per-moon spin handling. Regular moons default to synchronous parent-facing rotation, Earth's Moon keeps its tuned tidal-lock presentation offsets, several irregular moons use measured sidereal spin periods, and Hyperion is treated as a chaotic rotator rather than a locked body. The Moon's current orbital phase is also calibrated to a known new-moon epoch so realtime illumination is more plausible, while still remaining an analytical approximation rather than a full lunar ephemeris.
- Voyager 1 and 2 do not use Keplerian approximations here. Their positions come from sampled JPL Horizons trajectory data in the Solar System Barycenter / Ecliptic J2000 frame and are played back with binary search plus linear interpolation between samples.
- In Ephemeris mode, positions come from pre-computed state vectors fetched from the JPL Horizons API and stored in SQL Server as `(x, y, z, vx, vy, vz)` tuples in the Solar System Barycenter / Ecliptic J2000 frame. At runtime the backend converts these to heliocentric coordinates before returning them to the frontend. The frontend then locates the two bracketing samples for the requested simulation time and applies **Hermite cubic interpolation** using both position and velocity at each endpoint, giving a smooth $C^1$-continuous position curve that respects the body's true velocity rather than just linearly blending positions. Each periodic body is then cadence-checked against its local cached sample spacing; if the available ephemeris spacing is too coarse for that body's period, runtime falls back to Kepler for that body until sufficient data is available. Where a body's ephemeris coverage begins or ends, an anchor correction is computed from the difference between the last known ephemeris position and the Kepler orbit at that boundary, and that offset is smoothly applied to the Kepler positions outside the covered range so the body does not jump when transitioning between modes. Orbit lines in ephemeris mode are sampled from this same interpolated trajectory over a symmetric window centered on the current simulation time, and the midpoint vertex is pinned to the body's exact current position every frame to eliminate visible drift between full line refreshes.
- For long-period moons in ephemeris mode, the rendered local orbit track may appear as an open arc or spiral rather than a closed ellipse. That is expected: the moon is sampled relative to a parent body that is itself moving heliocentrically during the finite ephemeris window, so the start and end of the sampled track do not generally land on the same point in space.

## Data Sources

- Planetary orbital elements are based on J2000-era values from [Jean Meeus, *Astronomical Algorithms*](https://www.willbell.com/math/mc1.htm)-style element tables, with ascending-node terms included for full 3D ecliptic orientation.
- Planet pole directions (for axial tilt and ring plane orientation) are sourced from the [IAU Working Group on Cartographic Coordinates and Rotational Elements (WGCCRE)](https://www.iau.org/science/scientific_bodies/working_groups/52/) report, J2000 epoch.
- Bright-star positions and proper motions are derived from the [Hipparcos Input Catalogue](https://www.cosmos.esa.int/web/hipparcos/catalogues) in a J2000 reference frame.
- Dwarf-planet orientation/orbit terms such as $\omega$ and $\Omega$ are sourced from the [JPL Small-Body Database](https://ssd.jpl.nasa.gov/tools/sbdb_query.html).
- Space probe trajectories (Voyager 1 & 2, Cassini, Pioneer 10 & 11, New Horizons, Juno, Parker Solar Probe, BepiColombo, Galileo, MESSENGER, Dawn, Rosetta, OSIRIS-REx) are sampled from [JPL Horizons](https://ssd.jpl.nasa.gov/horizons/) output in the Solar System Barycenter, Ecliptic J2000 frame.
- Minor planet orbital elements are sourced from the [MPC Orbit Database (MPCORB)](https://minorplanetcenter.net/iau/MPCORB.html), covering ~1.5 million numbered and unnumbered minor planets.
- Pre-computed state vectors (position and velocity) for supported bodies are fetched from the [JPL Horizons API](https://ssd.jpl.nasa.gov/horizons/) in the Solar System Barycenter / Ecliptic J2000 frame and stored in SQL Server for fast retrieval.

## Simulation Limits

- This is not an $N$-body gravity integrator. Bodies are propagated independently from fixed orbital elements, so mutual perturbations, resonant drift, precession, and other long-timescale dynamical effects are not numerically integrated in real time.
- This project intentionally stays analytical for most solar-system bodies so it can remain explorable into the deep future and deep past; outside of historically sampled spacecraft, it does not attempt full ephemeris playback.
- The bright-star model uses linear proper-motion extrapolation and is clamped to about $\pm10$ million years, which is a practical approximation rather than a full galactic-dynamics solution.
- Voyager playback is only exact within the sampled Horizons interval included in the project; outside that range the code falls back to simple linear extrapolation from the final segment.
- The asteroid belt, Kuiper belt, and scattered-disc visuals now rely on the catalog/database path when ephemeris-backed small bodies are loaded rather than on always-on procedural populations. The Oort cloud remains a procedural population with randomized orbital parameters chosen to match the intended structure, not a catalog-complete reconstruction of known distant bodies.
- A few small outer irregular moons in the current set still lack reliable published spin periods in the simulator data, so they are intentionally left without a claimed physically accurate spin solution instead of being assigned invented orbital-period rotation.
- Background stars, visual glow effects, and several atmospheric or storm-style surface effects are artistic or procedural layers added for presentation rather than strict scientific reconstruction.
- Distances follow the simulator's AU-to-scene conversion, but body radii, line thicknesses, trail density, and other render-scale choices are adjusted for legibility and interaction instead of strict one-to-one physical scale. `REAL SIZE` reduces that exaggeration substantially, but camera floors and interaction radii still include pragmatic visual compromises.

## Main Files

- `index.html`: app shell, UI, intro overlay, and CSS.
- `CHANGELOG.md`: project history summarized from git commit messages.
- `js/solar_system.js`: simulation logic, orbital math, input handling, stars, search, mobile UI wiring, and main scene behavior.
- `js/ephemeris.js`: self-contained ephemeris system — progressive 4-stage fetch, interpolated position lookup, Kepler/ephemeris anchor correction, body search, and cache management.
- `js/voyager_trajectories.js`: extracted Voyager trajectory dataset and playback helpers exposed to the main script.
- `js/three.min.js`: Three.js runtime.
- `textures/`: planetary textures, Moon texture, Saturn ring texture, Milky Way background, and intro nebula texture.
- `trajectory/`: Voyager trajectory source data used for the probe paths.
- `favicon/`: generated browser/app icon set and web manifest assets.
- `favicon.ico`: root favicon currently referenced by `index.html`.

## Tech

- Three.js
- Vanilla JavaScript
- HTML/CSS
- ASP.NET Core (backend API)
- SQL Server (ephemeris sample store)
- NASA/JPL Horizons trajectory and ephemeris data
- IAU WGCCRE planet pole/rotation data
- Bright star catalogue / Hipparcos-derived star data

## Author

Created by Sani Huttunen, 2026.

## License

MIT
