using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class StableHashTests
    {
        [Fact]
        public void Une_chaine_nulle_ou_vide_vaut_zero()
        {
            Assert.Equal(0L, StableHash.Of(null));
            Assert.Equal(0L, StableHash.Of(string.Empty));
        }

        [Fact]
        public void La_meme_chaine_donne_toujours_le_meme_hash()
        {
            Assert.Equal(StableHash.Of("SeaMoth"), StableHash.Of("SeaMoth"));
            Assert.Equal(StableHash.Of("Élodie"), StableHash.Of("Élodie"));
        }

        [Fact]
        public void La_casse_et_les_accents_changent_le_hash()
        {
            Assert.NotEqual(StableHash.Of("seamoth"), StableHash.Of("SeaMoth"));
            Assert.NotEqual(StableHash.Of("e"), StableHash.Of("é"));
        }

        [Fact]
        public void Les_deux_octets_d_un_caractere_UTF16_sont_haches()
        {
            // Ces deux chaînes ne diffèrent que par l'octet haut du même code UTF-16 :
            // hacher seulement l'octet bas les rendrait identiques.
            Assert.NotEqual(StableHash.Of("\u0100"), StableHash.Of("\u0000"));
        }
    }

    public sealed class ProtocolConsistencyTests
    {
        [Fact]
        public void La_cle_de_connexion_porte_la_version_du_protocole()
        {
            Assert.Equal(Protocol.ConnectionKey, "coop-subnautica-v" + Protocol.Version);
        }

        [Fact]
        public void Les_opcodes_sont_uniques_et_contigus_a_partir_de_un()
        {
            var values = Enum.GetValues(typeof(MessageType))
                .Cast<MessageType>()
                .Select(value => (int)value)
                .OrderBy(value => value)
                .ToArray();

            Assert.Equal(Enumerable.Range(1, values.Length).ToArray(), values);
        }

        [Fact]
        public void Tous_les_opcodes_ont_un_nom_explicite()
        {
            foreach (MessageType type in Enum.GetValues(typeof(MessageType)))
            {
                Assert.False(string.IsNullOrWhiteSpace(type.ToString()));
                Assert.DoesNotContain("Unknown", type.ToString());
            }
        }

        [Fact]
        public void Le_manifeste_versionne_annonce_la_meme_version_que_le_code()
        {
            var manifest = Path.Combine(RepoRoot(), "tools", "mod.manifest.json");
            Assert.True(File.Exists(manifest), $"manifeste introuvable : {manifest}");

            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;

            Assert.Equal(Protocol.Version, root.GetProperty("protocolVersion").GetInt32());

            var required = root.GetProperty("requiredFiles")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();

            Assert.Contains("Coop.Client.dll", required);
            Assert.Contains("Coop.Server.dll", required);
            Assert.Contains("Coop.Model.dll", required);
            Assert.Contains("LiteNetLib.dll", required);
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SubnauticaCoop.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("racine du dépôt (SubnauticaCoop.sln) introuvable");
        }
    }
}
