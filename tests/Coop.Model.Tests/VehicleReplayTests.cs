using System;
using System.Collections.Generic;
using System.IO;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// P3 : le flux de véhicule **réellement reçu** à l'étape J est rejoué hors du jeu.
    ///
    /// Le fichier `fixtures/vehicles-j.csv` a été écrit par le client pendant un run (2026-10-20 18:23,
    /// `COOP_RECORD=1`) : 129 états, l'intervalle mesuré avant chacun. Le test ne mesure donc pas une
    /// suite inventée, il mesure ce que le jeu a produit.
    ///
    /// La discrimination est le point : la même suite, appliquée **sans interpolation** (ce que faisait le
    /// client avant le §33), dépasse le seuil de l'étape J. Si un jour l'interpolation disparaît, ce test
    /// échoue en quelques millisecondes, sans lancer le jeu.
    /// </summary>
    public sealed class VehicleReplayTests
    {
        /// <summary>Cadence d'image du rejeu : celle mesurée à l'étape J (`fps≈120–140`).</summary>
        private const float FrameSeconds = 1f / 120f;

        /// <summary>Seuil de l'étape J : un pas par image plus grand que 1 m est un saut visible.</summary>
        private const float MaxStepThreshold = 1.0f;

        /// <summary>Seuil de l'étape J pour le pas moyen.</summary>
        private const float MaxMeanStepThreshold = 0.6f;

        private static List<VehicleReplay.Frame> Recorded(out int ignored)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "vehicles-j.csv");
            Assert.True(File.Exists(path), $"enregistrement introuvable : {path}");
            return VehicleReplay.Parse(File.ReadAllText(path), out ignored);
        }

        [Fact]
        public void L_enregistrement_se_relit_en_entier()
        {
            var frames = Recorded(out var ignored);

            Assert.Equal(0, ignored);
            Assert.Equal(129, frames.Count);

            // Deux propriétés du flux réel : un tick qui avance, et un intervalle d'annonce non nul.
            for (var i = 1; i < frames.Count; i++)
            {
                Assert.True(frames[i].Tick >= frames[i - 1].Tick, "le tick recule");
            }

            Assert.True(frames[1].GapSeconds > 0f, "le premier intervalle mesuré est nul");
        }

        [Fact]
        public void Le_flux_rejoue_respecte_les_seuils_de_l_etape_J()
        {
            var frames = Recorded(out _);
            var stats = VehicleReplay.Simulate(frames, FrameSeconds);

            // Le plus grand trajet entre deux annonces vaut ~2 m : c'est le saut que le client doit lisser.
            Assert.True(stats.MaxDistanceMeters >= 2f, stats.ToString());
            Assert.True(stats.MaxStepMeters <= MaxStepThreshold, stats.ToString());
            Assert.True(stats.MeanStepMeters <= MaxMeanStepThreshold, stats.ToString());
            Assert.True(stats.Frames > stats.States, stats.ToString());
        }

        [Fact]
        public void Sans_interpolation_le_meme_flux_echoue_au_seuil()
        {
            // Ce que le client faisait avant le §33 : appliquer la position telle quelle. Ce calcul est
            // volontairement **hors** de `VehicleReplay` — c'est l'absence de règle, pas une variante.
            var frames = Recorded(out _);
            var maxStep = 0f;
            for (var i = 1; i < frames.Count; i++)
            {
                var dx = frames[i].X - frames[i - 1].X;
                var dy = frames[i].Y - frames[i - 1].Y;
                var dz = frames[i].Z - frames[i - 1].Z;
                var distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (distance > maxStep)
                {
                    maxStep = distance;
                }
            }

            // La mesure est donc **discriminante** : elle passe avec la règle, échoue sans elle.
            Assert.True(
                maxStep > MaxStepThreshold,
                $"l'enregistrement ne discrimine pas : pas direct max={maxStep:F2} m");
        }

        [Fact]
        public void Un_intervalle_geometrique_et_un_intervalle_nul_ne_produisent_pas_de_saut()
        {
            var frames = new List<VehicleReplay.Frame>
            {
                new VehicleReplay.Frame { Tick = 1, GapSeconds = 0f, X = 0f, Y = 0f, Z = 0f },
                // 21 m en 2 ms : la rafale mesurée au §33 (`pas par image max=21,11 m`).
                new VehicleReplay.Frame { Tick = 2, GapSeconds = 0.002f, X = 21f, Y = 0f, Z = 0f },
            };

            var stats = VehicleReplay.Simulate(frames, FrameSeconds);

            Assert.True(stats.MaxStepMeters <= MaxStepThreshold, stats.ToString());
            Assert.True(stats.Frames >= 50, stats.ToString());
        }

        [Fact]
        public void Une_ligne_illisible_est_ignoree_et_comptee()
        {
            var text = "# commentaire\n1;0.1;0;0;0\nn'importe quoi\n2;x;0;0;0\n3;0.1;1;2;3\n";
            var frames = VehicleReplay.Parse(text, out var ignored);

            Assert.Equal(2, frames.Count);
            Assert.Equal(2, ignored);
        }

        [Fact]
        public void Format_et_TryParse_sont_symetriques()
        {
            var line = VehicleReplay.Format(42L, 0.05f, -120.75f, -21.932722f, -64.54028f);
            Assert.True(VehicleReplay.TryParse(line, out var frame));

            Assert.Equal(42L, frame.Tick);
            Assert.Equal(0.05f, frame.GapSeconds, 4);
            Assert.Equal(-120.75f, frame.X, 3);
            Assert.Equal(-21.932722f, frame.Y, 3);
            Assert.Equal(-64.54028f, frame.Z, 3);
        }
    }
}
