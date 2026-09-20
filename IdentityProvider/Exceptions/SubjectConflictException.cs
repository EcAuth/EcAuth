namespace IdentityProvider.Exceptions
{
    /// <summary>
    /// 要求された b2b_subject が、要求元 Client の Organization とは別の Organization に既に存在する
    /// 場合にスローされる例外（EcAuth#505）。
    /// <para>
    /// <c>B2BUser.Subject</c> はグローバル一意（<c>HasAlternateKey</c>）なので、別 Organization の
    /// Client から同じ subject で登録しようとしても作成できず、テナントフィルター配下の再取得でも
    /// 見つからない。プラグインは <c>ecauth_subject</c> を EC-CUBE の Member に永続化して再利用するため、
    /// テスト用テナントの client_id で試した後に本番 client_id へ差し替えると確実にこの状態になる。
    /// </para>
    /// HTTP API としては 409 Conflict に変換される。レスポンスにはどの Organization に存在するかを含めない。
    /// </summary>
    public class SubjectConflictException : Exception
    {
        public SubjectConflictException(string message, Exception? innerException = null)
            : base(message, innerException) { }
    }
}
