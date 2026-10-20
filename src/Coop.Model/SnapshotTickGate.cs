namespace Coop.Model
{
    /// <summary>
    /// Filtre d'ordre pour les flux **Unreliable** : un snapshot ne s'applique que si son tick est
    /// postérieur au dernier accepté.
    ///
    /// Pourquoi ce filtre existe : `CreatureSnapshot` voyage en Unreliable. Un paquet plus ancien livré
    /// après un plus récent écrivait une position **ancienne** par-dessus la récente, et la créature
    /// reculait d'un bond. Mesuré à trois clients le 2026-10-20 : 2,60 m et 2,99 m sur 0,03–0,04 s
    /// (65–87 m/s) pour des créatures qui nagent à 2 m/s, alors que le serveur n'avait pas bougé.
    ///
    /// Le cas symétrique compte autant : un serveur **redémarré** repart d'un tick bas. Sans exception,
    /// le client ignorerait tous ses états pendant des heures. Un recul de plus de
    /// <see cref="RestartToleranceTicks"/> (100 s à 20 Hz) est donc lu comme un redémarrage, pas comme
    /// du retard — et le seuil est explicite plutôt que magique.
    ///
    /// La classe ne connaît ni Unity ni le réseau : elle porte la règle, et se teste hors jeu.
    /// </summary>
    public sealed class SnapshotTickGate
    {
        /// <summary>Recul (en ticks) au-delà duquel on considère que le serveur a redémarré.</summary>
        public const long RestartToleranceTicks = 2000;

        private long _lastAccepted;

        /// <summary>Dernier tick accepté (0 tant qu'aucun tick n'a été vu).</summary>
        public long LastAccepted => _lastAccepted;

        /// <summary>Nombre d'états refusés parce qu'ils n'avançaient pas le temps.</summary>
        public int StaleCount { get; private set; }

        /// <summary>
        /// Vrai si le snapshot doit être appliqué. Un tick nul ou négatif n'apporte aucune information
        /// d'ordre : il est accepté **sans** déplacer le dernier tick connu.
        /// </summary>
        public bool TryAccept(long tick)
        {
            if (tick <= 0L)
            {
                return true;
            }

            if (tick <= _lastAccepted && _lastAccepted - tick < RestartToleranceTicks)
            {
                StaleCount++;
                return false;
            }

            _lastAccepted = tick;
            return true;
        }
    }
}
