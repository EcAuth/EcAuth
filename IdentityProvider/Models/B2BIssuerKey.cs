namespace IdentityProvider.Models
{
    /// <summary>
    /// <see cref="B2BUserIdentity.IssuerKey"/> の値を構成するヘルパー（EcAuthDocs#110）。
    ///
    /// issuer_key は「その識別子を発行した主体」を表す。値は EcAuth 側で導出し、
    /// プラグインに新しい必須パラメータを要求しない（パスキー経路は必ず client_id で
    /// 認証済みであり、SSO 経路は EcAuth 自身が IdP を知っているため）。
    ///
    /// アプリ種別（eccube / wp 等）をプレフィックスに含めないのは、EcAuth が Client の
    /// アプリ種別を知らないため（client テーブルにあるのは自由記述の app_name のみで、
    /// 種別を示すカラムが無い）。client_id はグローバル一意なのでアプリ種別は冗長であり、
    /// 必要になれば client_id から辿れる。
    /// </summary>
    public static class B2BIssuerKey
    {
        /// <summary>
        /// Client（アプリケーションインスタンス）単位の発行元プレフィックス。
        ///
        /// EC-CUBE / WordPress といった顧客サイトのアプリケーションだけでなく、
        /// accounts / stg-accounts の管理コンソール（<see cref="SubjectType.Account"/>）も
        /// これを使う。#110 の当初案は account_owner 経路に固定値 "ecauth-account" を
        /// 充てていたが、accounts と stg-accounts は別 Organization であるため、
        /// 同一人物が両方に申し込むと (issuer_key, external_id) が衝突して
        /// 申込処理が落ちる。各 accounts Org は Account 型 Client を 1 つ持つので、
        /// client_id を使えば特例なしにグローバル一意を保てる。
        /// </summary>
        public const string ClientPrefix = "client:";

        /// <summary>
        /// Client 由来の発行元識別子を組み立てる。
        /// </summary>
        /// <param name="clientId"><see cref="Client.ClientId"/>（グローバル一意）。</param>
        public static string ForClient(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException("clientId は必須です。", nameof(clientId));
            }

            return ClientPrefix + clientId;
        }
    }
}
