using Fido2NetLib;
using Fido2NetLib.Objects;
using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// B2Bパスキー認証サービスのインターフェース
    /// </summary>
    public interface IB2BPasskeyService
    {
        #region Request/Response Types

        /// <summary>
        /// 登録オプション生成リクエスト
        /// </summary>
        public class RegistrationOptionsRequest
        {
            /// <summary>
            /// クライアントID（文字列形式）
            /// </summary>
            public string ClientId { get; set; } = string.Empty;

            /// <summary>
            /// Relying Party ID（EC-CUBEサイトのドメイン）
            /// </summary>
            public string RpId { get; set; } = string.Empty;

            /// <summary>
            /// B2BユーザーのSubject（UUID）
            /// </summary>
            public string B2BSubject { get; set; } = string.Empty;

            /// <summary>
            /// ユーザー表示名
            /// </summary>
            public string? DisplayName { get; set; }

            /// <summary>
            /// デバイス名（"MacBook Pro", "iPhone" 等）
            /// </summary>
            public string? DeviceName { get; set; }
        }

        /// <summary>
        /// 登録オプション生成結果
        /// </summary>
        public class RegistrationOptionsResult
        {
            /// <summary>
            /// セッションID（検証時に使用）
            /// </summary>
            public string SessionId { get; set; } = string.Empty;

            /// <summary>
            /// WebAuthn登録オプション（Fido2.NetLib形式）
            /// </summary>
            public CredentialCreateOptions Options { get; set; } = null!;

            /// <summary>
            /// JITプロビジョニングでB2BUserが新規作成されたか
            /// </summary>
            public bool IsProvisioned { get; set; }
        }

        /// <summary>
        /// 登録検証リクエスト
        /// </summary>
        public class RegistrationVerifyRequest
        {
            /// <summary>
            /// セッションID（登録オプション生成時に取得）
            /// </summary>
            public string SessionId { get; set; } = string.Empty;

            /// <summary>
            /// クライアントID（文字列形式）
            /// </summary>
            public string ClientId { get; set; } = string.Empty;

            /// <summary>
            /// Authenticatorからのレスポンス
            /// </summary>
            public AuthenticatorAttestationRawResponse AttestationResponse { get; set; } = null!;

            /// <summary>
            /// デバイス名（"MacBook Pro", "iPhone" 等）
            /// </summary>
            public string? DeviceName { get; set; }
        }

        /// <summary>
        /// 登録検証結果
        /// </summary>
        public class RegistrationVerifyResult
        {
            /// <summary>
            /// 登録成功したか
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// 登録されたCredential ID（Base64URL形式）
            /// </summary>
            public string? CredentialId { get; set; }

            /// <summary>
            /// エラーメッセージ（失敗時）
            /// </summary>
            public string? ErrorMessage { get; set; }
        }

        /// <summary>
        /// 認証オプション生成リクエスト
        /// </summary>
        public class AuthenticationOptionsRequest
        {
            /// <summary>
            /// クライアントID（文字列形式）
            /// </summary>
            public string ClientId { get; set; } = string.Empty;

            /// <summary>
            /// Relying Party ID（EC-CUBEサイトのドメイン）
            /// </summary>
            public string RpId { get; set; } = string.Empty;

            /// <summary>
            /// B2BユーザーのSubject（特定ユーザーの認証時のみ、省略可能）
            /// </summary>
            public string? B2BSubject { get; set; }
        }

        /// <summary>
        /// 認証オプション生成結果
        /// </summary>
        public class AuthenticationOptionsResult
        {
            /// <summary>
            /// セッションID（検証時に使用）
            /// </summary>
            public string SessionId { get; set; } = string.Empty;

            /// <summary>
            /// WebAuthn認証オプション（Fido2.NetLib形式）
            /// </summary>
            public AssertionOptions Options { get; set; } = null!;
        }

        /// <summary>
        /// 認証検証リクエスト
        /// </summary>
        public class AuthenticationVerifyRequest
        {
            /// <summary>
            /// セッションID（認証オプション生成時に取得）
            /// </summary>
            public string SessionId { get; set; } = string.Empty;

            /// <summary>
            /// クライアントID（文字列形式）
            /// </summary>
            public string ClientId { get; set; } = string.Empty;

            /// <summary>
            /// Authenticatorからのレスポンス
            /// </summary>
            public AuthenticatorAssertionRawResponse AssertionResponse { get; set; } = null!;
        }

        /// <summary>
        /// 認証検証結果
        /// </summary>
        public class AuthenticationVerifyResult
        {
            /// <summary>
            /// 認証成功したか
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// 認証されたB2BユーザーのSubject
            /// </summary>
            public string? B2BSubject { get; set; }

            /// <summary>
            /// 使用されたCredential ID（Base64URL形式）
            /// </summary>
            public string? CredentialId { get; set; }

            /// <summary>
            /// エラーメッセージ（失敗時）
            /// </summary>
            public string? ErrorMessage { get; set; }
        }

        /// <summary>
        /// パスキー情報（一覧表示用）
        /// </summary>
        public class PasskeyInfo
        {
            /// <summary>
            /// Credential ID（Base64URL形式）
            /// </summary>
            public string CredentialId { get; set; } = string.Empty;

            /// <summary>
            /// デバイス名
            /// </summary>
            public string? DeviceName { get; set; }

            /// <summary>
            /// Authenticator Attestation GUID
            /// </summary>
            public Guid AaGuid { get; set; }

            /// <summary>
            /// トランスポート種別
            /// </summary>
            public string[] Transports { get; set; } = Array.Empty<string>();

            /// <summary>
            /// 登録日時
            /// </summary>
            public DateTimeOffset CreatedAt { get; set; }

            /// <summary>
            /// 最終使用日時
            /// </summary>
            public DateTimeOffset? LastUsedAt { get; set; }
        }

        #endregion

        #region Registration Methods

        /// <summary>
        /// パスキー登録オプションを生成する
        /// </summary>
        /// <param name="request">登録オプション生成リクエスト</param>
        /// <returns>登録オプション（WebAuthn CredentialCreateOptions）</returns>
        Task<RegistrationOptionsResult> CreateRegistrationOptionsAsync(RegistrationOptionsRequest request);

        /// <summary>
        /// パスキー登録を検証し、クレデンシャルを保存する
        /// </summary>
        /// <param name="request">登録検証リクエスト</param>
        /// <returns>登録検証結果</returns>
        Task<RegistrationVerifyResult> VerifyRegistrationAsync(RegistrationVerifyRequest request);

        #endregion

        #region Authentication Methods

        /// <summary>
        /// パスキー認証オプションを生成する
        /// </summary>
        /// <param name="request">認証オプション生成リクエスト</param>
        /// <returns>認証オプション（WebAuthn AssertionOptions）</returns>
        Task<AuthenticationOptionsResult> CreateAuthenticationOptionsAsync(AuthenticationOptionsRequest request);

        /// <summary>
        /// パスキー認証を検証する
        /// </summary>
        /// <param name="request">認証検証リクエスト</param>
        /// <returns>認証検証結果</returns>
        Task<AuthenticationVerifyResult> VerifyAuthenticationAsync(AuthenticationVerifyRequest request);

        #endregion

        #region Management Methods

        /// <summary>
        /// ユーザーのパスキー一覧を取得する
        /// </summary>
        /// <param name="b2bSubject">B2BユーザーのSubject</param>
        /// <returns>パスキー情報一覧</returns>
        Task<IReadOnlyList<PasskeyInfo>> GetCredentialsBySubjectAsync(string b2bSubject);

        /// <summary>
        /// パスキーを削除する
        /// </summary>
        /// <param name="b2bSubject">B2BユーザーのSubject</param>
        /// <param name="credentialId">Credential ID（Base64URL形式）</param>
        /// <returns>削除に成功した場合true</returns>
        Task<bool> DeleteCredentialAsync(string b2bSubject, string credentialId);

        /// <summary>
        /// ユーザーのパスキー数を取得する
        /// </summary>
        /// <param name="b2bSubject">B2BユーザーのSubject</param>
        /// <returns>パスキー数</returns>
        Task<int> CountCredentialsBySubjectAsync(string b2bSubject);

        #endregion
    }
}
