using System;
using NeonRush.Domain.Ports;
using UnityEngine;

namespace NeonRush.Infrastructure.Iap
{
    /// <summary>
    /// A validator that approves everything. <b>Development builds only.</b>
    ///
    /// It contains a hard guard that refuses to run in a release build and fails the purchase
    /// instead of approving it. That guard is the entire reason this class is safe to exist.
    ///
    /// The failure mode it prevents is one of the most common and most expensive in mobile games:
    /// someone writes a permissive validator to unblock development, everyone forgets, and it ships.
    /// The economy is then drained within days by anyone replaying a captured receipt — because the
    /// game will grant 5,000 gems to literally any string. Making that mistake *impossible*, rather
    /// than relying on someone remembering, is worth ten lines of code.
    /// </summary>
    public sealed class DevReceiptValidator : IReceiptValidator
    {
        public void Validate(IapPurchase purchase, Action<ValidationResult> onFinished)
        {
            if (!Debug.isDebugBuild && !UnityEngine.Application.isEditor)
            {
                // Shipping with this bound is a catastrophic misconfiguration. Refuse loudly rather
                // than silently minting currency for anyone who asks.
                Debug.LogError(
                    "[DevReceiptValidator] A development receipt validator is bound in a RELEASE build. " +
                    "Refusing all purchases. Bind ServerReceiptValidator in the composition root.");

                onFinished?.Invoke(ValidationResult.Invalid("dev validator in release build"));
                return;
            }

            Debug.LogWarning($"[DevReceiptValidator] Approving '{purchase.ProductId}' WITHOUT validation (dev build).");
            onFinished?.Invoke(ValidationResult.Valid());
        }
    }

    /// <summary>
    /// The real validator: it asks the backend, and the backend decides.
    ///
    /// The client's entire job here is to be a courier. It carries the receipt to the server and
    /// carries the verdict back. It does not inspect the receipt, it does not decide anything, and
    /// it holds no credential that would let it — because it runs on the attacker's phone.
    ///
    /// The Cloud Function on the other end must do all four of these, and skipping any one of them
    /// is a hole:
    ///
    ///  1. <b>Verify the signature</b> with Google Play Developer API / Apple verifyReceipt, using
    ///     the server's own credentials.
    ///  2. <b>Check the transaction id against already-redeemed receipts.</b> Without this, a valid
    ///     receipt can be replayed forever — and a genuinely-signed receipt from one real 0,99 €
    ///     purchase becomes an infinite gem faucet.
    ///  3. <b>Check the product id matches what was actually bought</b>, so a receipt for the 0,99 €
    ///     pack cannot be redeemed against the 39,99 € one.
    ///  4. <b>Credit the currency in Firestore.</b> The currency is created on the SERVER. The client
    ///     is only ever told what its new balance is.
    ///
    /// Until the backend exists, this is not bound — see the composition root. Binding a validator
    /// that cannot reach a server would mean either failing every purchase or, far worse, quietly
    /// approving them.
    /// </summary>
    public sealed class ServerReceiptValidator : IReceiptValidator
    {
        private readonly string _endpoint;
        private readonly MonoBehaviour _coroutineHost;

        /// <param name="endpoint">The Cloud Function URL that validates receipts.</param>
        /// <param name="coroutineHost">Any live MonoBehaviour — UnityWebRequest needs a coroutine to run on.</param>
        public ServerReceiptValidator(string endpoint, MonoBehaviour coroutineHost)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Endpoint required.", nameof(endpoint));

            _endpoint = endpoint;
            _coroutineHost = coroutineHost ?? throw new ArgumentNullException(nameof(coroutineHost));
        }

        public void Validate(IapPurchase purchase, Action<ValidationResult> onFinished)
        {
            _coroutineHost.StartCoroutine(ValidateRoutine(purchase, onFinished));
        }

        private System.Collections.IEnumerator ValidateRoutine(IapPurchase purchase, Action<ValidationResult> onFinished)
        {
            var body = JsonUtility.ToJson(new ValidationRequest
            {
                productId = purchase.ProductId,
                receipt = purchase.Receipt,
                transactionId = purchase.TransactionId,
                platform = UnityEngine.Application.platform.ToString(),
            });

            using var request = new UnityEngine.Networking.UnityWebRequest(_endpoint, "POST");

            var payload = System.Text.Encoding.UTF8.GetBytes(body);
            request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(payload);
            request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 20;

            yield return request.SendWebRequest();

            if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                // A network failure is NOT a rejection. The player may well have paid. Fail closed
                // (grant nothing) but say why — the purchase is still pending with the platform store
                // and will be re-delivered on the next launch, at which point we try again.
                onFinished?.Invoke(ValidationResult.Invalid($"network: {request.error}"));
                yield break;
            }

            ValidationResponse response;
            try
            {
                response = JsonUtility.FromJson<ValidationResponse>(request.downloadHandler.text);
            }
            catch (Exception e)
            {
                onFinished?.Invoke(ValidationResult.Invalid($"malformed response: {e.Message}"));
                yield break;
            }

            onFinished?.Invoke(response is { valid: true }
                ? ValidationResult.Valid()
                : ValidationResult.Invalid(response?.reason ?? "rejected by server"));
        }

        [Serializable]
        private sealed class ValidationRequest
        {
            public string productId;
            public string receipt;
            public string transactionId;
            public string platform;
        }

        [Serializable]
        private sealed class ValidationResponse
        {
            public bool valid;
            public string reason;
        }
    }
}
