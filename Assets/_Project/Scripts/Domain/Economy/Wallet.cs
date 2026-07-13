using System;
using System.Collections.Generic;
using NeonRush.Core.Events;

namespace NeonRush.Domain.Economy
{
    /// <summary>
    /// The player's balances.
    ///
    /// <b>This wallet is a prediction, not the truth.</b> The authoritative balance lives on the
    /// server (see ARCHITECTURE.md §8). This exists so the game can respond instantly — a player who
    /// picks up a coin must see the number rise on that frame, not after a network round trip. When
    /// the server disagrees, the server wins and this reconciles.
    ///
    /// Everything here follows from that one fact:
    ///
    ///  · Balances are held in <see cref="ObscuredInt"/>, so a memory scanner cannot find them.
    ///  · Every movement is recorded in a ledger with a reason, so the economy can be audited and
    ///    the client's claimed history can be checked against the server's.
    ///  · Debits are checked and can fail; they never silently go negative.
    ///  · Tampering is detected and reported rather than swallowed.
    ///
    /// Pure C#. No Unity. The entire economy is unit-testable in milliseconds.
    /// </summary>
    public sealed class Wallet
    {
        /// <summary>
        /// Cap on any single balance.
        ///
        /// Not paranoia: without it, a compromised or buggy grant can push a balance toward
        /// <see cref="int.MaxValue"/>, and the next legitimate credit silently overflows into a
        /// *negative* balance. The player then has minus two billion coins, cannot buy anything, and
        /// support cannot explain why. Refusing the credit loudly is far better than wrapping
        /// quietly.
        /// </summary>
        public const int MaxBalance = 999_999_999;

        /// <summary>
        /// How many transactions are retained locally.
        ///
        /// The ledger is a rolling window for offline sync and support, not a permanent record —
        /// the permanent record is the server's. Unbounded growth would mean a player who has run
        /// 50,000 times carries a 50,000-entry list into every save file.
        /// </summary>
        private const int LedgerCapacity = 200;

        private readonly IEventBus _bus;
        private readonly Dictionary<CurrencyType, ObscuredInt> _balances = new();
        private readonly List<Transaction> _ledger = new(LedgerCapacity);

        public Wallet(IEventBus bus, int startingCoins = 0, int startingGems = 0)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));

            if (startingCoins < 0) throw new ArgumentOutOfRangeException(nameof(startingCoins));
            if (startingGems < 0) throw new ArgumentOutOfRangeException(nameof(startingGems));

            _balances[CurrencyType.Coins] = new ObscuredInt(startingCoins);
            _balances[CurrencyType.Gems] = new ObscuredInt(startingGems);
        }

        /// <summary>The most recent transactions, oldest first. A rolling window; see <see cref="LedgerCapacity"/>.</summary>
        public IReadOnlyList<Transaction> Ledger => _ledger;

        /// <summary>
        /// True once tampering has been detected. The balance is not trustworthy and the game must
        /// resynchronise from the server before honouring any further spend.
        /// </summary>
        public bool IsCompromised { get; private set; }

        /// <summary>
        /// Current balance. Returns 0 and flags the wallet as compromised if the underlying memory
        /// has been modified — it never throws, because an honest player should not see a crash.
        /// </summary>
        public int Balance(CurrencyType currency)
        {
            if (!_balances.TryGetValue(currency, out var obscured)) return 0;

            if (obscured.TryGetValue(out var value)) return value;

            // Memory was modified externally. Do not throw at the player; report it, lock the
            // wallet, and let the sync layer restore the truth from the server.
            Compromise(currency);
            return 0;
        }

        /// <summary>True when the player can afford <paramref name="amount"/>.</summary>
        public bool CanAfford(CurrencyType currency, int amount) => Balance(currency) >= amount;

        /// <summary>
        /// Adds currency.
        /// </summary>
        /// <returns>The amount actually credited, which is less than requested if the cap was hit.</returns>
        public int Credit(CurrencyType currency, int amount, TransactionReason reason)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Credit cannot be negative. Use TryDebit.");
            if (amount == 0) return 0;

            if (!reason.IsFaucet())
            {
                // A sink reason on a credit means someone has the sign backwards. Left unchecked, the
                // economy dashboard would show gems being created by "StorePurchase", and the
                // inflation source would be invisible.
                throw new ArgumentException(
                    $"'{reason}' is a sink, but it is being used to credit currency. " +
                    "This inverts the economy funnel and makes inflation undiagnosable.",
                    nameof(reason));
            }

            var current = Balance(currency);
            if (IsCompromised) return 0;

            // Clamp rather than overflow. See MaxBalance.
            var credited = amount;
            var next = (long)current + amount;

            if (next > MaxBalance)
            {
                credited = MaxBalance - current;
                next = MaxBalance;
            }

            if (credited <= 0) return 0;

            _balances[currency] = new ObscuredInt((int)next);

            Record(new Transaction(currency, credited, (int)next, reason));
            _bus.Publish(new CurrencyChanged(currency, credited, (int)next, reason));

            return credited;
        }

        /// <summary>
        /// Removes currency, if the player can afford it.
        ///
        /// Returns false rather than throwing on insufficient funds, because that is not an error —
        /// it is an ordinary, expected outcome that the store must handle gracefully. It also
        /// publishes <see cref="PurchaseFailedInsufficientFunds"/>, which is the single most
        /// commercially valuable signal the economy produces: the exact moment a player wanted
        /// something they could not have.
        /// </summary>
        public bool TryDebit(CurrencyType currency, int amount, TransactionReason reason)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Debit cannot be negative. Use Credit.");
            if (amount == 0) return true;

            if (!reason.IsSink())
            {
                throw new ArgumentException(
                    $"'{reason}' is a faucet, but it is being used to debit currency.",
                    nameof(reason));
            }

            var current = Balance(currency);

            if (IsCompromised)
            {
                // Never honour a spend from a wallet we know has been tampered with. The balance is
                // a lie, and letting the player spend it converts a detected cheat into a granted one.
                return false;
            }

            if (current < amount)
            {
                _bus.Publish(new PurchaseFailedInsufficientFunds(currency, amount, current));
                return false;
            }

            var next = current - amount;
            _balances[currency] = new ObscuredInt(next);

            Record(new Transaction(currency, -amount, next, reason));
            _bus.Publish(new CurrencyChanged(currency, -amount, next, reason));

            return true;
        }

        /// <summary>
        /// Overwrites a balance with the server's authoritative value.
        ///
        /// This is the reconciliation path, and it is the only way a balance may be set directly. It
        /// also clears the compromised flag: once the server has told us the truth, the local lie no
        /// longer matters.
        /// </summary>
        public void SyncFromServer(CurrencyType currency, int authoritativeBalance)
        {
            if (authoritativeBalance < 0) throw new ArgumentOutOfRangeException(nameof(authoritativeBalance));

            var previous = _balances.TryGetValue(currency, out var o) && o.TryGetValue(out var p) ? p : 0;

            _balances[currency] = new ObscuredInt(authoritativeBalance);
            IsCompromised = false;

            if (previous != authoritativeBalance)
            {
                _bus.Publish(new CurrencyChanged(
                    currency,
                    authoritativeBalance - previous,
                    authoritativeBalance,
                    TransactionReason.AdminGrant));
            }
        }

        private void Compromise(CurrencyType currency)
        {
            IsCompromised = true;
            _bus.Publish(new WalletTamperDetected(currency));
        }

        private void Record(Transaction transaction)
        {
            if (_ledger.Count >= LedgerCapacity)
            {
                _ledger.RemoveAt(0);
            }

            _ledger.Add(transaction);
        }
    }

    /// <summary>One movement of currency. Immutable, and always carries why it happened.</summary>
    public readonly struct Transaction
    {
        public readonly CurrencyType Currency;

        /// <summary>Signed. Positive = credited, negative = debited.</summary>
        public readonly int Delta;

        /// <summary>Balance immediately after this transaction.</summary>
        public readonly int BalanceAfter;

        public readonly TransactionReason Reason;

        public Transaction(CurrencyType currency, int delta, int balanceAfter, TransactionReason reason)
        {
            Currency = currency;
            Delta = delta;
            BalanceAfter = balanceAfter;
            Reason = reason;
        }
    }
}
