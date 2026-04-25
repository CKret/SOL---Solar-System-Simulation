// ──
const renderer = new THREE.WebGLRenderer({ antialias:true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.setSize(window.innerWidth, window.innerHeight);
document.body.appendChild(renderer.domElement);

const scene  = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(48, window.innerWidth/window.innerHeight, 0.1, 2000000);

// ── Lights ────────────────────────────────────────────────────────────────────
scene.add(new THREE.AmbientLight(0x112233, 1.8));
const sunLight = new THREE.PointLight(0xfff4d0, 2.8, 0, 1.6);
scene.add(sunLight);

// ── Planet data ───────────────────────────────────────────────────────────────
// rotPeriod = sidereal rotation period in Earth days (negative = retrograde)
let earthAngle0 = 0; // set after planets are built

// J2000 orbital elements (Meeus Table 31.a + ascending node Omega).
// Full 3D Keplerian orbit: positions use omega (long of perihelion), Omega (ascending node), inc.
const PD = [
  { name:'MERCURY', sma:12.4,  ecc:0.206, inc:7.005, period:0.240847, L0:252.251, omega: 77.456, Omega: 48.331, r:0.32, color:0xAAAAAA, emissive:0x111111, tc:0x999999, diameter:'4,879 km',   dist:'57.9M km',  year:'88 days',    moons:'0',  type:'Terrestrial', rotPeriod:58.646  },
  { name:'VENUS',   sma:23.1,  ecc:0.007, inc:3.395, period:0.615197, L0:181.980, omega:131.564, Omega: 76.680, r:0.75, color:0xE8C068, emissive:0x1a0d00, tc:0xc8a040, diameter:'12,104 km',  dist:'108M km',   year:'225 days',   moons:'0',  type:'Terrestrial', rotPeriod:-243.02 },
  { name:'EARTH',   sma:32,    ecc:0.017, inc:0.000, period:1.000017, L0:100.464, omega:102.937, Omega:  0.000, r:0.80, color:0x3377BB, emissive:0x00090f, tc:0x1155aa, diameter:'12,742 km',  dist:'149.6M km', year:'365 days',   moons:'1',  type:'Terrestrial', rotPeriod:0.9973  },
  { name:'MARS',    sma:48.8,  ecc:0.093, inc:1.850, period:1.880848, L0:355.433, omega:336.060, Omega: 49.558, r:0.44, color:0xC1440E, emissive:0x180300, tc:0x882200, diameter:'6,779 km',   dist:'228M km',   year:'687 days',   moons:'2',  type:'Terrestrial', rotPeriod:1.026   },
  { name:'JUPITER', sma:166.5, ecc:0.049, inc:1.303, period:11.86262, L0: 34.396, omega: 14.331, Omega:100.464, r:2.80, color:0xC99040, emissive:0x130900, tc:0x886020, diameter:'139,820 km', dist:'778M km',   year:'11.9 yrs',   moons:'95', type:'Gas Giant',   rotPeriod:0.41354 },
  { name:'SATURN',  sma:305.2, ecc:0.057, inc:2.489, period:29.45701, L0: 50.077, omega: 93.057, Omega:113.665, r:2.35, color:0xE4D191, emissive:0x120f00, tc:0xb0a060, diameter:'116,460 km', dist:'1.43B km',  year:'29.5 yrs',   moons:'146',type:'Gas Giant',   rotPeriod:0.44401, rings:true, ri:1.35, ro:2.35, rc:0xC2A06B, rOp:0.75 },
  { name:'URANUS',  sma:614.1, ecc:0.047, inc:0.773, period:84.01685, L0:314.055, omega:173.005, Omega: 74.006, r:1.55, color:0x7DCCCC, emissive:0x001313, tc:0x3d8888, diameter:'50,724 km',  dist:'2.87B km',  year:'84 yrs',     moons:'28', type:'Ice Giant',   rotPeriod:-0.71833,rings:true, ri:1.64, ro:2.00, rc:0x5daaaa, rOp:0.55, tiltRings:true },
  { name:'NEPTUNE', sma:962.2, ecc:0.009, inc:1.770, period:164.7913, L0:304.349, omega: 48.124, Omega:131.784, r:1.50, color:0x4466FF, emissive:0x000820, tc:0x2233cc, diameter:'49,244 km',  dist:'4.50B km',  year:'165 yrs',    moons:'16', type:'Ice Giant',   rotPeriod:0.67125 },
];

// ── Kepler solver ─────────────────────────────────────────────────────────────
function keplerE(M, ecc) {
  // Normalize M to [-π, π] for better convergence
  M = M % (Math.PI*2);
  if (M > Math.PI)  M -= Math.PI*2;
  if (M < -Math.PI) M += Math.PI*2;

  // Better initial guess for high eccentricity (Danby 1988)
  let E = ecc > 0.8
    ? Math.sign(M) * Math.PI  // start at ±π for near-parabolic
    : M + ecc * Math.sin(M) * (1 + ecc * Math.cos(M));

  // More iterations for high eccentricity
  const iters = ecc > 0.9 ? 50 : 10;
  for (let i=0; i<iters; i++) {
    const dE = (E - ecc*Math.sin(E) - M) / (1 - ecc*Math.cos(E));
    E -= dE;
    if (Math.abs(dE) < 1e-10) break;
  }
  return E;
}

// ── Glow texture (used by stars, belts, trails) ─────────────────────────────
const glowCanvas = document.createElement('canvas');
glowCanvas.width = glowCanvas.height = 128;
const glowCtx = glowCanvas.getContext('2d');
// Outer soft halo
const halo = glowCtx.createRadialGradient(64,64,0,64,64,64);
halo.addColorStop(0.0,  'rgba(255,255,255,1.0)');
halo.addColorStop(0.05, 'rgba(255,255,255,1.0)');
halo.addColorStop(0.15, 'rgba(255,255,255,0.8)');
halo.addColorStop(0.3,  'rgba(255,255,255,0.4)');
halo.addColorStop(0.6,  'rgba(255,255,255,0.1)');
halo.addColorStop(1.0,  'rgba(255,255,255,0.0)');
glowCtx.fillStyle = halo;
glowCtx.fillRect(0, 0, 128, 128);
const glowTex = new THREE.CanvasTexture(glowCanvas);


// ── Stars — real bright stars with proper motion (Hipparcos catalog) ──────────
// Fields: [x,y,z, size, r,g,b, pmra_rad/yr, pmdec_rad/yr, ra_deg, dec_deg]
const STAR_DATA = [
  [1560.5,-1141.3,7762.9,3.42,0.80,0.90,1.00,6.35105922e-09,2.42406841e-09,78.6340,-8.2020],
  [167.1,1031.3,7931.5,3.25,1.00,0.50,0.30,1.20961013e-07,4.63481879e-08,88.7930,7.4070],
  [1205.0,884.8,7859.1,2.52,0.70,0.80,1.00,-3.94153523e-08,-6.24440021e-08,81.2830,6.3500],
  [828.7,-167.8,7955.2,2.49,0.80,0.90,1.00,7.22372385e-09,-5.13902502e-09,84.0530,-1.2020],
  [670.4,-271.2,7967.2,2.46,0.70,0.80,1.00,1.54655564e-08,-9.84171773e-09,85.1900,-1.9430],
  [974.8,-41.7,7940.3,2.16,0.70,0.80,1.00,3.10280756e-09,-3.34521440e-09,83.0010,-0.2990],
  [421.1,-1343.8,7875.1,2.26,0.80,0.90,1.00,7.51461206e-09,-6.93283564e-09,86.9390,-9.6700],
  [843.1,1380.1,7834.8,1.47,0.80,0.90,1.00,6.64194743e-09,-4.02395355e-09,83.8580,9.9340],
  [-3665.2,7047.2,950.6,2.41,1.00,0.70,0.40,-6.50183628e-07,-1.71914931e-07,165.4600,61.7510],
  [-4287.4,6662.0,1112.0,2.10,1.00,1.00,1.00,-3.95898852e-07,-1.59358257e-07,165.4600,56.3820],
  [-4735.0,6447.0,127.5,2.04,1.00,1.00,1.00,5.22435223e-07,5.33779863e-08,178.4580,53.6950],
  [-4343.4,6711.9,-292.8,1.51,1.00,1.00,1.00,3.94250485e-07,1.64739689e-07,183.8560,57.0330],
  [-4354.3,6629.2,-1045.9,2.44,0.80,0.90,1.00,5.41730807e-07,-4.35847499e-08,193.5070,55.9600],
  [-4292.3,6547.3,-1646.0,2.16,1.00,1.00,1.00,5.83473265e-07,-8.21274376e-08,200.9810,54.9260],
  [-4651.7,6066.3,-2358.4,2.39,0.80,0.90,1.00,-5.87739626e-07,-6.78739154e-08,206.8850,49.3130],
  [-4276.3,6552.3,-1667.8,1.11,1.00,1.00,1.00,5.83521747e-07,-7.91700741e-08,201.3060,54.9880],
  [4342.5,6673.9,775.6,2.16,1.00,0.70,0.40,2.44152170e-07,-1.55964561e-07,10.1270,56.5370],
  [4099.0,6868.1,164.3,2.13,1.00,1.00,0.85,2.53804810e-06,-8.71549555e-07,2.2950,59.1500],
  [3793.8,6977.7,958.4,2.21,0.80,0.90,1.00,1.24354709e-07,-1.85198826e-08,14.1770,60.7170],
  [3696.4,6944.6,1452.6,1.90,1.00,1.00,1.00,1.45991944e-06,-2.54721108e-07,21.4540,60.2350],
  [3115.4,7170.0,1698.5,1.49,0.80,0.90,1.00,-2.06773035e-07,-7.77156331e-08,28.5990,63.6700],
  [-6916.0,1658.8,3662.9,2.68,0.80,0.90,1.00,-1.20912532e-06,2.38043517e-08,152.0930,11.9670],
  [-7733.8,2012.8,369.5,2.22,1.00,1.00,1.00,-2.41931723e-06,-5.51621006e-07,177.2650,14.5720],
  [-6819.6,2715.4,3181.1,2.31,1.00,0.70,0.40,1.50665548e-06,-7.41183156e-07,154.9930,19.8420],
  [-7342.5,2804.8,1490.2,1.96,1.00,1.00,1.00,-9.99685810e-08,-6.63855374e-07,168.5270,20.5240],
  [-7621.3,2128.5,1177.2,1.50,1.00,1.00,1.00,-3.82808883e-07,-1.56497856e-07,171.2190,15.4300],
  [-2758.5,-3561.1,-6611.3,2.86,1.00,0.50,0.30,-4.92570700e-08,-1.12525255e-07,247.3520,-26.4320],
  [-733.1,-4826.0,-6338.2,2.53,0.70,0.80,1.00,-4.31484176e-08,-1.45201697e-07,263.4020,-37.1030],
  [-578.1,-5455.7,-5822.5,2.38,1.00,1.00,0.85,-1.75987366e-08,-1.22076085e-07,264.3300,-42.9970],
  [-3683.0,-3077.2,-6400.5,2.13,0.80,0.90,1.00,-4.71723712e-08,-1.55382785e-07,240.0830,-22.6220],
  [-3851.7,-2710.7,-6466.6,1.96,0.80,0.90,1.00,-2.86524886e-08,-1.21009495e-07,239.2210,-19.8060],
  [-570.2,-4847.5,-6338.5,1.89,0.80,0.90,1.00,-2.23499107e-08,-8.99329378e-08,264.8600,-37.2960],
  [-320.1,-5390.5,-5902.5,2.07,0.80,0.90,1.00,-2.57920878e-08,-9.63809598e-08,266.8960,-42.3620],
  [-3132.1,3759.0,6329.3,2.82,1.00,0.70,0.40,-3.03343072e-06,-2.22771886e-07,116.3290,28.0260],
  [-2724.8,4226.2,6222.1,2.55,1.00,1.00,1.00,-9.28175792e-07,-7.03900984e-07,113.6500,31.8890],
  [-1257.2,2258.6,7570.9,2.34,1.00,1.00,1.00,1.29930067e-08,-3.23322244e-07,99.4280,16.3990],
  [-2541.0,2994.5,6969.7,1.38,1.00,1.00,1.00,-2.16614753e-07,-6.61770675e-08,110.0310,21.9820],
  [-1379.9,3397.5,7110.1,1.71,1.00,0.70,0.40,1.06174196e-08,-1.91016590e-08,100.9830,25.1310],
  [-2067.9,2810.8,7198.8,1.23,1.00,1.00,1.00,-1.50292241e-09,-2.41437213e-08,106.0270,20.5700],
  [2751.3,2273.3,7159.8,2.99,1.00,0.50,0.30,3.04366029e-07,-9.18043187e-07,68.9800,16.5090],
  [1029.3,3830.5,6947.5,2.51,0.80,0.90,1.00,1.12864625e-07,-8.44642395e-07,81.5730,28.6080],
  [3947.6,3260.7,6146.9,1.33,0.80,0.90,1.00,8.57635402e-08,-2.17293492e-07,57.2910,24.0530],
  [4059.9,3268.3,6069.2,1.28,0.80,0.90,1.00,1.01083653e-07,-2.20056930e-07,56.2200,24.1130],
  [-1499.6,-2301.0,7513.7,4.38,1.00,1.00,1.00,-2.64713118e-06,-5.92995006e-06,101.2870,-16.7160],
  [-1770.8,-3875.1,6771.1,2.60,0.70,0.80,1.00,1.27505998e-08,1.11022333e-08,104.6560,-28.9720],
  [-2106.8,-3556.2,6849.4,2.40,1.00,0.90,0.60,-1.49322614e-08,1.91501404e-08,107.0970,-26.3930],
  [-752.6,-2466.3,7573.0,2.31,0.70,0.80,1.00,-1.65806279e-08,-1.35747831e-09,95.6750,-17.9560],
  [-2502.8,-3915.4,6511.9,2.03,0.80,0.90,1.00,-1.69199975e-08,1.08598265e-08,111.0240,-29.3030],
  [-3344.8,728.5,7230.6,3.30,1.00,1.00,0.85,-3.46443008e-06,-5.02654825e-06,114.8250,5.2250],
  [-2938.4,1153.3,7350.9,1.77,0.80,0.90,1.00,-2.51666782e-07,-1.93392177e-07,111.7880,8.2890],
  [-6270.3,2628.6,-4215.9,3.53,1.00,0.70,0.40,-5.30090431e-06,-9.69336474e-06,213.9150,19.1820],
  [-5355.9,3641.1,-4696.5,2.09,1.00,0.90,0.60,-2.94572793e-07,-7.70853753e-08,221.2470,27.0740],
  [-6660.3,2524.9,-3642.1,1.89,1.00,1.00,0.85,-3.68453550e-06,-1.68797579e-06,208.6710,18.3980],
  [-4945.5,4959.1,-3866.5,1.68,0.80,0.90,1.00,5.74795100e-07,6.50135146e-08,218.0190,38.3080],
  [-4271.8,5184.0,-4344.9,1.41,1.00,0.90,0.60,-1.84617050e-07,-1.38802157e-07,225.4860,40.3910],
  [-7312.7,-1548.5,-2850.8,2.91,0.70,0.80,1.00,-2.06045814e-07,-1.53831381e-07,201.2980,-11.1610],
  [-7865.7,-202.3,-1445.8,1.86,1.00,1.00,0.85,-1.42583704e-06,2.53848443e-07,190.4150,-1.4490],
  [-7566.8,1520.9,-2104.7,1.80,1.00,0.90,0.60,-1.32596542e-06,9.87080655e-08,195.5440,10.9590],
  [-7896.2,474.0,-1193.6,1.47,1.00,0.70,0.40,-2.94087979e-07,-7.85592089e-07,188.5960,3.3970],
  [1000.8,5011.1,-6155.3,3.48,1.00,1.00,1.00,9.74184611e-07,1.38768220e-06,279.2350,38.7840],
  [1448.4,4399.5,-6522.7,1.43,1.00,1.00,1.00,-8.38727668e-09,-6.54498469e-09,282.5200,33.3630],
  [1712.6,4320.7,-6511.4,1.56,1.00,1.00,1.00,-1.66291093e-08,-6.39954059e-09,284.7360,32.6900],
  [3673.8,1233.3,-6998.7,3.04,1.00,1.00,1.00,2.59971640e-06,1.86793863e-06,297.6960,8.8680],
  [3516.5,1473.4,-7033.0,1.87,1.00,0.50,0.30,7.50976392e-08,-1.49807427e-08,296.5650,10.6130],
  [3833.4,892.7,-6964.8,1.27,1.00,0.90,0.60,2.09245585e-07,-2.33413547e-06,298.8280,6.4070],
  [3645.2,5684.4,-4289.5,2.75,0.80,0.90,1.00,7.56309343e-09,7.51461206e-09,310.3580,45.2800],
  [3550.3,5169.7,-4966.8,2.16,1.00,0.90,0.60,-6.06017101e-09,1.74532925e-09,305.5570,40.2570],
  [4400.8,4470.1,-4965.0,2.02,1.00,0.70,0.40,-1.87622895e-08,-2.34261971e-07,311.5530,33.9700],
  [2724.6,3750.8,-6519.8,1.65,1.00,0.70,0.40,-3.44217714e-08,-2.90888209e-08,292.6800,27.9600],
  [3550.3,5169.7,-4966.8,2.01,1.00,0.90,0.60,6.39954059e-09,3.39369577e-10,305.5570,40.2570],
  [3239.8,6115.9,4012.4,2.43,1.00,1.00,0.85,1.16888579e-07,-1.26100038e-07,51.0810,49.8610],
  [4117.1,5243.9,4421.6,2.25,1.00,1.00,1.00,1.15870470e-08,-6.98131701e-09,47.0420,40.9570],
  [3198.7,5143.4,5226.3,1.80,0.80,0.90,1.00,1.13834252e-07,-4.39726009e-08,58.5320,40.0100],
  [1044.0,5754.5,5458.5,3.45,1.00,0.90,0.60,3.66131292e-07,-2.07078468e-06,79.1720,45.9980],
  [11.7,5651.6,5662.1,2.36,1.00,1.00,1.00,-2.73483398e-07,-4.26636039e-09,89.8820,44.9470],
  [1473.4,4838.2,6198.4,1.71,1.00,1.00,1.00,-3.53913987e-09,-1.19845942e-07,76.6290,37.2130],
  [695.0,-4517.9,-6565.5,2.43,1.00,0.90,0.60,-1.91113553e-07,-6.02138592e-07,276.0430,-34.3840],
  [1712.7,-3544.2,-6964.6,2.27,0.80,0.90,1.00,6.72436576e-08,-2.55254403e-07,283.8160,-26.2970],
  [1871.6,-3985.5,-6679.3,1.88,1.00,1.00,1.00,1.03022907e-07,-9.89504723e-08,285.6530,-29.8800],
  [533.3,-3979.2,-6919.7,1.88,0.80,0.90,1.00,-7.47097883e-08,-1.08889153e-07,274.4070,-29.8280],
  [183.1,-3433.6,-7223.3,1.81,1.00,1.00,0.85,-2.46770164e-08,-1.10489038e-07,271.4520,-25.4170],
  [-2990.9,-6985.8,-2500.7,3.67,1.00,0.90,0.60,-1.78375074e-05,2.29641696e-06,219.8990,-60.8350],
  [-3391.5,-6954.1,-2034.3,3.13,0.70,0.80,1.00,-1.61297512e-07,-1.12282849e-07,210.9560,-60.3730],
  [-5482.3,-4744.0,-3382.1,2.26,1.00,0.70,0.40,-2.51758896e-06,-2.51404982e-06,211.6710,-36.3700],
  [-3595.2,-7134.3,-419.2,3.04,0.70,0.80,1.00,-1.71478599e-07,-7.14130552e-08,186.6500,-63.0990],
  [-3950.3,-6906.4,-834.6,2.75,0.70,0.80,1.00,-2.33874120e-07,-6.21531139e-08,191.9300,-59.6890],
  [-4303.8,-6717.9,-588.9,2.55,1.00,0.50,0.30,1.35456943e-07,-1.28150800e-06,187.7920,-57.1130],
  [3941.7,-6727.3,1790.5,3.22,0.80,0.90,1.00,4.26733002e-07,-1.94313323e-07,24.4290,-57.2370],
  [1797.7,-709.2,7763.1,1.83,1.00,1.00,1.00,-4.00650026e-07,-3.56338056e-07,76.9620,-5.0860],
  [6698.7,-3954.2,-1868.7,2.80,1.00,1.00,1.00,1.59610360e-06,-7.96161027e-07,344.4130,-29.6220],
  [6349.7,-776.6,-4804.0,1.76,1.00,0.90,0.60,8.85754595e-08,-4.34877872e-08,322.8900,-5.5710],
  [7026.8,-44.7,-3823.8,1.73,1.00,0.70,0.40,8.30485836e-08,-3.35491067e-08,331.4460,-0.3200],
  [6846.8,3766.0,-1714.2,2.04,1.00,0.50,0.30,9.09752873e-07,6.65988554e-07,345.9440,28.0830],
  [7496.8,2098.2,-1842.8,2.01,0.80,0.90,1.00,2.96221159e-07,-2.06336703e-07,346.1900,15.2050],
  [7707.8,2095.4,445.6,1.80,0.70,0.80,1.00,2.27862430e-08,-3.99486473e-08,3.3090,15.1840],
  [6537.6,1372.0,-4402.0,2.07,1.00,0.50,0.30,1.45541067e-07,6.69042880e-09,326.0460,9.8750],
  [6986.1,3889.6,255.8,2.26,0.80,0.90,1.00,6.57795203e-07,-7.90003893e-07,2.0970,29.0910],
  [6204.4,4659.4,1948.3,2.26,1.00,0.50,0.30,8.51284343e-07,-5.44009432e-07,17.4330,35.6210],
  [5070.8,5387.2,3043.8,2.20,1.00,0.70,0.40,2.08857734e-07,-2.48127642e-07,30.9750,42.3300],
  [6237.4,3185.3,3866.3,2.29,1.00,0.70,0.40,9.24685134e-07,-7.06712903e-07,31.7930,23.4630],
  [6562.0,2841.9,3586.6,1.92,1.00,1.00,1.00,9.65264039e-07,-5.39840034e-07,28.6600,20.8080],
  [-4233.4,3596.4,-5757.1,2.16,1.00,1.00,1.00,5.83085414e-07,-4.32114434e-07,233.6720,26.7150],
  [-2842.0,2930.7,-6880.0,1.84,1.00,0.90,0.60,-4.78850473e-07,-7.21887571e-08,247.5550,21.4900],
  [-2294.4,4192.1,-6415.8,1.81,1.00,0.90,0.60,-2.23756058e-06,1.88199823e-06,250.3220,31.6020],
  [-852.3,1739.7,-7761.9,2.25,1.00,1.00,1.00,5.23938145e-07,-1.07420167e-06,263.7340,12.5600],
  [-1654.2,-2168.2,-7520.8,2.04,1.00,1.00,1.00,2.37316297e-07,4.71529786e-07,257.5950,-15.7250],
  [-5152.0,-1304.3,-5979.6,1.93,0.80,0.90,1.00,-4.71238898e-07,-3.19977030e-07,229.2520,-9.3830],
  [-5648.6,-2210.7,-5215.9,1.86,1.00,1.00,1.00,-5.12351098e-07,-3.31612558e-07,222.7190,-16.0420],
  [-6223.5,-1204.4,4880.3,2.31,1.00,0.70,0.40,-7.16554621e-08,1.61200549e-07,141.8970,-8.6590],
  [-7609.8,-2411.2,-525.6,1.95,0.80,0.90,1.00,-7.68817536e-07,2.45315723e-08,183.9510,-17.5420],
  [-7604.9,-2274.3,-996.6,1.73,0.80,0.90,1.00,-2.32080309e-07,-6.61867637e-07,187.4660,-16.5160],
  [-2903.6,-5882.8,4578.4,2.45,0.70,0.80,1.00,-3.06402246e-08,5.10993620e-08,122.3830,-47.3370],
  [-4248.7,-5500.0,3962.1,2.17,1.00,0.50,0.30,-1.13300957e-07,7.12676111e-08,136.9990,-43.4330],
  [-505.8,-6363.4,4821.9,3.94,1.00,1.00,1.00,9.66233666e-08,1.12670699e-07,95.9880,-52.6960],
  [-2364.6,-6893.7,3299.4,2.38,1.00,0.70,0.40,-1.21203420e-07,9.11934534e-08,125.6280,-59.5090],
  [-3097.5,-6877.0,2666.8,2.15,1.00,1.00,1.00,-9.22600435e-08,5.98744896e-08,139.2730,-59.2750],
  [-3146.7,-5142.6,5258.5,2.15,0.70,0.80,1.00,-1.49516539e-07,8.13032543e-08,120.8960,-40.0030],
  [-3556.3,-3292.6,6364.8,1.88,1.00,1.00,1.00,-1.17955169e-07,1.04622792e-07,119.1940,-24.3040],
  [2604.7,-6689.1,-3531.4,2.34,0.80,0.90,1.00,3.73791348e-08,-4.17666986e-07,306.4120,-56.7350],
  [4823.5,-5847.1,-2558.4,2.46,0.80,0.90,1.00,6.18622257e-07,-7.17087916e-07,332.0580,-46.9610],
  [5159.4,-5839.9,-1810.1,2.26,1.00,0.50,0.30,6.24585465e-07,-1.59988515e-08,340.6670,-46.8850],
  [-73.7,6259.9,-4980.8,2.16,1.00,0.50,0.30,-4.21787903e-08,-1.06901417e-07,269.1520,51.4890],
  [-629.4,6329.9,-4851.4,1.83,1.00,0.90,0.60,-7.36916795e-08,5.99229710e-08,262.6080,52.3010],
  [81.0,7999.3,63.2,2.31,1.00,0.90,0.60,2.14384610e-07,-5.69171262e-08,37.9550,89.2640],
  [-1605.8,7696.1,-1480.5,2.26,1.00,0.70,0.40,-1.56546338e-07,5.77413094e-08,222.6760,74.1560],
  [-1597.1,7601.3,-1915.7,1.67,1.00,1.00,1.00,8.22244003e-08,8.40182109e-08,230.1820,71.8340],
  [2806.9,7101.6,-2385.0,2.04,1.00,1.00,1.00,7.30856624e-07,2.34019564e-07,319.6450,62.5860],
  [2102.7,7544.0,-1633.1,1.56,0.70,0.80,1.00,7.52430833e-08,4.49907096e-08,322.1650,70.5610],
  [1706.6,7814.3,-154.2,1.57,1.00,0.70,0.40,-2.25777731e-07,-4.33084061e-07,354.8370,77.6320],
];

// Constellation line pairs — indices into STAR_DATA array
const CONST_LINE_INDICES = [
  [1,2],
  [1,3],
  [2,5],
  [5,3],
  [3,4],
  [4,6],
  [6,0],
  [0,5],
  [1,7],
  [2,7],
  [8,9],
  [9,10],
  [10,11],
  [11,12],
  [12,13],
  [13,14],
  [11,8],
  [17,16],
  [16,18],
  [18,19],
  [19,20],
  [21,23],
  [23,24],
  [24,22],
  [23,25],
  [25,22],
  [30,29],
  [29,26],
  [26,32],
  [32,28],
  [28,27],
  [27,31],
  [34,33],
  [34,37],
  [37,35],
  [33,36],
  [36,38],
  [38,35],
  [39,40],
  [39,41],
  [41,42],
  [42,43],
  [44,47],
  [44,46],
  [46,45],
  [46,48],
  [51,52],
  [51,53],
  [52,54],
  [54,55],
  [55,53],
  [56,57],
  [57,58],
  [58,59],
  [60,61],
  [61,62],
  [62,60],
  [63,64],
  [63,65],
  [66,67],
  [67,68],
  [67,69],
  [66,70],
  [71,72],
  [71,73],
  [74,75],
  [75,76],
  [76,40],
  [40,74],
  [81,80],
  [80,77],
  [77,79],
  [79,78],
  [78,80],
  [124,125],
  [125,126],
  [127,128],
  [128,129],
  [129,127],
  [97,98],
  [98,99],
  [91,92],
  [100,101],
  [110,111],
  [88,89],
  [120,121],
  [107,108],
  [94,93],
  [93,97],
  [97,95],
  [95,94],
  [94,96],
  [85,87],
  [86,87],
  [82,83],
  [83,84],
  [122,123],
  [102,55],
  [105,106],
];

const CONSTELLATION_NAMES = {
  And: 'Andromeda',
  Ari: 'Aries',
  Aqr: 'Aquarius',
  Aur: 'Auriga',
  Boo: 'Bootes',
  Car: 'Carina',
  Cas: 'Cassiopeia',
  CMa: 'Canis Major',
  Cen: 'Centaurus',
  Cep: 'Cepheus',
  CrB: 'Corona Borealis',
  Cru: 'Crux',
  Crv: 'Corvus',
  Cyg: 'Cygnus',
  Del: 'Delphinus',
  Eri: 'Eridanus',
  Gem: 'Gemini',
  Gru: 'Grus',
  Her: 'Hercules',
  Hya: 'Hydra',
  Leo: 'Leo',
  Lib: 'Libra',
  Lyr: 'Lyra',
  Oph: 'Ophiuchus',
  Ori: 'Orion',
  Pav: 'Pavo',
  Peg: 'Pegasus',
  Per: 'Perseus',
  PsA: 'Piscis Austrinus',
  Sco: 'Scorpius',
  Sgr: 'Sagittarius',
  Tau: 'Taurus',
  UMa: 'Ursa Major',
  UMi: 'Ursa Minor',
  Vir: 'Virgo',
};

const STAR_METADATA = {
  0: { name:'Rigel', constellation:'Orion' },
  1: { name:'Betelgeuse', constellation:'Orion' },
  2: { name:'Bellatrix', constellation:'Orion' },
  3: { name:'Alnilam', constellation:'Orion' },
  4: { name:'Alnitak', constellation:'Orion' },
  5: { name:'Mintaka', constellation:'Orion' },
  6: { name:'Saiph', constellation:'Orion' },
  7: { name:'Meissa', constellation:'Orion' },
  8: { name:'Dubhe', constellation:'Ursa Major' },
  9: { name:'Merak', constellation:'Ursa Major' },
  10: { name:'Phecda', constellation:'Ursa Major' },
  11: { name:'Megrez', constellation:'Ursa Major' },
  12: { name:'Alioth', constellation:'Ursa Major' },
  13: { name:'Mizar', constellation:'Ursa Major' },
  14: { name:'Alkaid', constellation:'Ursa Major' },
  15: { name:'Alcor', constellation:'Ursa Major' },
  16: { name:'Schedar', constellation:'Cassiopeia' },
  17: { name:'Caph', constellation:'Cassiopeia' },
  18: { name:'Navi', constellation:'Cassiopeia' },
  19: { name:'Ruchbah', constellation:'Cassiopeia' },
  20: { name:'Segin', constellation:'Cassiopeia' },
  21: { name:'Regulus', constellation:'Leo' },
  22: { name:'Denebola', constellation:'Leo' },
  23: { name:'Algieba', constellation:'Leo' },
  24: { name:'Zosma', constellation:'Leo' },
  25: { name:'Chertan', constellation:'Leo' },
  26: { name:'Antares', constellation:'Scorpius' },
  27: { name:'Shaula', constellation:'Scorpius' },
  28: { name:'Sargas', constellation:'Scorpius' },
  29: { name:'Lesath', constellation:'Scorpius' },
  30: { name:'Dschubba', constellation:'Scorpius' },
  31: { name:'Acrab', constellation:'Scorpius' },
  32: { name:'Mu Scorpii', constellation:'Scorpius' },
  33: { name:'Castor', constellation:'Gemini' },
  34: { name:'Pollux', constellation:'Gemini' },
  35: { name:'Alhena', constellation:'Gemini' },
  36: { name:'Wasat', constellation:'Gemini' },
  37: { name:'Mebsuta', constellation:'Gemini' },
  38: { name:'Mekbuda', constellation:'Gemini' },
  39: { name:'Aldebaran', constellation:'Taurus' },
  40: { name:'Elnath', constellation:'Taurus' },
  41: { name:'Ain', constellation:'Taurus' },
  42: { name:'Theta2 Tauri', constellation:'Taurus' },
  43: { name:'Theta1 Tauri', constellation:'Taurus' },
  44: { name:'Sirius', constellation:'Canis Major' },
  45: { name:'Adhara', constellation:'Canis Major' },
  46: { name:'Wezen', constellation:'Canis Major' },
  47: { name:'Mirzam', constellation:'Canis Major' },
  48: { name:'Aludra', constellation:'Canis Major' },
  51: { name:'Arcturus', constellation:'Bootes' },
  52: { name:'Izar', constellation:'Bootes' },
  53: { name:'Muphrid', constellation:'Bootes' },
  54: { name:'Seginus', constellation:'Bootes' },
  55: { name:'Nekkar', constellation:'Bootes' },
  56: { name:'Spica', constellation:'Virgo' },
  57: { name:'Porrima', constellation:'Virgo' },
  58: { name:'Vindemiatrix', constellation:'Virgo' },
  59: { name:'Zaniah', constellation:'Virgo' },
  60: { name:'Vega', constellation:'Lyra' },
  61: { name:'Sheliak', constellation:'Lyra' },
  62: { name:'Sulafat', constellation:'Lyra' },
  63: { name:'Rotanev', constellation:'Delphinus' },
  64: { name:'Sualocin', constellation:'Delphinus' },
  65: { name:'Delta Delphini', constellation:'Delphinus' },
  66: { name:'Deneb', constellation:'Cygnus' },
  67: { name:'Sadr', constellation:'Cygnus' },
  68: { name:'Albireo', constellation:'Cygnus' },
  69: { name:'Delta Cygni', constellation:'Cygnus' },
  70: { name:'Sadr', constellation:'Cygnus' },
  71: { name:'Mirfak', constellation:'Perseus' },
  72: { name:'Algol', constellation:'Perseus' },
  73: { name:'Atik', constellation:'Perseus' },
  74: { name:'Capella', constellation:'Auriga' },
  75: { name:'Menkalinan', constellation:'Auriga' },
  76: { name:'Elnath', constellation:'Auriga' },
  77: { name:'Kaus Australis', constellation:'Sagittarius' },
  78: { name:'Kaus Media', constellation:'Sagittarius' },
  79: { name:'Ascella', constellation:'Sagittarius' },
  80: { name:'Nunki', constellation:'Sagittarius' },
  81: { name:'Phi Sagittarii', constellation:'Sagittarius' },
  82: { name:'Alpha Centauri', constellation:'Centaurus' },
  83: { name:'Hadar', constellation:'Centaurus' },
  84: { name:'Menkent', constellation:'Centaurus' },
  85: { name:'Acrux', constellation:'Crux' },
  86: { name:'Mimosa', constellation:'Crux' },
  87: { name:'Gacrux', constellation:'Crux' },
  88: { name:'Achernar', constellation:'Eridanus' },
  89: { name:'Cursa', constellation:'Eridanus' },
  90: { name:'Fomalhaut', constellation:'Piscis Austrinus' },
  91: { name:'Sadalsuud', constellation:'Aquarius' },
  92: { name:'Sadalmelik', constellation:'Aquarius' },
  93: { name:'Scheat', constellation:'Pegasus' },
  94: { name:'Markab', constellation:'Pegasus' },
  95: { name:'Algenib', constellation:'Pegasus' },
  96: { name:'Enif', constellation:'Pegasus' },
  97: { name:'Alpheratz', constellation:'Andromeda' },
  98: { name:'Mirach', constellation:'Andromeda' },
  99: { name:'Almach', constellation:'Andromeda' },
  100: { name:'Hamal', constellation:'Aries' },
  101: { name:'Sheratan', constellation:'Aries' },
  102: { name:'Alphecca', constellation:'Corona Borealis' },
  103: { name:'Kornephoros', constellation:'Hercules' },
  104: { name:'Pi Herculis', constellation:'Hercules' },
  105: { name:'Rasalhague', constellation:'Ophiuchus' },
  106: { name:'Sabik', constellation:'Ophiuchus' },
  107: { name:'Zubeneschamali', constellation:'Libra' },
  108: { name:'Zubenelgenubi', constellation:'Libra' },
  109: { name:'Alphard', constellation:'Hydra' },
  110: { name:'Gienah', constellation:'Corvus' },
  111: { name:'Algorab', constellation:'Corvus' },
  114: { name:'Canopus', constellation:'Carina' },
  119: { name:'Peacock', constellation:'Pavo' },
  120: { name:'Alnair', constellation:'Grus' },
  121: { name:'Beta Gruis', constellation:'Grus' },
  122: { name:'Rasalgethi', constellation:'Hercules' },
  123: { name:'Eta Herculis', constellation:'Hercules' },
  124: { name:'Polaris', constellation:'Ursa Minor' },
  125: { name:'Kochab', constellation:'Ursa Minor' },
  126: { name:'Pherkad', constellation:'Ursa Minor' },
  127: { name:'Alderamin', constellation:'Cepheus' },
  128: { name:'Alfirk', constellation:'Cepheus' },
  129: { name:'Errai', constellation:'Cepheus' },
};

const STAR_HOVER_RADIUS_PX = 14;
const CONSTELLATION_HOVER_RADIUS_PX = 10;

const CONSTELLATION_GROUPS = [
  { key:'Orion', indices:[0,1,2,3,4,5,6,7] },
  { key:'Ursa Major', indices:[8,9,10,11,12,13,14,15] },
  { key:'Cassiopeia', indices:[16,17,18,19,20] },
  { key:'Leo', indices:[21,22,23,24,25] },
  { key:'Scorpius', indices:[26,27,28,29,30,31,32] },
  { key:'Gemini', indices:[33,34,35,36,37,38] },
  { key:'Taurus', indices:[39,40,41,42,43] },
  { key:'Canis Major', indices:[44,45,46,47,48] },
  { key:'Bootes', indices:[51,52,53,54,55] },
  { key:'Virgo', indices:[56,57,58,59] },
  { key:'Lyra', indices:[60,61,62] },
  { key:'Delphinus', indices:[63,64,65] },
  { key:'Cygnus', indices:[66,67,68,69,70] },
  { key:'Perseus', indices:[71,72,73] },
  { key:'Auriga', indices:[74,75,76] },
  { key:'Sagittarius', indices:[77,78,79,80,81] },
  { key:'Centaurus', indices:[82,83,84] },
  { key:'Crux', indices:[85,86,87] },
  { key:'Eridanus', indices:[88,89] },
  { key:'Piscis Austrinus', indices:[90] },
  { key:'Aquarius', indices:[91,92] },
  { key:'Pegasus', indices:[93,94,95,96] },
  { key:'Andromeda', indices:[97,98,99] },
  { key:'Aries', indices:[100,101] },
  { key:'Corona Borealis', indices:[102] },
  { key:'Hercules', indices:[103,104,122,123] },
  { key:'Ophiuchus', indices:[105,106] },
  { key:'Libra', indices:[107,108] },
  { key:'Hydra', indices:[109] },
  { key:'Corvus', indices:[110,111] },
  { key:'Carina', indices:[114] },
  { key:'Pavo', indices:[119] },
  { key:'Grus', indices:[120,121] },
  { key:'Ursa Minor', indices:[124,125,126] },
  { key:'Cepheus', indices:[127,128,129] },
];

function getStarTooltipText(starIndex) {
  const meta = STAR_METADATA[starIndex];
  if (!meta) return '';
  return `${meta.name} • ${meta.constellation}`;
}

function getConstellationTooltipText(lineIndex) {
  const [a, b] = CONST_LINE_INDICES[lineIndex];
  const aConstellation = STAR_METADATA[a]?.constellation;
  const bConstellation = STAR_METADATA[b]?.constellation;
  if (aConstellation && bConstellation) {
    return aConstellation === bConstellation
      ? `${aConstellation} constellation`
      : `${aConstellation} / ${bConstellation}`;
  }
  return aConstellation ? `${aConstellation} constellation` : bConstellation ? `${bConstellation} constellation` : '';
}

const constellationLabelsEl = document.getElementById('constellation-labels');
const constellationLabelEls = new Map();
for (const group of CONSTELLATION_GROUPS) {
  const labelEl = document.createElement('div');
  labelEl.className = 'constellation-label';
  labelEl.textContent = group.key;
  labelEl.style.display = 'none';
  constellationLabelsEl.appendChild(labelEl);
  constellationLabelEls.set(group.key, labelEl);
}

// Build star geometry
const N_STARS = STAR_DATA.length;
const starPosBuf = new Float32Array(N_STARS * 3);
const starColBuf = new Float32Array(N_STARS * 3);
const starSizBuf = new Float32Array(N_STARS);
for (let i=0; i<N_STARS; i++) {
  const s = STAR_DATA[i];
  starPosBuf[i*3]=s[0]; starPosBuf[i*3+1]=s[1]; starPosBuf[i*3+2]=s[2];
  starColBuf[i*3]=s[4]; starColBuf[i*3+1]=s[5]; starColBuf[i*3+2]=s[6];
  starSizBuf[i]=s[3];
}
const starGeo = new THREE.BufferGeometry();
starGeo.setAttribute('position', new THREE.BufferAttribute(starPosBuf,3));
starGeo.setAttribute('color',    new THREE.BufferAttribute(starColBuf,3));
// Use simple PointsMaterial — ShaderMaterial with custom size attr unreliable in r128
// Sky group — stars and constellations tilt with the ecliptic in vortex/top mode
const skyGroup = new THREE.Group();
scene.add(skyGroup);

const starMesh = new THREE.Points(starGeo,
  new THREE.PointsMaterial({vertexColors:true, size:2.0, sizeAttenuation:false, transparent:true, opacity:0.95})
);
skyGroup.add(starMesh);

// Constellation line geometry — indices into starPosBuf, rebuilt each frame
const N_LINES = CONST_LINE_INDICES.length;
const constLinePosBuf = new Float32Array(N_LINES * 6);
const constLineGeo = new THREE.BufferGeometry();
constLineGeo.setAttribute('position', new THREE.BufferAttribute(constLinePosBuf,3));
const constLineMesh = new THREE.LineSegments(constLineGeo,
  new THREE.LineBasicMaterial({color:0x334466, transparent:true, opacity:0.35})
);
skyGroup.add(constLineMesh);

// Faint background stars (random, no proper motion)
{
  const N=6000, pos=new Float32Array(N*3), col=new Float32Array(N*3);
  for (let i=0;i<N;i++){
    const th=Math.random()*Math.PI*2, ph=Math.acos(2*Math.random()-1);
    const r=450000+Math.random()*100000;
    pos[i*3]=r*Math.sin(ph)*Math.cos(th);
    pos[i*3+1]=r*Math.sin(ph)*Math.sin(th)*0.35;
    pos[i*3+2]=r*Math.cos(ph);
    const v=0.12+Math.random()*0.22;
    col[i*3]=v; col[i*3+1]=v; col[i*3+2]=v+Math.random()*0.08;
  }
  const g=new THREE.BufferGeometry();
  g.setAttribute('position',new THREE.BufferAttribute(pos,3));
  g.setAttribute('color',new THREE.BufferAttribute(col,3));
  skyGroup.add(new THREE.Points(g, new THREE.PointsMaterial({vertexColors:true, size:0.5})));
}

// Vortex-only star streak layer: subtle directional motion cue along galactic travel.
const VORTEX_STREAK_COUNT = 900;
const vortexStreakPosBuf = new Float32Array(VORTEX_STREAK_COUNT * 6);
const vortexStreakGeo = new THREE.BufferGeometry();
vortexStreakGeo.setAttribute('position', new THREE.BufferAttribute(vortexStreakPosBuf, 3));
const vortexStreakMat = new THREE.LineBasicMaterial({
  color: 0xaabbe0,
  transparent: true,
  opacity: 0,
  depthWrite: false,
});
const vortexStreaks = new THREE.LineSegments(vortexStreakGeo, vortexStreakMat);
vortexStreaks.frustumCulled = false;
vortexStreaks.visible = false;
skyGroup.add(vortexStreaks);

for (let i = 0; i < VORTEX_STREAK_COUNT; i++) {
  const th = Math.random() * Math.PI * 2;
  const ph = Math.acos(2 * Math.random() - 1);
  const r = 430000 + Math.random() * 130000;
  const x = r * Math.sin(ph) * Math.cos(th);
  const y = r * Math.sin(ph) * Math.sin(th) * 0.35;
  const z = r * Math.cos(ph);
  const len = 900 + Math.random() * 1700;
  const drift = (Math.random() - 0.5) * 180;
  vortexStreakPosBuf[i*6]     = x;
  vortexStreakPosBuf[i*6 + 1] = y;
  vortexStreakPosBuf[i*6 + 2] = z;
  vortexStreakPosBuf[i*6 + 3] = x + drift;
  vortexStreakPosBuf[i*6 + 4] = y + drift * 0.06;
  vortexStreakPosBuf[i*6 + 5] = z - len;
}
vortexStreakGeo.attributes.position.needsUpdate = true;

// Update star positions based on simTime (proper motion)
let lastStarUpdateSimTime = Number.NaN;
const STAR_UPDATE_STEP_YEARS = 1;
function updateStarPositions() {
  if (Number.isFinite(lastStarUpdateSimTime) && Math.abs(simTime - lastStarUpdateSimTime) < STAR_UPDATE_STEP_YEARS) {
    return;
  }
  lastStarUpdateSimTime = simTime;
  const R = 500000;
  const PI = Math.PI;
  // Clamp to ±10M years — linear proper motion extrapolation stays reasonable at this scale
  const t = Math.max(-10000000, Math.min(10000000, simTime));
  for (let i=0; i<N_STARS; i++) {
    const s   = STAR_DATA[i];
    const ra0  = s[9]  * PI / 180;
    const dec0 = s[10] * PI / 180;
    const pmra  = s[7];
    const pmdec = s[8];
    const dec = dec0 + pmdec * t;
    const clampedDec = Math.max(-PI/2, Math.min(PI/2, dec));
    const ra  = ra0  + (pmra / Math.max(0.001, Math.cos(dec0))) * t;
    const x = R * Math.cos(clampedDec) * Math.cos(ra);
    const y = R * Math.sin(clampedDec);
    const z = R * Math.cos(clampedDec) * Math.sin(ra);
    starPosBuf[i*3]   = Number.isFinite(x) ? x : 0;
    starPosBuf[i*3+1] = Number.isFinite(y) ? y : 0;
    starPosBuf[i*3+2] = Number.isFinite(z) ? z : 0;
  }
  starGeo.attributes.position.needsUpdate = true;

  // Update constellation lines to match moved stars
  for (let i=0; i<N_LINES; i++) {
    const a = CONST_LINE_INDICES[i][0], b = CONST_LINE_INDICES[i][1];
    constLinePosBuf[i*6]   = starPosBuf[a*3];
    constLinePosBuf[i*6+1] = starPosBuf[a*3+1];
    constLinePosBuf[i*6+2] = starPosBuf[a*3+2];
    constLinePosBuf[i*6+3] = starPosBuf[b*3];
    constLinePosBuf[i*6+4] = starPosBuf[b*3+1];
    constLinePosBuf[i*6+5] = starPosBuf[b*3+2];
  }
  constLineGeo.attributes.position.needsUpdate = true;
}

// ── Procedural planet textures ────────────────────────────────────────────────
// Generated via Canvas 2D — no external URLs needed, works in any sandbox.
// Each planet gets a hand-crafted texture matching its real appearance.

function makeTex(w, h, fn) {
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  const ctx = c.getContext('2d');
  fn(ctx, w, h);
  return new THREE.CanvasTexture(c);
}

// Seeded noise helpers
function hash(n) { return (Math.sin(n) * 43758.5453) % 1; }
function noise2(x, y) {
  const ix = Math.floor(x), iy = Math.floor(y);
  const fx = x-ix, fy = y-iy;
  const ux = fx*fx*(3-2*fx), uy = fy*fy*(3-2*fy);
  const a=hash(ix+iy*57), b=hash(ix+1+iy*57), c=hash(ix+(iy+1)*57), d=hash(ix+1+(iy+1)*57);
  return a + (b-a)*ux + (c-a)*uy + (d-c)*ux*uy + (b-a-d+c)*ux*uy;
}
function fbm(x, y, oct=5) {
  let v=0, amp=0.5, freq=1, max=0;
  for(let i=0;i<oct;i++){v+=noise2(x*freq,y*freq)*amp;max+=amp;amp*=0.5;freq*=2.1;}
  return v/max;
}

function makeSunTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    for (let y=0; y<H; y++) for (let x=0; x<W; x++) {
      const n=fbm(x/80+10,y/80+10,6), m=fbm(x/40+20,y/40+20,4), v=n*0.6+m*0.4;
      ctx.fillStyle=`rgb(${Math.min(255,220+v*120)|0},${Math.min(255,100+v*140)|0},${Math.max(0,20-v*30)|0})`;
      ctx.fillRect(x,y,1,1);
    }
  });
}
function makeMercuryTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    for (let y=0;y<H;y++) for (let x=0;x<W;x++) {
      const v=0.45+fbm(x/60+5,y/60+5,6)*0.45, c=(100+v*120)|0;
      ctx.fillStyle=`rgb(${c},${(c*0.95)|0},${(c*0.88)|0})`; ctx.fillRect(x,y,1,1);
    }
    for (let i=0;i<80;i++) {
      const cx=Math.random()*W,cy=Math.random()*H,r=2+Math.random()*12;
      ctx.beginPath();ctx.arc(cx,cy,r,0,Math.PI*2);ctx.strokeStyle='rgba(60,55,50,0.5)';ctx.lineWidth=1.5;ctx.stroke();
      ctx.beginPath();ctx.arc(cx,cy,r*0.7,0,Math.PI*2);ctx.fillStyle='rgba(70,65,58,0.35)';ctx.fill();
    }
  });
}
function makeVenusTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    for (let y=0;y<H;y++) for (let x=0;x<W;x++) {
      const v=fbm(x/120+30,y/40+30,5)*0.6+fbm(x/60+40,y/30+40,3)*0.4;
      ctx.fillStyle=`rgb(${(210+v*40)|0},${(170+v*35)|0},${(80+v*20)|0})`; ctx.fillRect(x,y,1,1);
    }
  });
}
function makeEarthTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    ctx.fillStyle='#1a4a8a'; ctx.fillRect(0,0,W,H);
    const imgData=ctx.getImageData(0,0,W,H), d=imgData.data;
    for (let y=0;y<H;y++) for (let x=0;x<W;x++) {
      const land=fbm(x/W*4+1,y/H*4+1,6)*0.65+fbm(x/W*8+5,y/H*8+5,4)*0.35;
      const lat=Math.abs(y/H-0.5)*2, idx=(y*W+x)*4;
      if (land>0.52) {
        const elev=(land-0.52)/0.48;
        if (lat>0.82||elev>0.88){d[idx]=240;d[idx+1]=245;d[idx+2]=250;}
        else if (elev>0.68){d[idx]=120;d[idx+1]=100;d[idx+2]=75;}
        else if (lat<0.25&&elev<0.4){d[idx]=34;d[idx+1]=100;d[idx+2]=34;}
        else{d[idx]=60+elev*80|0;d[idx+1]=120+elev*40|0;d[idx+2]=40+elev*20|0;}
      } else {
        const depth=1-(land/0.52);
        d[idx]=(15+depth*10)|0;d[idx+1]=(60+depth*30)|0;d[idx+2]=(120+depth*40)|0;
      }
      d[idx+3]=255;
    }
    ctx.putImageData(imgData,0,0);
    ctx.globalAlpha=0.35;
    for (let y=0;y<H;y++) for (let x=0;x<W;x+=2) {
      if(fbm(x/50+60,y/25+60,4)>0.62){ctx.fillStyle='rgba(255,255,255,0.8)';ctx.fillRect(x,y,2,1);}
    }
    ctx.globalAlpha=1;
  });
}
function makeMoonTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    const imgData = ctx.getImageData(0, 0, W, H), data = imgData.data;
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const base = fbm(x/55 + 140, y/55 + 140, 6);
      const mare = fbm(x/24 + 210, y/24 + 210, 4);
      const ridge = fbm(x/110 + 70, y/110 + 70, 3);
      let tone = 140 + base * 78;
      if (mare > 0.57) tone -= 34 + (mare - 0.57) * 80;
      tone += (ridge - 0.5) * 18;
      const idx = (y * W + x) * 4;
      data[idx] = tone;
      data[idx + 1] = tone;
      data[idx + 2] = Math.max(0, tone - 6);
      data[idx + 3] = 255;
    }
    ctx.putImageData(imgData, 0, 0);

    for (let i = 0; i < 220; i++) {
      const cx = Math.random() * W;
      const cy = Math.random() * H;
      const radius = 2 + Math.random() * 16;
      const crater = ctx.createRadialGradient(cx, cy, 0, cx, cy, radius);
      crater.addColorStop(0.0, 'rgba(215,215,215,0.12)');
      crater.addColorStop(0.55, 'rgba(110,110,110,0.08)');
      crater.addColorStop(0.72, 'rgba(70,70,70,0.22)');
      crater.addColorStop(1.0, 'rgba(0,0,0,0.0)');
      ctx.fillStyle = crater;
      ctx.beginPath();
      ctx.arc(cx, cy, radius, 0, Math.PI * 2);
      ctx.fill();

      ctx.strokeStyle = 'rgba(230,230,230,0.10)';
      ctx.lineWidth = Math.max(0.6, radius * 0.08);
      ctx.beginPath();
      ctx.arc(cx - radius * 0.08, cy - radius * 0.08, radius * 0.82, 0, Math.PI * 2);
      ctx.stroke();
    }
  });
}
function makeMarsTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    const imgData=ctx.getImageData(0,0,W,H),d=imgData.data;
    for (let y=0;y<H;y++) for (let x=0;x<W;x++) {
      const v=fbm(x/80+70,y/80+70,6)*0.7+fbm(x/30+80,y/30+80,3)*0.3, idx=(y*W+x)*4;
      d[idx]=(150+v*90)|0;d[idx+1]=(55+v*40)|0;d[idx+2]=(30+v*20)|0;d[idx+3]=255;
    }
    ctx.putImageData(imgData,0,0);
    let g=ctx.createRadialGradient(W/2,0,0,W/2,0,H*0.2);
    g.addColorStop(0,'rgba(240,235,230,0.9)');g.addColorStop(1,'rgba(240,235,230,0)');
    ctx.fillStyle=g;ctx.fillRect(0,0,W,H*0.22);
    g=ctx.createRadialGradient(W/2,H,0,W/2,H,H*0.15);
    g.addColorStop(0,'rgba(240,235,230,0.8)');g.addColorStop(1,'rgba(240,235,230,0)');
    ctx.fillStyle=g;ctx.fillRect(0,H*0.82,W,H*0.18);
    ctx.fillStyle='rgba(80,30,20,0.3)';ctx.fillRect(W*0.35,H*0.42,W*0.3,H*0.06);
  });
}
function makeJupiterTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    for (const b of [{y:0.00,h:0.08,c:'#C9A87A'},{y:0.08,h:0.06,c:'#8B5E3C'},{y:0.14,h:0.10,c:'#D4AA80'},{y:0.24,h:0.05,c:'#9B7048'},{y:0.29,h:0.12,c:'#E0C090'},{y:0.41,h:0.06,c:'#8B6040'},{y:0.47,h:0.12,c:'#D4A870'},{y:0.59,h:0.05,c:'#9B7050'},{y:0.64,h:0.10,c:'#C9A070'},{y:0.74,h:0.06,c:'#8B5838'},{y:0.80,h:0.10,c:'#D4A878'},{y:0.90,h:0.10,c:'#C09060'}])
      {ctx.fillStyle=b.c;ctx.fillRect(0,b.y*H,W,b.h*H+1);}
    for (let y=0;y<H;y++) for (let x=0;x<W;x+=3) {
      const n=fbm(x/30+90,y/15+90,3)-0.5;
      if(Math.abs(n)>0.15){ctx.fillStyle=`rgba(${n>0?220:80},${n>0?160:80},${n>0?80:40},0.12)`;ctx.fillRect(x,y,3,1);}
    }
    ctx.save();ctx.translate(W*0.3,H*0.62);ctx.scale(2.2,1);
    const grs=ctx.createRadialGradient(0,0,0,0,0,W*0.04);
    grs.addColorStop(0,'rgba(180,60,40,0.85)');grs.addColorStop(0.5,'rgba(160,70,50,0.6)');grs.addColorStop(1,'rgba(140,80,50,0)');
    ctx.fillStyle=grs;ctx.beginPath();ctx.arc(0,0,W*0.04,0,Math.PI*2);ctx.fill();ctx.restore();
  });
}
function makeSaturnTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    for (const b of [{y:0.00,h:0.12,c:'#E8D8A0'},{y:0.12,h:0.08,c:'#C8B070'},{y:0.20,h:0.15,c:'#F0E0A8'},{y:0.35,h:0.06,c:'#C0A060'},{y:0.41,h:0.18,c:'#ECD898'},{y:0.59,h:0.07,c:'#C8B068'},{y:0.66,h:0.14,c:'#E8D898'},{y:0.80,h:0.20,c:'#D4C080'}])
      {ctx.fillStyle=b.c;ctx.fillRect(0,b.y*H,W,b.h*H+1);}
    for (let y=0;y<H;y++) for (let x=0;x<W;x+=4) {
      const n=(fbm(x/40+100,y/12+100,3)-0.5)*0.2;
      if(Math.abs(n)>0.08){ctx.fillStyle='rgba(200,170,80,0.1)';ctx.fillRect(x,y,4,1);}
    }
  });
}
function makeSaturnRingTex() {
  // 2D radial gradient — matches Three.js RingGeometry UV (center=inner, edge=outer).
  // Linear gradients cause cross-banding because UVs are circular, not linear.
  const S = 512;
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = S;
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, S, S);
  const cx = S/2, cy = S/2, R = S/2;
  const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, R);
  g.addColorStop(0.000, 'rgba(180,160,110,0.00)'); // D ring inner (transparent)
  g.addColorStop(0.020, 'rgba(185,165,112,0.08)');
  g.addColorStop(0.120, 'rgba(190,168,115,0.13)'); // D ring outer
  g.addColorStop(0.125, 'rgba(195,170,118,0.22)'); // C ring start
  g.addColorStop(0.180, 'rgba(208,182,125,0.32)');
  g.addColorStop(0.230, 'rgba(200,174,120,0.26)');
  g.addColorStop(0.298, 'rgba(202,177,122,0.28)'); // C ring end
  g.addColorStop(0.300, 'rgba(228,204,148,0.90)'); // B ring start (bright)
  g.addColorStop(0.340, 'rgba(240,217,160,0.96)');
  g.addColorStop(0.385, 'rgba(232,208,150,0.93)');
  g.addColorStop(0.425, 'rgba(244,220,164,0.97)');
  g.addColorStop(0.465, 'rgba(230,205,148,0.91)');
  g.addColorStop(0.512, 'rgba(237,212,157,0.94)');
  g.addColorStop(0.538, 'rgba(222,198,142,0.86)'); // B ring end
  g.addColorStop(0.542, 'rgba(50,38,25,0.20)');    // Cassini Division start
  g.addColorStop(0.548, 'rgba(30,22,14,0.06)');
  g.addColorStop(0.564, 'rgba(30,22,14,0.06)');
  g.addColorStop(0.570, 'rgba(50,38,25,0.18)');    // Cassini Division end
  g.addColorStop(0.575, 'rgba(220,195,138,0.84)'); // A ring start
  g.addColorStop(0.615, 'rgba(227,202,145,0.79)');
  g.addColorStop(0.655, 'rgba(217,192,134,0.73)');
  g.addColorStop(0.724, 'rgba(212,187,130,0.60)');
  g.addColorStop(0.728, 'rgba(70,52,35,0.18)');    // Encke gap
  g.addColorStop(0.733, 'rgba(212,187,130,0.56)');
  g.addColorStop(0.760, 'rgba(207,180,124,0.50)'); // A ring end
  g.addColorStop(0.800, 'rgba(90,70,48,0.12)');    // Roche Division
  g.addColorStop(0.832, 'rgba(180,155,105,0.00)');
  g.addColorStop(0.840, 'rgba(242,222,167,0.62)'); // F ring
  g.addColorStop(0.848, 'rgba(238,218,162,0.58)');
  g.addColorStop(0.858, 'rgba(180,155,105,0.00)');
  g.addColorStop(1.000, 'rgba(180,155,105,0.00)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, S, S);
  const tex = new THREE.CanvasTexture(canvas);
  tex.needsUpdate = true;
  return tex;
}

function makeUranusRingTex() {
  // Uranus has narrow, dark rings - very different from Saturn's broad bright rings.
  // 9 main rings mapped across the radial extent (ri=1.64r to ro=2.00r).
  // Rings 6,5,4 (inner), alpha, beta, eta, gamma, delta, epsilon (outer bright).
  const S = 512;
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = S;
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, S, S);
  const cx = S/2, cy = S/2, R = S/2;
  const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, R);
  // Map: 0 = inner edge (ring 6 at 1.64r) → 1 = outer edge (epsilon at 2.00r)
  // All gaps are essentially transparent
  g.addColorStop(0.000, 'rgba(60,80,90, 0.00)');  // inner gap
  // Rings 6, 5, 4 (narrow, very dark)
  g.addColorStop(0.074, 'rgba(70,90,100, 0.55)'); // ring 6
  g.addColorStop(0.082, 'rgba(60,80,90,  0.00)');
  g.addColorStop(0.097, 'rgba(70,90,100, 0.50)'); // ring 5
  g.addColorStop(0.104, 'rgba(60,80,90,  0.00)');
  g.addColorStop(0.118, 'rgba(70,90,100, 0.48)'); // ring 4
  g.addColorStop(0.126, 'rgba(60,80,90,  0.00)');
  // gap
  g.addColorStop(0.300, 'rgba(60,80,90,  0.00)');
  // alpha ring (slightly wider)
  g.addColorStop(0.305, 'rgba(80,100,110,0.60)');
  g.addColorStop(0.325, 'rgba(80,100,110,0.58)');
  g.addColorStop(0.330, 'rgba(60,80,90,  0.00)');
  // beta ring
  g.addColorStop(0.395, 'rgba(80,100,110,0.55)');
  g.addColorStop(0.412, 'rgba(80,100,110,0.53)');
  g.addColorStop(0.418, 'rgba(60,80,90,  0.00)');
  // eta ring (faint)
  g.addColorStop(0.515, 'rgba(65,85,95,  0.35)');
  g.addColorStop(0.525, 'rgba(60,80,90,  0.00)');
  // gamma ring
  g.addColorStop(0.545, 'rgba(80,100,110,0.58)');
  g.addColorStop(0.558, 'rgba(60,80,90,  0.00)');
  // delta ring
  g.addColorStop(0.590, 'rgba(80,100,110,0.62)');
  g.addColorStop(0.605, 'rgba(60,80,90,  0.00)');
  // wide gap before epsilon
  g.addColorStop(0.700, 'rgba(60,80,90,  0.00)');
  // epsilon ring — brightest and widest Uranus ring
  g.addColorStop(0.940, 'rgba(60,80,90,  0.00)');
  g.addColorStop(0.950, 'rgba(100,125,140,0.85)');
  g.addColorStop(0.960, 'rgba(115,140,155,0.90)');
  g.addColorStop(0.970, 'rgba(105,130,145,0.85)');
  g.addColorStop(0.980, 'rgba(60,80,90,  0.00)');
  g.addColorStop(1.000, 'rgba(60,80,90,  0.00)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, S, S);
  const tex = new THREE.CanvasTexture(canvas);
  tex.needsUpdate = true;
  return tex;
}
function makeUranusTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    const g=ctx.createLinearGradient(0,0,0,H);
    g.addColorStop(0,'#5ACDCD');g.addColorStop(0.5,'#7DDDCC');g.addColorStop(1,'#5ACDCD');
    ctx.fillStyle=g;ctx.fillRect(0,0,W,H);
    for (let y=0;y<H;y++) for (let x=0;x<W;x+=3) {
      const n=fbm(x/80+110,y/20+110,3)*0.15;
      ctx.fillStyle=`rgba(100,220,210,${n})`;ctx.fillRect(x,y,3,1);
    }
  });
}
function makeNeptuneTex() {
  return makeTex(512, 256, (ctx, W, H) => {
    const g=ctx.createLinearGradient(0,0,0,H);
    g.addColorStop(0,'#2233AA');g.addColorStop(0.4,'#3355CC');g.addColorStop(0.6,'#2244BB');g.addColorStop(1,'#1A2288');
    ctx.fillStyle=g;ctx.fillRect(0,0,W,H);
    for (let y=0;y<H;y++) for (let x=0;x<W;x+=2) {
      const n=fbm(x/40+120,y/20+120,4)-0.5;
      if(Math.abs(n)>0.1){ctx.fillStyle='rgba(60,80,200,0.2)';ctx.fillRect(x,y,2,1);}
    }
    ctx.save();ctx.translate(W*0.6,H*0.45);ctx.scale(1.8,1);
    const ds=ctx.createRadialGradient(0,0,0,0,0,W*0.05);
    ds.addColorStop(0,'rgba(15,20,80,0.7)');ds.addColorStop(1,'rgba(15,20,80,0)');
    ctx.fillStyle=ds;ctx.beginPath();ctx.arc(0,0,W*0.05,0,Math.PI*2);ctx.fill();ctx.restore();
  });
}

const PLANET_TEX = {
  sun:        makeSunTex(),
  moon:       makeMoonTex(),
  MERCURY:    makeMercuryTex(),
  VENUS:      makeVenusTex(),
  EARTH:      makeEarthTex(),
  MARS:       makeMarsTex(),
  JUPITER:    makeJupiterTex(),
  SATURN:     makeSaturnTex(),
  URANUS:     makeUranusTex(),
  NEPTUNE:    makeNeptuneTex(),
  saturnRing: makeSaturnRingTex(),
};

// ── Real photo textures ───────────────────────────────────────────────────────
// Loaded asynchronously from unpkg (in CSP allowlist).
// On success, swapped into the material. Procedural stays as fallback.
// three.js npm package includes these planet textures at r128.
const txLoad = new THREE.TextureLoader();

function loadSwap(urls, onLoad) {
  // Try each URL in sequence, call onLoad with the first that succeeds
  function tryNext(i) {
    if (i >= urls.length) return; // all failed — keep procedural
    txLoad.load(urls[i], tex => onLoad(tex), undefined, () => tryNext(i + 1));
  }
  tryNext(0);
}

const PHOTO_URLS = {
  sun:     ['textures/sun.jpg'],
  moon:    ['textures/moon.jpg'],
  MERCURY: ['textures/mercury.jpg'],
  VENUS:   ['textures/venus.jpg'],
  EARTH:   ['textures/earth.jpg'],
  MARS:    ['textures/mars.jpg'],
  JUPITER: ['textures/jupiter.jpg'],
  SATURN:  ['textures/saturn.jpg'],
  URANUS:  ['textures/uranus.jpg'],
  NEPTUNE: ['textures/neptune.jpg'],
  saturnRing: ['textures/saturn_ring.png'],
};

// meshes registered after planet creation, so we defer the swap
const _photoSwapQueue = []; // {urls, getMesh}
function queuePhotoSwap(key, getMeshMat) {
  if (!PHOTO_URLS[key]) return;
  loadSwap(PHOTO_URLS[key], tex => {
    const target = getMeshMat();
    if (target) { target.map = tex; target.needsUpdate = true; }
  });
}

// Shared circular dot texture for belt particles
const _dotCanvas = document.createElement('canvas');
_dotCanvas.width = _dotCanvas.height = 16;
const _dotCtx = _dotCanvas.getContext('2d');
const _dotGrad = _dotCtx.createRadialGradient(8,8,0,8,8,8);
_dotGrad.addColorStop(0,   'rgba(255,255,255,1)');
_dotGrad.addColorStop(0.5, 'rgba(255,255,255,0.8)');
_dotGrad.addColorStop(1,   'rgba(255,255,255,0)');
_dotCtx.fillStyle = _dotGrad;
_dotCtx.fillRect(0,0,16,16);
const dotTex = new THREE.CanvasTexture(_dotCanvas);

// ── Solar pivot — moves through galactic space in vortex/top mode ─────────────
// Approximate the ecliptic-to-galactic plane angle (~60.19°).
// World +Y is treated as galactic north and +Z as the travel direction within the galactic plane.
// Positive tilt makes ecliptic north lean toward the forward travel direction.
const ECLIPTIC_TILT = THREE.MathUtils.degToRad(60.19);
const solarPivot = new THREE.Group();
solarPivot.rotation.x = ECLIPTIC_TILT;
scene.add(solarPivot);

// ── Sun ───────────────────────────────────────────────────────────────────────
const sunMesh = new THREE.Mesh(
  new THREE.SphereGeometry(4.5, 32, 32),
  new THREE.MeshBasicMaterial({ map: PLANET_TEX.sun })
);
solarPivot.add(sunMesh);
const SOLAR_ORBIT_EXCLUSION_RADIUS = 4.9;
const MIN_COMET_PERIHELION = 5.2;
const COMET_ORBIT_LINE_EXCLUSION_RADIUS = 8.0;

function createOrbitLineOutsideSun(points, material, exclusionRadius = SOLAR_ORBIT_EXCLUSION_RADIUS, clipToBoundary = false) {
  const positions = [];
  const radiusSq = exclusionRadius * exclusionRadius;

  function addSegment(start, end) {
    positions.push(start.x, start.y, start.z, end.x, end.y, end.z);
  }

  for (let i = 0; i < points.length - 1; i++) {
    const start = points[i];
    const end = points[i + 1];
    const startOutside = start.lengthSq() >= radiusSq;
    const endOutside = end.lengthSq() >= radiusSq;
    const delta = new THREE.Vector3().subVectors(end, start);
    const a = delta.dot(delta);
    const b = 2 * start.dot(delta);
    const c = start.dot(start) - radiusSq;
    const roots = [];
    const disc = b * b - 4 * a * c;

    if (disc >= 0 && a > 1e-9) {
      const discSqrt = Math.sqrt(disc);
      const t1 = (-b - discSqrt) / (2 * a);
      const t2 = (-b + discSqrt) / (2 * a);
      if (t1 > 0 && t1 < 1) roots.push(t1);
      if (t2 > 0 && t2 < 1) roots.push(t2);
      roots.sort((left, right) => left - right);
    }

    if (roots.length === 0 || !clipToBoundary) {
      if (startOutside && endOutside) addSegment(start, end);
      continue;
    }

    if (startOutside) {
      addSegment(start, start.clone().lerp(end, roots[0]));
    }
    if (endOutside) {
      addSegment(start.clone().lerp(end, roots[roots.length - 1]), end);
    }
  }

  const geometry = new THREE.BufferGeometry();
  if (positions.length) {
    geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
    geometry.setDrawRange(0, positions.length / 3);
  } else {
    geometry.setAttribute('position', new THREE.Float32BufferAttribute([0, 0, 0], 3));
    geometry.setDrawRange(0, 0);
    geometry.boundingSphere = new THREE.Sphere(new THREE.Vector3(), 0);
  }
  return new THREE.LineSegments(geometry, material);
}

function getVisualCometOrbit(cd, minPerihelion = MIN_COMET_PERIHELION) {
  const minQ = Math.min(minPerihelion, Math.max(0.001, cd.sma * 0.98));
  const visualEcc = Math.min(cd.ecc, Math.max(0, 1 - minQ / cd.sma));
  return {
    ecc: visualEcc,
    b: cd.sma * Math.sqrt(Math.max(0, 1 - visualEcc * visualEcc)),
    c: cd.sma * visualEcc,
  };
}
// Queue real sun texture
queuePhotoSwap('sun', () => sunMesh.material);

// ── Glow sprite for trail points ──────────────────────────────────────────────
// Multi-layer glow: sharp bright core + wide soft halo for plasma look
const trailPointMat = new THREE.ShaderMaterial({
  uniforms: { map: { value: glowTex } },
  vertexShader: `
    attribute float size;
    attribute vec3 color;
    varying vec3 vColor;
    void main() {
      vColor = color;
      vec4 mvPos = modelViewMatrix * vec4(position, 1.0);
      gl_PointSize = size * (200.0 / -mvPos.z);
      gl_Position = projectionMatrix * mvPos;
    }
  `,
  fragmentShader: `
    uniform sampler2D map;
    varying vec3 vColor;
    void main() {
      float a = texture2D(map, gl_PointCoord).r;
      // Boost brightness with screen-like blend
      vec3 c = vColor + vColor * a * 0.5;
      gl_FragColor = vec4(min(c, vec3(1.0)), a);
    }
  `,
  transparent: true,
  depthWrite:  false,
  blending:    THREE.AdditiveBlending,
});

// ── Build planets ─────────────────────────────────────────────────────────────
const TRAIL_LEN = 1200;
const planets = [];

function createEarthOrientationMarker(radius) {
  const markerRadius = radius * 1.16;
  const group = new THREE.Group();

  const axisMat = new THREE.LineBasicMaterial({ color:0xffffff, transparent:true, opacity:0.9 });
  const equatorMat = new THREE.LineBasicMaterial({ color:0xffd166, transparent:true, opacity:0.8 });
  const meridianMat = new THREE.LineBasicMaterial({ color:0x66e0ff, transparent:true, opacity:0.72 });
  const northMat = new THREE.MeshBasicMaterial({ color:0xff5050 });
  const southMat = new THREE.MeshBasicMaterial({ color:0x4da6ff });
  const forwardMat = new THREE.MeshBasicMaterial({ color:0x5cff7a });

  const axisGeo = new THREE.BufferGeometry().setFromPoints([
    new THREE.Vector3(0, -markerRadius, 0),
    new THREE.Vector3(0, markerRadius, 0),
  ]);
  group.add(new THREE.Line(axisGeo, axisMat));

  const equatorPts = [];
  for (let i = 0; i < 64; i++) {
    const a = (i / 64) * Math.PI * 2;
    equatorPts.push(new THREE.Vector3(Math.cos(a) * markerRadius, 0, Math.sin(a) * markerRadius));
  }
  group.add(new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(equatorPts), equatorMat));

  const meridianPts = [];
  for (let i = 0; i <= 32; i++) {
    const a = -Math.PI / 2 + (i / 32) * Math.PI;
    meridianPts.push(new THREE.Vector3(0, Math.sin(a) * markerRadius, Math.cos(a) * markerRadius));
  }
  group.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(meridianPts), meridianMat));

  const markerGeo = new THREE.SphereGeometry(radius * 0.09, 12, 12);
  const north = new THREE.Mesh(markerGeo, northMat);
  north.position.y = markerRadius;
  group.add(north);

  const south = new THREE.Mesh(markerGeo, southMat);
  south.position.y = -markerRadius;
  group.add(south);

  const forward = new THREE.Mesh(new THREE.SphereGeometry(radius * 0.07, 12, 12), forwardMat);
  forward.position.z = markerRadius;
  group.add(forward);

  return group;
}

function createEarthTravelMarker(radius) {
  const markerLen = radius * 1.85;
  const shaftMat = new THREE.LineBasicMaterial({ color:0xff66ff, transparent:true, opacity:0.9 });
  const tipMat = new THREE.MeshBasicMaterial({ color:0xff66ff });
  const group = new THREE.Group();

  const shaftGeo = new THREE.BufferGeometry().setFromPoints([
    new THREE.Vector3(0, 0, 0),
    new THREE.Vector3(0, 0, markerLen),
  ]);
  group.add(new THREE.Line(shaftGeo, shaftMat));

  const tip = new THREE.Mesh(new THREE.ConeGeometry(radius * 0.11, radius * 0.28, 12), tipMat);
  tip.rotation.x = Math.PI / 2;
  tip.position.z = markerLen;
  group.add(tip);

  return group;
}

for (const d of PD) {
  const b = d.sma * Math.sqrt(1 - d.ecc*d.ecc);
  const c = d.sma * d.ecc; // focus offset

  // incGrp handles orbital inclination; lives inside solarPivot
  const incGrp = new THREE.Group();
  incGrp.rotation.x = 0; // inclination now handled in 3D position calculation
  solarPivot.add(incGrp);

  // Orbit ellipse — centred correctly on focus (Sun)
  // x = sma*cos(t) - c  →  sun is at origin of incGrp
  const oPts = [];
  for (let i=0;i<=360;i++){
    const t=(i/360)*Math.PI*2;
    // Full 3D orbit ellipse with inclination and ascending node
    const oR = d.sma*(1-d.ecc*d.ecc)/(1+d.ecc*Math.cos(t));
    const oOmPeri = ((d.omega||0)-(d.Omega||0))*Math.PI/180; // arg of perihelion from node
    const oU = oOmPeri + t; // arg of latitude
    const ocO = Math.cos((d.Omega||0)*Math.PI/180), osO = Math.sin((d.Omega||0)*Math.PI/180);
    const oci = Math.cos((d.inc||0)*Math.PI/180), osi = Math.sin((d.inc||0)*Math.PI/180);
    const oxE = oR*(ocO*Math.cos(oU)-osO*Math.sin(oU)*oci);
    const oyE = oR*(osO*Math.cos(oU)+ocO*Math.sin(oU)*oci);
    const ozE = oR*Math.sin(oU)*osi;
    oPts.push(new THREE.Vector3(oxE, ozE, -oyE));
  }
  const orbitLine = createOrbitLineOutsideSun(
    oPts,
    new THREE.LineBasicMaterial({color:0x223366, transparent:true, opacity:0.55})
  );
  incGrp.add(orbitLine);

  // Planet sphere — textured, inside a tiltGroup that orients the spin axis correctly
  const tex = PLANET_TEX[d.name];
  const mat = d.name === 'EARTH'
    ? new THREE.MeshPhongMaterial({ map:tex, specular:0x000000, shininess:0 })
    : new THREE.MeshPhongMaterial({ map:tex, shininess: d.name==='JUPITER'||d.name==='SATURN' ? 8 : 15 });
  const mesh = new THREE.Mesh(new THREE.SphereGeometry(d.r, 40, 40), mat);
  if (d.name === 'EARTH') {
    const orientationMarker = createEarthOrientationMarker(d.r);
    orientationMarker.visible = false;
    mesh.add(orientationMarker);
    mesh.userData.orientationMarker = orientationMarker;

    const travelMarker = createEarthTravelMarker(d.r);
    travelMarker.visible = false;
    mesh.add(travelMarker);
    mesh.userData.travelMarker = travelMarker;
  }
  // Earth cloud layer — separate transparent sphere slightly larger than Earth
  let cloudMesh = null;
  if (d.name === 'EARTH') {
    // ── Dynamic cloud + storm system (Web Worker, off-thread) ───────────────
    const CW = 1024, CH = 512;
    const cloudCanvas = document.createElement('canvas');
    cloudCanvas.width = CW; cloudCanvas.height = CH;
    const cloudCtx = cloudCanvas.getContext('2d');
    const cloudTex = new THREE.CanvasTexture(cloudCanvas);

    const MAX_TROPICAL_STORMS = 5;

    function _spawnStorm() {
      const hem = Math.random() < 0.5 ? 1 : -1;
      return { lat:hem*(0.08+Math.random()*0.12), lon:Math.random(),
        dlat:(Math.random()-0.5)*0.0009, dlon:0.00028+Math.random()*0.00034,
        phase:0, intensity:0,
        maxInt:0.6+Math.random()*0.4, sign:hem,
        lifespan:9+Math.random()*8, timer:0,
        radiusScale:0.7+Math.random()*0.45,
        eyeScale:0.75+Math.random()*0.35,
        spinPhase:Math.random()*Math.PI*2,
        spinRate:(1.4+Math.random()*1.1)*hem };
    }
    let _storms = [];
    for (let i=0;i<MAX_TROPICAL_STORMS;i++) _storms.push(_spawnStorm());

    const _workerSrc = `
// Smooth lattice hash — same approach as main thread, no sin() artifacts
function h1(n){const s=Math.sin(n)*43758.5453123;return s-Math.floor(s);}
function h3(x,y,z){return h1(x+y*57.0+z*131.0);}
function radialHint(d2){return Math.sqrt(d2)+0.001;}
function n3(x,y,z){
  const ix=Math.floor(x),iy=Math.floor(y),iz=Math.floor(z);
  const fx=x-ix,fy=y-iy,fz=z-iz;
  const ux=fx*fx*(3-2*fx),uy=fy*fy*(3-2*fy),uz=fz*fz*(3-2*fz);
  return h3(ix,iy,iz)*(1-ux)*(1-uy)*(1-uz)+h3(ix+1,iy,iz)*ux*(1-uy)*(1-uz)
        +h3(ix,iy+1,iz)*(1-ux)*uy*(1-uz)+h3(ix+1,iy+1,iz)*ux*uy*(1-uz)
        +h3(ix,iy,iz+1)*(1-ux)*(1-uy)*uz+h3(ix+1,iy,iz+1)*ux*(1-uy)*uz
        +h3(ix,iy+1,iz+1)*(1-ux)*uy*uz+h3(ix+1,iy+1,iz+1)*ux*uy*uz;
}
function fb(x,y,z,o){let v=0,a=0.5,f=1,m=0;for(let i=0;i<o;i++){v+=a*n3(x*f,y*f,z*f);m+=a;a*=0.5;f*=2.1;}return v/m;}
function spawnStorm(){
  const hem=Math.random()<0.5?1:-1;
  return {
    lat:hem*(0.08+Math.random()*0.12),lon:Math.random(),
    dlat:(Math.random()-0.5)*0.0009,dlon:0.00028+Math.random()*0.00034,
    phase:0,intensity:0,maxInt:0.6+Math.random()*0.4,sign:hem,
    lifespan:9+Math.random()*8,timer:0,
    radiusScale:0.7+Math.random()*0.45,
    eyeScale:0.75+Math.random()*0.35,
    spinPhase:Math.random()*Math.PI*2,
    spinRate:(1.4+Math.random()*1.1)*hem
  };
}
function stepStorms(ss,dt){
  const d=dt*365.25*0.5;
  for(let i=ss.length-1;i>=0;i--){
    const s=ss[i];
    s.timer+=d; s.lon=(s.lon+s.dlon*d)%1;s.lat+=s.dlat*d;
    s.spinPhase += s.spinRate * d * (0.45 + 0.55*Math.min(1, s.intensity/Math.max(0.001, s.maxInt)));
    if(Math.abs(s.lat)<0.06)s.dlat=Math.sign(s.lat||1)*Math.abs(s.dlat);
    if(Math.abs(s.lat)>0.24)s.dlat=-Math.sign(s.lat)*Math.abs(s.dlat);
    if(Math.abs(s.lat)>0.28){
      s.intensity *= 0.96;
      s.phase = 2;
    }
    const pd=s.lifespan/3;
    if(s.phase===0){s.intensity=s.maxInt*Math.pow(Math.min(1,s.timer/pd),1.8);if(s.timer>=pd){s.phase=1;s.timer=0;}}
    else if(s.phase===1){s.intensity=s.maxInt*(0.9+0.1*Math.sin(s.timer*1.5));if(s.timer>=pd){s.phase=2;s.timer=0;}}
    else{s.intensity=s.maxInt*Math.pow(Math.max(0,1-s.timer/pd),2);
      if(s.timer>=pd){ss[i]=spawnStorm();}}
  }
}
self.onmessage=function(e){
  const {ss,dt,W,H,seed,simTime=0}=e.data;
  stepStorms(ss,dt);
  const buf=new Uint8ClampedArray(W*H*4);
  // seed slowly drifts noise coords — clouds visibly morph between frames
  const tx=Math.sin(seed*0.3)*0.4, ty=Math.cos(seed*0.2)*0.4, tz=Math.sin(seed*0.17+1.1)*0.3;
  const simDays = simTime * 365.25;
  const regimeNoise = fb(simDays*0.012, 7.3, 19.1, 3) - 0.5;
  let activeStorminess = 0;
  for (const s of ss) activeStorminess += Math.max(0, s.intensity);
  const stormClimateBoost = Math.min(0.16, activeStorminess * 0.012);
  const cloudCoverBias =
    Math.sin(simDays*0.045 + 0.7) * 0.034 +
    Math.sin(simDays*0.011 - 1.2) * 0.028 +
    regimeNoise * 0.135 +
    stormClimateBoost;
  for(let y=0;y<H;y++){
    const lat=(y/H-0.5)*Math.PI,cL=Math.cos(lat),sL=Math.sin(lat);
    for(let x=0;x<W;x++){
      const lon=(x/W)*Math.PI*2;
      const sx=cL*Math.cos(lon),sy=cL*Math.sin(lon),sz=sL;
      let wx=0,wy=0,wz=0;
      for(const s of ss){
        if(s.intensity<0.05)continue;
        const sl=s.lat*Math.PI/2,so=s.lon*Math.PI*2;
        const vx=Math.cos(sl)*Math.cos(so),vy=Math.cos(sl)*Math.sin(so),vz=Math.sin(sl);
        const dx=sx-vx,dy=sy-vy,dz=sz-vz,d2=dx*dx+dy*dy+dz*dz;
        const r=0.04+s.intensity*0.08;
        const dist=Math.sqrt(d2)+0.001;
        const inf=Math.exp(-d2/(r*r))*s.intensity*s.sign*(6.0/dist);
        wx+=(dy*vz-dz*vy)*inf;wy+=(dz*vx-dx*vz)*inf;wz+=(dx*vy-dy*vx)*inf;
      }
      const sc=3.4;
      const latN=Math.abs(lat)/(Math.PI/2);
      const latS=lat/(Math.PI/2);
      const tropicalBand=Math.exp(-Math.pow((latN-0.16)/0.11,2));
      const northTrack=Math.exp(-Math.pow((latS-0.43)/0.040,2));
      const southTrack=Math.exp(-Math.pow((latS+0.43)/0.040,2));
      const polarBand=Math.exp(-Math.pow((latN-0.70)/0.10,2));
       let n=fb(sx*sc+wx+tx,sy*sc+wy+ty,sz*sc+wz+tz,6)*0.515
         +fb(sx*sc*2.7+4.1+wx*2.8+tx,sy*sc*2.7+4.1+wy*2.8+ty,sz*sc*2.7+wz*2.8+tz,4)*0.282
         +fb(sx*sc*6+8.3+tx*2,sy*sc*6+8.3+ty*2,sz*sc*6+tz*2,3)*0.125;
      const streakSeed = seed*0.045;
      const jet1 = Math.pow(Math.max(0, fb(lon*1.1 + lat*7.5 + tx*0.18, lat*24.0 + ty*0.35, streakSeed + 3.1, 4) - 0.56), 2.1);
      const jet2 = Math.pow(Math.max(0, fb(lon*1.0 - lat*6.7 - tx*0.15, lat*20.0 - ty*0.28, streakSeed + 8.4, 4) - 0.57), 2.0);
      const polarJet = Math.pow(Math.max(0, fb(lon*0.85 + lat*5.0, lat*18.0 + tz*0.2, streakSeed + 14.2, 3) - 0.585), 1.8);
      const northFrontCore = Math.pow(Math.max(0, fb(lon*1.55 - latS*6.6 + tx*0.22, latS*30.0 + ty*0.25, streakSeed + 22.1, 4) - 0.595), 2.9);
      const southFrontCore = Math.pow(Math.max(0, fb(lon*1.45 + latS*6.2 - tx*0.20, -latS*29.0 + ty*0.20, streakSeed + 27.4, 4) - 0.595), 2.9);
      const northChevron = Math.pow(Math.max(0, 0.5 + 0.5*Math.cos(lon*8.6 + latS*42.0 - streakSeed*10.0)), 5.8);
      const southChevron = Math.pow(Math.max(0, 0.5 + 0.5*Math.cos(lon*8.1 - latS*40.0 + streakSeed*9.4)), 5.8);
      const northTail = Math.pow(Math.max(0, fb(lon*1.10 - latS*10.5, latS*18.0 + tz*0.18, streakSeed + 31.8, 3) - 0.57), 2.2);
      const southTail = Math.pow(Math.max(0, fb(lon*1.05 + latS*10.0, -latS*17.5 + tz*0.18, streakSeed + 36.5, 3) - 0.57), 2.2);
      const northArrows = northFrontCore * (0.45 + 0.95*northChevron);
      const southArrows = southFrontCore * (0.45 + 0.95*southChevron);
      n += (jet1*0.06 + jet2*0.05) * (northTrack + southTrack);
      n += northTrack * (northArrows*0.72 + northTail*0.26);
      n += southTrack * (southArrows*0.72 + southTail*0.26);
      n += polarJet * 0.18 * polarBand;
      n += Math.pow(Math.max(0, fb(lon*0.8 + tx*0.12, lat*8.0 + ty*0.12, streakSeed + 18.7, 3) - 0.60), 1.7) * tropicalBand * 0.08;
      // Simpler storm model from the older version: dense eyewall plus a small clear eye.
      for(const s of ss){
        if(s.intensity<0.1)continue;
        const sl=s.lat*Math.PI/2,so=s.lon*Math.PI*2;
        const vx=Math.cos(sl)*Math.cos(so),vy=Math.cos(sl)*Math.sin(so),vz=Math.sin(sl);
        const dx=sx-vx,dy=sy-vy,dz=sz-vz;
        const d2=dx*dx+dy*dy+dz*dz;
        const life = s.maxInt > 0 ? Math.min(1, s.intensity / s.maxInt) : 0;
        const gather = Math.pow(life, 0.85);
        const ringR=(0.028 + gather*0.07) * s.radiusScale;
        const ringGain=0.22 + 0.55*gather;
        let refx=0, refy=0, refz=1;
        if(Math.abs(vz)>0.92){refx=0; refy=1; refz=0;}
        let ex=refy*vz-refz*vy, ey=refz*vx-refx*vz, ez=refx*vy-refy*vx;
        const eLen=Math.sqrt(ex*ex+ey*ey+ez*ez)+1e-6;
        ex/=eLen; ey/=eLen; ez/=eLen;
        const nx=vy*ez-vz*ey, ny=vz*ex-vx*ez, nz=vx*ey-vy*ex;
        const px=dx*ex+dy*ey+dz*ez;
        const py=dx*nx+dy*ny+dz*nz;
        const theta=Math.atan2(py, px);
        const innerSpin=Math.pow(Math.max(0, 0.5 + 0.5*Math.cos(theta*2.0 - radialHint(d2)*20.0 - s.spinPhase)), 4.4);
        const outerSpin=Math.pow(Math.max(0, 0.5 + 0.5*Math.cos(theta*3.5 - radialHint(d2)*12.5 - s.spinPhase*1.15)), 3.1);
        const stormBody=Math.exp(-d2/(ringR*ringR));
        const stormShieldR = ringR * (2.8 + 0.9*gather);
        const stormShield = Math.exp(-d2/(stormShieldR*stormShieldR));
        n+=stormBody*ringGain*s.intensity*(0.74 + 0.62*innerSpin);
        n+=stormShield*(0.06 + 0.24*gather)*s.intensity;
        if (gather > 0.35) {
          const outerR = ringR * (1.9 - 0.35*gather);
          n += Math.exp(-d2/(outerR*outerR)) * (0.08 + 0.28*outerSpin) * s.intensity * gather;
        }
        if(s.intensity>0.2){
          const eyeLife = Math.max(0, (gather - 0.45) / 0.55);
          const er=(0.0018 + 0.0032*eyeLife) * s.eyeScale;
          n-=Math.exp(-d2/(er*er))*(0.9 + 0.55*eyeLife)*s.intensity;
        }
      }
      const aL=Math.abs(lat)/(Math.PI/2),pb=Math.max(0,(aL-0.78)/0.22);
      let c=Math.max(0,(n-(0.438-cloudCoverBias))/0.64);c=Math.min(1,c+pb*0.9);
      const alphaScale = 478 * (1.0 + Math.max(-0.08, cloudCoverBias)*2.25);
      const al=Math.min(255,Math.pow(c,1.29)*alphaScale)|0;
      const idx=(y*W+x)*4;buf[idx]=255;buf[idx+1]=255;buf[idx+2]=255;buf[idx+3]=al;
    }
  }
  self.postMessage({buf,ss},[buf.buffer]);
};`;

    const _nxtCanvas = document.createElement('canvas');
    _nxtCanvas.width = CW; _nxtCanvas.height = CH;
    const _nxtCtx = _nxtCanvas.getContext('2d');
    const _nxtTex = new THREE.CanvasTexture(_nxtCanvas);

    // Next-frame mesh, initially invisible
    const cloudMeshB = new THREE.Mesh(
      new THREE.SphereGeometry(d.r * 1.009, 40, 40),
      new THREE.MeshPhongMaterial({
        map: _nxtTex, transparent: true, opacity: 0,
        depthWrite: false, shininess: 0, specular: 0x000000,
      })
    );

    // ── Crossfade state ────────────────────────────────────────────────────
    const _canA = cloudCanvas,  _ctxA = cloudCtx,  _texA = cloudTex;
    const _canB = _nxtCanvas,   _ctxB = _nxtCtx,   _texB = _nxtTex;

    const _cloudWorker = new Worker(URL.createObjectURL(new Blob([_workerSrc],{type:'application/javascript'})));

    // Create meshes FIRST so onmessage can reference them safely
    cloudMesh = new THREE.Mesh(
      new THREE.SphereGeometry(d.r * 1.008, 40, 40),
      new THREE.MeshPhongMaterial({
        map: _texA, transparent: true, opacity: 0,
        depthWrite: false, shininess: 0, specular: 0x000000,
      })
    );

    cloudMesh.userData.cloudMeshB = cloudMeshB;
    cloudMesh.material.map = _texA;
    cloudMeshB.material.map = _texB;

    let _currentMesh = cloudMesh;
    let _nextMesh = cloudMeshB;
    let _currentCtx = _ctxA;
    let _nextCtx = _ctxB;
    let _currentTex = _texA;
    let _nextTex = _texB;
    let _workerBusy = false;
    let _cloudFrame = 0;
    let _firstFrame = true;
    let _isFading = false;
    let _fadeStartRealTime = 0;
    let _lastCloudSimT = 0;
    let _pendingFrame = null;
    let _pendingStorms = null;
    let _requestQueued = false;
    const FADE_DURATION_REAL = 5400;

    function _requestNextCloudFrame(simTime) {
      if (_workerBusy) {
        _requestQueued = true;
        return;
      }
      const cloudDt = _firstFrame ? 0 : Math.max(0.002, Math.abs(simTime - _lastCloudSimT));
      _lastCloudSimT = simTime;
      _workerBusy = true;
      _requestQueued = false;
      _cloudWorker.postMessage({ss:_storms, dt:cloudDt, W:CW, H:CH, seed:++_cloudFrame * 7.3, simTime});
    }

    function _stageNextCloudFrame(buf) {
      const img = _nextCtx.createImageData(CW, CH);
      img.data.set(buf);
      _nextCtx.putImageData(img, 0, 0);
      _nextTex.needsUpdate = true;
      _nextMesh.material.opacity = 0;
      _nextMesh.material.needsUpdate = true;
    }

    function _startCloudFade() {
      _fadeStartRealTime = Date.now();
      _isFading = true;
    }

    _cloudWorker.onmessage = function(e) {
      _workerBusy = false;
      if (_firstFrame) {
        _storms = e.data.ss;
        const img = _currentCtx.createImageData(CW, CH);
        img.data.set(e.data.buf);
        _currentCtx.putImageData(img, 0, 0);
        _currentTex.needsUpdate = true;
        _currentMesh.material.opacity = 0.95;
        _nextMesh.material.opacity = 0;
        _firstFrame = false;
      } else if (_isFading) {
        _pendingFrame = e.data.buf;
        _pendingStorms = e.data.ss;
      } else {
        _storms = e.data.ss;
        _stageNextCloudFrame(e.data.buf);
        _startCloudFade();
        _requestQueued = true;
      }
    };

    _requestNextCloudFrame(0);

    cloudMesh.userData.updateClouds = function(newSimTime, dtReal) {
      if (_isFading) {
        const fadeProgress = Math.min(1.0, (Date.now() - _fadeStartRealTime) / FADE_DURATION_REAL);
        const t = fadeProgress * fadeProgress * (3 - 2 * fadeProgress);
        _currentMesh.material.opacity = 0.95 * (1 - t);
        _nextMesh.material.opacity = 0.95 * t;
        _currentMesh.material.needsUpdate = true;
        _nextMesh.material.needsUpdate = true;
        if (fadeProgress >= 1.0) {
          _currentMesh.material.opacity = 0;
          _nextMesh.material.opacity = 0.95;
          [_currentMesh, _nextMesh] = [_nextMesh, _currentMesh];
          [_currentCtx, _nextCtx] = [_nextCtx, _currentCtx];
          [_currentTex, _nextTex] = [_nextTex, _currentTex];
          _isFading = false;
          if (_pendingFrame) {
            _storms = _pendingStorms;
            _stageNextCloudFrame(_pendingFrame);
            _pendingFrame = null;
            _pendingStorms = null;
            _startCloudFade();
            _requestQueued = true;
          }
        }
      }
      if (_requestQueued && !_workerBusy) {
        _requestNextCloudFrame(newSimTime);
      } else if (!_firstFrame && !_isFading && !_workerBusy && !_pendingFrame) {
        _requestNextCloudFrame(newSimTime);
      }
    };
  }
  // tiltGroup applies axial tilt in solarPivot local space so it stays locked to the ecliptic.
  const tiltGroup = new THREE.Group();
  const _oblDeg = { MERCURY:7.04, VENUS:177.36, EARTH:23.44, MARS:25.19,
                    JUPITER:3.13, SATURN:26.73, URANUS:97.77, NEPTUNE:28.32 }[d.name];
  const _lonDeg  = { MERCURY:280.0, VENUS:272.8, EARTH:90.0, MARS:352.9,
                    JUPITER:336.0, SATURN:40.6, URANUS:257.3, NEPTUNE:299.4 }[d.name];
  if (_oblDeg !== undefined) {
    const obl = THREE.MathUtils.degToRad(_oblDeg);
    const lon = THREE.MathUtils.degToRad(_lonDeg);
    // Pole in ecliptic coords: (sin(obl)*cos(lon), sin(obl)*sin(lon), cos(obl))
    const ecl_x = Math.sin(obl) * Math.cos(lon);
    const ecl_y = Math.sin(obl) * Math.sin(lon);
    const ecl_z = Math.cos(obl);
    const target = new THREE.Vector3(ecl_x, ecl_z, -ecl_y).normalize();
    const from = new THREE.Vector3(0, 1, 0);
    const rotAxis = new THREE.Vector3().crossVectors(from, target);
    if (rotAxis.lengthSq() > 1e-12) {
      const angle = Math.acos(Math.max(-1, Math.min(1, from.dot(target))));
      tiltGroup.setRotationFromAxisAngle(rotAxis.normalize(), angle);
    } else if (from.dot(target) < 0) {
      tiltGroup.setRotationFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI);
    }
  }
  tiltGroup.add(mesh);
  if (cloudMesh) {
    tiltGroup.add(cloudMesh);
    if (cloudMesh.userData.cloudMeshB) tiltGroup.add(cloudMesh.userData.cloudMeshB);
  }
  incGrp.add(tiltGroup);

  // Queue real photo texture swap (fires when loaded, keeps procedural until then)
  queuePhotoSwap(d.name, () => mat);

  // Rings
  let ringMat = null;
  let ringRef = null;
  if (d.rings) {
    // Rings must inherit the same translated, tilted frame as the planet body.
    const ringGrp = new THREE.Group();
    tiltGroup.add(ringGrp);
    ringRef = ringGrp;

    if (d.name === 'SATURN') {
      // Saturn: multi-band ring system with real proportions
      // Texture maps full D→F ring radial extent
      const ringTex = PLANET_TEX.saturnRing;
      ringMat = new THREE.MeshBasicMaterial({
        map: ringTex, color: 0xffffff,
        side: THREE.DoubleSide, transparent: true, opacity: 1.0,
        depthWrite: false,
      });
      // Full ring disc: D ring inner (1.11r) to beyond F ring (2.34r)
      const ring = new THREE.Mesh(new THREE.RingGeometry(d.r*1.11, d.r*2.34, 256, 1), ringMat);
      ring.rotation.x = Math.PI / 2;
      ringGrp.add(ring);
      queuePhotoSwap('saturnRing', () => ringMat);
    } else {
      // Uranus: simple tilted ring
      // Uranus: narrow dark rings (epsilon, delta, gamma, eta, beta, alpha, 4, 5, 6)
      // Each ring is very narrow and dark - quite different from Saturn
      const uraRingTex = makeUranusRingTex();
      const uMat = new THREE.MeshBasicMaterial({
        map: uraRingTex, side: THREE.DoubleSide, transparent: true, opacity: 0.8, depthWrite: false
      });
      const ring = new THREE.Mesh(new THREE.RingGeometry(d.r*d.ri, d.r*d.ro, 192, 1), uMat);
      ring.rotation.x = Math.PI / 2;
      ringGrp.add(ring);
    }

  }

  // Trail — glowing Points for fire/comet effect
  const trailWorldPts = [];
  const trailGeo = new THREE.BufferGeometry();
  const trailPosBuf = new Float32Array(TRAIL_LEN * 3);
  const trailColBuf = new Float32Array(TRAIL_LEN * 3);
  const trailSizBuf = new Float32Array(TRAIL_LEN);
  trailGeo.setAttribute('position', new THREE.BufferAttribute(trailPosBuf, 3));
  trailGeo.setAttribute('color',    new THREE.BufferAttribute(trailColBuf, 3));
  trailGeo.setAttribute('size',     new THREE.BufferAttribute(trailSizBuf, 1));
  trailGeo.setDrawRange(0, 0);
  const trailLine = new THREE.Points(trailGeo, trailPointMat.clone());
  trailLine.frustumCulled = false;
  scene.add(trailLine);

  const tc = new THREE.Color(d.color);

  const planet = {
    d, incGrp, tiltGroup, mesh, cloudMesh, orbitLine, trailLine, trailGeo, trailPosBuf, trailColBuf, trailSizBuf, trailWorldPts,
    tc, ring: ringRef,
    b, c,
    angle0: (d.L0 - d.omega) * Math.PI / 180,  // M0 = L0 - omega (mean anomaly at J2000)
    omegaRad: d.omega * Math.PI / 180,           // longitude of perihelion
    OmegaRad: (d.Omega||0) * Math.PI / 180,      // longitude of ascending node
    incRad:   (d.inc||0) * Math.PI / 180,         // inclination
  };
  planets.push(planet);
}

// Capture Earth's angle0 for probe launch position calculation
earthAngle0 = planets.find(p => p.d.name === 'EARTH').angle0;
// With correct J2000 positions, Voyager JPL data aligns directly — no rotation needed.

// ── Moon data ─────────────────────────────────────────────────────────────────
// sma = scene units from planet centre (planet radius ~1 unit baseline)
// period = Earth years, ecc = eccentricity, inc = degrees
// r = visual radius in scene units
const MOON_DATA = [
  // ── Earth ──────────────────────────────────────────────────────────────────
  { planet:'EARTH',   name:'Moon',      sma:2.8,  period:0.0748,  ecc:0.055, inc:5.1,  r:0.22, color:0xAAAAAA, tidalLock:true, tidalYawDeg:-90, tidalPitchDeg:-10, tidalRollDeg:0 },

  // ── Mars ───────────────────────────────────────────────────────────────────
  { planet:'MARS',    name:'Phobos',    sma:1.4,  period:0.000865,ecc:0.015, inc:1.1,  r:0.06, color:0x997755 },
  { planet:'MARS',    name:'Deimos',    sma:2.2,  period:0.00326, ecc:0.000, inc:1.8,  r:0.04, color:0x887766 },

  // ── Jupiter (95 moons — showing all named significant ones) ────────────────
  // Galilean moons
  { planet:'JUPITER', name:'Io',        sma:4.5,  period:0.00485, ecc:0.004, inc:0.04, r:0.25, color:0xFFDD44 },
  { planet:'JUPITER', name:'Europa',    sma:6.2,  period:0.00972, ecc:0.009, inc:0.47, r:0.21, color:0xCCBB99 },
  { planet:'JUPITER', name:'Ganymede',  sma:9.0,  period:0.01960, ecc:0.001, inc:0.21, r:0.30, color:0xAA9977 },
  { planet:'JUPITER', name:'Callisto',  sma:14.0, period:0.04560, ecc:0.007, inc:0.51, r:0.28, color:0x777766 },
  // Inner moons
  { planet:'JUPITER', name:'Amalthea',  sma:3.8,  period:0.00137, ecc:0.003, inc:0.37, r:0.07, color:0x886655 },
  { planet:'JUPITER', name:'Thebe',     sma:4.5,  period:0.00188, ecc:0.018, inc:1.07, r:0.05, color:0x887766 },
  { planet:'JUPITER', name:'Metis',     sma:3.2,  period:0.00100, ecc:0.000, inc:0.02, r:0.04, color:0x998877 },
  { planet:'JUPITER', name:'Adrastea',  sma:3.3,  period:0.00103, ecc:0.002, inc:0.03, r:0.03, color:0x998877 },
  // Outer irregular moons (Himalia group)
  { planet:'JUPITER', name:'Himalia',   sma:42.0, period:0.6050,  ecc:0.162, inc:27.5, r:0.07, color:0x887755 },
  { planet:'JUPITER', name:'Elara',     sma:44.0, period:0.6310,  ecc:0.217, inc:26.6, r:0.05, color:0x776644 },
  { planet:'JUPITER', name:'Pasiphae',  sma:82.0, period:1.8910,  ecc:0.409, inc:151., r:0.05, color:0x776655 },
  { planet:'JUPITER', name:'Sinope',    sma:84.0, period:1.9810,  ecc:0.250, inc:158., r:0.04, color:0x776655 },
  { planet:'JUPITER', name:'Lysithea',  sma:43.0, period:0.6180,  ecc:0.112, inc:28.3, r:0.04, color:0x776644 },
  { planet:'JUPITER', name:'Carme',     sma:79.0, period:1.7630,  ecc:0.253, inc:165., r:0.05, color:0x665544 },
  { planet:'JUPITER', name:'Ananke',    sma:73.0, period:1.5810,  ecc:0.244, inc:148., r:0.04, color:0x665544 },
  { planet:'JUPITER', name:'Leda',      sma:39.0, period:0.5530,  ecc:0.164, inc:27.5, r:0.03, color:0x776655 },
  { planet:'JUPITER', name:'Thebe',     sma:3.6,  period:0.00188, ecc:0.018, inc:1.07, r:0.04, color:0x887766 },

  // ── Saturn (146 moons — showing all significant named ones) ────────────────
  // Major moons
  { planet:'SATURN',  name:'Mimas',     sma:5.2,  period:0.00513, ecc:0.020, inc:1.57, r:0.10, color:0xCCCCBB },
  { planet:'SATURN',  name:'Enceladus', sma:6.5,  period:0.00693, ecc:0.005, inc:0.02, r:0.12, color:0xEEEEFF },
  { planet:'SATURN',  name:'Tethys',    sma:8.2,  period:0.01007, ecc:0.000, inc:1.09, r:0.15, color:0xCCCCBB },
  { planet:'SATURN',  name:'Dione',     sma:10.5, period:0.01460, ecc:0.002, inc:0.02, r:0.16, color:0xBBBBAA },
  { planet:'SATURN',  name:'Rhea',      sma:14.5, period:0.02480, ecc:0.001, inc:0.35, r:0.20, color:0xCCBBAA },
  { planet:'SATURN',  name:'Titan',     sma:27.0, period:0.04370, ecc:0.029, inc:0.33, r:0.30, color:0xDD9944 },
  { planet:'SATURN',  name:'Hyperion',  sma:31.0, period:0.05710, ecc:0.123, inc:0.43, r:0.08, color:0xAA9977 },
  { planet:'SATURN',  name:'Iapetus',   sma:64.0, period:0.21790, ecc:0.029, inc:15.5, r:0.18, color:0x887755 },
  { planet:'SATURN',  name:'Phoebe',    sma:185., period:1.5080,  ecc:0.164, inc:175., r:0.07, color:0x665544 },
  // Inner small moons
  { planet:'SATURN',  name:'Janus',     sma:4.3,  period:0.00371, ecc:0.007, inc:0.17, r:0.05, color:0xAAA999 },
  { planet:'SATURN',  name:'Epimetheus',sma:4.3,  period:0.00371, ecc:0.010, inc:0.34, r:0.04, color:0xAAA999 },
  { planet:'SATURN',  name:'Helene',    sma:10.5, period:0.01460, ecc:0.005, inc:0.21, r:0.03, color:0xBBBBAA },
  { planet:'SATURN',  name:'Telesto',   sma:8.2,  period:0.01007, ecc:0.000, inc:1.18, r:0.03, color:0xCCCCBB },
  { planet:'SATURN',  name:'Calypso',   sma:8.2,  period:0.01007, ecc:0.000, inc:1.56, r:0.03, color:0xCCCCBB },
  { planet:'SATURN',  name:'Pan',       sma:3.95, period:0.00314, ecc:0.000, inc:0.00, r:0.02, color:0xDDCCBB },
  { planet:'SATURN',  name:'Atlas',     sma:4.05, period:0.00322, ecc:0.000, inc:0.00, r:0.02, color:0xCCBBAA },
  { planet:'SATURN',  name:'Prometheus',sma:4.15, period:0.00334, ecc:0.002, inc:0.01, r:0.03, color:0xBBAA99 },
  { planet:'SATURN',  name:'Pandora',   sma:4.25, period:0.00342, ecc:0.004, inc:0.05, r:0.03, color:0xBBAA99 },
  { planet:'SATURN',  name:'Daphnis',   sma:4.08, period:0.00317, ecc:0.000, inc:0.00, r:0.02, color:0xCCBBAA },

  // ── Uranus (28 moons) ──────────────────────────────────────────────────────
  { planet:'URANUS',  name:'Miranda',   sma:3.5,  period:0.00383, ecc:0.001, inc:4.34, r:0.09, color:0xBBBBCC },
  { planet:'URANUS',  name:'Ariel',     sma:5.0,  period:0.00638, ecc:0.001, inc:0.04, r:0.13, color:0xBBBBCC },
  { planet:'URANUS',  name:'Umbriel',   sma:6.5,  period:0.00933, ecc:0.004, inc:0.13, r:0.12, color:0x888899 },
  { planet:'URANUS',  name:'Titania',   sma:9.5,  period:0.01529, ecc:0.001, inc:0.08, r:0.16, color:0xAABBBB },
  { planet:'URANUS',  name:'Oberon',    sma:12.0, period:0.02228, ecc:0.001, inc:0.07, r:0.16, color:0x998899 },
  { planet:'URANUS',  name:'Caliban',   sma:44.0, period:0.9790,  ecc:0.159, inc:141., r:0.04, color:0x665544 },
  { planet:'URANUS',  name:'Sycorax',   sma:58.0, period:1.2780,  ecc:0.522, inc:159., r:0.05, color:0x665544 },
  { planet:'URANUS',  name:'Prospero',  sma:66.0, period:1.6890,  ecc:0.445, inc:152., r:0.03, color:0x554433 },
  { planet:'URANUS',  name:'Setebos',   sma:72.0, period:1.9780,  ecc:0.591, inc:158., r:0.03, color:0x554433 },
  { planet:'URANUS',  name:'Puck',      sma:3.0,  period:0.00238, ecc:0.000, inc:0.32, r:0.05, color:0xAAABBB },
  { planet:'URANUS',  name:'Portia',    sma:2.6,  period:0.00188, ecc:0.000, inc:0.07, r:0.04, color:0xBBBBCC },
  { planet:'URANUS',  name:'Rosalind',  sma:2.7,  period:0.00200, ecc:0.000, inc:0.28, r:0.03, color:0xBBBBCC },
  { planet:'URANUS',  name:'Belinda',   sma:2.8,  period:0.00209, ecc:0.000, inc:0.03, r:0.03, color:0xBBBBCC },
  { planet:'URANUS',  name:'Cressida',  sma:2.55, period:0.00179, ecc:0.000, inc:0.01, r:0.04, color:0xBBBBCC },

  // ── Neptune (16 moons) ─────────────────────────────────────────────────────
  { planet:'NEPTUNE', name:'Triton',    sma:5.5,  period:0.01609, ecc:0.000, inc:157., r:0.22, color:0xAABBCC },
  { planet:'NEPTUNE', name:'Nereid',    sma:56.0, period:0.99970, ecc:0.751, inc:7.23, r:0.08, color:0x889977 },
  { planet:'NEPTUNE', name:'Proteus',   sma:3.5,  period:0.00292, ecc:0.000, inc:0.04, r:0.09, color:0x777788 },
  { planet:'NEPTUNE', name:'Larissa',   sma:2.9,  period:0.00219, ecc:0.001, inc:0.20, r:0.06, color:0x778877 },
  { planet:'NEPTUNE', name:'Galatea',   sma:2.6,  period:0.00188, ecc:0.000, inc:0.05, r:0.05, color:0x778877 },
  { planet:'NEPTUNE', name:'Despina',   sma:2.3,  period:0.00161, ecc:0.000, inc:0.07, r:0.05, color:0x778877 },
  { planet:'NEPTUNE', name:'Thalassa',  sma:2.1,  period:0.00148, ecc:0.000, inc:0.21, r:0.04, color:0x778877 },
  { planet:'NEPTUNE', name:'Naiad',     sma:1.9,  period:0.00130, ecc:0.000, inc:4.69, r:0.04, color:0x778877 },
  { planet:'NEPTUNE', name:'Halimede',  sma:95.0, period:3.1740,  ecc:0.571, inc:112., r:0.03, color:0x554433 },
  { planet:'NEPTUNE', name:'Sao',       sma:112., period:4.0340,  ecc:0.293, inc:48.5, r:0.03, color:0x554433 },
  { planet:'NEPTUNE', name:'Laomedeia', sma:118., period:4.1900,  ecc:0.392, inc:34.7, r:0.03, color:0x554433 },
  { planet:'NEPTUNE', name:'Neso',      sma:200., period:9.3740,  ecc:0.571, inc:136., r:0.03, color:0x554433 },
];

const IRREGULAR_MOON_NAMES = new Set([
  'Himalia', 'Elara', 'Pasiphae', 'Sinope', 'Lysithea', 'Carme', 'Ananke', 'Leda',
  'Phoebe', 'Hyperion',
  'Caliban', 'Sycorax', 'Prospero', 'Setebos',
  'Nereid', 'Halimede', 'Sao', 'Laomedeia', 'Neso',
]);

const MOON_SPIN_MODELS = {
  Phobos:    { mode:'synchronous' },
  Deimos:    { mode:'synchronous' },
  Triton:    { mode:'synchronous' },
  Himalia:   { mode:'period',  periodDays:7.7819 / 24 },
  Sinope:    { mode:'period',  periodDays:13.16 / 24 },
  Lysithea:  { mode:'period',  periodDays:12.78 / 24 },
  Carme:     { mode:'period',  periodDays:10.40 / 24 },
  Ananke:    { mode:'period',  periodDays:8.31 / 24 },
  Phoebe:    { mode:'period',  periodDays:9.27365 / 24 },
  Hyperion:  { mode:'chaotic', periodDays:13 / 24 },
  Caliban:   { mode:'period',  periodDays:9.948 / 24 },
  Sycorax:   { mode:'period',  periodDays:6.9162 / 24 },
  Prospero:  { mode:'period',  periodDays:7.145 / 24 },
  Setebos:   { mode:'period',  periodDays:4.255 / 24 },
  Nereid:    { mode:'period',  periodDays:11.594 / 24 },
};

function hashNameToUnit(name) {
  let hash = 2166136261;
  for (let index = 0; index < name.length; index++) {
    hash ^= name.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0) / 0xffffffff;
}

function getMoonSpinModel(md) {
  if (md.tidalLock) {
    return {
      mode:'synchronous',
      yawDeg: md.tidalYawDeg ?? 270,
      pitchDeg: md.tidalPitchDeg ?? 0,
      rollDeg: md.tidalRollDeg ?? 0,
    };
  }

  if (MOON_SPIN_MODELS[md.name]) {
    return { ...MOON_SPIN_MODELS[md.name] };
  }

  if (!IRREGULAR_MOON_NAMES.has(md.name)) {
    return { mode:'synchronous' };
  }

  return { mode:'unknown' };
}

// Build moons
const moons = [];
for (const md of MOON_DATA) {
  const parentPlanet = planets.find(p => p.d.name === md.planet);
  if (!parentPlanet) continue;

  const b = md.sma * Math.sqrt(1 - md.ecc*md.ecc);
  const c = md.sma * md.ecc;

  // Moon lives in an inclined group parented to the planet's incGrp (not mesh)
  // so the planet's axial rotation doesn't carry the moon around with it
  const moonIncGrp = new THREE.Group();
  moonIncGrp.rotation.x = THREE.MathUtils.degToRad(md.inc);
  parentPlanet.incGrp.add(moonIncGrp);

  // Orbit ring (visible in solar mode when zoomed in)
  const oPts = [];
  for (let i=0;i<=128;i++){
    const t=(i/128)*Math.PI*2;
    oPts.push(new THREE.Vector3(md.sma*Math.cos(t)-c, 0, -b*Math.sin(t)));
  }
  const moonOrbitLine = new THREE.Line(
    new THREE.BufferGeometry().setFromPoints(oPts),
    new THREE.LineBasicMaterial({ color:0x334455, transparent:true, opacity:0.35 })
  );
  moonIncGrp.add(moonOrbitLine);

  // Moon mesh
  const moonMat = md.name === 'Moon'
    ? new THREE.MeshPhongMaterial({ map: PLANET_TEX.moon, specular: 0x000000, shininess: 0 })
    : new THREE.MeshPhongMaterial({ color:md.color, shininess:8 });
  const moonMesh = new THREE.Mesh(
    new THREE.SphereGeometry(md.r, 16, 16),
    moonMat
  );
  if (md.name === 'Moon') queuePhotoSwap('moon', () => moonMat);
  moonIncGrp.add(moonMesh);

  const spinModel = getMoonSpinModel(md);
  const spinSeed = hashNameToUnit(md.name);
  const baseRotation = new THREE.Euler(
    (spinSeed - 0.5) * 0.7,
    spinSeed * Math.PI * 2,
    (0.5 - spinSeed) * 0.35,
  );

  moons.push({
    md, moonMesh, moonIncGrp, moonOrbitLine,
    parentPlanet,
    b, c,
    angle0: THREE.MathUtils.degToRad(md.phaseDeg ?? 0),
    spinModel,
    spinSeed,
    baseRotation,
  });
}

// ── Dwarf planets ─────────────────────────────────────────────────────────────
// Treated like planets but smaller — included in solar view and focus bar
const DWARF_DATA = [
  // omega, Omega from JPL small body database
  { name:'CERES',    sma:88.5,  ecc:0.076, inc:10.6, omega: 73.6,  Omega: 80.3,  period:4.604,   L0: 95.989, r:0.18, color:0x998877, rotPeriod:0.3781,  diameter:'945 km',    dist:'414M km',   year:'4.6 yrs',   moons:'0', type:'Dwarf Planet' },
  { name:'PLUTO',    sma:1263,  ecc:0.248, inc:17.1, omega:112.6,  Omega:110.3,  period:247.9,   L0: 14.882, r:0.20, color:0xBBAA88, rotPeriod:-6.387,  diameter:'2,377 km',  dist:'5.9B km',   year:'248 yrs',   moons:'5', type:'Dwarf Planet' },
  { name:'ERIS',     sma:2169,  ecc:0.436, inc:44.0, omega:151.4,  Omega: 35.9,  period:559.0,   L0:204.163, r:0.19, color:0xCCCCCC, rotPeriod:15.786,  diameter:'2,326 km',  dist:'~10B km',   year:'559 yrs',   moons:'1', type:'Dwarf Planet' },
  { name:'MAKEMAKE', sma:1454,  ecc:0.159, inc:29.0, omega:295.0,  Omega: 79.4,  period:306.0,   L0:139.000, r:0.15, color:0xBB9966, rotPeriod:0.9511,  diameter:'1,430 km',  dist:'~7B km',    year:'306 yrs',   moons:'1', type:'Dwarf Planet' },
  { name:'HAUMEA',   sma:1380,  ecc:0.191, inc:28.2, omega:239.0,  Omega:122.1,  period:284.0,   L0:198.000, r:0.13, color:0xDDDDCC, rotPeriod:0.1631,  diameter:'1,560 km',  dist:'~6.5B km',  year:'284 yrs',   moons:'2', type:'Dwarf Planet' },
  { name:'SEDNA',    sma:16192, ecc:0.845, inc:11.9, omega:311.0,  Omega:144.5,  period:11408.0, L0:358.000, r:0.14, color:0xCC6644, rotPeriod:10.273,  diameter:'~1,000 km', dist:'~76-936B AU',year:'11,408 yrs',moons:'0', type:'Extreme TNO'  },
  { name:'GONGGONG', sma:2156,  ecc:0.499, inc:30.7, omega:207.5,  Omega:336.9,  period:552.0,   L0:207.000, r:0.12, color:0xAA8877, rotPeriod:0.9333,  diameter:'~1,230 km', dist:'~8B km',    year:'552 yrs',   moons:'1', type:'Dwarf Planet' },
  { name:'QUAOAR',   sma:1398,  ecc:0.041, inc:7.99, omega:147.5,  Omega:188.9,  period:288.0,   L0:188.000, r:0.11, color:0x997766, rotPeriod:0.7361,  diameter:'~1,110 km', dist:'~6.5B km',  year:'288 yrs',   moons:'1', type:'TNO'          },
  { name:'ORCUS',    sma:1253,  ecc:0.227, inc:20.6, omega: 72.3,  Omega:268.6,  period:246.0,   L0: 90.000, r:0.10, color:0x888899, rotPeriod:0.5507,  diameter:'~910 km',   dist:'~5.9B km',  year:'246 yrs',   moons:'1', type:'Dwarf Planet' },
];

const dwarfs = [];
for (const d of DWARF_DATA) {
  const b = d.sma * Math.sqrt(1 - d.ecc*d.ecc);
  const c = d.sma * d.ecc;

  const incGrp = new THREE.Group();
  incGrp.rotation.x = 0; // inclination handled in 3D position calculation
  solarPivot.add(incGrp);

  // Orbit ring (faint, dashed look via opacity)
  const oPts = [];
  for (let i=0;i<=256;i++){
    const t=(i/256)*Math.PI*2;
    // Full 3D orbit ellipse with inclination and ascending node
    const oR = d.sma*(1-d.ecc*d.ecc)/(1+d.ecc*Math.cos(t));
    const oOmPeri = ((d.omega||0)-(d.Omega||0))*Math.PI/180; // arg of perihelion from node
    const oU = oOmPeri + t; // arg of latitude
    const ocO = Math.cos((d.Omega||0)*Math.PI/180), osO = Math.sin((d.Omega||0)*Math.PI/180);
    const oci = Math.cos((d.inc||0)*Math.PI/180), osi = Math.sin((d.inc||0)*Math.PI/180);
    const oxE = oR*(ocO*Math.cos(oU)-osO*Math.sin(oU)*oci);
    const oyE = oR*(osO*Math.cos(oU)+ocO*Math.sin(oU)*oci);
    const ozE = oR*Math.sin(oU)*osi;
    oPts.push(new THREE.Vector3(oxE, ozE, -oyE));
  }
  const orbitLine = createOrbitLineOutsideSun(
    oPts,
    new THREE.LineBasicMaterial({color:0x334455, transparent:true, opacity:0.25})
  );
  incGrp.add(orbitLine);

  const mesh = new THREE.Mesh(
    new THREE.SphereGeometry(d.r, 16, 16),
    new THREE.MeshPhongMaterial({ color:d.color, shininess:8 })
  );
  incGrp.add(mesh);

  dwarfs.push({ d, incGrp, mesh, orbitLine, b, c, angle0: ((d.L0-(d.omega||0)) * Math.PI / 180) });
}

// Pluto's moon Charon
{
  const pluto = dwarfs.find(d=>d.d.name==='PLUTO');
  if (pluto) {
    const charonGrp = new THREE.Group();
    pluto.mesh.add(charonGrp);
    const charon = new THREE.Mesh(
      new THREE.SphereGeometry(0.10, 12, 12),
      new THREE.MeshPhongMaterial({ color:0x888899, shininess:5 })
    );
    charonGrp.add(charon);
    pluto.charon = charon;
    pluto.charonAngle = Math.random()*Math.PI*2;
  }
}

// ── Asteroid belt ─────────────────────────────────────────────────────────────
// Main belt: between Mars (sma=46) and Jupiter (sma=90), ~2.2–3.2 AU
// Kuiper belt: beyond Neptune (sma=228), ~280–500 scene units
// Scattered disc: highly inclined, 400–800 scene units
// ── Orbiting asteroid/TNO belts ───────────────────────────────────────────────
// Each particle orbits with its own period from Kepler's 3rd law: period = (sma/32)^1.5 years
// Positions recomputed each frame from simTime — inner particles faster, outer slower.
const beltMat = new THREE.ShaderMaterial({
  uniforms: {},
  vertexShader: `
    uniform float size;
    void main() {
      gl_PointSize = 2.0;
      gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }`,
  fragmentShader: `
    void main() {
      float d = length(gl_PointCoord - vec2(0.5));
      if (d > 0.5) discard;
      gl_FragColor = vec4(0.67, 0.60, 0.47, 0.6);
    }`,
  transparent: true, depthWrite: false,
});

function makeOrbitingBelt(count, smaMin, smaMax, incMax, color, opacity, size, attenuate=true) {
  const pos    = new Float32Array(count * 3);
  const params = new Float32Array(count * 5); // sma, ecc, inc, angle0, node
  for (let i=0; i<count; i++) {
    const sma    = smaMin + Math.random() * (smaMax - smaMin);
    const ecc    = Math.random() * 0.08; // low enough perihelion stays inside belt
    const inc    = (Math.random() - 0.5) * 2 * incMax * Math.PI / 180;
    const angle0 = Math.random() * Math.PI * 2;
    const node   = Math.random() * Math.PI * 2;
    params[i*5]   = sma;
    params[i*5+1] = ecc;
    params[i*5+2] = inc;
    params[i*5+3] = angle0;
    params[i*5+4] = node;
    const r  = sma * (1 - ecc * Math.cos(angle0));
    const xOrb = r * Math.cos(angle0);
    const zOrb = r * Math.sin(angle0);
    const sinI = Math.sin(inc), cosI = Math.cos(inc);
    const sinN = Math.sin(node), cosN = Math.cos(node);
    pos[i*3]   =  xOrb * cosN - zOrb * sinN * cosI;
    pos[i*3+1] =  zOrb * sinI;
    pos[i*3+2] =  xOrb * sinN + zOrb * cosN * cosI;
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  const r = ((color >> 16) & 0xff) / 255;
  const g = ((color >> 8)  & 0xff) / 255;
  const b = ( color        & 0xff) / 255;
  const mat = new THREE.PointsMaterial({
    map: dotTex,
    color: new THREE.Color(r, g, b),
    size: size,
    transparent: true,
    opacity: opacity,
    sizeAttenuation: attenuate,
    depthWrite: false,
    alphaTest: 0.01,
  });
  const pts = new THREE.Points(geo, mat);
  pts.frustumCulled = false;
  solarPivot.add(pts);
  return { pts, geo, pos, params, count };
}

// Mars sma=46, Jupiter sma=90, Neptune sma=228
// Main belt: strictly between Mars and Jupiter (2.2–3.2 AU = 70–102 scene units)
// Kuiper belt: beyond Neptune (30–50 AU = 960–1600 scene units)
// Scattered disc: 38–70 AU = 1216–2240 scene units
const mainBelt      = makeOrbitingBelt(6000,   70,  102,   4, 0xAA9977, 0.55, 0.35);
const kuiperBelt    = makeOrbitingBelt(4000,  990, 1620,   8, 0x8899AA, 0.28, 0.30);
const scatteredDisc = makeOrbitingBelt(1500, 1220, 2250,  35, 0x667788, 0.22, 0.28);

// Oort cloud — true spherical shell, not a disc
// Each particle orbits with random inclination — very slow (periods of millions of years)
const OOT_COUNT = 4000;
const ootPos    = new Float32Array(OOT_COUNT * 3);
const ootParams = new Float32Array(OOT_COUNT * 5); // sma, ecc, inc, angle0, node
for (let i=0; i<OOT_COUNT; i++) {
  const sma    = 580 + Math.random() * 1200; // scene units (~18k–56k AU)
  const ecc    = Math.random() * 0.6;
  const inc    = Math.acos(2 * Math.random() - 1); // uniform on sphere
  const angle0 = Math.random() * Math.PI * 2;
  const node   = Math.random() * Math.PI * 2;
  ootParams[i*5]   = sma;
  ootParams[i*5+1] = ecc;
  ootParams[i*5+2] = inc;
  ootParams[i*5+3] = angle0;
  ootParams[i*5+4] = node;
  // Initial position
  const r    = sma * (1 - ecc * Math.cos(angle0));
  const xOrb = r * Math.cos(angle0);
  const zOrb = r * Math.sin(angle0);
  const sinI = Math.sin(inc), cosI = Math.cos(inc);
  const sinN = Math.sin(node), cosN = Math.cos(node);
  ootPos[i*3]   =  xOrb * cosN - zOrb * sinN * cosI;
  ootPos[i*3+1] =  zOrb * sinI;
  ootPos[i*3+2] =  xOrb * sinN + zOrb * cosN * cosI;
}
const ootGeo = new THREE.BufferGeometry();
ootGeo.setAttribute('position', new THREE.BufferAttribute(ootPos, 3));
const ootCloud = {
  geo: ootGeo,
  pts: new THREE.Points(ootGeo,
    new THREE.PointsMaterial({color:0x99BBDD, size:1.2, transparent:true, opacity:0.35, sizeAttenuation:false})
  )
};
solarPivot.add(ootCloud.pts);

function updateOortCloud() {
  const AU = 32;
  const TWO_PI = Math.PI * 2;
  for (let i=0; i<OOT_COUNT; i++) {
    const sma    = ootParams[i*5];
    const ecc    = ootParams[i*5+1];
    const inc    = ootParams[i*5+2];
    const angle0 = ootParams[i*5+3];
    const node   = ootParams[i*5+4];
    // Period in years from Kepler's 3rd law (sma in AU)
    const smaAU  = sma / AU;
    const period = Math.pow(smaAU, 1.5);
    const M      = (TWO_PI * simTime / period) + angle0;
    const E      = keplerE(M, ecc);
    const r      = sma * (1 - ecc * Math.cos(E));
    const xOrb   = r * Math.cos(E);
    const zOrb   = r * Math.sin(E);
    const sinI = Math.sin(inc), cosI = Math.cos(inc);
    const sinN = Math.sin(node), cosN = Math.cos(node);
    ootPos[i*3]   =  xOrb * cosN - zOrb * sinN * cosI;
    ootPos[i*3+1] =  zOrb * sinI;
    ootPos[i*3+2] =  xOrb * sinN + zOrb * cosN * cosI;
  }
  ootGeo.attributes.position.needsUpdate = true;
}

// ── Heliopause ────────────────────────────────────────────────────────────────
// The boundary where solar wind is stopped by interstellar medium.
// Voyager 1 crossed at ~121 AU (2012), Voyager 2 at ~119 AU (2018).
// Slightly oblate — compressed ~20% on the leading edge (direction of solar travel).
// 1 AU = 32 scene units → 120 AU = 3840 scene units
{
  const HP_AU = 120;
  const HP_R  = HP_AU * 32; // 32 scene units per AU
  const hpGeo = new THREE.SphereGeometry(HP_R, 64, 64);
  // Compress leading edge (−Z is travel direction in solar view)
  hpGeo.scale(1.0, 0.92, 1.18); // slightly elongated trailing, compressed leading
  const hpMat = new THREE.MeshBasicMaterial({
    color: 0x4488bb,
    transparent: true,
    opacity: 0.007,
    side: THREE.BackSide,
    depthWrite: false,
  });
  const hpMesh = new THREE.Mesh(hpGeo, hpMat);
  solarPivot.add(hpMesh);

  // Faint edge glow
  const hpGeo2 = new THREE.SphereGeometry(HP_R * 1.01, 64, 64);
  hpGeo2.scale(1.0, 0.92, 1.18);
  const hpMat2 = new THREE.MeshBasicMaterial({
    color: 0x66aacc,
    transparent: true,
    opacity: 0.01,
    side: THREE.FrontSide,
    depthWrite: false,
  });
  const hpMesh2 = new THREE.Mesh(hpGeo2, hpMat2);
  solarPivot.add(hpMesh2);
}


function updateBelt(belt) {
  const { pos, params, count, geo } = belt;
  const AU = 32;
  const TWO_PI = Math.PI * 2;
  for (let i=0; i<count; i++) {
    const sma    = params[i*5];
    const ecc    = params[i*5+1];
    const inc    = params[i*5+2];
    const angle0 = params[i*5+3];
    const node   = params[i*5+4];
    const period = Math.pow(sma / AU, 1.5);
    const phase  = (simTime / period) % 1.0;
    const ang    = TWO_PI * phase + angle0;
    const r      = sma * (1 - ecc * Math.cos(ang - angle0));
    const xOrb   = r * Math.cos(ang);
    const zOrb   = r * Math.sin(ang);
    const sinI = Math.sin(inc), cosI = Math.cos(inc);
    const sinN = Math.sin(node), cosN = Math.cos(node);
    pos[i*3]   =  xOrb * cosN - zOrb * sinN * cosI;
    pos[i*3+1] =  zOrb * sinI;
    pos[i*3+2] =  xOrb * sinN + zOrb * cosN * cosI;
  }
  geo.attributes.position.needsUpdate = true;
}


// ── Comets ────────────────────────────────────────────────────────────────────
// Real famous comets with accurate orbital elements.
// Each has a glowing nucleus + dust tail + ion tail (both pointing away from Sun).
// sma in scene units, period in years, ecc = eccentricity, inc = degrees
const COMET_DATA = [
  { name:"Halley's",    sma:570,   ecc:0.967, inc:162.3, period:75.3,   r:0.12, color:0xAADDFF },
  { name:"Hale-Bopp",   sma:5968,  ecc:0.995, inc:89.4,  period:2520,   r:0.14, color:0xCCEEFF },
  { name:"Hyakutake",   sma:54400, ecc:0.9998,inc:124.9, period:70000,  r:0.10, color:0xBBEEFF },
  { name:"Encke",       sma:71,    ecc:0.847, inc:11.8,  period:3.3,    r:0.08, color:0xDDEEAA },
  { name:"67P/C-G",     sma:111,   ecc:0.641, inc:7.04,  period:6.44,   r:0.09, color:0xBBBBAA },
  { name:"Tempel 1",    sma:101,   ecc:0.517, inc:10.5,  period:5.52,   r:0.08, color:0xCCBBAA },
  { name:"Wild 2",      sma:110,   ecc:0.539, inc:3.24,  period:6.41,   r:0.08, color:0xBBCCAA },
  { name:"Shoemaker-L9",sma:178,   ecc:0.980, inc:6.0,   period:800,    r:0.10, color:0xDDCCBB },
  { name:"NEOWISE",     sma:11424, ecc:0.9992,inc:128.9, period:6766,   r:0.11, color:0xAADDEE },
  { name:"Ikeya-Seki",  sma:2912,  ecc:0.9999,inc:141.9, period:880,    r:0.09, color:0xFFEEBB },
];

// Tail geometry: series of points streaming away from Sun
const TAIL_PTS = 120;

const comets = [];
for (const cd of COMET_DATA) {
  const visualOrbit = getVisualCometOrbit(cd);
  const b = visualOrbit.b;
  const c = visualOrbit.c;

  const incGrp = new THREE.Group();
  incGrp.rotation.x = THREE.MathUtils.degToRad(cd.inc);
  solarPivot.add(incGrp);

  // Orbit line (very faint — comets have extreme ellipses)
  const oPts = [];
  for (let i=0; i<=512; i++) {
    const t = (i/512)*Math.PI*2;
    oPts.push(new THREE.Vector3(cd.sma*Math.cos(t)-c, 0, b*Math.sin(t)));
  }
  const orbitLine = createOrbitLineOutsideSun(
    oPts,
    new THREE.LineBasicMaterial({ color:0x223344, transparent:true, opacity:0.2 }),
    COMET_ORBIT_LINE_EXCLUSION_RADIUS,
    false
  );
  incGrp.add(orbitLine);

  // Nucleus
  const nucleus = new THREE.Mesh(
    new THREE.SphereGeometry(cd.r, 12, 12),
    new THREE.MeshPhongMaterial({ color:cd.color, emissive:cd.color, emissiveIntensity:0.4, shininess:5 })
  );
  incGrp.add(nucleus);
  // Large invisible hit sphere so comets are easy to click
  const cometHit = new THREE.Mesh(
    new THREE.SphereGeometry(cd.r * 10, 6, 6),
    new THREE.MeshBasicMaterial({ visible:false })
  );
  cometHit.userData.cometNucleus = nucleus; // link back to nucleus
  nucleus.add(cometHit);

  // Coma (glow around nucleus) — updated each frame in updateComets
  const coma = new THREE.Mesh(
    new THREE.SphereGeometry(cd.r * 1.8, 12, 12),
    new THREE.MeshBasicMaterial({ color:cd.color, transparent:true, opacity:0.12 })
  );
  nucleus.add(coma);
  nucleus.userData.coma = coma;

  const DUST_PTS = 800;
  const dustGeo = new THREE.BufferGeometry();
  const dustPos = new Float32Array(DUST_PTS * 3);
  const dustCol = new Float32Array(DUST_PTS * 3);
  dustGeo.setAttribute('position', new THREE.BufferAttribute(dustPos, 3));
  dustGeo.setAttribute('color',    new THREE.BufferAttribute(dustCol, 3));
  dustGeo.setDrawRange(0, 0);
  const dustSiz = null;
  const dustTail = new THREE.Points(dustGeo,
    new THREE.PointsMaterial({ map:dotTex, vertexColors:true, size:0.25, transparent:true, opacity:0.9, sizeAttenuation:true, depthWrite:false, alphaTest:0.01 })
  );
  dustTail.raycast = () => {};
  dustTail.frustumCulled = false;
  scene.add(dustTail);

  const ION_PTS = 400;
  const ionGeo  = new THREE.BufferGeometry();
  const ionPos  = new Float32Array(ION_PTS * 3);
  const ionCol  = new Float32Array(ION_PTS * 3);
  ionGeo.setAttribute('position', new THREE.BufferAttribute(ionPos, 3));
  ionGeo.setAttribute('color',    new THREE.BufferAttribute(ionCol, 3));
  ionGeo.setDrawRange(0, 0);
  const ionSiz = null;
  const ionTail = new THREE.Points(ionGeo,
    new THREE.PointsMaterial({ map:dotTex, vertexColors:true, size:0.15, transparent:true, opacity:0.8, sizeAttenuation:true, depthWrite:false, alphaTest:0.01 })
  );
  ionTail.raycast = () => {};
  ionTail.frustumCulled = false;
  scene.add(ionTail);

  comets.push({
    cd, incGrp, nucleus, orbitLine,
    dustTail, dustGeo, dustPos, dustCol, dustSiz,
    ionTail,  ionGeo,  ionPos,  ionCol,  ionSiz,
    b, c, orbitEcc: visualOrbit.ecc,
    angle0: Math.random() * Math.PI * 2,
    tailDir: new THREE.Vector3(1, 0, 0), // smoothed tail direction
  });
}

// Update comet positions and tails
const _cometWPos  = new THREE.Vector3();
const _sunWPos2   = new THREE.Vector3();
const _cometToSun = new THREE.Vector3();
const _cometAwayFromSun = new THREE.Vector3();
const _cometTailRight = new THREE.Vector3();
const _cometTailUp = new THREE.Vector3();
function updateComets() {
  sunMesh.getWorldPosition(_sunWPos2);

  for (const cm of comets) {
    const M = (2*Math.PI*simTime/cm.cd.period) + cm.angle0;
    const E = keplerE(M, cm.orbitEcc);
    const lx = cm.cd.sma * Math.cos(E) - cm.c;
    const lz = cm.b * Math.sin(E);
    cm.nucleus.position.set(lx, 0, lz);
    // Get world position of nucleus
    cm.nucleus.getWorldPosition(_cometWPos);

    // Direction: directly away from Sun, no smoothing (lerp caused streak through Sun)
    _cometToSun.subVectors(_sunWPos2, _cometWPos);
    const distToSun = Math.max(_cometToSun.length(), 1);
    _cometAwayFromSun.copy(_cometToSun).negate().normalize();

    const AU = 32;
    const distAU = distToSun / AU;
    const tailStrength = Math.max(0, 1 - distAU / 6);
    cm.nucleus.userData.coma.visible = tailStrength > 0.01;
    const maxTailLen = 60;
    const dustLen = Math.min(tailStrength * 50, maxTailLen);
    const ionLen  = Math.min(tailStrength * 30, maxTailLen * 0.6);

    // Dust tail — stable fan shape, no random per frame
    _cometTailRight.crossVectors(_worldUp, _cometAwayFromSun);
    if (_cometTailRight.lengthSq() < 1e-8) _cometTailRight.set(1, 0, 0);
    else _cometTailRight.normalize();
    _cometTailUp.crossVectors(_cometAwayFromSun, _cometTailRight).normalize();
    const DUST_PTS = cm.dustPos.length / 3;
    for (let i=0; i<DUST_PTS; i++) {
      // Use golden angle for stable, even distribution
      const f      = i / DUST_PTS;
      const golden = i * 2.399963; // golden angle in radians
      const spread = f * f * dustLen * 0.12; // tight fan
      const len    = f * dustLen;
      const sx = Math.cos(golden) * spread * _cometTailRight.x + Math.sin(golden) * spread * _cometTailUp.x;
      const sy = Math.cos(golden) * spread * _cometTailRight.y + Math.sin(golden) * spread * _cometTailUp.y;
      const sz = Math.cos(golden) * spread * _cometTailRight.z + Math.sin(golden) * spread * _cometTailUp.z;
      cm.dustPos[i*3]   = _cometWPos.x + _cometAwayFromSun.x*len + sx;
      cm.dustPos[i*3+1] = _cometWPos.y + _cometAwayFromSun.y*len + sy;
      cm.dustPos[i*3+2] = _cometWPos.z + _cometAwayFromSun.z*len + sz;
      const bright = Math.pow(1-f, 0.8) * tailStrength;
      cm.dustCol[i*3]   = bright * 0.9;
      cm.dustCol[i*3+1] = bright * 0.92;
      cm.dustCol[i*3+2] = bright * 1.0;
    }
    cm.dustGeo.attributes.position.needsUpdate = true;
    cm.dustGeo.attributes.color.needsUpdate    = true;
    cm.dustGeo.setDrawRange(0, tailStrength > 0.01 ? DUST_PTS : 0);

    // Ion tail — narrower, straighter, more blue
    const ION_PTS = cm.ionPos.length / 3;
    for (let i=0; i<ION_PTS; i++) {
      const f      = i / ION_PTS;
      const golden = i * 2.399963;
      const spread = f * f * ionLen * 0.04;
      const len    = f * ionLen;
      const sx = Math.cos(golden) * spread * _cometTailRight.x + Math.sin(golden) * spread * _cometTailUp.x;
      const sy = Math.cos(golden) * spread * _cometTailRight.y + Math.sin(golden) * spread * _cometTailUp.y;
      const sz = Math.cos(golden) * spread * _cometTailRight.z + Math.sin(golden) * spread * _cometTailUp.z;
      cm.ionPos[i*3]   = _cometWPos.x + _cometAwayFromSun.x*len + sx;
      cm.ionPos[i*3+1] = _cometWPos.y + _cometAwayFromSun.y*len + sy;
      cm.ionPos[i*3+2] = _cometWPos.z + _cometAwayFromSun.z*len + sz;
      const bright = Math.pow(1-f, 1.5) * tailStrength * 0.7;
      cm.ionCol[i*3]   = bright * 0.5;
      cm.ionCol[i*3+1] = bright * 0.8;
      cm.ionCol[i*3+2] = bright * 1.0;
    }
    cm.ionGeo.attributes.position.needsUpdate = true;
    cm.ionGeo.attributes.color.needsUpdate    = true;
    cm.ionGeo.setDrawRange(0, tailStrength > 0.01 ? ION_PTS : 0);
  }
}


const sunTrailGeo = new THREE.BufferGeometry();

//  Voyager probes 
const { AU_SCENE, VOYAGER_DATA, getProbePos } = window.SOLVoyager;

// Earth orbital params (scene units, years)
const EARTH_SMA = 32, EARTH_ECC = 0.017, EARTH_PERIOD = 1.0;

function getEarthPos(year) {
  const b = EARTH_SMA * Math.sqrt(1 - EARTH_ECC * EARTH_ECC);
  const c = EARTH_SMA * EARTH_ECC;
  const M = (2 * Math.PI * year / EARTH_PERIOD) + earthAngle0;
  const E = keplerE(M, EARTH_ECC);
    const nuE = 2*Math.atan2(Math.sqrt(1+EARTH_ECC)*Math.sin(E/2), Math.sqrt(1-EARTH_ECC)*Math.cos(E/2));
  const rE  = EARTH_SMA*(1-EARTH_ECC*EARTH_ECC)/(1+EARTH_ECC*Math.cos(nuE));
  const lonE = nuE + 102.937*Math.PI/180; // Earth omega (Omega=0 so no ascending node correction)
  return new THREE.Vector3(rE*Math.cos(lonE), 0, -rE*Math.sin(lonE)); // Earth inc=0
}


// Build spacecraft meshes — simple cross/antenna shape
function makeSpacecraftMesh(color) {
  const grp = new THREE.Group();
  // Main bus (small box)
  grp.add(new THREE.Mesh(
    new THREE.BoxGeometry(0.4, 0.15, 0.4),
    new THREE.MeshPhongMaterial({ color, emissive:color, emissiveIntensity:0.5 })
  ));
  // Dish antenna (flat cone pointing forward)
  const dish = new THREE.Mesh(
    new THREE.ConeGeometry(0.35, 0.12, 12),
    new THREE.MeshPhongMaterial({ color:0xCCCCCC, emissive:0x444444 })
  );
  dish.rotation.x = Math.PI / 2;
  dish.position.z = 0.12;
  grp.add(dish);
  // Solar panels (flat boxes)
  const panel = new THREE.BoxGeometry(1.2, 0.02, 0.25);
  const panelMat = new THREE.MeshPhongMaterial({ color:0x334466, emissive:0x111133 });
  const p1 = new THREE.Mesh(panel, panelMat);
  const p2 = new THREE.Mesh(panel, panelMat);
  p1.position.x =  0.8;
  p2.position.x = -0.8;
  grp.add(p1, p2);
  return grp;
}

const probes = [];
for (const vd of VOYAGER_DATA) {
  const mesh = makeSpacecraftMesh(vd.color);
  mesh.scale.setScalar(2.5);
  solarPivot.add(mesh);

  // Trail: Line geometry using trajectory array directly → smooth curves at flybys.
  const PROBE_TRAIL_PTS = vd.trajectory.length;
  const trailGeo = new THREE.BufferGeometry();
  const trailPos = new Float32Array(PROBE_TRAIL_PTS * 3);
  const trailCol = new Float32Array(PROBE_TRAIL_PTS * 3);
  trailGeo.setAttribute('position', new THREE.BufferAttribute(trailPos, 3));
  trailGeo.setAttribute('color',    new THREE.BufferAttribute(trailCol, 3));
  trailGeo.setDrawRange(0, 0);
  const trailMat = new THREE.LineBasicMaterial({
    vertexColors: true, transparent: true, opacity: 0.95,
    linewidth: 1, depthWrite: false,
  });
  const trailLine = new THREE.Line(trailGeo, trailMat);
  trailLine.frustumCulled = false;
  scene.add(trailLine);
  // Small dot trail layered on top for the dotted look
  const dotGeo = new THREE.BufferGeometry();
  const dotPos = new Float32Array(PROBE_TRAIL_PTS * 3);
  const dotCol = new Float32Array(PROBE_TRAIL_PTS * 3);
  const dotSiz = new Float32Array(PROBE_TRAIL_PTS);
  dotGeo.setAttribute('position', new THREE.BufferAttribute(dotPos, 3));
  dotGeo.setAttribute('color',    new THREE.BufferAttribute(dotCol, 3));
  dotGeo.setAttribute('size',     new THREE.BufferAttribute(dotSiz, 1));
  dotGeo.setDrawRange(0, 0);
  const dotLine = new THREE.Points(dotGeo, trailPointMat.clone());
  dotLine.frustumCulled = false;
  scene.add(dotLine);

  // Glow point at probe location
  const glowGeo = new THREE.BufferGeometry();
  const glowPos = new Float32Array(3);
  const glowCol = new Float32Array([1, 1, 1]);
  const glowSiz = new Float32Array([3.0]);
  glowGeo.setAttribute('position', new THREE.BufferAttribute(glowPos, 3));
  glowGeo.setAttribute('color',    new THREE.BufferAttribute(glowCol, 3));
  glowGeo.setAttribute('size',     new THREE.BufferAttribute(glowSiz, 1));
  const glowPt = new THREE.Points(glowGeo, trailPointMat.clone());
  glowPt.frustumCulled = false;
  scene.add(glowPt);

  probes.push({
    vd, mesh, trailGeo, trailPos, trailCol, trailLine,
    dotGeo, dotPos, dotCol, dotSiz, dotLine,
    glowGeo, glowPos, glowPt, PROBE_TRAIL_PTS,
  });
}

function updateProbes() {
  for (const pr of probes) {
    const vd = pr.vd;
    const currentYear = 2000 + simTime;

    // Only show after launch
    const launched = currentYear >= vd.launchYear;
    pr.mesh.visible = launched && (viewMode === 'solar');
    pr.glowPt.visible = launched && (viewMode === 'solar');
    pr.trailLine.visible = launched && (viewMode === 'solar') && orbitsOn;
    pr.dotLine.visible   = launched && (viewMode === 'solar') && orbitsOn;

    if (!launched) continue;

    // Current position from waypoints
    const pos = getProbePos(vd, currentYear);
    pr.mesh.position.copy(pos);

    // Orient toward direction of travel
    const posNext = getProbePos(vd, currentYear + 0.5);
    const dir = posNext.clone().sub(pos).normalize();
    pr.mesh.lookAt(pos.clone().add(dir));

    // Scale mesh
    const scale = Math.max(2, Math.min(20, camR * 0.02));
    pr.mesh.scale.setScalar(scale);

    // Glow point
    pr.glowPos[0] = pos.x; pr.glowPos[1] = pos.y; pr.glowPos[2] = pos.z;
    pr.glowGeo.attributes.position.needsUpdate = true;

    // Trail — render directly from trajectory array for accurate flyby curves.
    const traj = vd.trajectory;
    const cr = ((vd.color >> 16) & 0xff) / 255;
    const cg = ((vd.color >> 8)  & 0xff) / 255;
    const cb = ( vd.color        & 0xff) / 255;
    const startSim = vd.launchYear - 2000;
    const simSpan = Math.max(1e-9, simTime - startSim);
    const DAILY_STRIDE = 2;             // every other daily = smooth line
    const HOURLY_THRESH = 1.5 / 365.25; // dt < 1.5 days = hourly data
    const DOT_STRIDE = 6;               // dots every 6th daily point
    let n = 0, nd = 0;
    for (let i = 0; i < traj.length; i++) {
      const t = traj[i][0];
      if (t < startSim || t > simTime) continue;
      const dt = i > 0 ? traj[i][0] - traj[i-1][0] : 999;
      const isHourly = dt < HOURLY_THRESH;
      // Line: keep hourly always, daily every other point
      if (!isHourly && (i % DAILY_STRIDE !== 0)) continue;
      const f = Math.max(0, Math.min(1, (t - startSim) / simSpan));
      const fade = 0.08 + 0.92 * f;
      pr.trailPos[n*3]   = Number.isFinite(traj[i][1]) ? traj[i][1] : 0;
      pr.trailPos[n*3+1] = Number.isFinite(traj[i][2]) ? traj[i][2] : 0;
      pr.trailPos[n*3+2] = Number.isFinite(traj[i][3]) ? traj[i][3] : 0;
      pr.trailCol[n*3]   = cr * fade;
      pr.trailCol[n*3+1] = cg * fade;
      pr.trailCol[n*3+2] = cb * fade;
      // Dots: only on daily-spaced points (not hourly) to avoid blob at flybys
      if (!isHourly && n % DOT_STRIDE === 0) {
        pr.dotPos[nd*3]   = traj[i][1];
        pr.dotPos[nd*3+1] = traj[i][2];
        pr.dotPos[nd*3+2] = traj[i][3];
        pr.dotCol[nd*3]   = cr * fade;
        pr.dotCol[nd*3+1] = cg * fade;
        pr.dotCol[nd*3+2] = cb * fade;
        pr.dotSiz[nd] = 0.4 + f * 0.8;
        nd++;
      }
      n++;
    }
    // If simTime is beyond last data point, append extrapolated current position
    // so the trail always extends to the probe's actual rendered location
    const lastT = traj[traj.length - 1][0];
    if (simTime > lastT && n > 0 && n < pr.PROBE_TRAIL_PTS) {
      pr.trailPos[n*3]   = Number.isFinite(pos.x) ? pos.x : 0;
      pr.trailPos[n*3+1] = Number.isFinite(pos.y) ? pos.y : 0;
      pr.trailPos[n*3+2] = Number.isFinite(pos.z) ? pos.z : 0;
      pr.trailCol[n*3]   = cr;
      pr.trailCol[n*3+1] = cg;
      pr.trailCol[n*3+2] = cb;
      n++;
    }
    pr.trailGeo.attributes.position.needsUpdate = true;
    pr.trailGeo.attributes.color.needsUpdate = true;
    pr.trailGeo.setDrawRange(0, n);
    pr.dotGeo.attributes.position.needsUpdate = true;
    pr.dotGeo.attributes.color.needsUpdate = true;
    pr.dotGeo.attributes.size.needsUpdate = true;
    pr.dotGeo.setDrawRange(0, nd);
  }
}


const sunTrailBuf = new Float32Array(TRAIL_LEN * 3);
const sunTrailColBuf = new Float32Array(TRAIL_LEN * 3);
const sunTrailSizBuf = new Float32Array(TRAIL_LEN);
sunTrailGeo.setAttribute('position', new THREE.BufferAttribute(sunTrailBuf, 3));
sunTrailGeo.setAttribute('color',    new THREE.BufferAttribute(sunTrailColBuf, 3));
sunTrailGeo.setAttribute('size',     new THREE.BufferAttribute(sunTrailSizBuf, 1));
sunTrailGeo.setDrawRange(0, 0);
const sunTrailLine = new THREE.Points(sunTrailGeo, trailPointMat.clone());
sunTrailLine.frustumCulled = false;
scene.add(sunTrailLine);


let viewMode  = 'solar'; // 'solar' | 'vortex'
let paused    = false;
let trailsOn  = true;
let orbitsOn  = true;
let constellationsOn = true;
const DEBUG_FLAGS = {
  earthOrientationMarker: false,
  earthTravelMarker: false,
};

function setEarthOrientationMarkerVisible(visible) {
  DEBUG_FLAGS.earthOrientationMarker = !!visible;
  for (const planet of planets) {
    if (planet.mesh.userData.orientationMarker) {
      planet.mesh.userData.orientationMarker.visible = DEBUG_FLAGS.earthOrientationMarker;
    }
  }
}

function setEarthTravelMarkerVisible(visible) {
  DEBUG_FLAGS.earthTravelMarker = !!visible;
  for (const planet of planets) {
    if (planet.mesh.userData.travelMarker) {
      planet.mesh.userData.travelMarker.visible = DEBUG_FLAGS.earthTravelMarker;
    }
  }
}

window.SOL_DEBUG = Object.assign(window.SOL_DEBUG || {}, {
  flags: DEBUG_FLAGS,
  setEarthOrientationMarkerVisible,
  setEarthTravelMarkerVisible,
});
setEarthOrientationMarkerVisible(DEBUG_FLAGS.earthOrientationMarker);
setEarthTravelMarkerVisible(DEBUG_FLAGS.earthTravelMarker);

const J2000_MS = Date.UTC(2000, 0, 1, 12, 0, 0);
const MS_PER_YEAR = 365.25 * 24 * 3600 * 1000;
function getCurrentSimTime() {
  return (Date.now() - J2000_MS) / MS_PER_YEAR;
}
let simTime   = getCurrentSimTime();
let sunZ      = 0; // galactic travel — derived after speed constants initialize, never accumulated

// Camera: spherical coords around a target point
// User rotates/zooms; target smoothly follows Sun in vortex mode
let camTheta  = 0.4;   // azimuth
let camPhi    = 1.08;  // polar
let camR      = 800;   // radius
const camTarget = new THREE.Vector3(0,0,0);
const lyValEl = document.getElementById('ly-val');
// Per-view default phi/r (theta is user-controlled)
const VIEW_DEFAULTS = {
  solar:  { phi:1.08, r:800   },
  vortex: { phi:1.18, r:5200  },
};
let targetPhi = VIEW_DEFAULTS.solar.phi;
let targetR   = VIEW_DEFAULTS.solar.r;

// Speed: sim-years per real second
const spdEl  = document.getElementById('spd');
const spdVal_el = document.getElementById('spd-val');
function getSimSpeed(){
  // Slider 0-100 maps to 1 hour/s → 3 years/s
  // 1 hour = 1/8760 years, 3 years = 3
  // log10(1/8760) ≈ -3.943, log10(3) ≈ 0.477
  const t = parseFloat(spdEl.value) / 100;
  return Math.pow(10, t * (0.477 - (-3.943)) + (-3.943));
}
function updateSpdLabel(){
  const s = getSimSpeed();
  const d = s * 365.25;
  const h = s * 8760;
  if      (h < 24)   spdVal_el.textContent = h.toFixed(1)  + ' hr/s';
  else if (d < 365)  spdVal_el.textContent = d.toFixed(1)  + ' d/s';
  else               spdVal_el.textContent = s.toFixed(2)  + ' yr/s';
}
spdEl.addEventListener('input', updateSpdLabel);
updateSpdLabel();

// ── Timeline slider ───────────────────────────────────────────────────────────
// Slider value = years offset from J2000. simTime = slider value.
// Display as actual calendar year: 2000 + simTime.
const tlEl   = document.getElementById('tl');
const tlYear = document.getElementById('timeline-year');
let timelineDragging = false;
let lastTimelineLabel = '';
let lastTimelineSliderValue = Number.NaN;

function formatYear(simT) {
  const calYear = 2000 + simT;
  const absYear = Math.abs(calYear);
  // For very large timescales use compact notation
  if (absYear >= 1e9) return (calYear/1e9).toFixed(2) + 'B yr ' + (calYear < 0 ? 'BC' : 'AD');
  if (absYear >= 1e6) return (calYear/1e6).toFixed(2) + 'M yr ' + (calYear < 0 ? 'BC' : 'AD');
  if (absYear >= 1e4) return Math.round(absYear).toLocaleString() + ' ' + (calYear < 0 ? 'BC' : 'AD');
  // Within ~10,000 years: show full date and time
  // Convert simT (fractional years from J2000) to a real date
  // J2000 epoch = 2000-01-01 12:00:00 UTC
  const dateMs = J2000_MS + simT * MS_PER_YEAR;
  const d = new Date(dateMs);
  // JS Date handles years 0–9999 directly; outside that we need manual formatting
  const yr = d.getUTCFullYear();
  if (yr < 1 || yr > 9999) {
    // Fallback for edge of JS date range
    const absY = Math.abs(calYear);
    return absY.toFixed(0) + ' ' + (calYear < 0 ? 'BC' : 'AD');
  }
  const months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
  const mon  = months[d.getUTCMonth()];
  const day  = String(d.getUTCDate()).padStart(2, '0');
  const hour = String(d.getUTCHours()).padStart(2, '0');
  const min  = String(d.getUTCMinutes()).padStart(2, '0');
  return `${day} ${mon} ${yr}  ${hour}:${min} UTC`;
}

function updateTimelineDisplay() {
  const nextLabel = formatYear(simTime);
  if (nextLabel !== lastTimelineLabel) {
    tlYear.textContent = nextLabel;
    lastTimelineLabel = nextLabel;
  }
  if (!timelineDragging) {
    const nextSliderValue = Math.max(-5e5, Math.min(5e5, simTime));
    if (nextSliderValue !== lastTimelineSliderValue) {
      tlEl.value = nextSliderValue;
      lastTimelineSliderValue = nextSliderValue;
    }
  }
}

tlEl.addEventListener('mousedown', () => { timelineDragging = true; });
tlEl.addEventListener('touchstart', () => { timelineDragging = true; });
tlEl.addEventListener('mouseup',   () => { timelineDragging = false; });
tlEl.addEventListener('touchend',  () => { timelineDragging = false; });
tlEl.addEventListener('input', () => {
  simTime = parseFloat(tlEl.value);
  sunZ = simTime * GALACTIC_SCENE_SPEED;
  updateTimelineDisplay();
});

// Step buttons: ±10, 100, 1000, 10000 years
document.querySelectorAll('.tlstep').forEach(b => {
  b.addEventListener('click', () => {
    simTime += parseFloat(b.dataset.step);
    sunZ = simTime * GALACTIC_SCENE_SPEED;
    updateTimelineDisplay();
  });
});

// Hard point buttons
document.querySelectorAll('.tlhp').forEach(b => {
  b.addEventListener('click', () => {
    simTime = b.classList.contains('now') ? getCurrentSimTime() : parseFloat(b.dataset.year);
    sunZ = simTime * GALACTIC_SCENE_SPEED;
    updateTimelineDisplay();
  });
});

// Galactic travel speed: 230 km/s ≈ 7.25 ly/yr. Scale to scene units.
const GALACTIC_SCENE_SPEED = 214; // scene units per sim-year — calibrated so v_orbital/v_sun ratios are correct

// ── View switching ────────────────────────────────────────────────────────────
function setView(mode) {
  if (mode !== 'solar' && mode !== 'vortex') mode = 'solar';
  viewMode = mode;
  document.querySelectorAll('.vbtn').forEach(b=>b.classList.remove('active'));
  document.getElementById('btn-'+mode).classList.add('active');
  const def = VIEW_DEFAULTS[mode];
  targetPhi = def.phi;
  targetR   = def.r;

  if (mode === 'vortex' && !focusMesh) camTheta = -0.72;

  // Orbit rings only in solar mode
  for (const p of planets) p.orbitLine.visible = orbitsOn && (mode==='solar');
  for (const d of dwarfs) d.orbitLine.visible = orbitsOn && (mode==='solar');
  for (const c of comets) c.orbitLine.visible = orbitsOn && (mode==='solar');
  for (const m of moons) m.moonOrbitLine.visible = orbitsOn && (mode==='solar');
  // Trails only relevant in vortex mode
  document.getElementById('trails-btn').style.display = mode === 'solar' ? 'none' : '';
  updateSpdLabel();
}

document.getElementById('btn-solar').addEventListener('click',   ()=>setView('solar'));
document.getElementById('btn-vortex').addEventListener('click',  ()=>setView('vortex'));

function clearTrails() {
  sunTrailGeo.setDrawRange(0,0);
  sunTrailGeo.setDrawRange(0,0);
  for (const p of planets) {
    p.trailWorldPts.length = 0;
    p.trailGeo.setDrawRange(0,0);
  }
}

const helpPanel = document.getElementById('help-panel');
const helpBtn = document.getElementById('help-btn');
function setHelpOpen(open) {
  helpPanel.classList.toggle('show', open);
  helpPanel.setAttribute('aria-hidden', open ? 'false' : 'true');
  helpBtn.classList.toggle('active', open);
}
helpBtn.addEventListener('click', () => setHelpOpen(!helpPanel.classList.contains('show')));

const mobileMedia = window.matchMedia('(max-width: 900px)');
const coarsePointerMedia = window.matchMedia('(pointer: coarse)');
const mobileBackdrop = document.getElementById('mobile-backdrop');
const mobilePanelButtons = Array.from(document.querySelectorAll('.mnav-btn'));

function isMobileUiViewport() {
  return mobileMedia.matches || (coarsePointerMedia.matches && window.innerHeight <= 600);
}

function syncMobilePanelButtons() {
  const activePanel = document.body.dataset.mobilePanel || '';
  for (const button of mobilePanelButtons) {
    button.classList.toggle('active', button.dataset.mobilePanel === activePanel);
  }
}

function closeMobilePanels() {
  document.body.dataset.mobilePanel = '';
  document.body.classList.remove('mobile-panel-open');
  syncMobilePanelButtons();
}

function setMobilePanel(panel) {
  if (!document.body.classList.contains('mobile-ui')) return;
  const nextPanel = document.body.dataset.mobilePanel === panel ? '' : panel;
  document.body.dataset.mobilePanel = nextPanel;
  document.body.classList.toggle('mobile-panel-open', !!nextPanel);
  syncMobilePanelButtons();
  if (nextPanel !== 'controls') setHelpOpen(false);
  if (nextPanel === 'search') {
    window.setTimeout(() => {
      const input = document.getElementById('search-input');
      input?.focus();
      input?.select();
    }, 30);
  }
}

function syncMobileUi() {
  const isMobileUi = isMobileUiViewport();
  document.body.classList.toggle('mobile-ui', isMobileUi);
  if (!isMobileUi) {
    closeMobilePanels();
    setHelpOpen(false);
  }
  syncMobilePanelButtons();
}

for (const button of mobilePanelButtons) {
  button.addEventListener('click', () => setMobilePanel(button.dataset.mobilePanel));
}
mobileBackdrop?.addEventListener('click', closeMobilePanels);
mobileMedia.addEventListener?.('change', syncMobileUi);
coarsePointerMedia.addEventListener?.('change', syncMobileUi);
syncMobileUi();

// ── Controls ──────────────────────────────────────────────────────────────────
const fullscreenBtn = document.getElementById('fullscreen-btn');

function getFullscreenElement() {
  return document.fullscreenElement || document.webkitFullscreenElement || document.msFullscreenElement || null;
}

function updateFullscreenButton() {
  if (!fullscreenBtn) return;
  const isFullscreen = !!getFullscreenElement();
  fullscreenBtn.classList.toggle('is-fullscreen', isFullscreen);
  fullscreenBtn.title = isFullscreen ? 'Exit fullscreen' : 'Enter fullscreen';
  fullscreenBtn.setAttribute('aria-label', isFullscreen ? 'Exit fullscreen' : 'Enter fullscreen');
  fullscreenBtn.setAttribute('aria-pressed', isFullscreen ? 'true' : 'false');
}

async function toggleFullscreen() {
  const fullscreenEl = getFullscreenElement();
  const docEl = document.documentElement;
  try {
    if (fullscreenEl) {
      if (document.exitFullscreen) await document.exitFullscreen();
      else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
      else if (document.msExitFullscreen) document.msExitFullscreen();
    } else {
      if (docEl.requestFullscreen) await docEl.requestFullscreen({ navigationUI:'hide' });
      else if (docEl.webkitRequestFullscreen) docEl.webkitRequestFullscreen();
      else if (docEl.msRequestFullscreen) docEl.msRequestFullscreen();
    }
  } catch (_) {
    // Ignore rejected fullscreen requests; button state is synced from the actual document state.
  }
  updateFullscreenButton();
}

fullscreenBtn?.addEventListener('click', toggleFullscreen);
document.addEventListener('fullscreenchange', updateFullscreenButton);
document.addEventListener('webkitfullscreenchange', updateFullscreenButton);
document.addEventListener('msfullscreenchange', updateFullscreenButton);
updateFullscreenButton();

document.getElementById('pause-btn').addEventListener('click', ()=>{
  paused=!paused;
  document.getElementById('pause-btn').textContent=paused?'RESUME':'PAUSE';
});
document.getElementById('orbits-btn').addEventListener('click', ()=>{
  orbitsOn=!orbitsOn;
  document.getElementById('orbits-btn').textContent=orbitsOn?'ORBITS ON':'ORBITS OFF';
  for(const p of planets) p.orbitLine.visible = orbitsOn && (viewMode==='solar');
  for(const d of dwarfs) d.orbitLine.visible = orbitsOn && (viewMode==='solar');
  for(const c of comets) c.orbitLine.visible = orbitsOn && (viewMode==='solar');
  for(const m of moons) m.moonOrbitLine.visible = orbitsOn && (viewMode==='solar');
});
document.getElementById('const-btn').addEventListener('click', () => {
  constellationsOn = !constellationsOn;
  document.getElementById('const-btn').textContent = constellationsOn ? 'CONSTELLATIONS ON' : 'CONSTELLATIONS OFF';
  constLineMesh.visible = constellationsOn;
  for(const d of dwarfs)  d.orbitLine.visible = orbitsOn && (viewMode==='solar');
  for(const c of comets)  c.orbitLine.visible = orbitsOn && (viewMode==='solar');
});
document.getElementById('trails-btn').addEventListener('click', ()=>{
  trailsOn=!trailsOn;
  document.getElementById('trails-btn').textContent=trailsOn?'TRAILS ON':'TRAILS OFF';
  sunTrailLine.visible=trailsOn;
  for(const p of planets) p.trailLine.visible=trailsOn;
});

// ── Hunter / Orion button ─────────────────────────────────────────────────────
// Orion center: RA=83.98° Dec=-1.08° — unit vector in scene space
const ORION_DIR = new THREE.Vector3(0.1048, -0.0188, 0.9943).normalize();

const _constellationFocusDir = new THREE.Vector3();
const _constellationFocusPos = new THREE.Vector3();
const _constellationTargetDir = new THREE.Vector3();

function getConstellationDirection(group) {
  const starAttr = starGeo.getAttribute('position');
  _constellationFocusDir.set(0, 0, 0);
  let count = 0;
  for (const starIndex of group.indices) {
    _constellationFocusPos.fromBufferAttribute(starAttr, starIndex);
    if (_constellationFocusPos.lengthSq() <= 1e-6) continue;
    _constellationFocusDir.add(_constellationFocusPos.normalize());
    count++;
  }
  if (!count || _constellationFocusDir.lengthSq() <= 1e-6) return null;
  return _constellationFocusDir.normalize();
}

function aimCameraAtConstellation(group, options = {}) {
  const thetaOffset = options.thetaOffset ?? 0;
  const phiOffset = options.phiOffset ?? 0;
  const skyDir = getConstellationDirection(group) || ORION_DIR;

  constellationsOn = true;
  document.getElementById('const-btn').textContent = 'CONSTELLATIONS ON';
  constLineMesh.visible = true;

  _constellationTargetDir.copy(skyDir);
  if (viewMode !== 'solar') {
    const inv = new THREE.Euler(-solarPivot.rotation.x, 0, 0);
    _constellationTargetDir.applyEuler(inv);
  }

  camTheta = Math.atan2(_constellationTargetDir.x, _constellationTargetDir.z) + thetaOffset + Math.PI;
  camPhi = Math.acos(Math.max(-1, Math.min(1, _constellationTargetDir.y))) + phiOffset;
  camPhi = THREE.MathUtils.clamp(camPhi, 1e-3, Math.PI * 2 - 1e-3);
  targetPhi = camPhi;
  cameraRoll = 0;
  lookAtSun = false;
  btnLookAtSun.classList.remove('active');
}

function focusConstellationFromSearch(group, options = {}) {
  setView('solar');
  clearFocusSelection();
  aimCameraAtConstellation(group, options);
  document.getElementById('btn-orion').classList.remove('active');
}

function focusConstellationFromButton(group, options = {}) {
  aimCameraAtConstellation(group, options);
}

document.getElementById('btn-orion').addEventListener('click', () => {
  const btn = document.getElementById('btn-orion');
  if (btn.classList.contains('active')) {
    btn.classList.remove('active');
    closeMobilePanels();
    return;
  }
  focusConstellationFromButton(
    { key:'Orion', indices:[0,1,2,3,4,5,6,7] },
    { thetaOffset: 0.25, phiOffset: -0.15 }
  );
  btn.classList.add('active');
  closeMobilePanels();
});
let focusMesh = null;
// Consistent snap zoom: object fills ~30% of view regardless of size
// FOV=48°, tan(24°)=0.4452, factor = 1/(0.30*0.4452) = 7.49
function snapZoom(r) { return Math.max(5, r * 7.5); }
let focusTransitioning = false; // true during the snap-to animation

function setFocus(name) {
  const prev = focusMesh;
  geoLock = false; btnGeoLock.classList.remove('active');
  if (name === 'sun') {
    focusMesh = sunMesh;
    showInfo('sun', null);
  } else if (name === null || name === '') {
    focusMesh = null;
    focusedInfoType = null;
    focusedInfoObj = null;
    infoPanelDismissed = false;
    syncInfoPanelVisibility();
  } else {
    const p = planets.find(p => p.d.name === name);
    focusMesh = p ? p.mesh : null;
    if (p) showInfo('planet', p);
  }
  document.querySelectorAll('.fbtn').forEach(b => {
    b.classList.toggle('active', b.dataset.focus === name);
  });
  // Clear pan offset and trigger transition lerp
  userPanOffset.set(0, 0, 0);
  focusTransitioning = true;
  document.getElementById('btn-orion').classList.remove('active');
  lookAtSun = false;
  btnLookAtSun.classList.remove('active');
  // Zoom: sun zooms in close, planets zoom to their orbit scale, null = solar view
  if (focusMesh === sunMesh) {
    targetR = 80; // close-up view of the Sun
  } else if (focusMesh) {
    const p = planets.find(p => p.mesh === focusMesh);
    if (p) targetR = snapZoom(p.d.r);
  } else {
    targetR = VIEW_DEFAULTS[viewMode].r;
  }
  closeMobilePanels();
}

document.querySelectorAll('.fbtn[data-focus]').forEach(b => {
  b.addEventListener('click', () => setFocus(b.dataset.focus));
});

// Dwarf planet focus buttons
document.querySelectorAll('.fbtn[data-focus-dwarf]').forEach(b => {
  b.addEventListener('click', () => {
    const dw = dwarfs.find(d => d.d.name === b.dataset.focusDwarf);
    if (!dw) return;
    document.querySelectorAll('.fbtn').forEach(x => x.classList.remove('active'));
    document.getElementById('btn-orion').classList.remove('active');
    b.classList.add('active');
    focusMesh = dw.mesh;
    userPanOffset.set(0,0,0);
    targetR = snapZoom(dw.d.r);
    lookAtSun = false;
    btnLookAtSun.classList.remove('active');
    showInfo('dwarf', dw);
    closeMobilePanels();
  });
});

// Comet focus buttons
document.querySelectorAll('.fbtn[data-focus-comet]').forEach(b => {
  b.addEventListener('click', () => {
    const cm = comets.find(c => c.cd.name === b.dataset.focusComet);
    if (!cm) return;
    document.querySelectorAll('.fbtn').forEach(x => x.classList.remove('active'));
    document.getElementById('btn-orion').classList.remove('active');
    b.classList.add('active');
    focusMesh = cm.nucleus;
    userPanOffset.set(0,0,0);
    targetR = snapZoom(cm.cd.r);
    lookAtSun = false;
    btnLookAtSun.classList.remove('active');
    showInfo('comet', cm);
    closeMobilePanels();
  });
});

// Probe focus buttons
document.querySelectorAll('.fbtn[data-focus-probe]').forEach(b => {
  b.addEventListener('click', () => {
    const pr = probes.find(p => p.vd.name === b.dataset.focusProbe);
    if (!pr) return;
    document.querySelectorAll('.fbtn').forEach(x => x.classList.remove('active'));
    document.getElementById('btn-orion').classList.remove('active');
    b.classList.add('active');
    focusMesh = pr.mesh;
    userPanOffset.set(0,0,0);
    targetR = snapZoom(3);
    lookAtSun = false;
    btnLookAtSun.classList.remove('active');
    showInfo('probe', pr);
    closeMobilePanels();
  });
});

// Look-at-Sun toggle — only meaningful when focused on a planet
let lookAtSun = false;
const btnLookAtSun = document.getElementById('btn-lookat-sun');
btnLookAtSun.classList.remove('active');
btnLookAtSun.addEventListener('click', () => {
  if (lookAtSun) {
    lookAtSun = false;
    btnLookAtSun.classList.remove('active');
  } else {
    lookAtSun = true;
    btnLookAtSun.classList.add('active');
    if (focusMesh) {
      camTheta  = 0;
      camPhi    = Math.PI / 2;
      targetPhi = camPhi;
    }
  }
});

// Geo-lock: attach camera and target to the focused planet's local frame.
let geoLock = false;
let geoLockLocalCameraDir = new THREE.Vector3(0, 0, 1);
let geoLockLocalTargetPos = new THREE.Vector3();
let geoLockLocalUp = new THREE.Vector3(0, 1, 0);
let cameraRoll = 0;
const _geoLockLocalNorth = new THREE.Vector3(0, 1, 0);
const _geoLockRight = new THREE.Vector3();
const _focusLocalCameraPos = new THREE.Vector3();
const _focusWorldQuat2 = new THREE.Quaternion();
function rotateGeoLockView(dx, dy) {
  if (!focusMesh || !geoLock) return;
  const radius = focusMesh.geometry?.parameters?.radius ?? 1;
  const yaw = -dx * 0.005;
  const pitch = -dy * 0.005;
  geoLockLocalCameraDir.applyAxisAngle(_geoLockLocalNorth, yaw).normalize();
  geoLockLocalUp.applyAxisAngle(_geoLockLocalNorth, yaw).normalize();
  _geoLockRight.crossVectors(geoLockLocalUp, geoLockLocalCameraDir).normalize();
  if (_geoLockRight.lengthSq() > 1e-8) {
    geoLockLocalCameraDir.applyAxisAngle(_geoLockRight, pitch).normalize();
    geoLockLocalUp.applyAxisAngle(_geoLockRight, pitch).normalize();
  }
  geoLockLocalTargetPos.copy(geoLockLocalCameraDir).multiplyScalar(radius);
}
function rollFocusedView(dx) {
  if (geoLock && focusMesh) {
    geoLockLocalUp.applyAxisAngle(geoLockLocalCameraDir, dx * 0.005).normalize();
  } else {
    cameraRoll += dx * 0.005;
  }
}
function rotateFocusedView(dx, dy) {
  document.getElementById('btn-orion').classList.remove('active');
  if (geoLock && focusMesh) {
    rotateGeoLockView(dx, dy);
  } else if (lookAtSun && focusMesh) {
    camTheta += dx * 0.005;
    camPhi   += dy * 0.005;
    targetPhi = camPhi;
  } else {
    if (lookAtSun) {
      const diff = camera.position.clone().sub(camTarget);
      camR     = diff.length();
      targetR  = camR;
      camPhi   = Math.acos(Math.max(-1, Math.min(1, diff.y / camR)));
      camTheta = Math.atan2(diff.x, diff.z);
      targetPhi = camPhi;
      lookAtSun = false;
      btnLookAtSun.classList.remove('active');
    }
    camTheta -= dx * 0.005;
    camPhi   -= dy * 0.005;
    targetPhi = camPhi;
  }
}
const btnGeoLock = document.getElementById('btn-geo-lock');
btnGeoLock.addEventListener('click', () => {
  geoLock = !geoLock;
  btnGeoLock.classList.toggle('active', geoLock);
  if (geoLock && focusMesh) {
    focusMesh.updateWorldMatrix(true, false);
    _focusLocalCameraPos.copy(camera.position);
    geoLockLocalCameraDir.copy(focusMesh.worldToLocal(_focusLocalCameraPos)).normalize();
    const radius = focusMesh.geometry.parameters.radius;
    geoLockLocalTargetPos.copy(geoLockLocalCameraDir).multiplyScalar(radius);
    focusMesh.getWorldQuaternion(_focusWorldQuat2).invert();
    geoLockLocalUp.copy(camera.up).applyQuaternion(_focusWorldQuat2).normalize();
    targetPhi = camPhi;
    targetR = camR;
  }
});

let dragging=false, rDragging=false, prevX=0, prevY=0;
// panDelta accumulates user pan offset in view space
const userPanOffset = new THREE.Vector3(0,0,0);
const _mousePanRight = new THREE.Vector3();
const _mousePanView = new THREE.Vector3();
const _worldUp = new THREE.Vector3(0, 1, 0);

let mouseDragDist = 0;
renderer.domElement.addEventListener('mousedown', e=>{
  if(e.button===0)dragging=true;
  if(e.button===2)rDragging=true;
  prevX=e.clientX; prevY=e.clientY;
  mouseDragDist = 0;
  e.preventDefault();
});
renderer.domElement.addEventListener('contextmenu', e=>e.preventDefault());
window.addEventListener('mouseup', ()=>{ dragging=false; rDragging=false; });
window.addEventListener('mousemove', e=>{
  const dx=e.clientX-prevX, dy=e.clientY-prevY;
  prevX=e.clientX; prevY=e.clientY;
  if(dragging){ mouseDragDist += Math.sqrt(dx*dx+dy*dy);
    rotateFocusedView(dx, dy);
  }
  if(rDragging){
    if (focusMesh) {
      rollFocusedView(dx);
    } else {
      // Pan perpendicular to view direction
      _mousePanView.set(
        Math.sin(camPhi)*Math.sin(camTheta),
        Math.cos(camPhi),
        Math.sin(camPhi)*Math.cos(camTheta)
      );
      _mousePanRight.crossVectors(_mousePanView, _worldUp).normalize();
      const s  = camR * 0.0012;
      userPanOffset.addScaledVector(_mousePanRight, dx*s);
      userPanOffset.addScaledVector(_worldUp,       dy*s);
    }
  }
  hoverCheck(e.clientX, e.clientY);
});
renderer.domElement.addEventListener('wheel', e=>{
  // Minimum zoom: close enough to see the focused object clearly
  const minR = focusMesh ? Math.max(focusMesh.geometry?.parameters?.radius ?? 1, 1) * 2.5 : 8;
  camR = Math.max(minR, Math.min(80000, camR*(1+e.deltaY*0.001)));
  targetR = camR;
},{passive:true});

let lastTD=null;
renderer.domElement.addEventListener('touchstart', e=>{
  if(e.touches.length===1){dragging=true;prevX=e.touches[0].clientX;prevY=e.touches[0].clientY;}
  if(e.touches.length===2)lastTD=Math.hypot(e.touches[0].clientX-e.touches[1].clientX,e.touches[0].clientY-e.touches[1].clientY);
  e.preventDefault();
},{passive:false});
renderer.domElement.addEventListener('touchend',()=>{dragging=false;lastTD=null;});
renderer.domElement.addEventListener('touchmove',e=>{
  if(e.touches.length===1&&dragging){
    const dx=e.touches[0].clientX-prevX,dy=e.touches[0].clientY-prevY;
    prevX=e.touches[0].clientX;prevY=e.touches[0].clientY;
    if (geoLock && focusMesh) {
      rotateGeoLockView(dx, dy);
    } else {
      camTheta-=dx*0.005;camPhi-=dy*0.005;targetPhi=camPhi;
    }
  }
  if(e.touches.length===2){
    const d=Math.hypot(e.touches[0].clientX-e.touches[1].clientX,e.touches[0].clientY-e.touches[1].clientY);
    if(lastTD){
      const minR = focusMesh ? Math.max(focusMesh.geometry?.parameters?.radius ?? 1, 1) * 2.5 : 8;
      camR=Math.max(minR,Math.min(80000,camR*(lastTD/d)));targetR=camR;
    }
    lastTD=d;
  }
  e.preventDefault();
},{passive:false});

// ── Raycasting ────────────────────────────────────────────────────────────────
const raycaster = new THREE.Raycaster();
const m2 = new THREE.Vector2();
const tooltip  = document.getElementById('tooltip');
const infoPanel= document.getElementById('planet-info');
const infoHideBtn = document.getElementById('pi-hide-btn');
const infoToggleBtn = document.getElementById('info-toggle-btn');
const searchInputEl = document.getElementById('search-input');
let infoPanelDismissed = false;
let focusedInfoType = null;
let focusedInfoObj = null;

const KM_PER_AU = 149597870.7;
const SECONDS_PER_YEAR = 365.25 * 86400;
const KM_S_PER_AU_PER_YEAR = KM_PER_AU / SECONDS_PER_YEAR;
const SUN_GALACTIC_SPEED_KMS = 230;

function formatSpeedKmS(speedKmS) {
  if (!Number.isFinite(speedKmS)) return '—';
  if (speedKmS >= 100) return speedKmS.toFixed(0) + ' km/s';
  if (speedKmS >= 10) return speedKmS.toFixed(1) + ' km/s';
  return speedKmS.toFixed(2) + ' km/s';
}

function getHeliocentricSpeedKmS(semiMajorSceneUnits, radiusSceneUnits) {
  if (!(semiMajorSceneUnits > 0) || !(radiusSceneUnits > 0)) return null;
  const semiMajorAu = semiMajorSceneUnits / AU_SCENE;
  const radiusAu = radiusSceneUnits / AU_SCENE;
  const muSun = 4 * Math.PI * Math.PI;
  const speedAuPerYear = Math.sqrt(Math.max(0, muSun * ((2 / radiusAu) - (1 / semiMajorAu))));
  return speedAuPerYear * KM_S_PER_AU_PER_YEAR;
}

function getProbeInstantSpeedKmS(vd) {
  const currentYear = 2000 + simTime;
  const dtYears = 1 / 365.25;
  const prevPos = getProbePos(vd, currentYear - dtYears);
  const nextPos = getProbePos(vd, currentYear + dtYears);
  const speedAuPerYear = prevPos.distanceTo(nextPos) / AU_SCENE / (2 * dtYears);
  return speedAuPerYear * KM_S_PER_AU_PER_YEAR;
}

function getInfoVelocity(type, obj) {
  if (type === 'sun') return formatSpeedKmS(SUN_GALACTIC_SPEED_KMS);
  if (type === 'planet') return formatSpeedKmS(getHeliocentricSpeedKmS(obj.d.sma, obj.tiltGroup.position.length()));
  if (type === 'dwarf') return formatSpeedKmS(getHeliocentricSpeedKmS(obj.d.sma, obj.mesh.position.length()));
  if (type === 'comet') return formatSpeedKmS(getHeliocentricSpeedKmS(obj.cd.sma, obj.nucleus.position.length()));
  if (type === 'probe') return formatSpeedKmS(getProbeInstantSpeedKmS(obj.vd));
  return 'N/A';
}

function syncInfoPanelVisibility() {
  const hasFocus = !!focusMesh;
  const showInfoPanel = hasFocus && !infoPanelDismissed;
  infoPanel.classList.toggle('show', showInfoPanel);
  if (infoToggleBtn) {
    infoToggleBtn.classList.toggle('show', hasFocus && infoPanelDismissed);
    infoToggleBtn.textContent = showInfoPanel ? 'Hide info' : 'Show info';
    infoToggleBtn.setAttribute('aria-label', showInfoPanel ? 'Hide info panel' : 'Show info panel');
    infoToggleBtn.setAttribute('aria-pressed', showInfoPanel ? 'true' : 'false');
  }
}

function renderInfoContent(type, obj) {
  const set = (id, val) => { const el=document.getElementById(id); if(el) el.textContent=val??'—'; };
  const lbl = (diam, dist, year, moons, vel, type_) => {
    set('pi-lbl-diam', diam); set('pi-lbl-dist', dist);
    set('pi-lbl-year', year); set('pi-lbl-moons', moons); set('pi-lbl-vel', vel); set('pi-lbl-type', type_);
  };
  if (type==='sun') {
    lbl('DIAMETER','DISTANCE','GALACTIC ORBIT','PLANETS','ORBITAL SPEED','TYPE');
    set('pi-name','THE SUN'); set('pi-diam','1,392,700 km'); set('pi-dist','—');
    set('pi-year','225M yr'); set('pi-moons','8 planets'); set('pi-vel',getInfoVelocity(type, obj)); set('pi-type','G-type Main Sequence');
  } else if (type==='planet') {
    lbl('DIAMETER','FROM SUN','ORBITAL PERIOD','MOONS','ORBITAL SPEED','TYPE');
    set('pi-name',obj.d.name); set('pi-diam',obj.d.diameter); set('pi-dist',obj.d.dist);
    set('pi-year',obj.d.year); set('pi-moons',obj.d.moons); set('pi-vel',getInfoVelocity(type, obj)); set('pi-type',obj.d.type);
  } else if (type==='moon') {
    lbl('ORBITS','ORBITAL PERIOD','ECCENTRICITY','INCLINATION','ORBITAL SPEED','TYPE');
    set('pi-name',obj.md.name); set('pi-diam',obj.md.planet);
    set('pi-dist',(obj.md.period*365.25).toFixed(2)+' days');
    set('pi-year',obj.md.ecc.toFixed(3)); set('pi-moons',obj.md.inc.toFixed(1)+'°'); set('pi-vel',getInfoVelocity(type, obj));
    set('pi-type','Natural satellite');
  } else if (type==='dwarf') {
    lbl('DIAMETER','FROM SUN','ORBITAL PERIOD','MOONS','ORBITAL SPEED','TYPE');
    set('pi-name',obj.d.name); set('pi-diam',obj.d.diameter); set('pi-dist',obj.d.dist);
    set('pi-year',obj.d.year); set('pi-moons',obj.d.moons); set('pi-vel',getInfoVelocity(type, obj)); set('pi-type',obj.d.type);
  } else if (type==='comet') {
    lbl('NUCLEUS SIZE','ECCENTRICITY','ORBITAL PERIOD','INCLINATION','ORBITAL SPEED','TYPE');
    set('pi-name',obj.cd.name); set('pi-diam','~'+Math.round(obj.cd.r*10)+'km');
    set('pi-dist',obj.cd.ecc.toFixed(4));
    set('pi-year',obj.cd.period<1000?obj.cd.period.toFixed(1)+' yrs':(obj.cd.period/1000).toFixed(1)+'k yrs');
    set('pi-moons',obj.cd.inc.toFixed(1)+'°'); set('pi-vel',getInfoVelocity(type, obj)); set('pi-type','Comet');
  } else if (type==='probe') {
    const distAU = (getProbePos(obj.vd, 2000+simTime).length()/AU_SCENE).toFixed(1);
    lbl('LAUNCHED','DISTANCE','SPEED','STATUS','CURRENT SPEED','TYPE');
    set('pi-name',obj.vd.name); set('pi-diam',obj.vd.info.launch);
    set('pi-dist',distAU+' AU from Sun'); set('pi-year',obj.vd.info.speed);
    set('pi-moons',obj.vd.info.status); set('pi-vel',getInfoVelocity(type, obj)); set('pi-type',obj.vd.info.note);
  }
}

function showInfo(type, obj) {
  focusedInfoType = type;
  focusedInfoObj = obj;
  renderInfoContent(type, obj);
  infoPanelDismissed = false;
  syncInfoPanelVisibility();
}

function clearFocusSelection() {
  if (!focusMesh && !infoPanel.classList.contains('show')) return;
  focusMesh = null;
  focusedInfoType = null;
  focusedInfoObj = null;
  targetR = VIEW_DEFAULTS[viewMode].r;
  userPanOffset.set(0,0,0);
  geoLock = false;
  btnGeoLock.classList.remove('active');
  lookAtSun = false;
  btnLookAtSun.classList.remove('active');
  document.querySelectorAll('.fbtn').forEach(b=>b.classList.remove('active'));
  infoPanelDismissed = false;
  syncInfoPanelVisibility();
}

infoHideBtn?.addEventListener('click', () => {
  infoPanelDismissed = true;
  syncInfoPanelVisibility();
});

infoToggleBtn?.addEventListener('click', () => {
  if (!focusMesh) return;
  infoPanelDismissed = !infoPanelDismissed;
  syncInfoPanelVisibility();
});

function getHit(cx,cy){
  m2.set((cx/window.innerWidth)*2-1, -(cy/window.innerHeight)*2+1);
  raycaster.setFromCamera(m2, camera);
  const targets=[sunMesh,...planets.map(p=>p.mesh),...moons.map(m=>m.moonMesh),...dwarfs.map(d=>d.mesh),...comets.map(c=>c.nucleus),...probes.map(p=>p.mesh)];
  const hits=raycaster.intersectObjects(targets, true);
  if (!hits.length) return null;
  const obj = hits[0].object;
  // Resolve invisible hit sphere back to its comet nucleus
  if (obj.userData.cometNucleus) return obj.userData.cometNucleus;
  // Resolve coma (child of nucleus) back to its nucleus
  if (obj.parent && comets.some(c => c.nucleus === obj.parent)) return obj.parent;
  return obj;
}

const _hoverWorldA = new THREE.Vector3();
const _hoverWorldB = new THREE.Vector3();
const _hoverScreenA = new THREE.Vector3();
const _hoverScreenB = new THREE.Vector3();

function projectWorldToScreen(world, out) {
  out.copy(world).project(camera);
  if (out.z < -1 || out.z > 1) return false;
  out.x = (out.x * 0.5 + 0.5) * window.innerWidth;
  out.y = (-out.y * 0.5 + 0.5) * window.innerHeight;
  return true;
}

function pointSegmentDistanceSq(px, py, ax, ay, bx, by) {
  const abx = bx - ax;
  const aby = by - ay;
  const lenSq = abx * abx + aby * aby;
  if (lenSq <= 1e-6) {
    const dx = px - ax;
    const dy = py - ay;
    return dx * dx + dy * dy;
  }
  const t = Math.max(0, Math.min(1, ((px - ax) * abx + (py - ay) * aby) / lenSq));
  const dx = px - (ax + abx * t);
  const dy = py - (ay + aby * t);
  return dx * dx + dy * dy;
}

function getSkyHover(cx, cy) {
  let bestStarIndex = -1;
  let bestStarDistSq = STAR_HOVER_RADIUS_PX * STAR_HOVER_RADIUS_PX;
  const starAttr = starGeo.getAttribute('position');

  for (let i = 0; i < starAttr.count; i++) {
    if (!STAR_METADATA[i]) continue;
    _hoverWorldA.fromBufferAttribute(starAttr, i);
    starMesh.localToWorld(_hoverWorldA);
    if (!projectWorldToScreen(_hoverWorldA, _hoverScreenA)) continue;
    const dx = _hoverScreenA.x - cx;
    const dy = _hoverScreenA.y - cy;
    const distSq = dx * dx + dy * dy;
    if (distSq < bestStarDistSq) {
      bestStarDistSq = distSq;
      bestStarIndex = i;
    }
  }

  if (bestStarIndex !== -1) {
    const text = getStarTooltipText(bestStarIndex);
    if (text) return { text, interactive: false };
  }

  if (!constLineMesh.visible || !constellationsOn) return null;

  let bestLineIndex = -1;
  let bestLineDistSq = CONSTELLATION_HOVER_RADIUS_PX * CONSTELLATION_HOVER_RADIUS_PX;
  const lineAttr = constLineGeo.getAttribute('position');

  for (let i = 0; i < N_LINES; i++) {
    _hoverWorldA.fromBufferAttribute(lineAttr, i * 2);
    _hoverWorldB.fromBufferAttribute(lineAttr, i * 2 + 1);
    constLineMesh.localToWorld(_hoverWorldA);
    constLineMesh.localToWorld(_hoverWorldB);
    if (!projectWorldToScreen(_hoverWorldA, _hoverScreenA) || !projectWorldToScreen(_hoverWorldB, _hoverScreenB)) continue;
    const distSq = pointSegmentDistanceSq(cx, cy, _hoverScreenA.x, _hoverScreenA.y, _hoverScreenB.x, _hoverScreenB.y);
    if (distSq < bestLineDistSq) {
      bestLineDistSq = distSq;
      bestLineIndex = i;
    }
  }

  if (bestLineIndex !== -1) {
    const text = getConstellationTooltipText(bestLineIndex);
    if (text) return { text, interactive: false };
  }

  return null;
}

function updateConstellationLabels() {
  if (!constellationLabelsEl) return;

  const labelsVisible = constellationsOn && constLineMesh.visible;
  if (!labelsVisible) {
    for (const labelEl of constellationLabelEls.values()) {
      labelEl.style.display = 'none';
    }
    return;
  }

  const starAttr = starGeo.getAttribute('position');
  for (const group of CONSTELLATION_GROUPS) {
    const labelEl = constellationLabelEls.get(group.key);
    if (!labelEl) continue;

    let sumX = 0;
    let count = 0;
    let minY = Infinity;

    for (const starIndex of group.indices) {
      _hoverWorldA.fromBufferAttribute(starAttr, starIndex);
      starMesh.localToWorld(_hoverWorldA);
      if (!projectWorldToScreen(_hoverWorldA, _hoverScreenA)) continue;
      sumX += _hoverScreenA.x;
      minY = Math.min(minY, _hoverScreenA.y);
      count++;
    }

    if (!count) {
      labelEl.style.display = 'none';
      continue;
    }

    const centerX = sumX / count;
    const labelY = minY - 18;
    const onScreen = centerX >= 24 && centerX <= window.innerWidth - 24 && labelY >= 20 && labelY <= window.innerHeight - 20;
    if (!onScreen) {
      labelEl.style.display = 'none';
      continue;
    }

    labelEl.style.display = 'block';
    labelEl.style.left = `${centerX}px`;
    labelEl.style.top = `${labelY}px`;
  }
}

function hoverCheck(cx,cy){
  const h=getHit(cx,cy);
  if(h){
    const p=planets.find(p=>p.mesh===h);
    const mn=moons.find(m=>m.moonMesh===h);
    const dw=dwarfs.find(d=>d.mesh===h);
    const cm=comets.find(c=>c.nucleus===h);
    const pr=probes.find(p=>p.mesh===h||p.mesh.children.includes(h));
    tooltip.textContent=h===sunMesh?'THE SUN':p?p.d.name:mn?(mn.md.name+' ('+mn.md.planet+')'):dw?dw.d.name:cm?cm.cd.name+' (comet)':pr?pr.vd.name:'';
    tooltip.style.display='block';tooltip.style.left=(cx+12)+'px';tooltip.style.top=(cy-8)+'px';
    renderer.domElement.style.cursor='pointer';
  } else {
    const skyHover = getSkyHover(cx,cy);
    if (skyHover) {
      tooltip.textContent = skyHover.text;
      tooltip.style.display='block';
      tooltip.style.left=(cx+12)+'px';
      tooltip.style.top=(cy-8)+'px';
      renderer.domElement.style.cursor = skyHover.interactive ? 'pointer' : 'default';
    } else {
      tooltip.style.display='none';renderer.domElement.style.cursor='default';
    }
  }
}
renderer.domElement.addEventListener('click',e=>{
  if(mouseDragDist > 4) return; // suppress click after drag
  const h=getHit(e.clientX,e.clientY);
  if(!h){
    // Click on empty space: deselect and return to solar view
    clearFocusSelection();
    return;
  }
  if(h===sunMesh){
    if(focusMesh===sunMesh){ focusMesh=null; targetR=VIEW_DEFAULTS[viewMode].r; userPanOffset.set(0,0,0); document.querySelectorAll('.fbtn').forEach(b=>b.classList.remove('active')); infoPanelDismissed=false; syncInfoPanelVisibility(); return; }
    setFocus('sun');
  } else {
    const p=planets.find(p=>p.mesh===h);
    const mn=moons.find(m=>m.moonMesh===h);
    if(p){
      if(focusMesh===p.mesh){ focusMesh=null; targetR=VIEW_DEFAULTS[viewMode].r; userPanOffset.set(0,0,0); document.querySelectorAll('.fbtn').forEach(b=>b.classList.remove('active')); infoPanelDismissed=false; syncInfoPanelVisibility(); return; }
      setFocus(p.d.name);
    } else if(mn){
      focusMesh=mn.moonMesh; userPanOffset.set(0,0,0); targetR=snapZoom(mn.md.r);
      document.querySelectorAll('.fbtn').forEach(b=>b.classList.remove('active'));
      showInfo('moon', mn);
    } else {
      const dw=dwarfs.find(d=>d.mesh===h);
      const cm=comets.find(c=>c.nucleus===h);
      if(dw){
        focusMesh=dw.mesh; userPanOffset.set(0,0,0); targetR=snapZoom(dw.d.r);
        document.querySelectorAll('.fbtn').forEach(b=>b.classList.remove('active'));
        showInfo('dwarf', dw);
      } else if(cm){
        focusMesh=cm.nucleus; userPanOffset.set(0,0,0); targetR=snapZoom(cm.cd.r);
        document.querySelectorAll('.fbtn').forEach(b=>b.classList.remove('active'));
        showInfo('comet', cm);
      } else {
        const pr=probes.find(p=>p.mesh===h||p.mesh.children.some(c=>c===h));
        if(pr){
          focusMesh=pr.mesh; userPanOffset.set(0,0,0); targetR=snapZoom(3);
          document.querySelectorAll('.fbtn').forEach(b=>b.classList.remove('active'));
          showInfo('probe', pr);
        }
      }
    }
  }
});

// ── Analytical trail computation ──────────────────────────────────────────────
// Instead of sampling world positions over time (which gives straight lines for
// fast planets), we compute the EXACT world position at any past time analytically.
// Every frame we regenerate the full trail as a dense set of points — smooth curves
// regardless of orbital speed.
//
// Planet world position at time t:
//   Local (in solarPivot frame, inclined by inc):
//     E  = keplerE(2π*t/period + angle0, ecc)
//     lx = sma*cos(E) - c
//     lz = b*sin(E)
//   After inclination rotation (around X by inc):
//     ly_inclined = -lz * sin(inc)
//     lz_inclined =  lz * cos(inc)
//   solarPivot tilt (around X by ECLIPTIC_TILT) + position (0, 0, sunZatT):
//     sunZatT = t * GALACTIC_SCENE_SPEED  (linear travel)
//   Then rotate the local vector by ECLIPTIC_TILT and offset by sunZatT.

const TRAIL_PTS   = 2000; // more points = smoother curves + denser fire
const TRAIL_YEARS = 20.0;

// Pre-allocate trail buffers (reused every frame)
for (const p of planets) {
  p.trailPosBuf = new Float32Array(TRAIL_PTS * 3);
  p.trailColBuf = new Float32Array(TRAIL_PTS * 3);
  p.trailSizBuf = new Float32Array(TRAIL_PTS);
  p.trailGeo.setAttribute('position', new THREE.BufferAttribute(p.trailPosBuf, 3));
  p.trailGeo.setAttribute('color',    new THREE.BufferAttribute(p.trailColBuf, 3));
  p.trailGeo.setAttribute('size',     new THREE.BufferAttribute(p.trailSizBuf, 1));
  p.trailGeo.setDrawRange(0, 0);
}
const sunTrailPosBuf2 = new Float32Array(TRAIL_PTS * 3);
const sunTrailColBuf2 = new Float32Array(TRAIL_PTS * 3);
const sunTrailSizBuf2 = new Float32Array(TRAIL_PTS);
sunTrailGeo.setAttribute('position', new THREE.BufferAttribute(sunTrailPosBuf2, 3));
sunTrailGeo.setAttribute('color',    new THREE.BufferAttribute(sunTrailColBuf2, 3));
sunTrailGeo.setAttribute('size',     new THREE.BufferAttribute(sunTrailSizBuf2, 1));
sunTrailGeo.setDrawRange(0, 0);

function computeAnalyticTrails() {
  if (viewMode === 'solar' || !trailsOn) return;
  // Hide trails while scrubbing timeline
  if (timelineDragging) {
    for (const p of planets) p.trailGeo.setDrawRange(0,0);
    sunTrailGeo.setDrawRange(0,0);
    return;
  }

  const sinTilt = Math.sin(ECLIPTIC_TILT);
  const cosTilt = Math.cos(ECLIPTIC_TILT);
  const vortexMode = viewMode === 'vortex';

  // Each planet shows 2 full orbits so helix shape is always clear.
  // A fixed lookback means outer planets show only a tiny arc — looks like
  // straight lines shooting ahead, which is misleading.
  for (const p of planets) {
    // Show 2 orbits but hard cap at 500 years max to prevent huge world-space coords
    const lookback = vortexMode
      ? Math.min(p.d.period * 1.35, Math.min(260, Math.abs(simTime)))
      : Math.min(p.d.period * 2.0, Math.min(500, Math.abs(simTime)));
    const dt_step  = lookback / (TRAIL_PTS - 1);
    const sinInc = Math.sin(p.d.inc * Math.PI / 180);
    const cosInc = Math.cos(p.d.inc * Math.PI / 180);
    const pos = p.trailPosBuf;
    const col = p.trailColBuf;
    const siz = p.trailSizBuf;
    const hr = p.tc.r, hg = p.tc.g, hb = p.tc.b;

    for (let i = 0; i < TRAIL_PTS; i++) {
      const t  = simTime - lookback + i * dt_step;
      const M  = (2 * Math.PI * t / p.d.period) + p.angle0;
      const E  = keplerE(M, p.d.ecc);
      const nu = 2*Math.atan2(Math.sqrt(1+p.d.ecc)*Math.sin(E/2), Math.sqrt(1-p.d.ecc)*Math.cos(E/2));
      const r  = p.d.sma*(1-p.d.ecc*p.d.ecc)/(1+p.d.ecc*Math.cos(nu));
      const u  = (p.omegaRad - p.OmegaRad) + nu;
      const cO = Math.cos(p.OmegaRad), sO = Math.sin(p.OmegaRad);
      const ci = Math.cos(p.incRad),   si = Math.sin(p.incRad);
      const xEcl = r*(cO*Math.cos(u) - sO*Math.sin(u)*ci);
      const yEcl = r*(sO*Math.cos(u) + cO*Math.sin(u)*ci);
      const zEcl = r*Math.sin(u)*si;
      const lx = xEcl; const lz = -yEcl; // sim x,z in ecliptic plane
      const ix =  lx;
      const iy = -lz * sinInc;
      const iz =  lz * cosInc;
      // Trail Z: offset relative to current Sun position (solarPivot always at origin)
      // so trails spiral backward from current position
      const sunZatT = (t - simTime) * GALACTIC_SCENE_SPEED;
      const py = iy * cosTilt - iz * sinTilt;
      const pz = iy * sinTilt + iz * cosTilt + sunZatT;
      pos[i*3]   = Number.isFinite(ix) ? ix : 0;
      pos[i*3+1] = Number.isFinite(py) ? py : 0;
      pos[i*3+2] = Number.isFinite(pz) ? pz : 0;

      const f  = i / (TRAIL_PTS - 1);
      const f2 = f * f;
      const f4 = f2 * f2;

      // Blackbody plasma ramp (tail→head = dark→red→orange→yellow→white)
      // R: appears early, stays high
      // G: appears mid, creates yellow then white
      // B: only at the very tip for white-hot
      if (vortexMode) {
        const fade = Math.pow(f, 2.6);
        const warmth = 0.25 + 0.75 * fade;
        const cool = 0.12 + 0.88 * Math.pow(f, 1.5);
        col[i*3]   = Math.min(1, 0.95 * warmth);
        col[i*3+1] = Math.min(1, 0.72 * cool);
        col[i*3+2] = Math.min(1, 0.48 * Math.pow(f, 3.4));
        siz[i] = 0.05 + Math.pow(f, 5) * 5.2;
      } else {
        col[i*3]   = Math.min(1, f2 * 2.0);                          // red channel — on from ~50%
        col[i*3+1] = Math.min(1, Math.max(0, f - 0.4) / 0.6 * 1.5); // green — on from 40%, yellow
        col[i*3+2] = Math.min(1, Math.max(0, f - 0.82) / 0.18);     // blue — only last 18%, white tip
        siz[i] = 0.1 + f4 * 4.0;
      }
    }

    p.trailGeo.attributes.position.needsUpdate = true;
    p.trailGeo.attributes.color.needsUpdate    = true;
    p.trailGeo.attributes.size.needsUpdate     = true;
    p.trailGeo.setDrawRange(0, TRAIL_PTS);
  }

  // Sun trail: 2 Earth-years lookback
  const sunLookback = vortexMode ? Math.min(3.5, Math.abs(simTime)) : Math.min(2.0, Math.abs(simTime));
  const sunDtStep   = sunLookback / (TRAIL_PTS - 1);
  for (let i = 0; i < TRAIL_PTS; i++) {
    const t = simTime - sunLookback + i * sunDtStep;
    sunTrailPosBuf2[i*3]   = 0;
    sunTrailPosBuf2[i*3+1] = 0;
    const sunTrailZ = (t - simTime) * GALACTIC_SCENE_SPEED;
    sunTrailPosBuf2[i*3+2] = Number.isFinite(sunTrailZ) ? sunTrailZ : 0;
    const f  = i / (TRAIL_PTS - 1);
    const f4 = f * f * f * f;
    if (vortexMode) {
      const fade = Math.pow(f, 2.4);
      sunTrailColBuf2[i*3]   = Math.min(1, 1.00 * fade);
      sunTrailColBuf2[i*3+1] = Math.min(1, 0.82 * fade);
      sunTrailColBuf2[i*3+2] = Math.min(1, 0.42 * Math.pow(f, 3.1));
      sunTrailSizBuf2[i] = 0.08 + f4 * 6.0;
    } else {
      sunTrailColBuf2[i*3]   = Math.min(1, f * f * 2.0);
      sunTrailColBuf2[i*3+1] = Math.min(1, Math.max(0, f - 0.4) / 0.6 * 1.5);
      sunTrailColBuf2[i*3+2] = Math.min(1, Math.max(0, f - 0.82) / 0.18);
      sunTrailSizBuf2[i] = 0.1 + f4 * 5.0;
    }
  }
  sunTrailGeo.attributes.position.needsUpdate = true;
  sunTrailGeo.attributes.color.needsUpdate    = true;
  sunTrailGeo.attributes.size.needsUpdate     = true;
  sunTrailGeo.setDrawRange(0, TRAIL_PTS);
}

  // ── Animate ───────────────────────────────────────────────────────────────────
let lastT = performance.now();
const _wpos = new THREE.Vector3();
const _lockedCameraPos = new THREE.Vector3();
const _lockedTargetPos = new THREE.Vector3();
const _lockedWorldUp = new THREE.Vector3();
const _focusWorldQuat = new THREE.Quaternion();
const _camAway = new THREE.Vector3();
const _camRight = new THREE.Vector3();
const _camOffset = new THREE.Vector3();
const _camLookDir = new THREE.Vector3();
const _moonTargetWorld = new THREE.Vector3();
const _earthTravelWorldDir = new THREE.Vector3(0, 0, 1);
const _earthTravelLocalDir = new THREE.Vector3();
const _earthTravelMarkerBaseDir = new THREE.Vector3(0, 0, 1);
const _earthTravelMeshQuat = new THREE.Quaternion();

function updateEarthTravelMarker(planet) {
  const travelMarker = planet.mesh.userData.travelMarker;
  if (!travelMarker) return;

  _earthTravelLocalDir
    .copy(_earthTravelWorldDir)
    .applyQuaternion(planet.mesh.getWorldQuaternion(_earthTravelMeshQuat).invert())
    .normalize();

  travelMarker.quaternion.setFromUnitVectors(_earthTravelMarkerBaseDir, _earthTravelLocalDir);
}

function animate(){
  requestAnimationFrame(animate);
  const now=performance.now();
  const dt=Math.min((now-lastT)/1000, 0.1);
  lastT=now;

  // ── Sim advance ────────────────────────────────────────────────────────────
  if(!paused){
    const ds = dt * getSimSpeed();
    simTime += ds;
    sunZ     = simTime * GALACTIC_SCENE_SPEED; // deterministic — no float accumulation
  }

  // ── Solar pivot position ───────────────────────────────────────────────────
  // Always keep solarPivot at origin — avoids float32 precision loss at large simTime.
  // In vortex mode we offset the camera target instead to simulate galactic travel.
  if(viewMode==='solar'){
    solarPivot.rotation.x = 0;
  } else {
    solarPivot.rotation.x = ECLIPTIC_TILT;
  }
  solarPivot.position.set(0,0,0);
  sunLight.position.set(0,0,0);
  // Sync sky tilt with ecliptic so constellations rotate correctly in vortex
  skyGroup.rotation.x = solarPivot.rotation.x;
  vortexStreaks.visible = viewMode === 'vortex';
  vortexStreakMat.opacity += (((viewMode === 'vortex') ? 0.16 : 0.0) - vortexStreakMat.opacity) * 0.08;
  vortexStreaks.rotation.y = sunZ * 0.000015;
  vortexStreaks.position.z = -((sunZ * 0.22) % 2400);

  // ── Planet positions ───────────────────────────────────────────────────────
  for(const p of planets){
    const M = (2*Math.PI*simTime/p.d.period) + p.angle0;
    const E = keplerE(M, p.d.ecc);
    // Correct Kepler: use true anomaly + omega for accurate ecliptic position
    const nu = 2*Math.atan2(Math.sqrt(1+p.d.ecc)*Math.sin(E/2), Math.sqrt(1-p.d.ecc)*Math.cos(E/2));
    const r  = p.d.sma*(1-p.d.ecc*p.d.ecc)/(1+p.d.ecc*Math.cos(nu));
    const u  = (p.omegaRad - p.OmegaRad) + nu; // arg of latitude from ascending node
    const cO = Math.cos(p.OmegaRad), sO = Math.sin(p.OmegaRad);
    const ci = Math.cos(p.incRad),   si = Math.sin(p.incRad);
    const xEcl = r*(cO*Math.cos(u) - sO*Math.sin(u)*ci);
    const yEcl = r*(sO*Math.cos(u) + cO*Math.sin(u)*ci);
    const zEcl = r*Math.sin(u)*si;
    p.tiltGroup.position.set(xEcl, zEcl, -yEcl); // planet position (tiltGroup moves, mesh stays at origin within)
    // Axial rotation around correctly tilted axis
    p.mesh.rotation.y = (simTime * 365.25 / p.d.rotPeriod) * Math.PI * 2;
    if (p.mesh.userData.travelMarker) updateEarthTravelMarker(p);
    if (p.cloudMesh) {
      p.cloudMesh.rotation.y = (simTime * 365.25 / (p.d.rotPeriod * 1.08)) * Math.PI * 2;
      if (p.cloudMesh.userData.updateClouds && !paused) p.cloudMesh.userData.updateClouds(simTime, dt);
      if (p.cloudMesh.userData.cloudMeshB) {
        p.cloudMesh.userData.cloudMeshB.rotation.y = p.cloudMesh.rotation.y;
      }
    }
    // Fade planet when a probe passes close (so trajectory is visible through it)
    let nearProbe = false;
    const fadeDistSq = (p.d.r * 3) * (p.d.r * 3);
    for (const pr of probes) {
      if (pr.mesh.position.distanceToSquared(p.tiltGroup.position) < fadeDistSq) { nearProbe = true; break; }
    }
    if (p.mesh.material.transparent !== nearProbe) {
      p.mesh.material.transparent = nearProbe;
      p.mesh.material.needsUpdate = true;
    }
    p.mesh.material.opacity = nearProbe ? 0.35 : 1.0;
    p.orbitLine.visible = orbitsOn && (viewMode==='solar');
    p.trailLine.visible = trailsOn && (viewMode!=='solar');
  }

  // ── Asteroid/TNO belt orbits ────────────────────────────────────────────────
  updateBelt(mainBelt);
  updateBelt(kuiperBelt);
  updateBelt(scatteredDisc);
  updateOortCloud();
  updateComets();
  updateProbes();
  updateStarPositions();

  // ── Dwarf planet positions ──────────────────────────────────────────────────
  for(const p of dwarfs){
    const M = (2*Math.PI*simTime/p.d.period) + p.angle0;
    const E = keplerE(M, p.d.ecc);
    const nuD = 2*Math.atan2(Math.sqrt(1+p.d.ecc)*Math.sin(E/2), Math.sqrt(1-p.d.ecc)*Math.cos(E/2));
    const rD  = p.d.sma*(1-p.d.ecc*p.d.ecc)/(1+p.d.ecc*Math.cos(nuD));
    const uD  = ((p.d.omega||0) - (p.d.Omega||0))*Math.PI/180 + nuD;
    const cOD=Math.cos((p.d.Omega||0)*Math.PI/180), sOD=Math.sin((p.d.Omega||0)*Math.PI/180);
    const ciD=Math.cos((p.d.inc||0)*Math.PI/180),   siD=Math.sin((p.d.inc||0)*Math.PI/180);
    const xED=rD*(cOD*Math.cos(uD)-sOD*Math.sin(uD)*ciD);
    const yED=rD*(sOD*Math.cos(uD)+cOD*Math.sin(uD)*ciD);
    const zED=rD*Math.sin(uD)*siD;
    p.mesh.position.set(xED, zED, -yED);
    p.mesh.rotation.y = (simTime * 365.25 / p.d.rotPeriod) * Math.PI * 2;
    p.orbitLine.visible = orbitsOn && (viewMode==='solar');
    // Charon orbits Pluto
    if (p.charon) {
      p.charonAngle += 0.00015;
      p.charon.position.set(Math.cos(p.charonAngle)*0.6, 0, Math.sin(p.charonAngle)*0.6);
    }
  }
  for(const m of moons){
    const M = (2*Math.PI*simTime/m.md.period) + m.angle0;
    const E = keplerE(M, m.md.ecc);
    // moonIncGrp is in parentPlanet.incGrp space.
    // Planet's local pos in that space = parentPlanet.mesh.position
    // Moon orbits around the planet, so add planet pos as offset.
    // The inclination rotation on moonIncGrp handles the tilt.
    // We store the orbital position in moonIncGrp's own space (before inc rotation),
    // but since moonIncGrp is at origin of incGrp, we need to translate it to planet pos.
    m.moonIncGrp.position.copy(m.parentPlanet.tiltGroup.position);
    m.moonMesh.position.set(m.md.sma*Math.cos(E) - m.c, 0, -m.b*Math.sin(E));
    if (m.spinModel.mode === 'synchronous') {
      m.moonIncGrp.getWorldPosition(_moonTargetWorld);
      m.moonMesh.lookAt(_moonTargetWorld);
      m.moonMesh.rotateY(THREE.MathUtils.degToRad(m.spinModel.yawDeg ?? 270));
      m.moonMesh.rotateX(THREE.MathUtils.degToRad(m.spinModel.pitchDeg ?? 0));
      m.moonMesh.rotateZ(THREE.MathUtils.degToRad(m.spinModel.rollDeg ?? 0));
    } else if (m.spinModel.mode === 'period') {
      m.moonMesh.rotation.set(0, (simTime * 365.25 / m.spinModel.periodDays) * Math.PI * 2, 0);
    } else if (m.spinModel.mode === 'chaotic') {
      const chaoticTurn = (simTime * 365.25 / m.spinModel.periodDays) * Math.PI * 2;
      const chaoticPhase = m.spinSeed * Math.PI * 2;
      m.moonMesh.rotation.set(
        Math.sin(chaoticTurn * 0.73 + chaoticPhase) * 0.8,
        -(chaoticTurn * 1.11 + chaoticPhase * 0.5),
        Math.cos(chaoticTurn * 1.37 - chaoticPhase) * 0.55,
      );
    } else {
      m.moonMesh.rotation.copy(m.baseRotation);
    }
    m.moonOrbitLine.visible = orbitsOn && (viewMode==='solar');
  }
  for (const c of comets) {
    c.orbitLine.visible = orbitsOn && (viewMode==='solar');
  }
  if (focusMesh && focusedInfoType) {
    renderInfoContent(focusedInfoType, focusedInfoObj);
  }
  // Constellation lines only visible when zoomed out (not in solar view)
  constLineMesh.visible = constellationsOn; // respect toggle
  sunTrailLine.visible = trailsOn && (viewMode !== 'solar');

  // ── Analytical trails (smooth curves, computed from exact past positions) ──
  computeAnalyticTrails();

  // ── Camera ─────────────────────────────────────────────────────────────────
  camPhi += (targetPhi - camPhi)*0.05;
  camR   += (targetR   - camR  )*0.05;

  if (focusMesh) {
    focusMesh.getWorldPosition(camTarget);
  } else if (viewMode === 'solar') {
    camTarget.copy(userPanOffset);
  } else if (viewMode === 'vortex') {
    camTarget.set(userPanOffset.x, userPanOffset.y + 180, userPanOffset.z - 1050);
  } else {
    // Fallback keeps free camera centered if an unsupported mode is forced externally.
    camTarget.set(userPanOffset.x, userPanOffset.y, userPanOffset.z);
  }

  if (geoLock && focusMesh) {
    focusMesh.updateWorldMatrix(true, false);
    _lockedCameraPos.copy(geoLockLocalCameraDir).multiplyScalar(camR);
    focusMesh.localToWorld(_lockedCameraPos);
    _lockedTargetPos.copy(geoLockLocalTargetPos);
    focusMesh.localToWorld(_lockedTargetPos);
    _lockedWorldUp.copy(geoLockLocalUp)
      .applyQuaternion(focusMesh.getWorldQuaternion(_focusWorldQuat))
      .normalize();
    camTarget.copy(_lockedTargetPos);
    camera.position.copy(_lockedCameraPos);
    camera.up.copy(_lockedWorldUp);
    camera.lookAt(_lockedTargetPos);
  } else if (lookAtSun && focusMesh) {
    sunMesh.getWorldPosition(_wpos);
    const sunPos = _wpos;
    const planetPos = camTarget;

    // Direction from Sun to planet (the "behind planet" axis)
    _camAway.subVectors(planetPos, sunPos).normalize();

    // Build perpendicular axes for camera orbit around the planet
    _camRight.crossVectors(_camAway, _worldUp);
    if (_camRight.lengthSq() < 1e-8) _camRight.set(1, 0, 0);
    else _camRight.normalize();

    // Compute camera offset using camPhi/camTheta
    const sinPhi = Math.sin(camPhi - Math.PI/2);
    const cosPhi = Math.cos(camPhi - Math.PI/2);
    _camOffset.set(0, 0, 0)
      .addScaledVector(_camAway,  cosPhi * Math.cos(camTheta))
      .addScaledVector(_camRight, cosPhi * Math.sin(camTheta))
      .addScaledVector(_worldUp,  sinPhi)
      .normalize()
      .multiplyScalar(camR);

    camera.position.copy(planetPos).add(_camOffset);
    // Flip up vector when past the pole so lookAt wraps cleanly (same logic as normal mode)
    camera.up.set(0, cosPhi >= 0 ? 1 : -1, 0);
    _camLookDir.subVectors(planetPos, camera.position).normalize();
    camera.up.applyAxisAngle(_camLookDir, cameraRoll);
    camera.lookAt(planetPos);
  } else {
    camera.position.set(
      camR*Math.sin(camPhi)*Math.sin(camTheta) + camTarget.x,
      camR*Math.cos(camPhi)                    + camTarget.y,
      camR*Math.sin(camPhi)*Math.cos(camTheta) + camTarget.z
    );
    // Flip camera up vector when upside-down so lookAt doesn't flip 180°
    const phiMod = ((camPhi % (Math.PI*2)) + Math.PI*2) % (Math.PI*2);
    camera.up.set(0, (phiMod < Math.PI) ? 1 : -1, 0);
    _camLookDir.subVectors(camTarget, camera.position).normalize();
    camera.up.applyAxisAngle(_camLookDir, cameraRoll);
    camera.lookAt(camTarget);
  }

  sunMesh.scale.setScalar(1);
  // Sun sidereal rotation: 25.38 days at equator
  sunMesh.rotation.y = (simTime * 365.25 / 25.38) * Math.PI * 2;

  // ── HUD ────────────────────────────────────────────────────────────────────
  const lyT = simTime*7.25;
  lyValEl.textContent = lyT<1000 ? lyT.toFixed(1) : (lyT/1000).toFixed(2)+'k';
  updateTimelineDisplay();
  updateConstellationLabels();

  renderer.render(scene, camera);
}

function onResize() {
  camera.aspect = window.innerWidth/window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
  syncMobileUi();
}
window.addEventListener('resize', onResize);
window.addEventListener('orientationchange', () => setTimeout(onResize, 300));

// ── Intro overlay ────────────────────────────────────────────────────────────
const introOverlay = document.getElementById('intro-overlay');
const INTRO_DURATION_MS = 10000;
const INTRO_HUD_REVEAL_MS = 7200;
let introFinished = !introOverlay;
let introTimer = null;
let introHudTimer = null;

if (introOverlay) {
  paused = true;

  const revealIntroHud = () => {
    document.body.classList.remove('intro-active');
    window.clearTimeout(introHudTimer);
    introHudTimer = null;
  };

  const finishIntro = () => {
    if (introFinished) return;
    introFinished = true;
    revealIntroHud();
    introOverlay.classList.add('done');
    paused = false;
    window.clearTimeout(introTimer);
    window.setTimeout(() => introOverlay.remove(), 32);
  };

  introHudTimer = window.setTimeout(revealIntroHud, INTRO_HUD_REVEAL_MS);
  introTimer = window.setTimeout(finishIntro, INTRO_DURATION_MS);
}

// Init view
setView('solar');
animate();


// ── Search ────────────────────────────────────────────────────────────────────
(function() {
  const input = document.getElementById('search-input');
  const dropdown = document.getElementById('search-dropdown');

  // Build searchable object list
  function buildCatalog() {
    const catalog = [];

    // Sun
    catalog.push({ label:'Sun', sub:'Star', color:'#FFE050', group:'STAR',
      action: () => focusObject('sun') });

    // Planets + their moons
    for (const p of planets) {
      catalog.push({ label: p.d.name, sub: p.d.type, color: '#'+p.d.color.toString(16).padStart(6,'0'), group:'PLANETS',
        action: () => focusObject('planet', p) });
      const pMoons = moons.filter(m => m.parentPlanet === p);
      for (const m of pMoons) {
        catalog.push({ label: m.md.name, sub: p.d.name + ' moon', color:'#aaaaaa', group:'MOONS OF ' + p.d.name,
          action: () => focusObject('moon', m) });
      }
    }

    // Dwarf planets
    for (const dw of dwarfs) {
      catalog.push({ label: dw.d.name, sub: dw.d.type, color: '#'+dw.d.color.toString(16).padStart(6,'0'), group:'DWARF PLANETS',
        action: () => focusObject('dwarf', dw) });
    }

    // Comets
    for (const cm of comets) {
      catalog.push({ label: cm.cd.name, sub: 'Comet', color:'#aaddff', group:'COMETS',
        action: () => focusObject('comet', cm) });
    }

    // Probes
    for (const pr of probes) {
      catalog.push({ label: pr.vd.name, sub: 'Space probe', color:'#'+pr.vd.color.toString(16).padStart(6,'0'), group:'PROBES',
        action: () => focusObject('probe', pr) });
    }

    // Constellations
    for (const group of CONSTELLATION_GROUPS) {
      catalog.push({
        label: group.key,
        sub: `${group.indices.length} star${group.indices.length === 1 ? '' : 's'}`,
        color: '#b8d6ff',
        group: 'CONSTELLATIONS',
        action: () => focusConstellationFromSearch(group),
      });
    }

    return catalog;
  }

  function focusObject(type, obj) {
    document.querySelectorAll('.fbtn').forEach(x => x.classList.remove('active'));
    document.getElementById('btn-orion').classList.remove('active');
    userPanOffset.set(0, 0, 0);
    lookAtSun = false;
    btnLookAtSun.classList.remove('active');

    if (type === 'sun') {
      focusMesh = sunMesh;
      targetR = snapZoom(109);
      showInfo('sun', null);
    } else if (type === 'planet') {
      focusMesh = obj.mesh;
      targetR = snapZoom(obj.d.r);
      showInfo('planet', obj);
      // highlight matching fbtn if exists
      const btn = [...document.querySelectorAll('.fbtn[data-focus]')].find(b => b.dataset.focus === obj.d.name);
      if (btn) btn.classList.add('active');
    } else if (type === 'moon') {
      focusMesh = obj.moonMesh;
      targetR = snapZoom(obj.md.r);
      showInfo('moon', obj);
    } else if (type === 'dwarf') {
      focusMesh = obj.mesh;
      targetR = snapZoom(obj.d.r);
      showInfo('dwarf', obj);
    } else if (type === 'comet') {
      focusMesh = obj.nucleus;
      targetR = snapZoom(obj.cd.r);
      showInfo('comet', obj);
    } else if (type === 'probe') {
      focusMesh = obj.mesh;
      targetR = snapZoom(3);
      showInfo('probe', obj);
    }

    // Close search
    input.value = '';
    dropdown.style.display = 'none';
    input.blur();
  }

  let catalog = [];
  let activeIdx = -1;
  let visibleItems = [];

  function renderDropdown(query) {
    const q = query.trim().toLowerCase();
    dropdown.innerHTML = '';
    visibleItems = [];
    activeIdx = -1;

    const filtered = q === ''
      ? catalog
      : catalog.filter(e => e.label.toLowerCase().includes(q) || e.group.toLowerCase().includes(q) || e.sub.toLowerCase().includes(q));

    if (filtered.length === 0) {
      dropdown.style.display = 'none';
      return;
    }

    // Group by group label
    const groups = {};
    for (const entry of filtered) {
      if (!groups[entry.group]) groups[entry.group] = [];
      groups[entry.group].push(entry);
    }

    for (const [groupName, entries] of Object.entries(groups)) {
      const grpEl = document.createElement('div');
      grpEl.className = 'search-group';
      const lbl = document.createElement('div');
      lbl.className = 'search-group-label';
      lbl.textContent = groupName;
      grpEl.appendChild(lbl);

      for (const entry of entries) {
        const item = document.createElement('div');
        item.className = 'search-item';
        const dot = document.createElement('div');
        dot.className = 'search-dot';
        dot.style.background = entry.color;
        const name = document.createElement('span');
        name.textContent = entry.label;
        const sub = document.createElement('span');
        sub.className = 'search-sub';
        sub.textContent = entry.sub;
        const runEntry = () => {
          entry.action();
          dropdown.style.display = 'none';
          input.blur();
          closeMobilePanels();
        };
        item.appendChild(dot);
        item.appendChild(name);
        item.appendChild(sub);
        item.addEventListener('pointerdown', e => { e.preventDefault(); runEntry(); });
        item.addEventListener('click', e => { e.preventDefault(); });
        grpEl.appendChild(item);
        visibleItems.push({ el: item, entry });
      }

      dropdown.appendChild(grpEl);
    }

    dropdown.style.display = 'block';
  }

  function setActive(idx) {
    visibleItems.forEach(v => v.el.classList.remove('active'));
    activeIdx = Math.max(-1, Math.min(visibleItems.length - 1, idx));
    if (activeIdx >= 0) {
      visibleItems[activeIdx].el.classList.add('active');
      visibleItems[activeIdx].el.scrollIntoView({ block:'nearest' });
    }
  }

  input.addEventListener('focus', () => {
    if (!catalog.length) catalog = buildCatalog();
    renderDropdown(input.value);
  });

  input.addEventListener('input', () => {
    if (!catalog.length) catalog = buildCatalog();
    renderDropdown(input.value);
  });

  input.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(activeIdx + 1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(activeIdx - 1); }
    else if (e.key === 'Enter') {
      if (activeIdx >= 0) visibleItems[activeIdx].entry.action();
      else if (visibleItems.length === 1) visibleItems[0].entry.action();
    }
    else if (e.key === 'Escape') { dropdown.style.display = 'none'; input.blur(); }
  });

  document.addEventListener('click', e => {
    if (!document.getElementById('search-wrap').contains(e.target)) {
      dropdown.style.display = 'none';
    }
    if (!helpPanel.contains(e.target) && e.target !== helpBtn) {
      setHelpOpen(false);
    }
  });
})();

document.addEventListener('keydown', e => {
  const target = e.target;
  const isTyping = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable);

  if (e.key === 'Escape') {
    setHelpOpen(false);
    closeMobilePanels();
    if (document.activeElement === searchInputEl) {
      searchInputEl.blur();
      return;
    }
    clearFocusSelection();
    return;
  }

  if (isTyping) return;

  const key = e.key.toLowerCase();
  if (e.key === '/') {
    e.preventDefault();
    setHelpOpen(false);
    searchInputEl.focus();
    searchInputEl.select();
  } else if (key === 'h') {
    e.preventDefault();
    setHelpOpen(!helpPanel.classList.contains('show'));
  } else if (key === ' ') {
    e.preventDefault();
    document.getElementById('pause-btn').click();
  } else if (key === 'o') {
    e.preventDefault();
    document.getElementById('orbits-btn').click();
  } else if (key === 't') {
    e.preventDefault();
    document.getElementById('trails-btn').click();
  } else if (key === 'c') {
    e.preventDefault();
    document.getElementById('const-btn').click();
  } else if (key === 'l') {
    e.preventDefault();
    btnLookAtSun.click();
  } else if (key === 'g') {
    e.preventDefault();
    btnGeoLock.click();
  } else if (key === '1') {
    e.preventDefault();
    setView('solar');
  } else if (key === '2') {
    e.preventDefault();
    setView('vortex');
  }
});
