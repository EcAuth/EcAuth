using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// EcAuth サービス利用者（Account）の取得サービス。
    /// Account は B2BUser と Subject を共有するため認証自体は B2BPasskeyController を流用し、
    /// 本サービスは Account 固有情報（補助情報・管理対象 Organization）の取得に責務を絞る。
    /// </summary>
    public interface IAccountService
    {
        /// <summary>
        /// Account が管理する Organization 1 件分の情報。
        /// managed_orgs クレームの 1 要素に対応する。
        /// </summary>
        /// <param name="OrganizationId">管理対象 Organization の内部 ID</param>
        /// <param name="Code">管理対象 Organization のコード（サブドメイン解決に使う安定値）</param>
        /// <param name="Role">Account のその Organization に対するロール（owner / admin / member）</param>
        public record ManagedOrganization(int OrganizationId, string Code, string Role);

        /// <summary>
        /// Subject で Account を取得する。
        /// Account は所属テナント（accounts / stg-accounts）のクエリフィルター対象のため、
        /// テナントが正しく解決されたリクエスト内で呼び出すことを前提とする。
        /// </summary>
        Task<Account?> GetBySubjectAsync(string subject);

        /// <summary>
        /// Account が管理する Organization 一覧を取得する。
        /// 管理対象は顧客テナント（別テナント）のため、<c>IgnoreQueryFilters</c> で横断的に引き当てる。
        /// </summary>
        Task<IReadOnlyList<ManagedOrganization>> GetManagedOrganizationsAsync(string subject);
    }
}
