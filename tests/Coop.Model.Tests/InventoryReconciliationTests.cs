using System;
using System.Collections.Generic;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// v20 : la réconciliation ne doit **jamais** toucher à ce que le registre ne suit pas. Le cas qui
    /// compte est l'admission : l'instantané y est vide, et une réconciliation naïve viderait tout
    /// l'inventaire sauvegardé du joueur dès la connexion.
    /// </summary>
    public sealed class InventoryReconciliationTests
    {
        private static Func<string, int> Local(params (string Name, int Count)[] items)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                counts[item.Name] = item.Count;
            }

            return name => counts.TryGetValue(name, out var count) ? count : 0;
        }

        private static InventoryEntry[] Snapshot(params (string Name, int Count)[] items)
        {
            var entries = new InventoryEntry[items.Length];
            for (var index = 0; index < items.Length; index++)
            {
                entries[index] = new InventoryEntry(items[index].Name, items[index].Count);
            }

            return entries;
        }

        [Fact]
        public void L_instantane_d_admission_ne_vide_pas_l_inventaire_du_jeu()
        {
            // Le registre ne contient que la session : les objets de la sauvegarde ne sont pas à lui.
            var reconciliation = InventoryReconciliation.Compute(
                null,
                Array.Empty<InventoryEntry>(),
                Local(("PowerCell", 12), ("Titanium", 40)));

            Assert.True(reconciliation.IsEmpty);
            Assert.Equal(0, reconciliation.Examined);
            Assert.Equal(0, reconciliation.MissingLocally);
        }

        [Fact]
        public void Un_ramassage_accorde_ne_retire_rien()
        {
            var reconciliation = InventoryReconciliation.Compute(
                Array.Empty<InventoryEntry>(),
                Snapshot(("PowerCell", 1)),
                Local(("PowerCell", 1)));

            Assert.True(reconciliation.IsEmpty);
            Assert.Equal(1, reconciliation.Examined);
        }

        [Fact]
        public void Un_debit_accorde_retire_l_unite_que_le_jeu_possede_encore()
        {
            var reconciliation = InventoryReconciliation.Compute(
                Snapshot(("PowerCell", 1)),
                Array.Empty<InventoryEntry>(),
                Local(("PowerCell", 1)));

            Assert.Single(reconciliation.Removals);
            Assert.Equal("PowerCell", reconciliation.Removals[0].TechTypeName);
            Assert.Equal(1, reconciliation.Removals[0].Count);
        }

        [Fact]
        public void Un_debit_de_deux_unites_retire_les_deux()
        {
            var reconciliation = InventoryReconciliation.Compute(
                Snapshot(("Titanium", 3)),
                Snapshot(("Titanium", 1)),
                Local(("Titanium", 3)));

            Assert.Single(reconciliation.Removals);
            Assert.Equal(2, reconciliation.Removals[0].Count);
        }

        [Fact]
        public void Un_objet_jamais_cite_par_un_instantane_n_est_jamais_touche()
        {
            // Le joueur a 5 PowerCell de sa sauvegarde, le registre n'en a jamais parlé.
            var reconciliation = InventoryReconciliation.Compute(
                Array.Empty<InventoryEntry>(),
                Array.Empty<InventoryEntry>(),
                Local(("PowerCell", 5)));

            Assert.True(reconciliation.IsEmpty);
            Assert.Equal(0, reconciliation.Examined);
        }

        [Fact]
        public void Un_deja_retire_localement_ne_produit_pas_de_retrait()
        {
            // Le débit a vidé le registre et le jeu a déjà consommé l'objet (ou le joueur l'a lâché).
            var reconciliation = InventoryReconciliation.Compute(
                Snapshot(("Titanium", 1)),
                Array.Empty<InventoryEntry>(),
                Local());

            Assert.True(reconciliation.IsEmpty);
        }

        [Fact]
        public void Le_serveur_en_avance_est_compte_sans_creer_d_objet()
        {
            var reconciliation = InventoryReconciliation.Compute(
                Snapshot(("Titanium", 1)),
                Snapshot(("Titanium", 2)),
                Local());

            Assert.True(reconciliation.IsEmpty);
            Assert.Equal(1, reconciliation.MissingLocally);
            Assert.Equal(1, reconciliation.Examined);
        }

        [Fact]
        public void La_reconciliation_est_idempotente_apres_application()
        {
            var next = Array.Empty<InventoryEntry>();
            var first = InventoryReconciliation.Compute(
                Snapshot(("PowerCell", 1)),
                next,
                Local(("PowerCell", 1)));

            Assert.Equal(1, first.Removals[0].Count);

            // Après application, le local ne contient plus l'objet.
            var second = InventoryReconciliation.Compute(
                Snapshot(("PowerCell", 1)),
                next,
                Local());

            Assert.True(second.IsEmpty);
        }

        [Fact]
        public void Rejouer_contre_le_meme_instantane_est_idempotent()
        {
            // C'est l'invariant sur lequel repose la reprise d'un retrait reporté (§78) : le client compare
            // le local au **même** instantané serveur, autant de fois que nécessaire.
            var snapshot = Snapshot(("Titanium", 0), ("Copper", 1));

            var first = InventoryReconciliation.Compute(
                snapshot,
                snapshot,
                Local(("Titanium", 2), ("Copper", 1)));

            Assert.Single(first.Removals);
            Assert.Equal("Titanium", first.Removals[0].TechTypeName);
            Assert.Equal(2, first.Removals[0].Count);

            // Le jeu a retiré ses deux unités : le même instantané ne demande plus rien.
            var second = InventoryReconciliation.Compute(
                snapshot,
                snapshot,
                Local(("Copper", 1)));

            Assert.True(second.IsEmpty);
            Assert.Equal(0, second.MissingLocally);
        }

        [Fact]
        public void Les_retraits_sont_tries_par_nom()
        {
            var reconciliation = InventoryReconciliation.Compute(
                Snapshot(("Titanium", 1), ("Copper", 1), ("Bladderfish", 1)),
                Array.Empty<InventoryEntry>(),
                Local(("Titanium", 1), ("Copper", 1), ("Bladderfish", 1)));

            Assert.Equal(3, reconciliation.Removals.Length);
            Assert.Equal("Bladderfish", reconciliation.Removals[0].TechTypeName);
            Assert.Equal("Copper", reconciliation.Removals[1].TechTypeName);
            Assert.Equal("Titanium", reconciliation.Removals[2].TechTypeName);
        }

        [Fact]
        public void Un_compteur_local_negatif_est_traite_comme_zero()
        {
            var reconciliation = InventoryReconciliation.Compute(
                Snapshot(("Titanium", 1)),
                Array.Empty<InventoryEntry>(),
                _ => -3);

            Assert.True(reconciliation.IsEmpty);
            Assert.Equal(0, reconciliation.MissingLocally);
        }
    }
}
