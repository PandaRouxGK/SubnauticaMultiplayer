using System;

namespace Coop.Model
{
    /// <summary>
    /// Cadence des demandes de craft d'une session : un seau à jetons.
    ///
    /// Le registre borne déjà le coût par l'inventaire, mais rien ne bornait le **rythme** : un client
    /// pouvait envoyer des demandes en boucle (chaque refus coûte une lecture de recette et une écriture
    /// de journal côté serveur). Huit demandes en rafale sont acceptées, puis une toutes les
    /// `RefillMs` — soit ~120 par minute en régime établi — ce qui laisse largement passer un joueur qui
    /// crafts, et coupe une boucle automatisée.
    ///
    /// L'arithmétique est **entière** (milli-jetons) : une recharge partielle de 499/500 ne doit pas
    /// refuser un jeton disponible à cause d'un arrondi flottant (mesuré en test).
    ///
    /// Pur (aucune horloge implicite : le temps est passé en argument), donc testé hors jeu.
    /// </summary>
    public sealed class CraftBudget
    {
        public const int DefaultBurst = 8;
        public const long DefaultRefillMs = 500;

        private readonly int _burst;
        private readonly long _refillMs;
        private readonly long _capacity;

        // Un jeton vaut `_refillMs` milli-jetons.
        private long _milliTokens;
        private long _lastMs;
        private bool _started;

        public CraftBudget(int burst = DefaultBurst, long refillMs = DefaultRefillMs)
        {
            _burst = burst < 1 ? 1 : burst;
            _refillMs = refillMs < 1 ? 1 : refillMs;
            _capacity = _burst * _refillMs;
            _milliTokens = _capacity;
        }

        public int Burst => _burst;

        public long RefillMs => _refillMs;

        /// <summary>Jetons disponibles (fractionnaires : la recharge est continue). Pour les diagnostics.</summary>
        public double Tokens => (double)_milliTokens / _refillMs;

        /// <summary>
        /// Consomme un jeton si la cadence le permet. Sinon, `retryAfterMs` dit combien de temps attendre
        /// avant qu'un jeton ne soit disponible — le refus est donc **nommé et daté**, pas muet.
        /// </summary>
        public bool TryTake(long nowMs, out long retryAfterMs)
        {
            retryAfterMs = 0;

            if (!_started)
            {
                _started = true;
                _lastMs = nowMs;
                _milliTokens = _capacity;
            }

            // Une horloge qui recule (recalage) ne doit pas rendre de jetons : on repart de ce point sans
            // rien recharger.
            if (nowMs < _lastMs)
            {
                _lastMs = nowMs;
            }

            var elapsed = nowMs - _lastMs;
            if (elapsed > 0)
            {
                _milliTokens = Math.Min(_capacity, _milliTokens + elapsed);
                _lastMs = nowMs;
            }

            if (_milliTokens >= _refillMs)
            {
                _milliTokens -= _refillMs;
                return true;
            }

            retryAfterMs = _refillMs - _milliTokens;
            if (retryAfterMs < 1)
            {
                retryAfterMs = 1;
            }

            return false;
        }
    }
}
