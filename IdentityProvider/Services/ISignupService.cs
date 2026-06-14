using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// Account 申込フロー（Phase D-1）の中核サービス。
    /// 申込リクエストの受付（確認メール送信）、メール確認による本登録
    /// （Account / B2BUser / 顧客 Organization / Client / RsaKeyPair / AccountOrganization の原子生成）、
    /// および申込状況の照会を担う。設計書「申込 API 設計（Phase D）」を参照。
    /// </summary>
    public interface ISignupService
    {
        /// <summary>
        /// 申込リクエストを受け付ける。入力をバリデーションし、
        /// 確認トークン付きの <see cref="SignupRequest"/> を作成して確認メールを送信する。
        /// </summary>
        /// <param name="input">申込フォームの入力値。</param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>作成された <see cref="SignupRequest"/>。</returns>
        /// <exception cref="Exceptions.SignupValidationException">
        /// バリデーション違反時（HTTP 422 相当）。
        /// </exception>
        Task<SignupRequest> RequestAsync(SignupInput input, CancellationToken ct = default);

        /// <summary>
        /// 確認トークンを検証し、本登録レコード一式を 1 トランザクションで原子生成する。
        /// </summary>
        /// <param name="token">確認メールに埋め込まれた平文トークン。</param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>確認済みとなった <see cref="SignupRequest"/>。</returns>
        /// <exception cref="Exceptions.SignupValidationException">
        /// トークンが無効・期限切れ・確認済みの場合、または再バリデーション違反時（組織コード衝突は HTTP 409 相当）。
        /// </exception>
        Task<SignupRequest> ConfirmAsync(string token, CancellationToken ct = default);

        /// <summary>
        /// 確認トークンに対応する申込の状況を返す。
        /// </summary>
        /// <param name="token">確認トークン。</param>
        /// <param name="ct">キャンセルトークン。</param>
        Task<SignupStatus> GetStatusAsync(string token, CancellationToken ct = default);
    }

    /// <summary>
    /// 申込リクエストの入力値。Controller の DTO からマッピングして渡す。
    /// </summary>
    public sealed record SignupInput
    {
        public string? Email { get; init; }
        public string? OrganizationName { get; init; }
        public string? ContactName { get; init; }
        public string? ProductionSiteUrl { get; init; }
        public string? TestSiteUrl { get; init; }
        public string? EcCubeVersion { get; init; }

        /// <summary>同意した利用規約バージョン。未指定時は既定値を使用。</summary>
        public string? TermsVersion { get; init; }

        /// <summary>同意したプライバシーポリシーバージョン。未指定時は既定値を使用。</summary>
        public string? PrivacyVersion { get; init; }

        /// <summary>同意した Cookie ポリシーバージョン。未指定時は既定値を使用。</summary>
        public string? CookieVersion { get; init; }
    }

    /// <summary>
    /// 申込状況。
    /// </summary>
    public enum SignupStatus
    {
        /// <summary>該当する申込が存在しない。</summary>
        NotFound,

        /// <summary>確認待ち（未確認かつ有効期限内）。</summary>
        Pending,

        /// <summary>確認済み（本登録完了）。</summary>
        Confirmed,

        /// <summary>有効期限切れ（未確認のまま expires_at を経過）。</summary>
        Expired
    }
}
