using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// 顧客サイト 1 件分の Organization 一式（Organization / Client / RsaKeyPair /
    /// AccountOrganization）を払い出すサービス。
    ///
    /// <para>
    /// 申込フロー（<see cref="ISignupService"/>）とマイページのサイト追加
    /// （<c>POST /v1/account/organizations</c>）の両方から使う。申込で 1〜2 件まとめて作るのも、
    /// 後からサイトを 1 件足すのも「サイト = Organization を 1 つ作る」という同じ操作であり、
    /// 組織コードの導出規則・client_id の形・初期 redirect_uri / allowed_rp_ids がずれると
    /// 「申込で作ったサイトは動くが後から足したサイトは動かない」類の差異を生むため、
    /// 生成ロジックを 1 箇所に集約する。
    /// </para>
    /// </summary>
    public interface IOrganizationProvisioningService
    {
        /// <summary>
        /// サイト URL を検証し、組織コードを導出した <see cref="SiteEntry"/> を返す。
        /// </summary>
        /// <param name="url">申込・追加で入力されたサイト URL（https 必須）。</param>
        /// <param name="isSandbox">テストサイトとして作る場合 true（組織コードに <c>-sandbox</c> が付く）。</param>
        /// <param name="field">エラー時にクライアントへ返す入力フィールド名。</param>
        /// <exception cref="Exceptions.SignupValidationException">URL が https でない、ホスト名が無い等。</exception>
        SiteEntry BuildSite(string url, bool isSandbox, string field);

        /// <summary>
        /// 組織コードが使用可能か（長さ上限・他アカウントによる占有・削除済みドメイン）を検証する。
        /// </summary>
        /// <param name="sites">これから作るサイト。</param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <param name="statusCode">違反時に返す HTTP ステータス（申込リクエスト時は 422、confirm 時の競合は 409）。</param>
        /// <param name="ownedOrganizationIds">
        /// 呼び出し元アカウントが現に管理している Organization の Id。ここに含まれる Org は
        /// ドメイン占有の衝突とみなさない。本番サイトと同じドメインでサンドボックスを追加する
        /// ケース（自分の本番 Org が既にそのドメインを占有している）を通すために必要。
        /// 申込フローでは Org がまだ 1 つも無いため null でよい。
        /// </param>
        /// <exception cref="Exceptions.SignupValidationException">
        /// <c>invalid_site_url</c>（コードが DNS ラベル上限超過）/
        /// <c>organization_already_exists</c>（他アカウントが使用中）/
        /// <c>organization_deleted</c>（削除済みドメインの再登録）。
        /// </exception>
        Task EnsureOrganizationCodesAvailableAsync(
            IReadOnlyCollection<SiteEntry> sites,
            CancellationToken ct,
            int statusCode = 422,
            IReadOnlyCollection<int>? ownedOrganizationIds = null);

        /// <summary>
        /// Organization / Client / RsaKeyPair / AccountOrganization を作成する。
        ///
        /// <para>
        /// トランザクション管理は呼び出し側の責務。本メソッドは Organization の Id 採番のために
        /// 途中で <c>SaveChanges</c> を 2 回呼ぶため、失敗時にロールバックしたい場合は
        /// 呼び出し側でトランザクションを張ること。
        /// </para>
        /// </summary>
        /// <param name="site"><see cref="BuildSite"/> の結果。</param>
        /// <param name="organizationName">組織名（Organization.Name / Client.AppName）。</param>
        /// <param name="ecCubeVersion">初期 redirect_uri のコールバックパスを決める（"2" / "4" / "other"）。</param>
        /// <param name="accountSubject">この Organization のオーナーになる Account の subject。</param>
        /// <param name="parentOrganizationId">
        /// サンドボックス Org の場合に紐づける本番 Org の Id。本番 Org を作る場合は null。
        /// </param>
        /// <param name="ct">キャンセルトークン。</param>
        Task<ProvisionedSite> ProvisionAsync(
            SiteEntry site,
            string organizationName,
            string ecCubeVersion,
            string accountSubject,
            int? parentOrganizationId,
            CancellationToken ct = default);
    }

    /// <summary>
    /// 検証済みのサイト 1 件。<c>Host</c> は RP ID / 組織コード導出用（ポートを含まない）、
    /// <c>BaseUrl</c> は redirect_uri 組み立て用（非既定ポートとベースパスを含み、末尾はスラッシュ）。
    /// </summary>
    public sealed record SiteEntry(
        string Code, string Host, string BaseUrl, bool IsSandbox, string Field);

    /// <summary>払い出し結果。client_secret は暗号化済みの値が入っている。</summary>
    public sealed record ProvisionedSite(Organization Organization, Client Client);
}
