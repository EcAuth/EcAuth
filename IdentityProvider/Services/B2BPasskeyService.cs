using Fido2NetLib;
using Fido2NetLib.Objects;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Telemetry;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;

namespace IdentityProvider.Services
{
    /// <summary>
    /// B2Bパスキー認証サービスの実装
    /// </summary>
    public class B2BPasskeyService : IB2BPasskeyService
    {
        // Application Insights / traces テーブルでの集計クエリ
        // (`traces | where message startswith "Passkey.Verify.Failed"`) で利用するため、
        // メッセージテンプレートをクラス定数として一元化する。
        private const string PasskeyVerifyFailedLogTemplate =
            "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}";
        private const string PasskeyVerifyFailedExceptionLogTemplate =
            "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason}";

        private readonly EcAuthDbContext _context;
        private readonly IFido2 _fido2;
        private readonly IWebAuthnChallengeService _challengeService;
        private readonly IB2BUserService _userService;
        private readonly ILogger<B2BPasskeyService> _logger;
        private readonly Func<Fido2Configuration, IFido2> _fido2Factory;

        public B2BPasskeyService(
            EcAuthDbContext context,
            IFido2 fido2,
            IWebAuthnChallengeService challengeService,
            IB2BUserService userService,
            ILogger<B2BPasskeyService> logger,
            Func<Fido2Configuration, IFido2>? fido2Factory = null)
        {
            _context = context;
            _fido2 = fido2;
            _challengeService = challengeService;
            _userService = userService;
            _logger = logger;
            // マルチテナント対応: origin検証のために動的にFido2インスタンスを作成するためのファクトリー
            // テスト時にはモックを返すファクトリーを注入可能
            _fido2Factory = fido2Factory ?? (config => new Fido2(config));
        }

        #region Registration Methods

        /// <inheritdoc />
        public async Task<IB2BPasskeyService.RegistrationOptionsResult> CreateRegistrationOptionsAsync(
            IB2BPasskeyService.RegistrationOptionsRequest request)
        {
            // バリデーション
            if (string.IsNullOrWhiteSpace(request.ClientId))
                throw new ArgumentException("ClientId is required", nameof(request));
            if (string.IsNullOrWhiteSpace(request.RpId))
                throw new ArgumentException("RpId is required", nameof(request));
            // RP ID正規化（ドメイン名は大文字小文字を区別しない: RFC 4343）
            // ブラウザの window.location.hostname は小文字を返すため、
            // WebAuthn API の RP ID 検証で不一致にならないよう正規化する
            var rpId = request.RpId.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(request.B2BSubject))
                throw new ArgumentException("B2BSubject is required", nameof(request));
            // 登録トークン経路は subject がトークンで確定しており external_id を受け取らない（null）。
            // それ以外の経路では必須。以降は externalId の null 判定で経路を分ける。
            string? externalId = null;
            if (!request.ResolvedByRegistrationToken)
            {
                if (string.IsNullOrWhiteSpace(request.ExternalId))
                    throw new ArgumentException("ExternalId is required", nameof(request));
                if (request.ExternalId.Length > B2BUserIdentity.ExternalIdMaxLength)
                    throw new ArgumentException($"ExternalId must be {B2BUserIdentity.ExternalIdMaxLength} characters or less", nameof(request));
                externalId = request.ExternalId;
            }

            // UUID 形式の検証・正規化（小文字ハイフン付き形式に統一）
            if (!Guid.TryParse(request.B2BSubject, out var parsedB2BSubject))
                throw new ArgumentException("B2BSubject must be a valid UUID format", nameof(request));
            var b2bSubject = parsedB2BSubject.ToString();

            // 文字列長の制限
            if (request.DisplayName != null && request.DisplayName.Length > 128)
                throw new ArgumentException("DisplayName must be 128 characters or less", nameof(request));
            if (request.DeviceName != null && request.DeviceName.Length > 128)
                throw new ArgumentException("DeviceName must be 128 characters or less", nameof(request));
            if (request.UserName != null && request.UserName.Length > 128)
                throw new ArgumentException("UserName must be 128 characters or less", nameof(request));
            // 空白のみは「未指定」として扱い、フォールバックさせる（user_name は external_id へ、
            // display_name は user_name へ。旧プラグイン互換と同じ経路）。
            var userName = string.IsNullOrWhiteSpace(request.UserName) ? null : request.UserName;
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName;

            // クライアント取得
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .ExcludeDeletedOrganizations()
                .Include(c => c.Organization)
                .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

            if (client == null)
                throw new InvalidOperationException($"Client not found: {request.ClientId}");

            if (client.OrganizationId == null)
                throw new InvalidOperationException($"Client has no associated Organization: {request.ClientId}");

            // RP ID検証（ドメイン名は大文字小文字を区別しない: RFC 4343）
            if (!client.AllowedRpIds.Contains(rpId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"RpId is not allowed for this client: {rpId}");

            // 発行元識別子（EcAuthDocs#110）。external_id は発行元をまたぐと衝突しうる
            // （EC-CUBE の member_id=1 と WordPress の user_id=1 は同一ハッシュになる）ため、
            // 解決も登録も必ずこの名前空間の下で行う。
            var issuerKey = B2BIssuerKey.ForClient(client.ClientId);
            var issuerClientId = client.ClientId;

            // B2Bユーザー取得、存在しない場合は JIT プロビジョニング
            var user = await _userService.GetBySubjectAsync(b2bSubject);
            bool isProvisioned = false;
            string subjectResolution;

            if (user != null)
            {
                // Organization 境界チェック: B2BUser クエリフィルターは TenantName ベースなので、
                // 同一テナント内の別 Organization の subject がヒットし得る。
                // これを許すと cross-organization での external_id 上書きや credential 紐付けが可能になるため遮断する。
                EnsureUserBelongsToClientOrganization(user, client.OrganizationId.Value, b2bSubject);

                if (externalId != null)
                {
                    // Subject が一致: 発行元における識別子を insert-if-missing で補完する。
                    // プラグインが送る値が変わった場合（EcAuthDocs#110 の login_id → member_id 移行）は
                    // 同一 subject の下に新旧の identity 行が共存する（旧行はフォールバック用に残す）。
                    // 登録トークン経路は identity が申込確定時に作成済みで、同期する平文も持たないため何もしない。
                    await EnsureIdentityAsync(user, externalId, issuerKey, issuerClientId);
                }
                subjectResolution = IB2BPasskeyService.SubjectResolutions.AsRequested;
            }
            else if (externalId == null)
            {
                // 登録トークンは既存 B2BUser から発行されるため、通常ここには到達しない。
                // 到達した場合（トークン発行後の削除など）に identity 解決 / JIT へ進むと、
                // external_id を持たないこの経路では別人の解決や不正な作成につながるため、明示的に失敗させる。
                throw new InvalidOperationException(
                    $"B2BUser for registration token no longer exists: {b2bSubject}");
            }
            else
            {
                // 発行元の identity で既存ユーザーを検索（EC-CUBEプラグイン再インストール時の復旧）
                user = await _userService.GetByIdentityAsync(issuerKey, externalId);
                if (user != null)
                {
                    // external_id は login_id / メールアドレス等 PII を含み得るため Information ログには含めない
                    _logger.LogInformation(
                        "Resolved B2BUser via ExternalId fallback: RequestedSubject={RequestedSubject}, ResolvedSubject={ResolvedSubject}, OrganizationId={OrganizationId}",
                        b2bSubject, user.Subject, user.OrganizationId);
                    subjectResolution = IB2BPasskeyService.SubjectResolutions.FallbackByExternalId;
                }
                else
                {
                    try
                    {
                        _logger.LogInformation(
                            "JIT provisioning B2BUser: Subject={Subject}, OrganizationId={OrganizationId}",
                            b2bSubject, client.OrganizationId);

                        var createResult = await _userService.CreateAsync(new IB2BUserService.CreateUserRequest
                        {
                            Subject = b2bSubject,
                            ExternalId = externalId,
                            IssuerKey = issuerKey,
                            ClientId = issuerClientId,
                            UserType = "admin",
                            OrganizationId = client.OrganizationId.Value
                        });
                        user = createResult.User;
                        isProvisioned = true;
                        subjectResolution = IB2BPasskeyService.SubjectResolutions.Provisioned;

                        _logger.LogInformation(
                            "JIT provisioned B2BUser: Subject={Subject}, OrganizationId={OrganizationId}",
                            user.Subject, user.OrganizationId);
                    }
                    catch (DbUpdateException ex)
                    {
                        // 並行リクエストで Subject または (issuer_key, external_id) の一意制約違反が発生した場合、再取得を試みる。
                        // SQL エラーコードでは絞らない（B2BUserService と同じく DB プロバイダー非依存に、
                        // 実状態の再取得で race を判定する）。一意違反以外の障害（タイムアウト等）でも
                        // 再取得は副作用の無い読み取りなので安全で、どちらでも引けなければ元例外を
                        // 内包して失敗させる。
                        _logger.LogInformation(
                            "B2BUser already created by concurrent request, re-fetching: Subject={Subject}",
                            b2bSubject);
                        user = await _userService.GetBySubjectAsync(b2bSubject);
                        if (user == null)
                        {
                            user = await _userService.GetByIdentityAsync(issuerKey, externalId);
                            if (user != null)
                            {
                                subjectResolution = IB2BPasskeyService.SubjectResolutions.FallbackByExternalId;
                            }
                            else if (await SubjectExistsInAnyOrganizationAsync(b2bSubject))
                            {
                                // 並行リクエストではなく、subject が別テナントの Organization に既に存在する
                                // （Subject はグローバル一意、再取得はテナントフィルター配下なので見つからない）。
                                // 実運用ではテスト用テナントで登録した後に本番 client_id へ差し替えた
                                // ケースが大半（EcAuth#505）。原因が分かる専用エラーで返す。
                                _logger.LogWarning(
                                    "B2BSubject already exists in another organization: Subject={Subject}, ClientOrganizationId={ClientOrganizationId}",
                                    b2bSubject, client.OrganizationId);
                                throw new SubjectConflictException(
                                    "B2BSubject already exists in another organization.", ex);
                            }
                            else
                            {
                                throw new InvalidOperationException(
                                    $"Failed to create or retrieve B2BUser: {b2bSubject}", ex);
                            }
                        }
                        else
                        {
                            // race 経由でも別組織の subject が返りうるため、メインフローと同じ境界チェックを適用
                            EnsureUserBelongsToClientOrganization(user, client.OrganizationId.Value, b2bSubject);

                            // 並行リクエストが先に書き込んだ external_id と、今回のリクエストの external_id が
                            // 異なる場合があるため、メインフローと同じく identity を最新化する。
                            await EnsureIdentityAsync(user, externalId, issuerKey, issuerClientId);
                            subjectResolution = IB2BPasskeyService.SubjectResolutions.AsRequested;
                        }
                    }
                }
            }

            // ExternalId で既存ユーザーが見つかった場合、Subject が異なる可能性がある
            // 以降の処理は解決済みの Subject を使用する
            var resolvedSubject = user.Subject;

            // 既存のクレデンシャルを取得（除外リスト用）
            //
            // ここは発行元で絞らない（EcAuthDocs#110 リリース 3 で allowCredentials のみを絞る）。
            // excludeCredentials は「同じ認証器に同じユーザーの重複登録をさせない」ための機構で、
            // 認証器側のクレデンシャルは (rp_id, user_handle) で識別される。発行元で絞ると、
            // 複数の発行元 identity を持つ b2b_user（例: EC-CUBE と企業 SSO が同一人物に解決される
            // 構成）で同じ認証器が兄弟 Client のクレデンシャルを黙って上書きし、DB 側に二度と
            // 認証できない行が残る。列挙の観点でも、ここは client_secret で認証済みかつ解決済みの
            // subject に限った一覧なので Client 境界を越えない。
            var existingCredentials = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .Where(c => c.B2BSubject == resolvedSubject)
                .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
                .ToListAsync();

            // チャレンジ生成
            var challengeResult = await _challengeService.GenerateChallengeAsync(
                new IWebAuthnChallengeService.ChallengeRequest
                {
                    Type = "registration",
                    UserType = "b2b",
                    Subject = resolvedSubject,
                    RpId = rpId,
                    ClientId = client.Id
                });

            // Fido2ユーザー作成
            // WebAuthn の user.name は認証器・パスキー管理画面に表示されるアカウント名なので、
            // リクエスト由来の平文を使う（EcAuth はハッシュしか保持しないため DB から復元はできない）。
            // 優先順位: user_name（EcAuthDocs#110 以降のプラグインが login_id を送る）
            //   → external_id（user_name を送らない旧プラグイン。login_id がそのまま入っていた）
            //   → subject（external_id を受け取らない登録トークン経路。不透明な識別子である点は従来と同じ）
            var webAuthnUserName = userName ?? externalId ?? resolvedSubject;
            var fido2User = new Fido2User
            {
                Id = Encoding.UTF8.GetBytes(resolvedSubject),
                Name = webAuthnUserName,
                DisplayName = displayName ?? webAuthnUserName
            };

            // 認証器選択オプション
            var authenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                // accounts の管理コンソール（SubjectType.Account）はログイン時に b2b_subject を
                // 特定できないため、allowCredentials 空の discoverable credential フローで認証する。
                // そのため登録時点で resident key を必須にする（Preferred のままだと認証器によっては
                // 非 discoverable で登録され、以後ログインできなくなる）。
                ResidentKey = client.SubjectType == SubjectType.Account
                    ? ResidentKeyRequirement.Required
                    : ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred
            };

            // 登録オプション生成
            // 注: _fido2.RequestNewCredential()は内部で別のチャレンジを生成するため使用しない
            // _challengeServiceで生成したチャレンジと一致させるため、手動でCredentialCreateOptionsを構築
            var options = new CredentialCreateOptions
            {
                Rp = new PublicKeyCredentialRpEntity(rpId, client.Organization?.Name ?? "EcAuth"),
                User = fido2User,
                Challenge = WebEncoders.Base64UrlDecode(challengeResult.Challenge),
                PubKeyCredParams = PubKeyCredParam.Defaults,
                AuthenticatorSelection = authenticatorSelection,
                Attestation = AttestationConveyancePreference.None,
                ExcludeCredentials = existingCredentials
            };

            _logger.LogInformation(
                "Created registration options for B2BUser {Subject}, SessionId: {SessionId}",
                resolvedSubject,
                challengeResult.SessionId);

            return new IB2BPasskeyService.RegistrationOptionsResult
            {
                SessionId = challengeResult.SessionId,
                Options = options,
                IsProvisioned = isProvisioned,
                ResolvedSubject = resolvedSubject,
                SubjectResolution = subjectResolution
            };
        }

        /// <summary>
        /// subject で解決した B2BUser が、呼び出し元クライアントの Organization に属しているかを検証する。
        /// B2BUser の QueryFilter は TenantName ベースなので、同一テナント内の別 Organization の
        /// subject がヒットする可能性があり、それを許すと cross-organization 書き換えに繋がるため遮断する。
        /// ログ・例外メッセージには requestedSubject の値を含めない（別組織 subject の存在有無を漏らさないため）。
        /// </summary>
        /// <summary>
        /// subject が（テナントを問わず）いずれかの Organization に存在するかを返す。
        /// JIT の一意制約違反が「並行リクエスト」か「別テナントに既存」かを切り分けるための読み取り専用プローブ。
        /// </summary>
        private Task<bool> SubjectExistsInAnyOrganizationAsync(string b2bSubject)
        {
            return _context.B2BUsers
                .IgnoreQueryFilters()
                .AnyAsync(u => u.Subject == b2bSubject);
        }

        private void EnsureUserBelongsToClientOrganization(B2BUser user, int clientOrganizationId, string requestedSubject)
        {
            if (user.OrganizationId == clientOrganizationId)
            {
                return;
            }

            _logger.LogWarning(
                "B2BSubject does not belong to the client's organization. ClientOrganizationId={ClientOrganizationId}, UserOrganizationId={UserOrganizationId}",
                clientOrganizationId, user.OrganizationId);

            // 同一テナント内の別 Organization に存在する場合。別テナントに存在する場合（JIT の
            // 一意制約違反経由）と同じ 409 subject_conflict に揃え、プラグイン側の対処
            //（ecauth_subject のクリア）を一本化する。
            throw new SubjectConflictException("B2BSubject is not associated with this client's organization.");
        }

        /// <summary>
        /// 既存 B2BUser について、発行元における識別子行を最新化する。
        ///
        /// EcAuthDocs#110 の決定により、b2b_user_identity 側は「差し替え」ではなく「追加」する。
        /// 同一 issuer_key の下に旧 hash と新 hash を共存させることで、プラグイン更新前に
        /// 登録されたユーザーも引き続き解決でき、ハードカットオーバーが不要になる。
        /// 同一発行元の同一 external_id を別人が保有していれば
        /// <see cref="ExternalIdConflictException"/>（409 相当）が飛ぶ。
        ///
        /// 識別子の置き場は identity のみなので書き込みは 1 か所で完結する。
        /// 旧 b2b_user.external_id との二重書きとそれを包むトランザクションはリリース 5 で除去した。
        /// </summary>
        private Task EnsureIdentityAsync(
            B2BUser user, string requestedExternalId, string issuerKey, string? issuerClientId)
        {
            // external_id の具体値は PII を含み得るため Information ログには含めない。
            _logger.LogDebug(
                "Ensuring B2BUserIdentity: Subject={Subject}, IssuerKey={IssuerKey}",
                user.Subject, issuerKey);

            return _userService.EnsureIdentityAsync(
                user.Subject, issuerKey, requestedExternalId, issuerClientId);
        }

        /// <inheritdoc />
        public async Task<IB2BPasskeyService.RegistrationVerifyResult> VerifyRegistrationAsync(
            IB2BPasskeyService.RegistrationVerifyRequest request)
        {
            try
            {
                // チャレンジ取得
                WebAuthnChallenge? challenge;
                using (TimingScope.Begin("challenge_lookup"))
                {
                    challenge = await _challengeService.GetChallengeBySessionIdAsync(request.SessionId);
                }
                if (challenge == null)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "session_not_found", "Session not found or expired");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Session not found or expired"
                    };
                }

                // 期限チェック（defense-in-depth）
                // 注: GetChallengeBySessionIdAsync で既に期限切れチェック済みだが、
                // 多層防御として明示的に検証。将来の実装変更に対する安全性を確保。
                if (challenge.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "challenge_expired", "Challenge has expired");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Challenge has expired"
                    };
                }

                // Subject 突合（登録トークン経路のみ）
                // 認可に使ったトークンの Subject と、登録先となるチャレンジセッションの Subject が
                // 一致しない場合は、別アカウント宛のセッションへの登録試行として遮断する。
                if (!string.IsNullOrEmpty(request.ExpectedSubject)
                    && !string.Equals(challenge.Subject, request.ExpectedSubject, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "subject_mismatch", "Session subject does not match the authorized subject");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Session does not match the authorized account"
                    };
                }

                // タイプチェック
                if (challenge.Type != "registration")
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "challenge_type_invalid", "Invalid challenge type");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid challenge type"
                    };
                }

                // セッションとリクエスト元 Client の束縛検証
                //
                // コントローラーは request.ClientId で Client を認証するが、チャレンジセッションが
                // その Client のものであることは検証していない。ここで突合しないと、別 Client が
                // 発行したセッションを自分の client_secret で verify に持ち込め、そのセッションの
                // Subject に攻撃者の認証器を登録できてしまう（＝当該アカウントの乗っ取り）。
                // 登録トークン経路はコントローラー側で BoundSessionId と突合済みだが、
                // client_secret 経路には束縛が無い。認証側（VerifyAuthenticationAsync）は同じ検証を
                // 実施済みで、登録側だけが欠けていた。
                //
                // 注: GetChallengeBySessionIdAsync は Client / Organization を Include 済みだが、
                // ここでは navigation に依存せず明示的に引く。テストは challenge をモックで返すため
                // navigation が null になり、依存すると本番だけ通る経路が生まれてテストで守れない。
                Client? requestingClient;
                using (TimingScope.Begin("session_client_verify"))
                {
                    requestingClient = await _context.Clients
                        .IgnoreQueryFilters()
                        .ExcludeDeletedOrganizations()
                        .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);
                }

                if (requestingClient == null)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "client_not_found", "Client not found");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Client not found"
                    };
                }

                if (requestingClient.Id != challenge.ClientId)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "session_client_mismatch", "Session was not issued for this client");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Session does not belong to this client"
                    };
                }

                // GetChallengeBySessionIdAsync で既に Client.Organization を Include 済み
                var rpName = challenge.Client?.Organization?.Name ?? "EcAuth"; // フォールバック

                // CredentialCreateOptionsを復元
                var options = new CredentialCreateOptions
                {
                    Challenge = WebEncoders.Base64UrlDecode(challenge.Challenge),
                    Rp = new PublicKeyCredentialRpEntity(challenge.RpId!, rpName),
                    User = new Fido2User
                    {
                        Id = Encoding.UTF8.GetBytes(challenge.Subject!),
                        Name = challenge.Subject!,
                        DisplayName = challenge.Subject!
                    },
                    PubKeyCredParams = PubKeyCredParam.Defaults
                };

                // クレデンシャルの一意性チェック用デリゲート
                IsCredentialIdUniqueToUserAsyncDelegate isCredentialIdUnique = async (args, cancellationToken) =>
                {
                    var exists = await _context.B2BPasskeyCredentials
                        .IgnoreQueryFilters()
                        .AnyAsync(c => c.CredentialId == args.CredentialId, cancellationToken);
                    return !exists;
                };

                // 動的にoriginを構築（RP IDに基づく）
                // 開発環境: localhost:8081, 本番環境: 各店舗のドメイン
                // Fido2.NetLib 4.0.0ではorigin検証はFido2Configurationで設定するため、
                // リクエストごとに新しいFido2インスタンスを作成
                var allowedOrigins = new HashSet<string>
                {
                    $"https://{challenge.RpId}",
                    $"https://{challenge.RpId}:8081",  // 開発環境用ポート
                    $"https://{challenge.RpId}:443"
                };
                var dynamicConfig = new Fido2Configuration
                {
                    ServerDomain = challenge.RpId!,
                    ServerName = rpName,
                    Origins = allowedOrigins
                };
                var dynamicFido2 = _fido2Factory(dynamicConfig);

                // 登録検証（Fido2.NetLib 4.0.0 API）
                RegisteredPublicKeyCredential result;
                using (TimingScope.Begin("fido2_make_credential"))
                {
                    result = await dynamicFido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                    {
                        AttestationResponse = request.AttestationResponse,
                        OriginalOptions = options,
                        IsCredentialIdUniqueToUserCallback = isCredentialIdUnique
                    });
                }

                // クレデンシャル保存
                var credential = new B2BPasskeyCredential
                {
                    B2BSubject = challenge.Subject!,
                    // 発行元 Client（EcAuthDocs#110 リリース 2）。challenge は options 発行時に
                    // client_id で認証済みの Client と紐づいており、上の束縛検証でリクエスト元と
                    // 一致することを確認済みのため、ここで決定的に書ける。
                    // リリース 2 では書くだけで読まない（allowCredentials の絞り込みはリリース 3）。
                    ClientId = challenge.ClientId,
                    CredentialId = result.Id,
                    PublicKey = result.PublicKey,
                    SignCount = (uint)result.SignCount,
                    DeviceName = request.DeviceName,
                    AaGuid = result.AaGuid,
                    Transports = result.Transports?.Select(t => t.ToString().ToLowerInvariant()).ToArray()
                        ?? Array.Empty<string>(),
                    CreatedAt = DateTimeOffset.UtcNow
                };

                _context.B2BPasskeyCredentials.Add(credential);
                using (TimingScope.Begin("credential_persist"))
                {
                    await _context.SaveChangesAsync();
                }

                // チャレンジ消費
                using (TimingScope.Begin("challenge_consume"))
                {
                    await _challengeService.ConsumeChallengeAsync(request.SessionId);
                }

                _logger.LogInformation(
                    "Registered passkey for B2BUser {Subject}, CredentialId: {CredentialId}",
                    challenge.Subject,
                    WebEncoders.Base64UrlEncode(result.Id));

                return new IB2BPasskeyService.RegistrationVerifyResult
                {
                    Success = true,
                    CredentialId = WebEncoders.Base64UrlEncode(result.Id)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    PasskeyVerifyFailedExceptionLogTemplate,
                    request.ClientId, request.SessionId, "fido2_error");
                return new IB2BPasskeyService.RegistrationVerifyResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        #endregion

        #region Authentication Methods

        /// <inheritdoc />
        public async Task<IB2BPasskeyService.AuthenticationOptionsResult> CreateAuthenticationOptionsAsync(
            IB2BPasskeyService.AuthenticationOptionsRequest request)
        {
            // バリデーション
            if (string.IsNullOrWhiteSpace(request.ClientId))
                throw new ArgumentException("ClientId is required", nameof(request));
            if (string.IsNullOrWhiteSpace(request.RpId))
                throw new ArgumentException("RpId is required", nameof(request));
            // RP ID正規化（ドメイン名は大文字小文字を区別しない: RFC 4343）
            var rpId = request.RpId.ToLowerInvariant();

            // クライアント取得
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .ExcludeDeletedOrganizations()
                .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

            if (client == null)
                throw new InvalidOperationException($"Client not found: {request.ClientId}");

            // Organization 未設定の Client は Organization スコープを判定できないため拒否する。
            // 登録側（CreateRegistrationOptionsAsync）と verify 側（VerifyAuthenticationAsync）は
            // 既に拒否しており、ここで通しても後段で必ず落ちる。allowCredentials とチャレンジを
            // 発行してから verify で落とすのではなく、options 発行前に明示的に弾く。
            if (client.OrganizationId == null)
                throw new InvalidOperationException($"Client has no associated Organization: {request.ClientId}");

            // RP ID検証（ドメイン名は大文字小文字を区別しない: RFC 4343）
            if (!client.AllowedRpIds.Contains(rpId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"RpId is not allowed for this client: {rpId}");

            // B2BSubject の正規化（指定時のみ）
            string? b2bSubject = null;
            if (!string.IsNullOrWhiteSpace(request.B2BSubject))
            {
                if (Guid.TryParse(request.B2BSubject, out var parsedSubject))
                    b2bSubject = parsedSubject.ToString();
                else
                    b2bSubject = request.B2BSubject;
            }

            // 許可されるクレデンシャルを取得
            //
            // 発行元（b2b_passkey_credential.client_id）でリクエスト元 Client に絞る
            // （EcAuthDocs#110 問題 4）。同一ドメインに EC-CUBE と WordPress が同居する構成では
            // 両 Client の RP ID が一致するため、絞らないと「WordPress の管理者パスキーが
            // EC-CUBE 管理画面のログイン候補に出る」。Organization での絞り込みは
            // 1 org : 複数 Client を成立させる本 issue の目的上、分離にならない。
            //
            // これは候補提示の絞り込みであり、境界の強制は VerifyAuthenticationAsync の
            // 発行元 Client 検証が担う（絞り込み結果が空になると discoverable フローになり、
            // ここだけでは兄弟 Client のクレデンシャルを止められない）。
            var allowCredentials = new List<PublicKeyCredentialDescriptor>();
            if (b2bSubject != null)
            {
                // 特定ユーザーのクレデンシャルのみ（ユーザー確定済みの経路）
                //
                // b2b_subject はリクエスト由来の値で、この API は無認証で呼べる。Organization で
                // 絞らないと、他 Organization のユーザーの b2b_subject を指定するだけでその
                // クレデンシャル ID 一覧を取得でき（本人確認前の情報漏えい）、さらにその
                // allowCredentials で認証を試行できてしまう。Client の Organization に属する
                // ユーザーのクレデンシャルに限定する。
                allowCredentials = await _context.B2BPasskeyCredentials
                    .IgnoreQueryFilters()
                    .Where(c => c.B2BSubject == b2bSubject
                        && c.B2BUser != null
                        && c.B2BUser.OrganizationId == client.OrganizationId
                        // 発行元による絞り込み（EcAuthDocs#110 問題 4 / リリース 3〜4）
                        && c.ClientId == client.Id)
                    .Select(c => new PublicKeyCredentialDescriptor(
                        PublicKeyCredentialType.PublicKey,
                        c.CredentialId,
                        ParseTransports(c.TransportsJson)))
                    .ToListAsync();
            }
            else if (client.SubjectType != SubjectType.Account)
            {
                // b2b_subject 未指定（誰がログインするか未確定）の経路。
                //
                // 本来は allowCredentials を空にして discoverable credential フローに寄せたい。
                // この API は無認証で呼べるため、client_id を知る第三者が組織内の全クレデンシャル ID と
                // 登録ユーザー数を列挙できてしまうため（本人確認前の情報漏えい）。
                //
                // ただし EC-CUBE プラグインの管理画面ログインはこの経路を使っており、既存クレデンシャルは
                // ResidentKey=Preferred で登録されている。Preferred は discoverable を保証しないうえ、
                // B2BPasskeyCredential には resident key か否かを保存していないため、既存クレデンシャルが
                // discoverable かを事後に判定できない。ここで空にすると非 discoverable なクレデンシャルの
                // ユーザーがログイン不能になり、復旧にはログインが必要という詰みを招く。
                //
                // そのため稼働中の経路（Account 以外）は従来動作を維持し、列挙対策は
                // 「rk フラグの保存 → 新規登録を Required 化 → 既存ユーザーの再登録 → 空へ切替」
                // の移行を経て別途行う。accounts の管理コンソール（SubjectType.Account）は本 PR で
                // 新設した経路で既存ユーザーが存在せず、登録時に ResidentKey=Required としているため、
                // 上記 else 側（空の allowCredentials）で安全に運用できる。
                var orgUserSubjects = await _context.B2BUsers
                    .IgnoreQueryFilters()
                    .Where(u => u.OrganizationId == client.OrganizationId)
                    .Select(u => u.Subject)
                    .ToListAsync();

                allowCredentials = await _context.B2BPasskeyCredentials
                    .IgnoreQueryFilters()
                    .Where(c => orgUserSubjects.Contains(c.B2BSubject)
                        // 発行元による絞り込み（EcAuthDocs#110 問題 4 / リリース 3〜4）
                        && c.ClientId == client.Id)
                    .Select(c => new PublicKeyCredentialDescriptor(
                        PublicKeyCredentialType.PublicKey,
                        c.CredentialId,
                        ParseTransports(c.TransportsJson)))
                    .ToListAsync();
            }

            // チャレンジ生成
            // 発行する allowCredentials をチャレンジへ束縛し、verify 側で WebAuthn §7.2 Step 5 を
            // 実施できるようにする。descriptor から導出することで「発行した一覧」と
            // 「照合する一覧」が構造的に一致する。
            var challengeResult = await _challengeService.GenerateChallengeAsync(
                new IWebAuthnChallengeService.ChallengeRequest
                {
                    Type = "authentication",
                    UserType = "b2b",
                    Subject = b2bSubject,
                    RpId = rpId,
                    ClientId = client.Id,
                    AllowedCredentialIds = allowCredentials
                        .Select(c => WebEncoders.Base64UrlEncode(c.Id))
                        .ToList()
                });

            // 認証オプション生成
            // 注: _fido2.GetAssertionOptions()は内部で別のチャレンジを生成するため使用しない
            // _challengeServiceで生成したチャレンジと一致させるため、手動でAssertionOptionsを構築
            var options = new AssertionOptions
            {
                Challenge = WebEncoders.Base64UrlDecode(challengeResult.Challenge),
                RpId = rpId,
                AllowCredentials = allowCredentials,
                UserVerification = UserVerificationRequirement.Preferred
            };

            _logger.LogInformation(
                "Created authentication options, SessionId: {SessionId}, AllowCredentials: {Count}",
                challengeResult.SessionId,
                allowCredentials.Count);

            return new IB2BPasskeyService.AuthenticationOptionsResult
            {
                SessionId = challengeResult.SessionId,
                Options = options
            };
        }

        /// <inheritdoc />
        public async Task<IB2BPasskeyService.AuthenticationVerifyResult> VerifyAuthenticationAsync(
            IB2BPasskeyService.AuthenticationVerifyRequest request)
        {
            try
            {
                // チャレンジ取得
                WebAuthnChallenge? challenge;
                using (TimingScope.Begin("challenge_lookup"))
                {
                    challenge = await _challengeService.GetChallengeBySessionIdAsync(request.SessionId);
                }
                if (challenge == null)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "session_not_found", "Session not found or expired");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Session not found or expired"
                    };
                }

                // 期限チェック（defense-in-depth）
                // 注: GetChallengeBySessionIdAsync で既に期限切れチェック済みだが、
                // 多層防御として明示的に検証。将来の実装変更に対する安全性を確保。
                if (challenge.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "challenge_expired", "Challenge has expired");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Challenge has expired"
                    };
                }

                // タイプチェック
                if (challenge.Type != "authentication")
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "challenge_type_invalid", "Invalid challenge type");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid challenge type"
                    };
                }

                // セッションとリクエスト元 Client の束縛検証
                //
                // コントローラーは request.ClientId で Client を認証するが、チャレンジセッションが
                // その Client のものであることは検証していない。ここで突合しないと、別 Client が
                // 発行したセッションを自分の client_id で verify に持ち込め、認可コードが
                // リクエスト元 Client 宛に発行される（Client 境界の越境）。
                //
                // 注: GetChallengeBySessionIdAsync は Client / Organization を Include 済みだが、
                // ここでは navigation に依存せず明示的に引く。テストは challenge をモックで返すため
                // navigation が null になり、依存すると本番だけ通る経路が生まれてテストで守れない。
                Client? client;
                using (TimingScope.Begin("session_client_verify"))
                {
                    client = await _context.Clients
                        .IgnoreQueryFilters()
                        .ExcludeDeletedOrganizations()
                        .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);
                }

                if (client == null)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "client_not_found", "Client not found");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Client not found"
                    };
                }

                if (client.Id != challenge.ClientId)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "session_client_mismatch", "Session was not issued for this client");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Session does not belong to this client"
                    };
                }

                if (client.OrganizationId == null)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "client_organization_missing", "Client has no associated Organization");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Client has no associated Organization"
                    };
                }

                // クレデンシャル取得
                // Fido2.NetLib 4.0.0では Id は Base64URL文字列なのでデコードが必要
                var assertionCredentialIdBytes = WebEncoders.Base64UrlDecode(request.AssertionResponse.Id);
                // EF Coreは byte[] の == 演算子をSQLに変換可能（SequenceEqualは不可）
                B2BPasskeyCredential? credential;
                using (TimingScope.Begin("credential_lookup"))
                {
                    credential = await _context.B2BPasskeyCredentials
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(c => c.CredentialId == assertionCredentialIdBytes);
                }

                if (credential == null)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "credential_not_found", "Credential not found");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Credential not found"
                    };
                }

                // WebAuthn Level 3 §7.2 Step 5
                // 「pkOptions.allowCredentials が空でない場合、credential.id がその一覧の
                // いずれかを指すことを検証する」
                //
                // 比較はデコード後のバイト列を再エンコードした正規形で行う（クライアントが送る
                // Base64URL 文字列のパディング差異で不一致にならないようにする）。
                var assertionCredentialId = WebEncoders.Base64UrlEncode(assertionCredentialIdBytes);
                var allowedCredentialIds = challenge.AllowedCredentialIds;
                if (allowedCredentialIds is { Count: > 0 }
                    && !allowedCredentialIds.Contains(assertionCredentialId, StringComparer.Ordinal))
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "credential_not_allowed", "Credential is not in the allowCredentials issued for this session");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Credential is not allowed for this session"
                    };
                }

                // WebAuthn Level 3 §7.2 Step 6（ユーザー事前確定時）
                // 「認証セレモニー開始前にユーザーが特定されていた場合、その特定されたユーザー
                // アカウントが credential.rawId に等しい id を持つ credential record を含むことを
                // 検証する」
                //
                // b2b_subject 指定経路では options 発行時に RP がユーザーを確定しており、それが
                // challenge.Subject に保存されている。登録側（VerifyRegistrationAsync の
                // ExpectedSubject 突合）と対称になる。
                // challenge.Subject が null の経路（ユーザー未確定）は Step 6 の後者の要件を
                // isUserHandleOwner コールバックが満たす。
                if (!string.IsNullOrEmpty(challenge.Subject)
                    && !string.Equals(credential.B2BSubject, challenge.Subject, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "credential_subject_mismatch", "Credential does not belong to the subject identified for this session");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Credential does not belong to the session subject"
                    };
                }

                // Organization スコープ検証
                //
                // rpIdHash の検証は Fido2.NetLib が ServerDomain = challenge.RpId に基づいて行うため、
                // rp_id が異なるクレデンシャルは assertion 検証で落ちる。しかし同一 rp_id を共有する
                // 別 Organization（同一ドメインで本番サイトとサンドボックスサイトの両方を申し込んだ
                // 場合など）のクレデンシャルは通ってしまう。Client の Organization に属するユーザーの
                // クレデンシャルであることを明示的に検証する。
                bool credentialBelongsToClientOrganization;
                using (TimingScope.Begin("credential_organization_verify"))
                {
                    credentialBelongsToClientOrganization = await _context.B2BUsers
                        .IgnoreQueryFilters()
                        .AnyAsync(u => u.Subject == credential.B2BSubject
                            && u.OrganizationId == client.OrganizationId);
                }

                if (!credentialBelongsToClientOrganization)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "credential_organization_mismatch", "Credential owner does not belong to the client's Organization");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Credential does not belong to this organization"
                    };
                }

                // 発行元 Client 検証（EcAuthDocs#110 問題 4）
                //
                // options 側で allowCredentials を絞るだけでは境界にならない。絞り込んだ結果が空に
                // なると WebAuthn では「制限なしの discoverable credential フロー」になり、上の
                // §7.2 Step 5 の照合も仕様どおり allowCredentials が空の場合は適用されない。その
                // 状態では兄弟 Client が発行したクレデンシャルをブラウザが提示でき、直上の
                // Organization 検証は同一 Organization なので素通りする。
                // つまり絞り込みは候補提示の話であり、境界の強制はここで行う必要がある。
                if (credential.ClientId != client.Id)
                {
                    _logger.LogWarning(
                        PasskeyVerifyFailedLogTemplate,
                        request.ClientId, request.SessionId, "credential_issuer_mismatch", "Credential was issued by another client");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Credential was not issued for this client"
                    };
                }

                // AssertionOptionsを復元
                // AllowCredentials も発行時の値へ復元する。Fido2.NetLib 側も §7.2 Step 5 相当の
                // 照合を行うため、上の自前チェックとの二重防御になる（未記録の既存行では null）。
                var options = new AssertionOptions
                {
                    Challenge = WebEncoders.Base64UrlDecode(challenge.Challenge),
                    RpId = challenge.RpId,
                    AllowCredentials = allowedCredentialIds?
                        .Select(id => new PublicKeyCredentialDescriptor(WebEncoders.Base64UrlDecode(id)))
                        .ToList()
                };

                // ユーザーハンドル所有権チェック用デリゲート
                // credential オブジェクトは既にこのメソッドのスコープで取得済みのため、再クエリは不要
                IsUserHandleOwnerOfCredentialIdAsync isUserHandleOwner = (args, cancellationToken) =>
                {
                    var userHandle = Encoding.UTF8.GetString(args.UserHandle);
                    return Task.FromResult(credential.B2BSubject == userHandle);
                };

                // 動的にoriginを構築（RP IDに基づく）
                // Fido2.NetLib 4.0.0ではorigin検証はFido2Configurationで設定するため、
                // リクエストごとに新しいFido2インスタンスを作成
                var allowedOrigins = new HashSet<string>
                {
                    $"https://{challenge.RpId}",
                    $"https://{challenge.RpId}:8081",  // 開発環境用ポート
                    $"https://{challenge.RpId}:443"
                };
                var dynamicConfig = new Fido2Configuration
                {
                    ServerDomain = challenge.RpId!,
                    ServerName = "EcAuth",
                    Origins = allowedOrigins
                };
                var dynamicFido2 = _fido2Factory(dynamicConfig);

                // 認証検証（Fido2.NetLib 4.0.0 API）
                VerifyAssertionResult result;
                using (TimingScope.Begin("fido2_make_assertion"))
                {
                    result = await dynamicFido2.MakeAssertionAsync(new MakeAssertionParams
                    {
                        AssertionResponse = request.AssertionResponse,
                        OriginalOptions = options,
                        StoredPublicKey = credential.PublicKey,
                        StoredSignatureCounter = credential.SignCount,
                        IsUserHandleOwnerOfCredentialIdCallback = isUserHandleOwner
                    });
                }

                // SignCount更新
                credential.SignCount = result.SignCount;
                credential.LastUsedAt = DateTimeOffset.UtcNow;
                using (TimingScope.Begin("signcount_persist"))
                {
                    await _context.SaveChangesAsync();
                }

                // チャレンジ消費
                using (TimingScope.Begin("challenge_consume"))
                {
                    await _challengeService.ConsumeChallengeAsync(request.SessionId);
                }

                _logger.LogInformation(
                    "Authenticated B2BUser {Subject} with credential {CredentialId}",
                    credential.B2BSubject,
                    WebEncoders.Base64UrlEncode(credential.CredentialId));

                return new IB2BPasskeyService.AuthenticationVerifyResult
                {
                    Success = true,
                    B2BSubject = credential.B2BSubject,
                    CredentialId = WebEncoders.Base64UrlEncode(credential.CredentialId)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    PasskeyVerifyFailedExceptionLogTemplate,
                    request.ClientId, request.SessionId, "fido2_error");
                return new IB2BPasskeyService.AuthenticationVerifyResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        #endregion

        #region Management Methods

        /// <inheritdoc />
        public async Task<IReadOnlyList<IB2BPasskeyService.PasskeyInfo>> GetCredentialsBySubjectAsync(string b2bSubject)
        {
            if (string.IsNullOrWhiteSpace(b2bSubject))
                return Array.Empty<IB2BPasskeyService.PasskeyInfo>();

            var credentials = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .Where(c => c.B2BSubject == b2bSubject)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            return credentials.Select(c => new IB2BPasskeyService.PasskeyInfo
            {
                CredentialId = WebEncoders.Base64UrlEncode(c.CredentialId),
                DeviceName = c.DeviceName,
                AaGuid = c.AaGuid,
                Transports = c.Transports,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt
            }).ToList();
        }

        /// <inheritdoc />
        public async Task<bool> DeleteCredentialAsync(string b2bSubject, string credentialId)
        {
            if (string.IsNullOrWhiteSpace(b2bSubject) || string.IsNullOrWhiteSpace(credentialId))
                return false;

            byte[] credentialIdBytes;
            try
            {
                credentialIdBytes = WebEncoders.Base64UrlDecode(credentialId);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "無効なCredentialId形式です: {CredentialId}", credentialId);
                return false;
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "無効なCredentialId引数です: {CredentialId}", credentialId);
                return false;
            }

            // EF Coreは byte[] の == 演算子をSQLに変換可能（SequenceEqualは不可）
            var credential = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c =>
                    c.B2BSubject == b2bSubject &&
                    c.CredentialId == credentialIdBytes);

            if (credential == null)
                return false;

            _context.B2BPasskeyCredentials.Remove(credential);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Deleted passkey for B2BUser {Subject}, CredentialId: {CredentialId}",
                b2bSubject,
                credentialId);

            return true;
        }

        /// <inheritdoc />
        public async Task<int> CountCredentialsBySubjectAsync(string b2bSubject)
        {
            if (string.IsNullOrWhiteSpace(b2bSubject))
                return 0;

            return await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .CountAsync(c => c.B2BSubject == b2bSubject);
        }

        #endregion

        #region Helper Methods

        private static AuthenticatorTransport[]? ParseTransports(string? transportsJson)
        {
            if (string.IsNullOrEmpty(transportsJson))
                return null;

            try
            {
                var transports = System.Text.Json.JsonSerializer.Deserialize<string[]>(transportsJson);
                if (transports == null || transports.Length == 0)
                    return null;

                return transports
                    .Select(t => Enum.TryParse<AuthenticatorTransport>(t, true, out var transport) ? transport : (AuthenticatorTransport?)null)
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .ToArray();
            }
            catch (System.Text.Json.JsonException)
            {
                // JSON形式が不正な場合はnullを返す（ログは記録しない：パフォーマンス考慮）
                return null;
            }
        }

        #endregion
    }
}
