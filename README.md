# Solar System Simulator

A browser-based 3D solar system and deep-time space visualizer built with Three.js. It combines planetary orbits, real star motion, dwarf planets, comets, Voyager trajectories, responsive desktop/mobile controls, and a cinematic intro overlay into a single self-contained project.

## Run It

Recommended for local use: run a simple static server from the project root and open `http://localhost:8000/index.html` in a modern browser.

Example:

```bash
python -m http.server 8000
```

Opening `index.html` directly may still work in some browsers, but serving the folder is the safer default.

Project layout:

```text
.
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
│   ├── voyager_trajectories.js
│   └── three.min.js
├── README.md
├── textures/
│   ├── ...
├── three.min.js
└── trajectory/
	├── Voyager1-Jupiter.txt
	├── Voyager1-Saturn.txt
	├── Voyager1.txt
	├── Voyager2-Jupiter.txt
	├── Voyager2-Neptune.txt
	├── Voyager2-Saturn.txt
	├── Voyager2-Uranus.txt
	└── Voyager2.txt
```

No build step is required.

## Current Feature Set

### Core simulation
- 8 planets with axial rotation and elliptical orbits.
- Earth includes a separate animated cloud layer with a dynamic procedural storm system.
- 65 tracked moons across Earth, Mars, Jupiter, Saturn, Uranus, and Neptune, with explicit moon spin handling: synchronous rotation for regular/tidally evolved moons, published spin periods for several irregular moons, and special handling for cases such as Hyperion's chaotic rotation.
- 9 dwarf planets: Ceres, Pluto, Eris, Makemake, Haumea, Sedna, Gonggong, Quaoar, and Orcus.
- 10 named comets: Halley's, Hale-Bopp, Hyakutake, Encke, 67P/Churyumov-Gerasimenko, Tempel 1, Wild 2, Shoemaker-Levy 9, NEOWISE, and Ikeya-Seki.
- Both Voyager probes with trajectory data.
- Dense small-body fields for the asteroid belt, Kuiper belt, scattered disc, and Oort cloud, with 15,500 simulated small-body particles in total.

### Sky and time
- 130 named bright stars with spectral coloring and proper motion.
- Constellation lines that update with star movement.
- Persistent constellation name labels centered over visible constellations.
- Hover tooltips for bright stars and nearby constellation lines.
- Timeline scrubbing across deep time with landmark buttons such as Voyager launch and major historical or astronomical milestones.
- Simulation startup initializes from the real current UTC date and time rather than a fixed preset date.
- Deterministic orbital positioning from simulation time rather than accumulated stepping.

## Tracked Objects

- 95 focusable tracked objects in total: the Sun, 8 planets, 65 moons, 9 dwarf planets, 10 comets, and 2 Voyager probes.
- 15,725 individually simulated points and objects overall when you include the 130 bright stars and the 15,500 small-body particles used for the asteroid belt, Kuiper belt, scattered disc, and Oort cloud.

### Views and navigation
- `SOLAR SYSTEM` view for the standard orbital layout.
- `VORTEX` view for the solar system's helical galactic motion.
- Focus bar shortcuts for planets, dwarf planets, comets, and Voyager 1 / 2.
- Click-to-focus object inspection with an info panel that can be temporarily hidden without clearing focus.
- Search box with keyboard navigation for fast lookup of objects and constellations.

### UI and presentation
- Full-screen cinematic intro overlay in `index.html` with stars, flare, nebula, and animated `SOL` title treatment.
- Intro runs to completion before the main UI becomes interactive.
- Built-in help overlay and keyboard shortcut guide, opened from the bottom-left help button.
- Toggle buttons for trails, orbits, constellations, look-at-Sun mode, and geo lock.
- Orion shortcut button (`HUNTER / ORION`) for quick sky focus.
- Responsive mobile UI with a bottom dock and dedicated Search, Objects, Time, and Controls sheets.
- Touch-safe mobile search, panel management, and object info behavior.
- Persistent top-right fullscreen toggle button using the browser fullscreen API where supported.

## Controls

### Mouse
- Left drag: orbit the focused object or current view.
- Right drag: roll focused view, or pan when no object is focused.
- Scroll: zoom in and out.
- Click: focus an object and open its info panel.
- Hover near bright stars or constellation lines: show sky tooltip labels.

### Touch / mobile
- Bottom dock buttons open Search, Objects, Time, and Controls sheets.
- Tap objects to focus them and open the info panel.
- Use the info panel's `Hide` control to dismiss it temporarily while keeping the current focus.
- Use the top-right fullscreen button to enter or exit fullscreen on supported browsers.

### Keyboard
- `/`: focus search.
- `Space`: pause or resume time.
- `O`: toggle orbits.
- `T`: toggle trails.
- `C`: toggle constellations.
- `L`: toggle look-at-Sun mode.
- `G`: toggle geo lock.
- `H`: toggle help.
- `1`: switch to solar-system view.
- `2`: switch to vortex view.
- `Esc`: clear focus or close panels.

## Notes On Accuracy

- Planets, moons, dwarf planets, and comets are propagated from J2000 Keplerian elements rather than hand-authored animation paths. The simulation advances mean anomaly as $M(t)=M_0+2\pi t/P$, solves Kepler's equation $M=E-e\sin E$ with a Newton-style iterative solver, converts eccentric anomaly $E$ to true anomaly, and then rotates the orbit into 3D ecliptic space using inclination $i$, ascending node $\Omega$, and argument/longitude terms derived from the source elements.
- Orbit shapes are true ellipses built from the semimajor axis and eccentricity, using $b=a\sqrt{1-e^2}$ for the semiminor axis and $c=ae$ for the focus offset.
- Belt and cloud particles are also given orbital parameters and periods from Kepler's third law, $P\propto a^{3/2}$, so the asteroid belt, Kuiper belt, scattered disc, and Oort cloud are orbiting populations rather than static point clouds.
- Bright stars use catalog right ascension, declination, and proper motion in a J2000 frame. Their sky positions are advanced with linear proper-motion drift over simulation time, so constellations slowly deform across deep time instead of staying fixed.
- Moon orientation uses explicit per-moon spin handling. Regular moons default to synchronous parent-facing rotation, Earth's Moon keeps its tuned tidal-lock presentation offsets, several irregular moons use measured sidereal spin periods, and Hyperion is treated as a chaotic rotator rather than a locked body.
- Voyager 1 and 2 do not use Keplerian approximations here. Their positions come from sampled JPL Horizons trajectory data in the Solar System Barycenter / Ecliptic J2000 frame and are played back with binary search plus linear interpolation between samples.

## Data Sources

- Planetary orbital elements are based on J2000-era values from Jean Meeus-style element tables, with ascending-node terms included for full 3D ecliptic orientation.
- Bright-star positions and proper motions are derived from Hipparcos-style catalog data in a J2000 reference frame.
- Dwarf-planet orientation/orbit terms such as $\omega$ and $\Omega$ are sourced from JPL small-body style data.
- Voyager 1 and 2 trajectories are sampled from JPL Horizons output in the Solar System Barycenter, Ecliptic J2000 frame, then converted into the simulator's scene coordinates.

## Simulation Limits

- This is not an $N$-body gravity integrator. Bodies are propagated independently from fixed orbital elements, so mutual perturbations, resonant drift, precession, and other long-timescale dynamical effects are not numerically integrated in real time.
- The bright-star model uses linear proper-motion extrapolation and is clamped to about $\pm10$ million years, which is a practical approximation rather than a full galactic-dynamics solution.
- Voyager playback is only exact within the sampled Horizons interval included in the project; outside that range the code falls back to simple linear extrapolation from the final segment.
- The asteroid belt, Kuiper belt, scattered disc, and Oort cloud are procedural populations with randomized orbital parameters chosen to match the intended structure, not catalog-complete reconstructions of known small bodies.
- A few small outer irregular moons in the current set still lack reliable published spin periods in the simulator data, so they are intentionally left without a claimed physically accurate spin solution instead of being assigned invented orbital-period rotation.
- Background stars, visual glow effects, and several atmospheric or storm-style surface effects are artistic or procedural layers added for presentation rather than strict scientific reconstruction.
- Distances follow the simulator's AU-to-scene conversion, but body radii, line thicknesses, trail density, and other render-scale choices are adjusted for legibility and interaction instead of strict one-to-one physical scale.

## Main Files

- `index.html`: app shell, UI, intro overlay, and CSS.
- `js/solar_system.js`: simulation logic, orbital math, input handling, stars, search, mobile UI wiring, and main scene behavior.
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
- NASA/JPL-style orbital element data and trajectory datasets
- Bright star catalogue / Hipparcos-derived star data

## Author

Created by Sani Huttunen, 2026.

## License

MIT
