using System;
using System.Globalization;

namespace Coop.Model
{
    /// <summary>
    /// Identité de la **partie** chargée par une instance (P0, protocole v13).
    ///
    /// Pourquoi ce type existe : toute l'identité des entités repose sur « les deux instances ont
    /// chargé la même sauvegarde » (§28), et le handshake ne vérifiait ni la sauvegarde ni sa
    /// provenance — seulement la version du jeu et la liste de mods. Un client sur une vieille
    /// sauvegarde désynchronisait donc **silencieusement**. Cette empreinte transforme l'hypothèse
    /// en refus nommé.
    ///
    /// Ce qu'elle est, et ce qu'elle n'est pas :
    /// - elle identifie une **lignée** de partie (`slot` + `sessionId`) et son avancement
    ///   (`changeSet`) — pas le contenu octet par octet des fichiers : un hash de fichiers serait
    ///   faux dès que l'hôte sauvegarde, puisque le client tardif lit alors une sauvegarde plus
    ///   récente que celle enregistrée par le serveur au démarrage ;
    /// - `Hash` est une empreinte d'**intégrité** des champs (FNV-1a 64), pas une comparaison
    ///   d'identité : deux instances légitimement décalées d'un `changeSet` n'ont pas le même hash.
    /// </summary>
    public sealed class SaveIdentity
    {
        /// <summary>Nom du slot (`slot0000`).</summary>
        public string Slot;

        /// <summary>Identifiant de session de la sauvegarde (`aae68629-…`), stable entre les chargements.</summary>
        public string SessionId;

        /// <summary>Compteur d'écritures de la sauvegarde, lu dans le menu avec le slot.</summary>
        public int ChangeSet;

        /// <summary>Mode de jeu (`Freedom`, `Survival`, …).</summary>
        public string GameMode;

        /// <summary>
        /// Empreinte des quatre champs ci-dessus, calculée par <see cref="Create"/> et vérifiée à la
        /// lecture par <see cref="RecomputeHash"/>. Elle ne sert **pas** à comparer deux instances.
        /// </summary>
        public long Hash;

        /// <summary>Vrai si aucun slot n'a été résolu (client encore au menu, ou lecture impossible).</summary>
        public bool IsUnknown => string.IsNullOrEmpty(Slot);

        public static SaveIdentity Create(string slot, string sessionId, int changeSet, string gameMode)
        {
            var identity = new SaveIdentity
            {
                Slot = slot ?? string.Empty,
                SessionId = sessionId ?? string.Empty,
                ChangeSet = changeSet,
                GameMode = gameMode ?? string.Empty,
            };
            identity.Hash = identity.RecomputeHash();
            return identity;
        }

        /// <summary>Empreinte des champs, recalculable des deux côtés.</summary>
        public long RecomputeHash()
        {
            return StableHash.Of(Canonical());
        }

        /// <summary>
        /// Forme canonique : séparateur et ordre fixes, `changeSet` en culture invariante. Deux
        /// instances qui ont lu la même sauvegarde doivent produire exactement la même chaîne.
        /// </summary>
        public string Canonical()
        {
            // Les champs libres sont échappés : sans cela, `slot="a|b", session="c"` et
            // `slot="a", session="b|c"` produisaient la même chaîne, donc le même hash. Les
            // valeurs usuelles (`slot0000`, GUID, `Freedom`) traversent inchangées.
            return string.Join(
                "|",
                "save-v1",
                Escape(Slot),
                Escape(SessionId),
                ChangeSet.ToString(CultureInfo.InvariantCulture),
                Escape(GameMode));
        }

        /// <summary>Échappe les séparateurs pour que la forme canonique reste injective.</summary>
        private static string Escape(string value)
        {
            var text = value ?? string.Empty;
            if (text.IndexOf('\\') < 0 && text.IndexOf('|') < 0 &&
                text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)
            {
                return text;
            }

            var builder = new System.Text.StringBuilder(text.Length + 8);
            foreach (var character in text)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '|':
                        builder.Append("\\|");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    default:
                        builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>Résumé lisible pour les journaux — jamais interprété.</summary>
        public string Describe()
        {
            if (IsUnknown)
            {
                return "empreinte inconnue";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "slot='{0}' session='{1}' changeSet={2} mode={3} hash=0x{4:x16}",
                Slot,
                SessionId,
                ChangeSet,
                GameMode,
                Hash);
        }

        public void WriteTo(PacketWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            var slot = Slot ?? string.Empty;
            var session = SessionId ?? string.Empty;
            var mode = GameMode ?? string.Empty;

            if (slot.Length > Protocol.MaxSaveSlotLength)
            {
                throw new ProtocolException($"slot trop long ({slot.Length} > {Protocol.MaxSaveSlotLength})");
            }

            if (session.Length > Protocol.MaxSaveSessionLength)
            {
                throw new ProtocolException($"session de sauvegarde trop longue ({session.Length} > {Protocol.MaxSaveSessionLength})");
            }

            if (mode.Length > Protocol.MaxGameModeLength)
            {
                throw new ProtocolException($"mode de jeu trop long ({mode.Length} > {Protocol.MaxGameModeLength})");
            }

            writer.WriteString(slot);
            writer.WriteString(session);
            writer.WriteInt(ChangeSet);
            writer.WriteString(mode);
            writer.WriteLong(Hash);
        }

        public static SaveIdentity ReadFrom(PacketReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var identity = new SaveIdentity
            {
                Slot = reader.ReadString(),
                SessionId = reader.ReadString(),
                ChangeSet = reader.ReadInt(),
                GameMode = reader.ReadString(),
                Hash = reader.ReadLong(),
            };

            if (identity.Slot.Length > Protocol.MaxSaveSlotLength)
            {
                throw new ProtocolException($"slot reçu trop long ({identity.Slot.Length})");
            }

            if (identity.SessionId.Length > Protocol.MaxSaveSessionLength)
            {
                throw new ProtocolException($"session de sauvegarde reçue trop longue ({identity.SessionId.Length})");
            }

            if (identity.GameMode.Length > Protocol.MaxGameModeLength)
            {
                throw new ProtocolException($"mode de jeu reçu trop long ({identity.GameMode.Length})");
            }

            return identity;
        }
    }

    /// <summary>
    /// Règle de compatibilité entre l'empreinte du serveur et celle du client (P0).
    ///
    /// Elle est **pure** : elle ne lit ni disque ni jeu, donc elle se teste hors du jeu — c'est
    /// précisément ce que `tests/Coop.Model.Tests` fait.
    ///
    /// Pourquoi `changeSet` n'est pas une égalité stricte : l'hôte sauvegarde pendant la session,
    /// et un client qui rejoint ensuite lit une sauvegarde **plus récente** que celle que le serveur
    /// a enregistrée à son démarrage. Refuser ce cas rendrait toute reconnexion impossible. Un
    /// client **plus ancien** que le serveur, lui, est refusé : il n'a pas vu les écritures de
    /// l'hôte et son monde est en retard.
    /// </summary>
    public static class SaveCompatibility
    {
        /// <summary>
        /// Retourne <c>null</c> si les deux instances peuvent jouer ensemble, sinon le motif du refus.
        /// Un motif est toujours **nommé** : « incompatible » ne dit pas quoi corriger.
        /// </summary>
        public static string Explain(SaveIdentity server, SaveIdentity client)
        {
            if (client == null || client.IsUnknown)
            {
                return "empreinte de sauvegarde inconnue côté client (le client n'a pas encore résolu sa partie)";
            }

            if (server == null || server.IsUnknown)
            {
                return "empreinte de sauvegarde pas encore connue côté serveur";
            }

            if (client.Hash != client.RecomputeHash())
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "empreinte de sauvegarde altérée côté client (annoncée=0x{0:x16}, recalculée=0x{1:x16})",
                    client.Hash,
                    client.RecomputeHash());
            }

            // L'empreinte du serveur est vérifiée avec la même règle : sinon un champ serveur
            // modifié après coup passait inaperçu et la comparaison portait sur une identité
            // incohérente. Un hash serveur invalide est un défaut local, pas une faute du client.
            if (server.Hash != server.RecomputeHash())
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "empreinte de sauvegarde altérée côté serveur (annoncée=0x{0:x16}, recalculée=0x{1:x16})",
                    server.Hash,
                    server.RecomputeHash());
            }

            if (!string.Equals(server.Slot, client.Slot, StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "sauvegarde différente (client='{0}', serveur='{1}')",
                    client.Slot,
                    server.Slot);
            }

            if (!string.Equals(server.SessionId, client.SessionId, StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "session de sauvegarde différente (client='{0}', serveur='{1}')",
                    client.SessionId,
                    server.SessionId);
            }

            if (!string.Equals(server.GameMode, client.GameMode, StringComparison.OrdinalIgnoreCase))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "mode de jeu différent (client='{0}', serveur='{1}')",
                    client.GameMode,
                    server.GameMode);
            }

            if (client.ChangeSet < server.ChangeSet)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "sauvegarde cliente plus ancienne (client changeSet={0}, serveur={1}) — rechargez la partie à jour",
                    client.ChangeSet,
                    server.ChangeSet);
            }

            return null;
        }
    }
}
