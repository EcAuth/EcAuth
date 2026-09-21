using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Telemetry;
using IdpUtilities.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IOrganizationProvisioningService" />
    public class OrganizationProvisioningService : IOrganizationProvisioningService
    {
        // 組織コード導出: 英数字以外の連続を 1 つの "-" に畳み込むための正規表現。
        private static readonly Regex NonAlphanumericRun = new("[^a-z0-9]+", RegexOptions.Compiled);

        // サンドボックス（テストサイト）Org の組織コードに付ける接尾辞。
        public const string SandboxCodeSuffix = "-sandbox";

        // 組織コードはそのままテナント名になり {tenant}.ec-auth.io の 1 ラベルを構成する。
        // DNS のラベル上限は 63 オクテット（RFC 1035）。超えると Org は作れてもその
        // サブドメインに到達できず、プラグインの接続先が解決不能になる。
        private const int MaxOrganizationCodeLength = 63;

        // サイト URL のベースパス正規化: 末尾セグメントをファイル名とみなして落とす拡張子。
        private static readonly string[] WebDocumentExtensions = [".php", ".html", ".htm"];

        private readonly EcAuthDbContext _context;
        private readonly ISecretProtector _secretProtector;

        public OrganizationProvisioningService(
            EcAuthDbContext context,
            ISecretProtector secretProtector)
        {
            _context = context;
            _secretProtector = secretProtector;
        }

        /// <inheritdoc />
        public SiteEntry BuildSite(string url, bool isSandbox, string field)
        {
            var siteUrl = ValidateHttpsAndParseSiteUrl(url, field);
            return new SiteEntry(
                DeriveOrganizationCode(siteUrl.Host, isSandbox),
                siteUrl.Host,
                siteUrl.BaseUrl,
                isSandbox,
                field);
        }

        /// <inheritdoc />
        public async Task EnsureOrganizationCodesAvailableAsync(
            IReadOnlyCollection<SiteEntry> sites,
            CancellationToken ct,
            int statusCode = 422,
            IReadOnlyCollection<int>? ownedOrganizationIds = null)
        {
            // 組織コードはテナント名（= {tenant}.ec-auth.io の 1 ラベル）になるため、
            // DNS のラベル上限を超えるものは作らせない。作れても到達できないため。
            foreach (var site in sites)
            {
                if (site.Code.Length > MaxOrganizationCodeLength)
                {
                    throw new SignupValidationException(
                        "invalid_site_url",
                        "サイト URL のドメインが長すぎます。より短いドメインでお申し込みください。",
                        field: site.Field,
                        statusCode: 422);
                }
            }

            // 同一リクエスト内で導出後の組織コードが衝突していないか検知する。
            // 本番とテストは接尾辞（-sandbox）で必ず分かれるため通常は発火しないが、
            // 導出規則を将来変えたときに黙って 1 件に潰れるのを防ぐガードとして残す。
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var site in sites)
            {
                if (!seenCodes.Add(site.Code))
                {
                    throw new SignupValidationException(
                        "duplicate_site",
                        "本番サイトとテストサイトが同じ組織として扱われます。"
                            + "テストサイトには別のドメインをご指定ください。",
                        field: site.Field,
                        statusCode: 422);
                }
            }

            foreach (var site in sites)
            {
                // これから作るコードそのものの重複は、自分の Org でも許さない（unique 制約に触れる）。
                // ドメインの占有そのものは下のホスト単位のチェックが担うため、ここで見るのは
                // 「同じコードの Organization が既に存在するか」だけ。導出規則上、別ホストが同じコードに
                // 写ること（shop.example.jp と shop-example.jp）はあり得るので、DB の unique 制約違反
                // （409）に落ちる前に理由の分かる形で弾く。
                var codeTaken = await _context.Organizations
                    .IgnoreQueryFilters()
                    .Where(o => o.Code == site.Code)
                    .Select(o => new { o.DeletedAt })
                    .ToListAsync(ct);

                if (codeTaken.Count == 0)
                {
                    continue;
                }

                // 論理削除済みの Org は code を解放しない（課金集計で別サイトの利用期間が
                // 同一コードに混ざるため）。
                if (codeTaken.All(c => c.DeletedAt != null))
                {
                    throw new SignupValidationException(
                        "organization_deleted",
                        "このドメインは削除済みのサイトで使用されています。同じドメインでの再登録はできません。",
                        field: site.Field,
                        statusCode: statusCode);
                }

                throw new SignupValidationException(
                    "organization_already_exists",
                    "このドメインは既に EcAuth に登録されています。別のサイト URL でお申し込みください。",
                    field: site.Field,
                    statusCode: statusCode);
            }

            // ドメインの占有は Organization の組織コードではなく、Client が持つホスト
            // （allowed_rp_ids）の単位で判定する（EcAuthDocs#121 項目 1）。組織コード単位だと、
            // 既存 Organization に 2 つ目以降の Client を足したときのホストがどの組織コードにも
            // 写らず、別の申込者が同じホストで新しい Organization を作れてしまう。
            //
            // 同一申込内での本番＋サンドボックス併存はこれでも壊れない。Organization の作成は
            // このチェックより後（トランザクション内）で行うため、ペアの相方はまだ DB に無い。
            //
            // マイページからのサイト追加では相方が既に DB にあるので、呼び出し元アカウントが
            // 管理している Org（ownedOrganizationIds）は占有の衝突から除外する。これが無いと
            // 「本番と同じドメインでテスト環境を追加する」が自分の本番 Org に阻まれて必ず失敗する。
            foreach (var site in sites)
            {
                await EnsureHostsAvailableAsync(
                    BuildAllowedRpIds(site.Host), ct, statusCode, site.Field, ownedOrganizationIds);
            }
        }

        /// <inheritdoc />
        public async Task EnsureHostsAvailableAsync(
            IReadOnlyCollection<string> rpIds,
            CancellationToken ct,
            int statusCode = 422,
            string field = "site_url",
            IReadOnlyCollection<int>? ownedOrganizationIds = null)
        {
            var owned = ownedOrganizationIds ?? Array.Empty<int>();

            foreach (var rpId in rpIds)
            {
                // allowed_rp_ids は JSON 配列の文字列カラムで索引が無い。全 Client を読んで
                // メモリで判定すると Client 数に比例して重くなるため、JSON 上の要素表現
                // （"host" を引用符ごと）で LIKE の SQL 側プリフィルタを掛け、ヒットした行だけを
                // デシリアライズして完全一致で確定する。LIKE は大文字小文字を区別しない照合順序でも
                // 動くが、RP ID は保存時に小文字 Punycode へ正規化済みなので実質的に完全一致になる。
                var pattern = "%" + EscapeLikePattern(JsonSerializer.Serialize(rpId)) + "%";

                var candidates = await _context.Clients
                    .IgnoreQueryFilters()
                    .Where(c => c.OrganizationId != null
                        && c.AllowedRpIdsJson != null
                        && EF.Functions.Like(c.AllowedRpIdsJson, pattern, LikeEscapeChar))
                    .Select(c => new
                    {
                        OrganizationId = c.OrganizationId!.Value,
                        c.AllowedRpIdsJson,
                        OrganizationDeletedAt = c.Organization!.DeletedAt
                    })
                    .ToListAsync(ct);

                var blocking = candidates
                    .Where(c => !owned.Contains(c.OrganizationId))
                    .Where(c => DeserializeRpIds(c.AllowedRpIdsJson).Contains(rpId, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (blocking.Count == 0)
                {
                    continue;
                }

                // 論理削除済みの Org のホストも解放しない（組織コードと同じ扱い。課金集計で
                // 別サイトの利用期間が混ざるため）。再登録できない理由が「他人が使っている」のか
                // 「自分が削除した」のかで案内が変わるので、エラーコードを分ける。
                if (blocking.All(c => c.OrganizationDeletedAt != null))
                {
                    throw new SignupValidationException(
                        "organization_deleted",
                        "このドメインは削除済みのサイトで使用されています。同じドメインでの再登録はできません。",
                        field: field,
                        statusCode: statusCode);
                }

                throw new SignupValidationException(
                    "organization_already_exists",
                    "このドメインは既に EcAuth に登録されています。別のサイト URL でお申し込みください。",
                    field: field,
                    statusCode: statusCode);
            }
        }

        // LIKE パターンのエスケープ文字。RP ID に含まれうるのは英数・ハイフン・ドットだけだが、
        // 万一ワイルドカード（% _ [）が混ざっても部分一致に化けないよう機械的にエスケープする。
        private const string LikeEscapeChar = "\\";

        private static string EscapeLikePattern(string value)
        {
            return value
                .Replace(LikeEscapeChar, LikeEscapeChar + LikeEscapeChar)
                .Replace("%", LikeEscapeChar + "%")
                .Replace("_", LikeEscapeChar + "_")
                .Replace("[", LikeEscapeChar + "[");
        }

        private static List<string> DeserializeRpIds(string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
                // 壊れた JSON を持つ行は占有判定の対象にしない（Client.AllowedRpIds の getter は例外を
                // 投げるが、ここで 1 行のために申込全体を 500 にはしない）。
                return new List<string>();
            }
        }

        /// <inheritdoc />
        public async Task<ProvisionedSite> ProvisionAsync(
            SiteEntry site,
            string organizationName,
            string ecCubeVersion,
            string accountSubject,
            int? parentOrganizationId,
            CancellationToken ct = default)
        {
            var organization = new Organization
            {
                Code = site.Code,
                Name = organizationName,
                TenantName = site.Code,
                IsSandbox = site.IsSandbox,
                ParentOrganizationId = parentOrganizationId
            };
            _context.Organizations.Add(organization);
            // RsaKeyPair / AccountOrganization が OrganizationId を必要とするため、
            // ここで一度 SaveChanges して採番された Id を確定させる。
            await _context.SaveChangesAsync(ct);

            _context.RsaKeyPairs.Add(CreateRsaKeyPair(organization.Id));

            _context.AccountOrganizations.Add(new AccountOrganization
            {
                AccountSubject = accountSubject,
                OrganizationId = organization.Id,
                Role = "owner"
            });

            // 最初の Client は、後から足す Client（AddClientAsync）と同じ規則で作る。
            // AddClientAsync 内の SaveChanges で RsaKeyPair / AccountOrganization も一緒に確定する。
            var client = await AddClientAsync(organization, site, organizationName, ecCubeVersion, ct);

            return new ProvisionedSite(organization, client);
        }

        /// <inheritdoc />
        public async Task<Client> AddClientAsync(
            Organization organization,
            SiteEntry site,
            string appName,
            string ecCubeVersion,
            CancellationToken ct = default)
        {
            var client = CreateClient(organization, site, appName, ecCubeVersion);
            // 保存前に client_secret を暗号化する（レガシー/dev は平文パススルー）。
            // Key Vault 暗号化の所要時間を独立ステップとして計測する。
            using (TimingScope.Begin("client_secret_protect"))
            {
                client.ClientSecret = await _secretProtector.ProtectAsync(client.ClientSecret, ct);
            }
            _context.Clients.Add(client);

            await _context.SaveChangesAsync(ct);

            return client;
        }

        // ---- 組織コード導出・URL 処理 ----

        /// <summary>
        /// ホスト名から組織コードを導出する。
        /// lowercase → 先頭 www. 除去 → サブドメイン保持 → 英数以外の連続を "-" に置換 → 前後の "-" を trim。
        /// 例: <c>shop.example.jp → shop-example-jp</c>。
        ///
        /// サンドボックス（テストサイト）の Org には必ず <c>-sandbox</c> を付ける。理由は 2 つある:
        /// <list type="number">
        ///   <item>
        ///     本番と同じドメイン（あるいは <c>www.</c> の有無だけが違うドメイン）でもテスト Org を
        ///     作れるようにするため。付けないと導出後コードが本番と衝突し、テスト環境を持たない
        ///     顧客は検証にも本番 Org を使うしかなくなる（EcAuth#482 の問題 2）。
        ///   </item>
        ///   <item>
        ///     組織コードはそのままテナント名になり、プラグインが接続する
        ///     <c>https://{tenant}.ec-auth.io</c>（<c>ClientResolveController</c>）に現れる。
        ///     接続先を見ただけで本番かサンドボックスかが分かる。
        ///   </item>
        /// </list>
        /// </summary>
        public static string DeriveOrganizationCode(string host, bool isSandbox)
        {
            var normalized = StripWwwPrefix(host.Trim().ToLowerInvariant());
            var code = NonAlphanumericRun.Replace(normalized, "-").Trim('-');
            return isSandbox ? code + SandboxCodeSuffix : code;
        }

        /// <summary>
        /// 先頭の <c>www.</c> を除去する（無ければそのまま返す）。呼び出し側で小文字化済みであることを前提とする。
        /// </summary>
        private static string StripWwwPrefix(string host)
        {
            return host.StartsWith("www.", StringComparison.Ordinal)
                ? host["www.".Length..]
                : host;
        }

        /// <summary>
        /// URL が HTTPS であることを検証し、RP ID 用のホスト名と、コールバック URL の基点になる
        /// ベース URL（scheme + authority + 末尾スラッシュ付きベースパス）を返す。
        /// </summary>
        private static SiteUrl ValidateHttpsAndParseSiteUrl(string url, string field)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(uri.Host))
            {
                throw new SignupValidationException(
                    "invalid_site_url", "サイト URL は https:// で始まる正しい URL を入力してください。", field: field);
            }

            // IDN（国際化ドメイン）は Uri.Host だと Unicode のまま返り、組織コード導出の
            // [^a-z0-9] 除去で空文字や衝突を招く。IdnHost（Punycode, ASCII）を使う。
            // ブラウザが送る Host ヘッダも Punycode なので、プラグインが組み立てる
            // redirect_uri / rp_id と一致する。
            var host = uri.IdnHost;

            // 非既定ポートは redirect_uri の完全一致検証に必要なので保持する
            // （RP ID はポートを含まないドメイン名なので host 側では使わない）。
            var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";

            return new SiteUrl(host, $"https://{authority}{NormalizeBasePath(uri.AbsolutePath)}");
        }

        /// <summary>
        /// サイト URL のパスを、コールバック URL の基点になるベースパスへ正規化する（先頭・末尾がスラッシュ）。
        /// EC-CUBE 2 系・4 系ともサブディレクトリインストールがあり得るため、パスを捨てずに引き継ぐ。
        /// 末尾セグメントがウェブ文書の場合のみ落とす（<c>.../index.php</c> を貼られるケース）。
        /// </summary>
        private static string NormalizeBasePath(string absolutePath)
        {
            var path = string.IsNullOrEmpty(absolutePath) ? "/" : absolutePath;

            // ドットの有無で判定すると "ec-cube-4.2" のようなディレクトリ名をファイル名と
            // 誤判定してサブディレクトリごと落としてしまうため、拡張子で判定する。
            var lastSlash = path.LastIndexOf('/');
            var lastSegment = lastSlash >= 0 ? path[(lastSlash + 1)..] : string.Empty;
            if (WebDocumentExtensions.Any(ext => lastSegment.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                path = path[..(lastSlash + 1)];
            }

            if (!path.StartsWith('/'))
            {
                path = "/" + path;
            }

            return path.EndsWith('/') ? path : path + "/";
        }

        // ---- レコード生成（AccountsOrganizationSeeder の流儀を流用）----

        /// <summary>
        /// 顧客 Org 用 Client を生成する。ClientSecret 生成・AllowedRpIds 設定・RedirectUri 付与の流儀は
        /// <c>AccountsOrganizationSeeder.SeedClientAsync</c> / <c>SeedRedirectUriAsync</c> を流用する。
        /// client_id は Organization の組織コードから導出する（既存 Org への追加では site.Code は
        /// 追加先とは別のホストから導出された値なので使わない）。
        /// </summary>
        private Client CreateClient(
            Organization organization, SiteEntry site, string appName, string ecCubeVersion)
        {
            var client = new Client
            {
                ClientId = BuildClientId(organization.Code),
                ClientSecret = GenerateClientSecret(),
                AppName = appName,
                OrganizationId = organization.Id,
                SubjectType = SubjectType.B2B,
                AllowedRpIds = BuildAllowedRpIds(site.Host).ToList()
            };

            // プラグインが authenticate/verify に送る redirect_uri は完全一致で検証される
            // （B2BPasskeyController）。サイトのトップ URL では一致しないため、選択された
            // EC プラットフォームのコールバック URL を登録する。
            client.RedirectUris!.Add(new RedirectUri
            {
                Uri = BuildInitialRedirectUri(site, ecCubeVersion)
            });

            return client;
        }

        /// <inheritdoc />
        public string BuildInitialRedirectUri(SiteEntry site, string ecCubeVersion)
            => site.BaseUrl + CallbackPathFor(ecCubeVersion);

        /// <summary>
        /// 選択された EC プラットフォームのコールバックパスを返す（ベースパスからの相対）。
        /// EC-CUBE 4 系はルート <c>ecauth_callback</c>（<c>/ecauth/callback</c>）、
        /// 2 系は <c>HTTPS_URL . 'ecauth/callback.php'</c> を使う。
        /// </summary>
        private static string CallbackPathFor(string ecCubeVersion) => ecCubeVersion switch
        {
            "2" => "ecauth/callback.php",
            // "4" と "other"（EC-CUBE 以外）は 4 系と同じパスを初期値にする。
            _ => "ecauth/callback"
        };

        /// <summary>
        /// 初期の allowed_rp_ids を組み立てる。サイト URL が <c>www.</c> 付きでも管理画面は apex
        /// ドメインというケースがあるため、<c>www.</c> 除去版も許可しておく。
        /// RP ID はポートを含まないドメイン名（WebAuthn の valid domain string）。
        /// ホスト占有チェック（<see cref="EnsureHostsAvailableAsync"/>）の対象もこの集合で揃える。
        /// </summary>
        public IReadOnlyList<string> BuildAllowedRpIds(string host)
        {
            var rpIds = new List<string> { host };

            var stripped = StripWwwPrefix(host);
            if (!string.Equals(stripped, host, StringComparison.Ordinal))
            {
                rpIds.Add(stripped);
            }

            return rpIds;
        }

        /// <summary>
        /// RSA 鍵ペアを生成する。RSA.Create(2048) → Base64 エクスポートの流儀は
        /// <c>AccountsOrganizationSeeder.SeedRsaKeyPairAsync</c> を流用する。
        /// </summary>
        private static RsaKeyPair CreateRsaKeyPair(int organizationId)
        {
            using var rsa = RSA.Create(2048);
            return new RsaKeyPair
            {
                Kid = Guid.NewGuid().ToString(),
                OrganizationId = organizationId,
                PublicKey = Convert.ToBase64String(rsa.ExportRSAPublicKey()),
                PrivateKey = Convert.ToBase64String(rsa.ExportRSAPrivateKey()),
                IsActive = true
            };
        }

        private static string GenerateClientSecret()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Base64UrlTextEncoder.Encode(bytes);
        }

        /// <summary>
        /// 顧客 Org 用の client_id を組織コードから導出する。
        /// グローバルユニーク制約があるため、組織コードに短いランダムサフィックスを付与して衝突を避ける。
        /// </summary>
        private static string BuildClientId(string code)
        {
            return $"ec-{code}-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// サイト URL の解析結果。<c>Host</c> は RP ID / 組織コード導出用（ポートを含まない）、
        /// <c>BaseUrl</c> は redirect_uri 組み立て用（非既定ポートとベースパスを含み、末尾はスラッシュ）。
        /// </summary>
        private sealed record SiteUrl(string Host, string BaseUrl);
    }
}
