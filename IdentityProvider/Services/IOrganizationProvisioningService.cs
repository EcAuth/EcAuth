using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// 顧客 Organization 一式（Organization / Client / RsaKeyPair / AccountOrganization）と、
    /// 既存 Organization への Client 追加を払い出すサービス。
    ///
    /// <para>
    /// 申込フロー（<see cref="ISignupService"/>）とマイページのサイト追加
    /// （<c>POST /v1/account/organizations</c> / <c>POST /v1/account/organizations/{id}/clients</c>）の
    /// 両方から使う。Organization = 組織、Client = サイト（EC-CUBE / WordPress 等のアプリケーション）
    /// であり（EcAuthDocs#110 / #121）、申込で 1〜2 件まとめて作るのも、後から Organization を足すのも、
    /// 既存 Organization に Client を足すのも、同じ生成規則（組織コードの導出・client_id の形・
    /// 初期 redirect_uri / allowed_rp_ids）を通す必要がある。ずれると「申込で作ったサイトは動くが
    /// 後から足したサイトは動かない」類の差異を生むため、生成ロジックを 1 箇所に集約する。
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
        /// これから Organization を作るサイトについて、組織コードが使用可能か（長さ上限・コードそのものの
        /// 重複）と、サイトのホストが他の Organization に占有されていないか
        /// （<see cref="EnsureHostsAvailableAsync"/>）を検証する。
        /// </summary>
        /// <param name="sites">これから作るサイト。</param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <param name="statusCode">違反時に返す HTTP ステータス（申込リクエスト時は 422、confirm 時の競合は 409）。</param>
        /// <param name="ownedOrganizationIds">
        /// 呼び出し元アカウントが現に管理している Organization の Id。ここに含まれる Org の Client が
        /// 持つホストは占有の衝突とみなさない。本番サイトと同じドメインでサンドボックスを追加する
        /// ケース（自分の本番 Org が既にそのドメインを占有している）を通すために必要。
        /// 申込フローでは Org がまだ 1 つも無いため null でよい。
        /// </param>
        /// <exception cref="Exceptions.SignupValidationException">
        /// <c>invalid_site_url</c>（コードが DNS ラベル上限超過）/
        /// <c>organization_already_exists</c>（コードの重複、または他の Organization がホストを使用中）/
        /// <c>organization_deleted</c>（削除済み Organization のホストの再登録）。
        /// </exception>
        Task EnsureOrganizationCodesAvailableAsync(
            IReadOnlyCollection<SiteEntry> sites,
            CancellationToken ct,
            int statusCode = 422,
            IReadOnlyCollection<int>? ownedOrganizationIds = null);

        /// <summary>
        /// RP ID（ホスト）が他の Organization の Client の <c>allowed_rp_ids</c> に登録されていないかを
        /// 検証する。ドメインの占有は Organization の組織コードではなく、Client が持つホストの単位で
        /// 判定する（EcAuthDocs#121 項目 1）。
        ///
        /// <para>
        /// 判定は正規化済み RP ID（小文字 Punycode）の完全一致。同一ホストが複数の Organization に
        /// ぶら下がることによる帰属の曖昧さ・運用上の衝突を防ぐのが目的であり、乗っ取り防止ではない
        /// （WebAuthn はブラウザが RP ID と呼び出し元 origin の一致を強制するため、他人のドメインを
        /// 登録してもパスキーは使えない）。DNS 所有権の検証は行わない。
        /// </para>
        /// <para>
        /// ホスト単位の DB 制約は無いため、並行リクエストのすり抜けは先着順として許容する
        /// （Organization 新規作成の経路は <c>organization.code</c> の unique 制約が引き続き backstop になる）。
        /// トランザクション内で再実行して窓を狭めることは呼び出し側の責務。
        /// </para>
        /// </summary>
        /// <param name="rpIds">検証する RP ID（正規化済み）。</param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <param name="statusCode">違反時に返す HTTP ステータス。</param>
        /// <param name="field">エラー時にクライアントへ返す入力フィールド名。</param>
        /// <param name="ownedOrganizationIds">
        /// 呼び出し元アカウントが管理している Organization の Id。これらの Org の Client が持つホストは
        /// 衝突とみなさない（同一 Organization 内、および本番とそのサンドボックス Org 間の同一ホストは許可）。
        /// </param>
        /// <exception cref="Exceptions.SignupValidationException">
        /// <c>organization_already_exists</c>（他の Organization の Client が使用中）/
        /// <c>organization_deleted</c>（削除済み Organization の Client のみが使用中）。
        /// </exception>
        Task EnsureHostsAvailableAsync(
            IReadOnlyCollection<string> rpIds,
            CancellationToken ct,
            int statusCode = 422,
            string field = "site_url",
            IReadOnlyCollection<int>? ownedOrganizationIds = null);

        /// <summary>
        /// Organization / RsaKeyPair / AccountOrganization を作成し、最初の Client を
        /// <see cref="AddClientAsync"/> で追加する。
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

        /// <summary>
        /// 既存の Organization に Client（サイト）を 1 件追加する（EcAuthDocs#121 項目 3）。
        /// client_id の形・初期 redirect_uri / allowed_rp_ids・client_secret の暗号化は
        /// <see cref="ProvisionAsync"/> が作る最初の Client と同じ規則に従う。
        ///
        /// <para>
        /// <see cref="SiteEntry.Code"/> は使わない（Organization の組織コード・テナント名はそのまま）。
        /// 追加した Client も <c>{Organization.TenantName}.ec-auth.io</c> のテナントで解決される
        /// （<c>client-resolve</c> は client_id 単位）。<c>SaveChanges</c> は本メソッド内で呼ぶ。
        /// トランザクション管理は呼び出し側の責務。
        /// </para>
        /// </summary>
        /// <param name="organization">追加先の Organization（Id 採番済み）。</param>
        /// <param name="site"><see cref="BuildSite"/> の結果（Host / BaseUrl のみ使う）。</param>
        /// <param name="appName">Client.AppName。</param>
        /// <param name="ecCubeVersion">初期 redirect_uri のコールバックパスを決める（"2" / "4" / "other"）。</param>
        /// <param name="clientId">
        /// 使う client_id。<c>null</c> なら <see cref="NewClientId"/> で採番する。
        /// 再試行される単位（<c>ExecuteInRetryableUnitAsync</c>）から呼ぶ場合は、呼び出し側で
        /// 試行の外で採番した値を渡し、試行間で固定すること。Client には自然キーが無く、
        /// 同一 Organization に同じ初期 redirect_uri の Client が複数あってよいため、
        /// 「前回の試行がコミット済みか」は client_id（ユニーク）でしか判定できない。
        /// </param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>追加した Client。client_secret は暗号化済みの値が入っている。</returns>
        Task<Client> AddClientAsync(
            Organization organization,
            SiteEntry site,
            string appName,
            string ecCubeVersion,
            string? clientId = null,
            CancellationToken ct = default);

        /// <summary>
        /// 新しい client_id を採番する（<c>ec-{組織コード}-{GUID}</c>）。
        /// GUID 付きなのでグローバルに衝突せず、<c>IX_client_client_id</c> のユニークインデックスで
        /// 担保される。<see cref="AddClientAsync"/> の再試行を冪等にするため、呼び出し側が
        /// 試行の外で採番して固定する用途に公開している。
        /// </summary>
        string NewClientId(string organizationCode);

        /// <summary>
        /// サイトのホストから初期 <c>allowed_rp_ids</c> を組み立てる（host と、<c>www.</c> 付きなら
        /// その除去版）。占有チェックの対象ホストもこの集合で揃える。
        /// </summary>
        IReadOnlyList<string> BuildAllowedRpIds(string host);

        /// <summary>
        /// サイトのベース URL と EC プラットフォームから初期 <c>redirect_uri</c> を組み立てる。
        /// Client 追加の再試行時に「前回の試行が作った Client」を見つける手掛かりにも使う。
        /// </summary>
        string BuildInitialRedirectUri(SiteEntry site, string ecCubeVersion);
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
