using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sol.Api.Models;
using Sol.Api.Services;

namespace Sol.Api.Diagnostics
{
    public class JplFieldVerifier
    {
        private readonly AuthoritativeBodyCatalogReader _reader;

        public JplFieldVerifier(AuthoritativeBodyCatalogReader reader)
        {
            _reader = reader;
        }

        public async Task VerifyAllBodiesAsync()
        {
            var seeds = await _reader.ReadBodiesAsync(default);
            foreach (var seed in seeds)
            {
                Console.WriteLine($"Verifying: {seed.DisplayName} ({seed.Slug})");
                var issues = new List<string>();

                // Orbital Elements
                if (seed.Aphelion_AU == null) issues.Add("Aphelion_AU missing");
                if (seed.Perihelion_AU == null) issues.Add("Perihelion_AU missing");
                if (seed.Eccentricity == null) issues.Add("Eccentricity missing");
                if (seed.Inclination_deg == null) issues.Add("Inclination_deg missing");
                if (seed.SemiMajorAxis_AU == null) issues.Add("SemiMajorAxis_AU missing");
                if (seed.ArgumentOfPerihelion_deg == null) issues.Add("ArgumentOfPerihelion_deg missing");
                if (seed.LongitudeOfAscendingNode_deg == null) issues.Add("LongitudeOfAscendingNode_deg missing");
                if (seed.MeanAnomaly_deg == null) issues.Add("MeanAnomaly_deg missing");
                if (seed.MeanMotion_degPerDay == null) issues.Add("MeanMotion_degPerDay missing");
                if (seed.OrbitalPeriod_days == null) issues.Add("OrbitalPeriod_days missing");
                if (seed.Epoch_JD == null) issues.Add("Epoch_JD missing");

                // Physical Properties
                if (seed.MeanRadius_km == null) issues.Add("MeanRadius_km missing");
                if (seed.Density_gcm3 == null) issues.Add("Density_gcm3 missing");
                if (seed.Mass_1e23kg == null) issues.Add("Mass_1e23kg missing");
                if (seed.Volume_1e10km3 == null) issues.Add("Volume_1e10km3 missing");
                if (seed.SiderealRotPeriod_d == null) issues.Add("SiderealRotPeriod_d missing");
                if (seed.SiderealRotRate_radps == null) issues.Add("SiderealRotRate_radps missing");
                if (seed.MeanSolarDay_d == null) issues.Add("MeanSolarDay_d missing");
                if (seed.CoreRadius_km == null) issues.Add("CoreRadius_km missing");
                if (seed.GeometricAlbedo == null) issues.Add("GeometricAlbedo missing");
                if (seed.SurfaceEmissivity == null) issues.Add("SurfaceEmissivity missing");
                if (seed.GM_km3s2 == null) issues.Add("GM_km3s2 missing");
                if (seed.EquatorialRadius_km == null) issues.Add("EquatorialRadius_km missing");
                if (seed.MassRatioSunPlanet == null) issues.Add("MassRatioSunPlanet missing");
                if (seed.MomentOfInertia == null) issues.Add("MomentOfInertia missing");
                if (seed.EquatorialGravity_ms2 == null) issues.Add("EquatorialGravity_ms2 missing");
                if (seed.AtmosPressure_bar == null) issues.Add("AtmosPressure_bar missing");
                if (seed.MaxAngularDiam_arcsec == null) issues.Add("MaxAngularDiam_arcsec missing");
                if (seed.MeanTemperature_K == null) issues.Add("MeanTemperature_K missing");
                if (seed.VisualMag == null) issues.Add("VisualMag missing");
                if (seed.ObliquityToOrbit_arcmin == null) issues.Add("ObliquityToOrbit_arcmin missing");
                if (seed.HillSphereRadius_Rp == null) issues.Add("HillSphereRadius_Rp missing");
                if (seed.SiderealOrbPeriod_y == null) issues.Add("SiderealOrbPeriod_y missing");
                if (seed.SiderealOrbPeriod_d == null) issues.Add("SiderealOrbPeriod_d missing");
                if (seed.EscapeVelocity_kms == null) issues.Add("EscapeVelocity_kms missing");
                if (seed.MeanOrbitVelocity_kms == null) issues.Add("MeanOrbitVelocity_kms missing");

                // Solar/IR Constants
                if (seed.SolarConstant_Wm2_Mean == null) issues.Add("SolarConstant_Wm2_Mean missing");
                if (seed.SolarConstant_Wm2_Perihelion == null) issues.Add("SolarConstant_Wm2_Perihelion missing");
                if (seed.SolarConstant_Wm2_Aphelion == null) issues.Add("SolarConstant_Wm2_Aphelion missing");
                if (seed.MaxPlanetaryIR_Wm2_Mean == null) issues.Add("MaxPlanetaryIR_Wm2_Mean missing");
                if (seed.MaxPlanetaryIR_Wm2_Perihelion == null) issues.Add("MaxPlanetaryIR_Wm2_Perihelion missing");
                if (seed.MaxPlanetaryIR_Wm2_Aphelion == null) issues.Add("MaxPlanetaryIR_Wm2_Aphelion missing");
                if (seed.MinPlanetaryIR_Wm2_Mean == null) issues.Add("MinPlanetaryIR_Wm2_Mean missing");
                if (seed.MinPlanetaryIR_Wm2_Perihelion == null) issues.Add("MinPlanetaryIR_Wm2_Perihelion missing");
                if (seed.MinPlanetaryIR_Wm2_Aphelion == null) issues.Add("MinPlanetaryIR_Wm2_Aphelion missing");

                if (issues.Count == 0)
                    Console.WriteLine("  All fields populated.");
                else
                    Console.WriteLine("  Missing fields: " + string.Join(", ", issues));
            }
        }
    }
}
