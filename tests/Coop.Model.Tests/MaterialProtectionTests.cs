using System.Collections.Generic;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Protection des matériaux pendant la reprise d'un craft (v21). Elle vivait dans `Coop.Client` avec
    /// `UnityEngine.Time`, donc elle n'était prouvée que par la sonde en jeu. Le temps est désormais un
    /// argument : la fenêtre, les références partagées et la purge se testent hors jeu.
    /// </summary>
    public sealed class MaterialProtectionTests
    {
        private static readonly string[] Titanium = { "Titanium" };

        [Fact]
        public void Un_materiau_arme_est_protege_jusqu_a_l_echeance_incluse()
        {
            var protection = new MaterialProtection(windowMs: 10_000);
            Assert.Equal(new[] { "Titanium" }, protection.Arm(1_000, Titanium));

            Assert.True(protection.IsProtected(1_000, "Titanium"));
            Assert.True(protection.IsProtected(11_000, "Titanium"));
            Assert.False(protection.IsProtected(11_001, "Titanium"));
            Assert.Equal(0, protection.Count);
        }

        [Fact]
        public void Un_materiau_qui_expire_est_oublie_avec_ses_references()
        {
            var protection = new MaterialProtection(windowMs: 500);
            protection.Arm(0, Titanium);
            protection.Arm(0, Titanium);
            Assert.Equal(2, protection.TotalRefs);

            // La fenêtre est passée : l'entrée part, et une levée ultérieure ne peut pas « rendre » une
            // protection déjà échue (elle ne trouve plus rien).
            Assert.False(protection.IsProtected(501, "Titanium"));
            Assert.Equal(0, protection.TotalRefs);
            Assert.Empty(protection.Release(Titanium));
        }

        [Fact]
        public void Deux_recettes_qui_partagent_un_materiau_ne_le_decouvrent_qu_a_la_derniere_levee()
        {
            var protection = new MaterialProtection();
            protection.Arm(0, Titanium);
            protection.Arm(0, Titanium);

            Assert.Empty(protection.Release(Titanium));
            Assert.True(protection.IsProtected(1_000, "Titanium"));

            Assert.Equal(new[] { "Titanium" }, protection.Release(Titanium));
            Assert.False(protection.IsProtected(1_000, "Titanium"));
            Assert.Equal(0, protection.TotalRefs);
        }

        [Fact]
        public void Un_armement_plus_tardif_repousse_l_echeance_sans_la_raccourcir()
        {
            var protection = new MaterialProtection(windowMs: 10_000);
            protection.Arm(0, Titanium);
            protection.Arm(5_000, Titanium);

            // 15 000 = dernier armement + fenêtre ; l'ancienne échéance (10 000) ne doit pas gagner.
            Assert.True(protection.IsProtected(15_000, "Titanium"));
            Assert.False(protection.IsProtected(15_001, "Titanium"));
        }

        [Fact]
        public void L_armement_ignore_les_vides_et_ne_compte_pas_deux_fois_le_meme_nom()
        {
            var protection = new MaterialProtection();
            var names = new List<string> { "Titanium", "Titanium", "", null, "Copper" };

            Assert.Equal(new[] { "Titanium", "Copper" }, protection.Arm(0, names));
            Assert.Equal(2, protection.Count);
            Assert.Equal(2, protection.TotalRefs);
        }

        [Fact]
        public void Une_levee_de_noms_inconnus_ne_change_rien()
        {
            var protection = new MaterialProtection();
            protection.Arm(0, Titanium);

            Assert.Empty(protection.Release(new[] { "Copper", "", null }));
            Assert.Equal(1, protection.TotalRefs);
            Assert.True(protection.IsProtected(0, "Titanium"));
        }

        [Fact]
        public void La_purge_rend_le_nombre_de_materiaux_oublies()
        {
            var protection = new MaterialProtection(windowMs: 100);
            protection.Arm(0, new[] { "Titanium", "Copper" });
            protection.Arm(150, new[] { "Titanium" });

            // Copper est échu à 150, Titanium tient jusqu'à 10 150.
            Assert.Equal(1, protection.Prune(150));
            Assert.Equal(1, protection.Count);
            Assert.True(protection.IsProtected(150, "Titanium"));
            Assert.False(protection.IsProtected(150, "Copper"));
        }

        [Fact]
        public void Une_levee_sur_une_entree_echee_ne_ressuscite_rien()
        {
            var protection = new MaterialProtection(windowMs: 100);
            protection.Arm(0, Titanium);
            protection.Arm(0, Titanium);

            // L'entrée est échue mais pas encore purgée : la levée décrémente une référence, elle ne rend
            // pas la protection vivante, et la lecture suivante l'oublie.
            Assert.Empty(protection.Release(Titanium));
            Assert.Equal(1, protection.TotalRefs);
            Assert.False(protection.IsProtected(101, "Titanium"));
            Assert.Equal(0, protection.Count);
            Assert.Equal(0, protection.TotalRefs);
        }

        [Fact]
        public void Les_compteurs_restent_coherents_sur_des_operations_melees()
        {
            var protection = new MaterialProtection(windowMs: 1_000);
            protection.Arm(0, new[] { "Titanium", "Copper" });
            protection.Arm(500, Titanium);
            Assert.Equal(2, protection.Count);
            Assert.Equal(3, protection.TotalRefs);

            Assert.Empty(protection.Release(new[] { "Titanium" }));
            Assert.Equal(2, protection.TotalRefs);

            Assert.Equal(new[] { "Copper" }, protection.Release(new[] { "Copper" }));
            Assert.Equal(1, protection.Count);
            Assert.Equal(1, protection.TotalRefs);

            Assert.Equal(new[] { "Titanium" }, protection.Release(Titanium));
            Assert.Equal(0, protection.Count);
            Assert.Equal(0, protection.TotalRefs);
        }

        [Fact]
        public void Clear_oublie_tout_et_une_fenetre_invalide_est_ramenee_a_un()
        {
            var protection = new MaterialProtection(windowMs: 0);
            Assert.Equal(1, protection.WindowMs);

            protection.Arm(0, Titanium);
            Assert.True(protection.IsProtected(1, "Titanium"));

            protection.Clear();
            Assert.Equal(0, protection.Count);
            Assert.False(protection.IsProtected(0, "Titanium"));
        }
    }
}
