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
        /// マジックリンクのトークンを検証して単発消費し、認可コードを発行する。
        /// <para>
        /// 単発使用は Compare-And-Set（<c>used_at IS NULL</c> 条件の原子的 UPDATE）で保証する。
        /// 無効／期限切れ／使用済みはいずれも区別せず同一の <see cref="Exceptions.MagicLinkException"/>（400）をスローする。
        /// </para>
        /// </summary>
        /// <param name="token">メールリンクから受け取った平文トークン</param>
        /// <param name="ct">キャンセルトークン</param>
        /// <returns>認可コードを付与したクライアントのリダイレクト先 URL</returns>
        Task<MagicLinkVerifyResult> VerifyAsync(string token, CancellationToken ct = default);
    }

    /// <summary>
    /// マジックリンク検証の結果。フロントエンドは <see cref="RedirectUri"/> へブラウザ遷移し、
    /// クライアント（管理コンソール）が認可コードをトークンに交換する。
    /// </summary>
    /// <param name="RedirectUri">認可コード（<c>code</c>）を付与したクライアントのリダイレクト先 URL</param>
    public sealed record MagicLinkVerifyResult(string RedirectUri);
}
