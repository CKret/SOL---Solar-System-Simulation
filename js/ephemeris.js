'use strict';

// ── EphemerisSystem ───────────────────────────────────────────────────────────
// Provides two modes for body positioning:
//
//  'kepler'    — standard Keplerian propagation from orbital elements, anchored
//                to a real JPL Horizons position fetched once at startup so the
//                position at simTime=now is exact rather than drifted from J2000.
//
//  'ephemeris' — pre-fetched state vectors from the local API, with Hermite
//                interpolation between daily samples using position + velocity.
//
// Usage (in solar_system.js after animate() call):
//   EphemerisSystem.init({ apiBase: 'http://localhost:5000' }).then(() => {
//     EphemerisSystem.fetchAnchor(simTime, keplerPosFn);
//   });
//   // In animate loop, before Kepler computation:
//   const eph = EphemerisSystem.getPosition(bodyId, simTime);
//   if (eph) { pos.set(eph.x, eph.y, eph.z); } else { keplerCompute(); applyAnchor(bodyId, pos); }

const EphemerisSystem = (() => {

  // ── Constants ────────────────────────────────────────────────────────────────
  // 1 AU expressed in scene units. Must match EARTH_ORBIT_SCENE_RADIUS in solar_system.js.
  const AU_TO_SCENE = 32;
  // JD for J2000.0 (2000-Jan-1.5, noon UT)
  const J2000_JD = 2451545.0;
  // JD for Unix epoch (1970-Jan-1.0)
  const UNIX_JD  = 2440587.5;
  const DAY_YEARS = 1 / 365.25;
  const MONTH_YEARS = 31 / 365.25;
  const YEAR_YEARS = 1;
  const DAY_STAGE_STEP_DAYS = 1;
  const MONTH_STAGE_STEP_DAYS = 1;
  const YEAR_STAGE_STEP_DAYS = 1;
  const DECADE_STAGE_STEP_DAYS = 10;
  // How many days on each side of the target window to keep in the sample cache.
  // Samples outside this band are pruned when a new fetch begins.
  const CACHE_KEEP_MARGIN_DAYS = 365;
  // Ephemeris data range: BC 9999 to AD 9999 (in simTime years from J2000)
  const EPHEMERIS_MIN_SIMTIME_YEARS = -12000;  // approx BC 9999
  const EPHEMERIS_MAX_SIMTIME_YEARS = 12000;   // approx AD 9999
  // ── State ────────────────────────────────────────────────────────────────────
  let _apiBase  = 'http://localhost:5235';
  let _mode     = 'kepler';    // 'kepler' | 'ephemeris'
  let _ready    = false;
  let _bodies   = [];          // raw API body list
  let _loadedHMax = null;
  let _bodyWarmupRunning = false;
  let _bodyWarmupDone = false;
  let _keplerPositionProvider = null;
  let _cacheVersion = 0;       // increments whenever cached data changes
  let _maxEphemerisBodies = null;

  // slug → bodyId map built from _bodies
  const _slugToId = new Map();
  const _bodyById = new Map();
  const _bodyBySlug = new Map();

  // Ephemeris sample cache: Map<bodyId, Sample[]> sorted by jd
  // Sample = { jd, x, y, z, vx, vy, vz }  (all in scene units; vel in scene units/day)
  const _cache      = new Map();
  let _cacheStartJd = null;
  let _cacheEndJd   = null;
  let _targetStartJd = null;
  let _targetEndJd   = null;
  let _fetching     = false;
  let _queuedWindow = null;
  let _requestSerial = 0;
  let _activeFetchController = null;

  // Kepler anchor: Map<bodyId, {dx, dy, dz}> (scene-unit offset to add to Kepler result)
  const _anchor = new Map();
  const _boundaryAnchors = new Map();
  const _boundaryFetches = new Map();

  // ── Time helpers ─────────────────────────────────────────────────────────────
  function simTimeToJd(simTimeYears) {
    return J2000_JD + simTimeYears * 365.25;
  }

  function jdToSimTime(jd) {
    return (jd - J2000_JD) / 365.25;
  }

  function isWithinEphemerisRange(simTimeYears) {
    return simTimeYears >= EPHEMERIS_MIN_SIMTIME_YEARS && simTimeYears <= EPHEMERIS_MAX_SIMTIME_YEARS;
  }

  function jdToIso(jd) {
    // Only valid for AD dates representable by Date (post-100 AD or so).
    // For BC dates the import runs but the frontend only uses AD sim-time ranges.
    // Caller must verify time is within EPHEMERIS_MIN_SIMTIME_YEARS–EPHEMERIS_MAX_SIMTIME_YEARS.
    const ms = (jd - UNIX_JD) * 86400000;
    return new Date(ms).toISOString().slice(0, 19) + 'Z';
  }

  // ── Coordinate conversion ─────────────────────────────────────────────────────
  // API returns positions in AU, Ecliptic J2000 / Solar System Barycenter frame.
  // solar_system.js maps ecliptic (xE, yE, zE) → scene via out.set(xE, zE, -yE).
  // Velocities follow the same mapping (AU/day → scene-units/day).
  function toScene(xAu, yAu, zAu)    { return { x:  xAu*AU_TO_SCENE, y:  zAu*AU_TO_SCENE, z: -yAu*AU_TO_SCENE }; }
  function velToScene(vx, vy, vz)    { return { x:  vx *AU_TO_SCENE, y:  vz *AU_TO_SCENE, z: -vy *AU_TO_SCENE }; }

  // ── Hermite interpolation ─────────────────────────────────────────────────────
  // Cubic Hermite spline between two samples using position and velocity tangents.
  // t ∈ [0,1] where 0 = s0.jd, 1 = s1.jd.
  function hermiteAt(s0, s1, jd) {
    const dt = s1.jd - s0.jd;
    if (dt <= 0) return { x: s0.x, y: s0.y, z: s0.z };
    const t = (jd - s0.jd) / dt;
    const t2 = t * t, t3 = t2 * t;
    const h00 =  2*t3 - 3*t2 + 1;
    const h10 =    t3 - 2*t2 + t;
    const h01 = -2*t3 + 3*t2;
    const h11 =    t3 -   t2;
    // Velocity tangents must be scaled by dt to convert from scene-units/day to [0,1] space
    return {
      x: h00*s0.x + h10*s0.vx*dt + h01*s1.x + h11*s1.vx*dt,
      y: h00*s0.y + h10*s0.vy*dt + h01*s1.y + h11*s1.vy*dt,
      z: h00*s0.z + h10*s0.vz*dt + h01*s1.z + h11*s1.vz*dt,
    };
  }

  function lerpAt(s0, s1, jd) {
    const dt = s1.jd - s0.jd;
    if (dt <= 0) return { x: s0.x, y: s0.y, z: s0.z };
    const t = (jd - s0.jd) / dt;
    return {
      x: s0.x + (s1.x - s0.x) * t,
      y: s0.y + (s1.y - s0.y) * t,
      z: s0.z + (s1.z - s0.z) * t,
    };
  }

  // ── Binary search + interpolation ────────────────────────────────────────────
  function interpolateAt(samples, jd) {
    if (!samples || samples.length === 0) return null;
    if (samples.length === 1) return { x: samples[0].x, y: samples[0].y, z: samples[0].z };

    let lo = 0, hi = samples.length - 1;

    // Clamp to stored range
    if (jd <= samples[lo].jd) return { x: samples[lo].x, y: samples[lo].y, z: samples[lo].z };
    if (jd >= samples[hi].jd) return { x: samples[hi].x, y: samples[hi].y, z: samples[hi].z };

    // Binary search for bracketing pair
    while (hi - lo > 1) {
      const mid = (lo + hi) >>> 1;
      if (samples[mid].jd <= jd) lo = mid; else hi = mid;
    }

    const s0 = samples[lo], s1 = samples[hi];
    return (s0.vx != null && s1.vx != null) ? hermiteAt(s0, s1, jd) : lerpAt(s0, s1, jd);
  }

  function getLocalSampleStepDays(bodyId, simTimeYears) {
    const samples = _cache.get(bodyId);
    if (!samples || samples.length < 2) return null;

    const jd = simTimeToJd(simTimeYears);
    let lo = 0, hi = samples.length - 1;

    if (jd < samples[lo].jd || jd > samples[hi].jd) return null;

    while (hi - lo > 1) {
      const mid = (lo + hi) >>> 1;
      if (samples[mid].jd <= jd) lo = mid; else hi = mid;
    }

    return samples[hi].jd - samples[lo].jd;
  }

  // ── Fetch helpers ─────────────────────────────────────────────────────────────
  async function apiFetch(path, signal = null) {
    const r = await fetch(_apiBase + path, signal ? { signal } : undefined);
    if (!r.ok) throw new Error(`HTTP ${r.status} for ${path}`);
    return r.json();
  }

  async function searchBodies(query, limit = 150, namedOnly = true) {
    const q = (query ?? '').trim();
    const params = new URLSearchParams();
    if (q) params.set('q', q);
    params.set('limit', String(Math.max(1, Math.floor(limit))));
    params.set('namedOnly', namedOnly ? 'true' : 'false');
    return apiFetch(`/api/bodies/search?${params.toString()}`);
  }

  // ── Init ─────────────────────────────────────────────────────────────────────
  async function _loadBodies(hMax) {
    const hParam = hMax != null ? `?h_max=${hMax}` : '';
    const raw = await apiFetch(`/api/bodies${hParam}`);
    _loadedHMax = hMax;
    _slugToId.clear();
    _bodyById.clear();
    _bodyBySlug.clear();
    _bodies = raw;
    for (const b of _bodies) {
      _slugToId.set(b.slug, b.id);
      _bodyById.set(b.id, b);
      _bodyBySlug.set(b.slug, b);
    }
    console.log(`[Ephemeris] Bodies loaded — ${_bodies.length} bodies (h_max=${hMax ?? 'none'}).`);
  }

  function _mergeBodies(rows) {
    if (!Array.isArray(rows) || rows.length === 0) return 0;
    let added = 0;
    for (const b of rows) {
      if (!b || b.id == null) continue;
      const existing = _bodyById.get(b.id);
      if (!existing) {
        _bodies.push(b);
        added++;
      }
      _bodyById.set(b.id, b);
      if (b.slug) {
        _slugToId.set(b.slug, b.id);
        _bodyBySlug.set(b.slug, b);
      }
    }
    if (added > 0) _cacheVersion++;
    return added;
  }

  async function _loadBodiesRange(minExclusive, maxInclusive, batchSize = 10000) {
    let afterBodyId = null;
    let total = 0;

    for (;;) {
      const params = new URLSearchParams();
      if (maxInclusive != null) params.set('h_max', String(maxInclusive));
      if (minExclusive != null) params.set('h_min_exclusive', String(minExclusive));
      params.set('take', String(Math.max(1, Math.floor(batchSize))));
      if (afterBodyId != null) params.set('afterBodyId', String(afterBodyId));

      const batch = await apiFetch(`/api/bodies/batch?${params.toString()}`);
      const rows = batch.items || [];
      total += _mergeBodies(rows);
      afterBodyId = batch.nextAfterBodyId ?? afterBodyId;
      if (batch.done || rows.length === 0) break;
    }

    return total;
  }

  async function loadBodies(hMax) {
    await _loadBodies(hMax);
  }

  async function warmBodiesInBackground(startH = 12, endH = 25, batchSize = 10000) {
    if (_bodyWarmupRunning || _bodyWarmupDone || !_ready) return;
    _bodyWarmupRunning = true;

    try {
      const targetH = Math.floor(endH);
      if (_loadedHMax == null || _loadedHMax < targetH) {
        await loadBodies(targetH);
      }

      _bodyWarmupDone = true;
      console.log(`[Ephemeris] Background body warmup complete through H<=${targetH}. Cached ${_bodies.length.toLocaleString()} bodies.`);
    } catch (err) {
      console.warn('[Ephemeris] background body warmup failed:', err?.message || err);
    } finally {
      _bodyWarmupRunning = false;
    }
  }

  async function init(options = {}) {
    if (options.apiBase) _apiBase = options.apiBase;
    if (Number.isFinite(options.maxEphemerisBodies)) {
      _maxEphemerisBodies = Math.max(100, Math.min(5000, Math.floor(options.maxEphemerisBodies)));
    } else {
      _maxEphemerisBodies = null;
    }
    try {
      await _loadBodies(options.hMax ?? null);
      _ready = true;
    } catch (e) {
      console.warn('[Ephemeris] init failed:', e.message);
    }
    return _ready;
  }

  // ── Fetch window (ephemeris mode) ─────────────────────────────────────────────
  // Progressive 4-pass load: ±1 day → ±1 month → ±1 year → requested outer window.
  // hMax: if provided, includes small bodies where H <= hMax (otherwise authoritative only).
  async function fetchWindow(startSimTime, endSimTime, hMax) {
    const effectiveHMax = hMax ?? _loadedHMax;
    _targetStartJd = simTimeToJd(startSimTime);
    _targetEndJd = simTimeToJd(endSimTime);
    _queuedWindow = { startSimTime, endSimTime, hMax: effectiveHMax };
    if (_fetching) return;

    _fetching = true;
    try {
      while (_queuedWindow) {
        const nextWindow = _queuedWindow;
        _queuedWindow = null;
        await _runProgressiveFetch(nextWindow.startSimTime, nextWindow.endSimTime, nextWindow.hMax);
      }
    } finally {
      _fetching = false;
    }
  }

  function _pruneSampleCache() {
    if (_targetStartJd == null || _targetEndJd == null) return;
    const keepFrom = _targetStartJd - CACHE_KEEP_MARGIN_DAYS;
    const keepTo   = _targetEndJd   + CACHE_KEEP_MARGIN_DAYS;
    let pruned = 0;
    for (const [bodyId, samples] of _cache) {
      if (!samples || samples.length === 0) continue;
      if (samples[0].jd >= keepFrom && samples[samples.length - 1].jd <= keepTo) continue;
      let startIdx = 0;
      while (startIdx < samples.length && samples[startIdx].jd < keepFrom) startIdx++;
      let endIdx = samples.length - 1;
      while (endIdx >= startIdx && samples[endIdx].jd > keepTo) endIdx--;
      const kept = endIdx >= startIdx ? samples.slice(startIdx, endIdx + 1) : [];
      pruned += samples.length - kept.length;
      if (kept.length === 0) _cache.delete(bodyId);
      else _cache.set(bodyId, kept);
    }
    if (pruned > 0) {
      _cacheStartJd = keepFrom;
      _cacheEndJd   = keepTo;
      console.log(`[Ephemeris] Pruned ${pruned.toLocaleString()} old samples; cache window ${jdToIso(keepFrom)} → ${jdToIso(keepTo)}.`);
    }
  }

  async function _runProgressiveFetch(startSimTime, endSimTime, hMax) {
    _pruneSampleCache();
    const mid = (startSimTime + endSimTime) / 2;
    const radius = Math.max(DAY_YEARS, Math.abs(endSimTime - startSimTime) / 2);
    const stages = [
      { label: 'day', radius: Math.min(radius, DAY_YEARS), step: DAY_STAGE_STEP_DAYS },
      { label: 'month', radius: Math.min(radius, MONTH_YEARS), step: MONTH_STAGE_STEP_DAYS },
      { label: 'year', radius: Math.min(radius, YEAR_YEARS), step: YEAR_STAGE_STEP_DAYS },
      { label: 'decade', radius, step: DECADE_STAGE_STEP_DAYS },
    ];

    const seen = new Set();
    const requestId = ++_requestSerial;

    for (const stage of stages) {
      const key = `${stage.radius}|${stage.step}`;
      if (seen.has(key)) continue;
      seen.add(key);

      try {
        await _doFetch(mid, stage.radius, hMax, stage.step, stage.label, requestId);
      } catch (e) {
        console.warn(`[Ephemeris] ${stage.label} stage failed:`, e.message);
      }

      if (requestId !== _requestSerial) return;
    }
  }

  function getRequestedMaxBodies(hMax, stageLabel) {
    // User-configured override only — no hardcoded stage caps.
    // Fetching all bodies with H <= hMax ensures no authoritative body (planet, moon)
    // gets silently dropped by an arbitrary per-stage limit.
    if (Number.isFinite(_maxEphemerisBodies)) return _maxEphemerisBodies;
    return null;
  }

  // Fetches one window and merges it into the existing cache so the renderer keeps
  // using the best data already available while broader windows stream in.
  async function _doFetch(centerSimTime, radiusYears, hMax, step, stageLabel, requestId) {
    // Skip fetch if the requested time is outside the known ephemeris range
    if (!isWithinEphemerisRange(centerSimTime)) {
      console.warn(`[Ephemeris] Skipping ${stageLabel}: time ${centerSimTime.toFixed(1)} years is outside ephemeris range (BC 9999–AD 9999).`);
      return;
    }
    
    const centerJd = simTimeToJd(centerSimTime);
    const centerUtc = jdToIso(centerJd);
    const radiusDays = Math.max(0.5, radiusYears * 365.25);
    const startJd = centerJd - radiusDays;
    const endJd = centerJd + radiusDays;
    const hParam  = hMax != null ? `&h_max=${hMax}` : '';
    const maxBodies = getRequestedMaxBodies(hMax, stageLabel);
    const maxBodiesParam = Number.isFinite(maxBodies) ? `&maxBodies=${maxBodies}` : '';
    const url = `/api/ephemeris/window?centerUtc=${encodeURIComponent(centerUtc)}&radiusDays=${radiusDays}&step=${step}${hParam}${maxBodiesParam}`;

    console.log(`[Ephemeris] Fetching ${stageLabel} (step=${step}) centered ${centerUtc} radius ${radiusDays.toFixed(1)}d`);
    if (_activeFetchController) _activeFetchController.abort();
    const controller = new AbortController();
    _activeFetchController = controller;
    let data;
    try {
      data = await apiFetch(url, controller.signal);   // throws on HTTP error; cache unchanged
    } finally {
      if (_activeFetchController === controller) _activeFetchController = null;
    }
    if (requestId !== _requestSerial) return;

    const normalizedSamples = toHeliocentricSamples(data.samples || []);

    const incoming = new Map();
    for (const s of normalizedSamples) {
      const pos = toScene(s.x, s.y, s.z);
      const vel = (s.vx != null) ? velToScene(s.vx, s.vy, s.vz) : { vx: null, vy: null, vz: null };
      if (!incoming.has(s.bodyId)) incoming.set(s.bodyId, []);
      incoming.get(s.bodyId).push({ jd: s.sampleJd, x: pos.x, y: pos.y, z: pos.z, vx: vel.x, vy: vel.y, vz: vel.z });
    }

    for (const [bodyId, samples] of incoming) {
      samples.sort((a, b) => a.jd - b.jd);
      _cache.set(bodyId, mergeSamples(_cache.get(bodyId) || [], samples));
    }

    _cacheStartJd = _cacheStartJd == null ? startJd : Math.min(_cacheStartJd, startJd);
    _cacheEndJd   = _cacheEndJd   == null ? endJd   : Math.max(_cacheEndJd, endJd);
    _cacheVersion++;
    console.log(`[Ephemeris] Cached ${data.count} samples from ${stageLabel}; coverage ${jdToIso(_cacheStartJd)} → ${jdToIso(_cacheEndJd)}.`);
  }

  function mergeSamples(existing, incoming) {
    if (!existing.length) return incoming;
    if (!incoming.length) return existing;

    const merged = [];
    let i = 0;
    let j = 0;

    while (i < existing.length && j < incoming.length) {
      const left = existing[i];
      const right = incoming[j];

      if (left.jd < right.jd) {
        merged.push(left);
        i++;
        continue;
      }

      if (right.jd < left.jd) {
        merged.push(right);
        j++;
        continue;
      }

      merged.push(right);
      i++;
      j++;
    }

    while (i < existing.length) merged.push(existing[i++]);
    while (j < incoming.length) merged.push(incoming[j++]);
    return merged;
  }

  // ── Fetch anchor (kepler mode) ────────────────────────────────────────────────
  // Fetches one day of data centred on anchorSimTime for all authoritative bodies.
  // keplerPosFn(bodyId) must return the Kepler-computed scene position {x,y,z}
  // at the same simTime so the correction offset can be computed.
  async function fetchAnchor(anchorSimTime, keplerPosFn) {
    _keplerPositionProvider = keplerPosFn;
    
    // Skip anchor fetch if time is outside known ephemeris range (use pure Kepler)
    if (!isWithinEphemerisRange(anchorSimTime)) {
      console.warn(`[Ephemeris] Skipping anchor fetch: time ${anchorSimTime.toFixed(1)} years is outside ephemeris range (BC 9999–AD 9999).`);
      return;
    }
    
    const anchorJd = simTimeToJd(anchorSimTime);
    const anchorUtc = jdToIso(anchorJd);
    
    // Fetch a 2-day date-centered all-bodies window and interpolate to exact anchor JD
    const url = `/api/ephemeris/window?centerUtc=${encodeURIComponent(anchorUtc)}&radiusDays=1&step=1`;

    try {
      const data = await apiFetch(url);
      const normalizedSamples = toHeliocentricSamples(data.samples || []);

      // Group by bodyId
      const byBody = new Map();
      for (const s of normalizedSamples) {
        const pos = toScene(s.x, s.y, s.z);
        const vel = (s.vx != null) ? velToScene(s.vx, s.vy, s.vz) : { vx: null, vy: null, vz: null };
        if (!byBody.has(s.bodyId)) byBody.set(s.bodyId, []);
        byBody.get(s.bodyId).push({ jd: s.sampleJd, x: pos.x, y: pos.y, z: pos.z, vx: vel.x, vy: vel.y, vz: vel.z });
      }

      _anchor.clear();
      for (const [bodyId, samples] of byBody) {
        const real = interpolateAt(samples, anchorJd);
        if (!real) continue;
        const kepler = keplerPosFn(bodyId);
        if (!kepler) continue;
        _anchor.set(bodyId, { dx: real.x - kepler.x, dy: real.y - kepler.y, dz: real.z - kepler.z });
      }
      console.log(`[Ephemeris] Kepler anchor set for ${_anchor.size} bodies at JD ${anchorJd.toFixed(1)}.`);
    } catch (e) {
      console.warn('[Ephemeris] fetchAnchor failed:', e.message);
    }
  }

  function toHeliocentricSamples(samples) {
    if (!samples || samples.length === 0) return [];

    const sunBodyId = _slugToId.get('sun');
    if (sunBodyId == null) return samples;

    const jdKey = (jd) => Number(jd).toFixed(8);
    const sunByJd = new Map();
    for (const s of samples) {
      if (s.bodyId === sunBodyId) sunByJd.set(jdKey(s.sampleJd), s);
    }

    if (sunByJd.size === 0) return samples;

    return samples.map((s) => {
      const sun = sunByJd.get(jdKey(s.sampleJd));
      if (!sun) return s;

      if (s.bodyId === sunBodyId) {
        return {
          ...s,
          x: 0,
          y: 0,
          z: 0,
          vx: 0,
          vy: 0,
          vz: 0,
        };
      }

      return {
        ...s,
        x: s.x - sun.x,
        y: s.y - sun.y,
        z: s.z - sun.z,
        vx: (s.vx != null && sun.vx != null) ? (s.vx - sun.vx) : s.vx,
        vy: (s.vy != null && sun.vy != null) ? (s.vy - sun.vy) : s.vy,
        vz: (s.vz != null && sun.vz != null) ? (s.vz - sun.vz) : s.vz,
      };
    });
  }

  // ── Public position API ───────────────────────────────────────────────────────

  // Ephemeris mode: returns interpolated scene position or null if not in cache.
  function getPosition(bodyId, simTimeYears) {
    const jd = simTimeToJd(simTimeYears);
    const boundarySide = getBoundarySide(bodyId, jd);
    if (boundarySide) {
      ensureBoundaryAnchor(bodyId, boundarySide);
      return null;
    }

    const samples = _cache.get(bodyId);
    if (!samples || samples.length === 0) return null;
    if (jd < samples[0].jd || jd > samples[samples.length - 1].jd) return null;
    return interpolateAt(samples, jd);
  }

  // Returns sampled cached ephemeris positions for a body in [startSimTime, endSimTime].
  // Output points are scene-space and include jd so callers can align trajectories.
  function getTrajectory(bodyId, startSimTime, endSimTime, maxPoints = 180) {
    const samples = _cache.get(bodyId);
    if (!samples || samples.length < 2) return null;

    let startJd = simTimeToJd(startSimTime);
    let endJd = simTimeToJd(endSimTime);
    if (endJd < startJd) {
      const tmp = startJd;
      startJd = endJd;
      endJd = tmp;
    }

    const firstJd = samples[0].jd;
    const lastJd = samples[samples.length - 1].jd;
    const fromJd = Math.max(startJd, firstJd);
    const toJd = Math.min(endJd, lastJd);
    if (!(toJd > fromJd)) return null;

    const count = Math.max(2, Math.floor(maxPoints));
    const out = [];
    for (let i = 0; i < count; i++) {
      const t = i / (count - 1);
      const jd = fromJd + (toJd - fromJd) * t;
      const p = interpolateAt(samples, jd);
      if (!p) continue;
      out.push({ jd, x: p.x, y: p.y, z: p.z });
    }
    return out.length >= 2 ? out : null;
  }

  // Kepler mode: applies the anchor correction offset to a THREE.Vector3 in place.
  // Call this after computing the Kepler position.
  function applyAnchor(bodyId, simTimeYears, vec3) {
    const jd = simTimeToJd(simTimeYears);
    const boundarySide = getBoundarySide(bodyId, jd);
    if (boundarySide) ensureBoundaryAnchor(bodyId, boundarySide);

    const off = boundarySide
      ? _boundaryAnchors.get(makeBoundaryAnchorKey(bodyId, boundarySide)) || _anchor.get(bodyId)
      : _anchor.get(bodyId);
    if (!off) return;
    vec3.x += off.dx;
    vec3.y += off.dy;
    vec3.z += off.dz;
  }

  function getAnchorOffset(bodyId, simTimeYears) {
    if (bodyId == null) return null;
    const jd = simTimeToJd(simTimeYears);
    const boundarySide = getBoundarySide(bodyId, jd);
    if (boundarySide) ensureBoundaryAnchor(bodyId, boundarySide);
    return boundarySide
      ? _boundaryAnchors.get(makeBoundaryAnchorKey(bodyId, boundarySide)) || _anchor.get(bodyId)
      : _anchor.get(bodyId);
  }

  // Applies anchor in a relative frame: body minus reference body.
  // Useful for satellites so local orbital spacing stays stable while the parent frame is anchored.
  function applyRelativeAnchor(bodyId, referenceBodyId, simTimeYears, vec3) {
    const bodyOff = getAnchorOffset(bodyId, simTimeYears);
    const refOff = getAnchorOffset(referenceBodyId, simTimeYears);
    if (bodyOff) {
      vec3.x += bodyOff.dx;
      vec3.y += bodyOff.dy;
      vec3.z += bodyOff.dz;
    }
    if (refOff) {
      vec3.x -= refOff.dx;
      vec3.y -= refOff.dy;
      vec3.z -= refOff.dz;
    }
  }

  // Looks up a bodyId from a slug (as returned by /api/bodies).
  function bodyIdForSlug(slug) {
    return _slugToId.get(slug) ?? null;
  }

  function getBodyById(bodyId) {
    return _bodyById.get(bodyId) ?? null;
  }

  function getBodyBySlug(slug) {
    return _bodyBySlug.get(slug) ?? null;
  }

  function clearCache() {
    _requestSerial++;
    if (_activeFetchController) {
      _activeFetchController.abort();
      _activeFetchController = null;
    }
    _queuedWindow = null;
    _cache.clear();
    _cacheStartJd = null;
    _cacheEndJd   = null;
    _targetStartJd = null;
    _targetEndJd   = null;
    _fetching = false;
    _cacheVersion++;
  }

  function getBoundarySide(bodyId, jd) {
    const body = _bodyById.get(bodyId);
    if (!body || !body.hasEphemeris) return null;
    if (body.ephemerisMinJD != null && jd < body.ephemerisMinJD) return 'min';
    if (body.ephemerisMaxJD != null && jd > body.ephemerisMaxJD) return 'max';
    return null;
  }

  function makeBoundaryAnchorKey(bodyId, side) {
    return `${bodyId}:${side}`;
  }

  function ensureBoundaryAnchor(bodyId, side) {
    const key = makeBoundaryAnchorKey(bodyId, side);
    if (_boundaryAnchors.has(key) || _boundaryFetches.has(key) || !_keplerPositionProvider) return;

    const body = _bodyById.get(bodyId);
    const boundaryJd = side === 'min' ? body?.ephemerisMinJD : body?.ephemerisMaxJD;
    if (boundaryJd == null) return;

    // Boundary anchors are derived only from already-cached bulk windows.
    // Do not trigger per-body HTTP fetches here; that causes stutter under load.
    tryBuildBoundaryAnchorFromCache(bodyId, boundaryJd, key);
  }

  function tryBuildBoundaryAnchorFromCache(bodyId, boundaryJd, key) {
    const samples = _cache.get(bodyId);
    if (!samples || samples.length === 0) return false;

    const firstJd = samples[0].jd;
    const lastJd = samples[samples.length - 1].jd;
    // Only trust local cache if it actually brackets (or closely touches) the boundary.
    if (boundaryJd < firstJd - 2 || boundaryJd > lastJd + 2) return false;

    const real = interpolateAt(samples, boundaryJd);
    const kepler = _keplerPositionProvider(bodyId, jdToSimTime(boundaryJd));
    if (!real || !kepler) return false;

    _boundaryAnchors.set(key, {
      dx: real.x - kepler.x,
      dy: real.y - kepler.y,
      dz: real.z - kepler.z,
    });
    return true;
  }

  function setMode(m) {
    if (m !== 'kepler' && m !== 'ephemeris') return;
    _mode = m;
    console.log(`[Ephemeris] Mode → ${m}`);
  }

  function getMode()        { return _mode; }
  function isReady()        { return _ready; }
  function isFetching()     { return _fetching; }
  function hasCachedWindow(){ return _cacheStartJd != null; }
  function getBodies()      { return _bodies; }

  // Returns true when simTime is outside the window OR within a configurable
  // edge margin, so we can prefetch before hitting the boundary.
  function needsRefetch(simTimeYears, marginYears = 0) {
    if (_targetStartJd == null || _targetEndJd == null) return true;
    const jd = simTimeToJd(simTimeYears);
    if (jd < _targetStartJd || jd > _targetEndJd) return true;

    const marginJd = Math.max(0, Number(marginYears) || 0) * 365.25;
    if (marginJd <= 0) return false;
    return jd <= (_targetStartJd + marginJd) || jd >= (_targetEndJd - marginJd);
  }

  function getEphemerisWindowDays(bodyId) {
    const body = _bodyById.get(bodyId);
    if (!body?.hasEphemeris || body.ephemerisMinJD == null || body.ephemerisMaxJD == null) {
      return null;
    }
    return body.ephemerisMaxJD - body.ephemerisMinJD;
  }

  async function fetchBodyRange(bodyId, startSimTime, endSimTime, limit = null) {
    if (bodyId == null) return false;

    // Skip fetch if the requested time range is outside ephemeris bounds
    if (!isWithinEphemerisRange(startSimTime) || !isWithinEphemerisRange(endSimTime)) {
      console.warn(`[Ephemeris] Skipping fetchBodyRange for body ${bodyId}: time range ${startSimTime.toFixed(1)}–${endSimTime.toFixed(1)} years is outside ephemeris range (BC 9999–AD 9999).`);
      return false;
    }

    let startJd = simTimeToJd(startSimTime);
    let endJd = simTimeToJd(endSimTime);
    if (endJd < startJd) {
      const tmp = startJd;
      startJd = endJd;
      endJd = tmp;
    }

    const startUtc = jdToIso(startJd);
    const endUtc = jdToIso(endJd);
    const safeLimit = Number.isFinite(limit) ? Math.max(2, Math.floor(limit)) : null;
    const limitParam = safeLimit != null ? `&limit=${safeLimit}` : '';
    const bodyUrl = `/api/ephemeris/${bodyId}?startUtc=${encodeURIComponent(startUtc)}&endUtc=${encodeURIComponent(endUtc)}${limitParam}`;

    const sunBodyId = _slugToId.get('sun');
    const fetchSun = sunBodyId != null && sunBodyId !== bodyId;
    const sunUrl = fetchSun
      ? `/api/ephemeris/${sunBodyId}?startUtc=${encodeURIComponent(startUtc)}&endUtc=${encodeURIComponent(endUtc)}${limitParam}`
      : null;

    const [bodyData, sunData] = await Promise.all([
      apiFetch(bodyUrl),
      sunUrl ? apiFetch(sunUrl) : Promise.resolve(null),
    ]);

    const bodyRaw = Array.isArray(bodyData?.samples) ? bodyData.samples : [];
    const sunRaw = Array.isArray(sunData?.samples) ? sunData.samples : [];
    if (bodyRaw.length === 0) return false;

    const bodyScene = bodyRaw.map((s) => {
      const pos = toScene(s.x, s.y, s.z);
      const vel = (s.vx != null) ? velToScene(s.vx, s.vy, s.vz) : { x: null, y: null, z: null };
      return { jd: s.sampleJd, x: pos.x, y: pos.y, z: pos.z, vx: vel.x, vy: vel.y, vz: vel.z };
    }).sort((a, b) => a.jd - b.jd);

    const sunScene = sunRaw.map((s) => {
      const pos = toScene(s.x, s.y, s.z);
      const vel = (s.vx != null) ? velToScene(s.vx, s.vy, s.vz) : { x: null, y: null, z: null };
      return { jd: s.sampleJd, x: pos.x, y: pos.y, z: pos.z, vx: vel.x, vy: vel.y, vz: vel.z };
    }).sort((a, b) => a.jd - b.jd);

    const sunByJd = new Map();
    for (const s of sunScene) sunByJd.set(Number(s.jd).toFixed(8), s);

    const bodyHeliocentric = bodyScene.map((sample) => {
      let sun = sunByJd.get(Number(sample.jd).toFixed(8));
      if (!sun && sunScene.length > 0) {
        const sunInterp = interpolateAt(sunScene, sample.jd);
        if (sunInterp) {
          sun = { jd: sample.jd, x: sunInterp.x, y: sunInterp.y, z: sunInterp.z, vx: null, vy: null, vz: null };
        }
      }

      if (!sun) return sample;
      return {
        jd: sample.jd,
        x: sample.x - sun.x,
        y: sample.y - sun.y,
        z: sample.z - sun.z,
        vx: (sample.vx != null && sun.vx != null) ? (sample.vx - sun.vx) : sample.vx,
        vy: (sample.vy != null && sun.vy != null) ? (sample.vy - sun.vy) : sample.vy,
        vz: (sample.vz != null && sun.vz != null) ? (sample.vz - sun.vz) : sample.vz,
      };
    });

    _cache.set(bodyId, mergeSamples(_cache.get(bodyId) || [], bodyHeliocentric));

    if (sunBodyId != null && sunScene.length > 0) {
      const sunZero = sunScene.map((s) => ({ jd: s.jd, x: 0, y: 0, z: 0, vx: 0, vy: 0, vz: 0 }));
      _cache.set(sunBodyId, mergeSamples(_cache.get(sunBodyId) || [], sunZero));
    }

    _cacheStartJd = _cacheStartJd == null ? startJd : Math.min(_cacheStartJd, startJd);
    _cacheEndJd = _cacheEndJd == null ? endJd : Math.max(_cacheEndJd, endJd);
    _cacheVersion++;
    return true;
  }

  return {
    init,
    fetchWindow,
    fetchBodyRange,
    fetchAnchor,
    getPosition,
    getTrajectory,
    applyAnchor,
    applyRelativeAnchor,
    clearCache,
    bodyIdForSlug,
    getBodyById,
    getBodyBySlug,
    setMode,
    getMode,
    isReady,
    isFetching,
    hasCachedWindow,
    needsRefetch,
    getBodies,
    loadBodies,
    getEphemerisWindowDays,
    warmBodiesInBackground,

    searchBodies,
    simTimeToJd,
    getLocalSampleStepDays,
    getCacheVersion: () => _cacheVersion,
    getCachedBodyIds: () => [..._cache.keys()],
  };

})();
