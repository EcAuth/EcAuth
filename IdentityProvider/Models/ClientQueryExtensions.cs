using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Models
{
    /// <summary>
    /// Client クエリの共通条件。
    /// </summary>
    public static class ClientQueryExtensions
    {
        /// <summary>
        /// 論理削除された Organization に属する Client を除外する。
        ///
        /// <para>
        /// Client エンティティにはグローバルクエリフィルターが設定されておらず、認証系の経路は
        /// テナントをまたいで引くために <c>IgnoreQueryFilters()</c> を使う。そのため
        /// Organization 側の <c>DeletedAt</c> 条件はどちらの経路でも自動適用されない。
        /// client_id から Client を引いて認証・トークン発行の可否を決める箇所は、
        /// すべてこの拡張を通すこと。これを忘れると「マイページで削除したサイトから
        /// 引き続きログインできる」状態になる。
        /// </para>
        /// <para>
        /// <c>Organization == null</c> を許容しているのは、Organization に紐づかない Client
        /// （旧データ・プラットフォーム用）を除外しないため。削除判定の対象は
        /// あくまで Organization を持つ顧客サイトの Client。
        /// </para>
        /// </summary>
        public static IQueryable<Client> ExcludeDeletedOrganizations(this IQueryable<Client> clients)
        {
            return clients.Where(c => c.Organization == null || c.Organization.DeletedAt == null);
        }
    }
}
