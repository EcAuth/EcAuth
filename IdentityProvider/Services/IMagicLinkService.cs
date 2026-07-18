namespace IdentityProvider.Services
{
    /// <summary>
    /// パスキー紛失時のリカバリ動線として、メール経由のマジックリンクログインを提供するサービス。
    /// 設計書「マジックリンクログインフロー」を参照。
    /// </summary>
    public interface IMagicLinkService
    {
        /// <summary>
        /// マジックリンクの発行を要求する。Account が存在する場合のみログインリンクをメール送信する。
        /// <para>
        /// Email enumeration 対策のため、Account の存在有無に関わらず本メソッドは正常終了する
        /// （存在しない場合もダミーのハッシュ計算で処理時間を合わせる）。呼び出し側は常に同一の
        /// レスポンスを返すこと。レート制限を超過した場合のみ <see cref="Exceptions.MagicLinkException"/>
        /// （429）をスローする（閾値は Account 存在有無に依存しない）。
        /// </para>
        /// </summary>
        /// <param name="email">ログインを要求するメールアドレス（未正規化で可）</param>
        /// <param name="ipAddress">リクエスト元 IP（レート制限・監査用、取得不能なら null）</param>
        /// <param name="userAgent">リクエスト元 User-Agent（監査用、取得不能なら null）</param>
        /// <param name="ct">キャンセルトークン</param>
        Task RequestAsync(string? email, string? ipAddress, string? userAgent, CancellationToken ct = default);

        /// <summary>
        /// マジックリンクのトークンを検証して単発消費し、アクセストークン／ID トークンを直接発行する。
        /// <para>
        /// 単発使用は Compare-And-Set（<c>used_at IS NULL</c> 条件の原子的 UPDATE）で保証する。
        /// 無効／期限切れ／使用済みはいずれも区別せず同一の <see cref="Exceptions.MagicLinkException"/>（400）をスローする。
        /// </para>
        /// <para>
        /// <strong>認可コードを介さずトークンを直接返す理由</strong>: 管理コンソール（マイページ）は
        /// public client であり、<c>/v1/token</c> は public client に PKCE（RFC 7636）を必須とする。
        /// マジックリンクはメール往復（別端末・別ブラウザもあり得る）のため <c>code_verifier</c> を
        /// リンク着地側と紐づけられず、認可コード経由では原理的に PKCE 要件を満たせない。
        /// 「PKCE 免除の認可コード」を作ると横取り対策の不変条件に穴が空くため、
        /// リカバリ経路では認可コード自体を発行しない設計とする。トークン窃取に対する保護は
        /// トークンの単発消費（Compare-And-Set）と短い有効期限が担う。
        /// </para>
        /// </summary>
        /// <param name="token">メールリンクから受け取った平文トークン</param>
        /// <param name="ct">キャンセルトークン</param>
        /// <returns>発行済みトークン一式</returns>
        Task<MagicLinkVerifyResult> VerifyAsync(string token, CancellationToken ct = default);
    }

    /// <summary>
    /// マジックリンク検証の結果。フロントエンド（マイページ）はこのトークンをそのまま保持し、
    /// 認可コード交換（<c>/auth/callback</c>）を経ずにマイページを表示する。
    /// </summary>
    /// <param name="AccessToken">アクセストークン（<c>managed_orgs</c> クレームを含む）</param>
    /// <param name="IdToken">ID トークン</param>
    /// <param name="ExpiresIn">アクセストークンの有効期間（秒）</param>
    /// <param name="TokenType">トークン種別（<c>Bearer</c>）</param>
    public sealed record MagicLinkVerifyResult(
        string AccessToken,
        string IdToken,
        int ExpiresIn,
        string TokenType);
}
