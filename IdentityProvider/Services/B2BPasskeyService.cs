using Fido2NetLib;
using Fido2NetLib.Objects;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
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
            if (string.IsNullOrWhiteSpace(request.ExternalId))
                throw new ArgumentException("ExternalId is required", nameof(request));
            if (request.ExternalId.Length > B2BUser.ExternalIdMaxLength)
                throw new ArgumentException($"ExternalId must be {B2BUser.ExternalIdMaxLength} characters or less", nameof(request));

            // UUID 形式の検証・正規化（小文字ハイフン付き形式に統一）
            if (!Guid.TryParse(request.B2BSubject, out var parsedB2BSubject))
                throw new ArgumentException("B2BSubject must be a valid UUID format", nameof(request));
            var b2bSubject = parsedB2BSubject.ToString();

            // 文字列長の制限
            if (request.DisplayName != null && request.DisplayName.Length > 128)
                throw new ArgumentException("DisplayName must be 128 characters or less", nameof(request));
            if (request.DeviceName != null && request.DeviceName.Length > 128)
                throw new ArgumentException("DeviceName must be 128 characters or less", nameof(request));

            // クライアント取得
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.Organization)
                .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

            if (client == null)
                throw new InvalidOperationException($"Client not found: {request.ClientId}");

            if (client.OrganizationId == null)
                throw new InvalidOperationException($"Client has no associated Organization: {request.ClientId}");

            // RP ID検証（ドメイン名は大文字小文字を区別しない: RFC 4343）
            if (!client.AllowedRpIds.Contains(rpId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"RpId is not allowed for this client: {rpId}");

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

                // Subject が一致: external_id が変わっていたら自動同期（EC-CUBE login_id 変更への追随）
                user = await SyncExternalIdIfChangedAsync(user, request.ExternalId, client.OrganizationId.Value);
                subjectResolution = IB2BPasskeyService.SubjectResolutions.AsRequested;
            }
            else
            {
                // ExternalId で既存ユーザーを検索（EC-CUBEプラグイン再インストール時の復旧）
                user = await _userService.GetByExternalIdAsync(request.ExternalId, client.OrganizationId.Value);
                if (user != null)
                {
                    // external_id は login_id 等 PII を含み得るため Information ログには含めない
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
                            ExternalId = request.ExternalId,
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
                    catch (DbUpdateException)
                    {
                        // 並行リクエストで Subject または ExternalId の一意制約違反が発生した場合、再取得を試みる。
                        _logger.LogInformation(
                            "B2BUser already created by concurrent request, re-fetching: Subject={Subject}",
                            b2bSubject);
                        user = await _userService.GetBySubjectAsync(b2bSubject);
                        if (user == null)
                        {
                            user = await _userService.GetByExternalIdAsync(request.ExternalId, client.OrganizationId.Value);
                            subjectResolution = user != null
                                ? IB2BPasskeyService.SubjectResolutions.FallbackByExternalId
                                : throw new InvalidOperationException($"Failed to create or retrieve B2BUser: {b2bSubject}");
                        }
                        else
                        {
                            // race 経由でも別組織の subject が返りうるため、メインフローと同じ境界チェックを適用
                            EnsureUserBelongsToClientOrganization(user, client.OrganizationId.Value, b2bSubject);

                            // 並行リクエストが先に書き込んだ external_id と、今回のリクエストの external_id が
                            // 異なる場合があるため、メインフローと同じく同期ロジックを適用する。
                            user = await SyncExternalIdIfChangedAsync(user, request.ExternalId, client.OrganizationId.Value);
                            subjectResolution = IB2BPasskeyService.SubjectResolutions.AsRequested;
                        }
                    }
                }
            }

            // ExternalId で既存ユーザーが見つかった場合、Subject が異なる可能性がある
            // 以降の処理は解決済みの Subject を使用する
            var resolvedSubject = user.Subject;

            // 既存のクレデンシャルを取得（除外リスト用）
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
            var fido2User = new Fido2User
            {
                Id = Encoding.UTF8.GetBytes(resolvedSubject),
                Name = user.ExternalId,
                DisplayName = request.DisplayName ?? user.ExternalId
            };

            // 認証器選択オプション
            var authenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                ResidentKey = ResidentKeyRequirement.Preferred,
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
        private void EnsureUserBelongsToClientOrganization(B2BUser user, int clientOrganizationId, string requestedSubject)
        {
            if (user.OrganizationId == clientOrganizationId)
            {
                return;
            }

            _logger.LogWarning(
                "B2BSubject does not belong to the client's organization. ClientOrganizationId={ClientOrganizationId}, UserOrganizationId={UserOrganizationId}",
                clientOrganizationId, user.OrganizationId);

            throw new InvalidOperationException("B2BSubject is not associated with this client's organization.");
        }

        /// <summary>
        /// 既存 B2BUser の external_id が引数の requestedExternalId と異なる場合、
        /// 同一 Organization 内の衝突を確認したうえで external_id を同期する。
        /// 衝突がある場合は <see cref="ExternalIdConflictException"/> をスロー。
        /// </summary>
        private async Task<B2BUser> SyncExternalIdIfChangedAsync(
            B2BUser user, string requestedExternalId, int organizationId)
        {
            if (string.Equals(user.ExternalId, requestedExternalId, StringComparison.Ordinal))
            {
                return user;
            }

            // 先行チェック: 同一 Organization 内で他ユーザーが既にその external_id を使っていれば 409
            var conflictingUser = await _userService.GetByExternalIdAsync(requestedExternalId, organizationId);
            if (conflictingUser != null
                && !string.Equals(conflictingUser.Subject, user.Subject, StringComparison.Ordinal))
            {
                throw new ExternalIdConflictException(
                    $"ExternalId '{requestedExternalId}' is already used by another user in this organization.");
            }

            // external_id の具体値は PII を含み得るため Information ログには含めない。
            // 調査時の Old/New 追跡は Debug ログで opt-in、恒久追跡は DB の updated_at 等に委ねる。
            _logger.LogInformation(
                "Syncing ExternalId for B2BUser: Subject={Subject}, OrganizationId={OrganizationId}",
                user.Subject, user.OrganizationId);
            _logger.LogDebug(
                "ExternalId sync values: Subject={Subject}, Old={OldExternalId}, New={NewExternalId}",
                user.Subject, user.ExternalId, requestedExternalId);

            try
            {
                var updated = await _userService.UpdateAsync(new IB2BUserService.UpdateUserRequest
                {
                    Subject = user.Subject,
                    ExternalId = requestedExternalId
                });
                // 事前に GetBySubjectAsync で取得済みの user の subject で呼んでいるため、
                // 通常 null は返らない。並行削除等で null になった場合は silent 失敗を避けて例外化。
                return updated ?? throw new InvalidOperationException(
                    $"UpdateAsync returned null while syncing ExternalId for Subject '{user.Subject}'.");
            }
            catch (DbUpdateException ex)
            {
                // DB 更新失敗の原因を実状態で再確認する。
                // UNIQUE 制約違反（race で別ユーザーが同じ external_id を取得）なら 409 相当として
                // ExternalIdConflictException にラップするが、それ以外（タイムアウト、接続断、
                // 別制約違反など 500 相当 / 再試行対象）の障害までは 409 に吸収せず元例外を再スローする。
                // SQL エラーコード判定ではなく「現時点で別ユーザーが当該 external_id を保有しているか」で
                // 判定することで、DB プロバイダー非依存に race condition を検出できる。
                var owner = await _userService.GetByExternalIdAsync(requestedExternalId, organizationId);
                if (owner != null
                    && !string.Equals(owner.Subject, user.Subject, StringComparison.Ordinal))
                {
                    throw new ExternalIdConflictException(
                        "ExternalId is already used by another user in this organization.", ex);
                }

                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IB2BPasskeyService.RegistrationVerifyResult> VerifyRegistrationAsync(
            IB2BPasskeyService.RegistrationVerifyRequest request)
        {
            try
            {
                // チャレンジ取得
                var challenge = await _challengeService.GetChallengeBySessionIdAsync(request.SessionId);
                if (challenge == null)
                {
                    _logger.LogWarning(
                        "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}",
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
                        "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}",
                        request.ClientId, request.SessionId, "challenge_expired", "Challenge has expired");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Challenge has expired"
                    };
                }

                // タイプチェック
                if (challenge.Type != "registration")
                {
                    _logger.LogWarning(
                        "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}",
                        request.ClientId, request.SessionId, "challenge_type_invalid", "Invalid challenge type");
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid challenge type"
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
                var result = await dynamicFido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = request.AttestationResponse,
                    OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = isCredentialIdUnique
                });

                // クレデンシャル保存
                var credential = new B2BPasskeyCredential
                {
                    B2BSubject = challenge.Subject!,
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
                await _context.SaveChangesAsync();

                // チャレンジ消費
                await _challengeService.ConsumeChallengeAsync(request.SessionId);

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
                    "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason}",
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
                .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

            if (client == null)
                throw new InvalidOperationException($"Client not found: {request.ClientId}");

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
            IQueryable<B2BPasskeyCredential> credentialQuery = _context.B2BPasskeyCredentials
                .IgnoreQueryFilters();

            if (b2bSubject != null)
            {
                // 特定ユーザーのクレデンシャルのみ
                credentialQuery = credentialQuery.Where(c => c.B2BSubject == b2bSubject);
            }
            else
            {
                // クライアントに紐づくOrganizationの全ユーザー
                var orgUserSubjects = await _context.B2BUsers
                    .IgnoreQueryFilters()
                    .Where(u => u.OrganizationId == client.OrganizationId)
                    .Select(u => u.Subject)
                    .ToListAsync();

                credentialQuery = credentialQuery.Where(c => orgUserSubjects.Contains(c.B2BSubject));
            }

            var allowCredentials = await credentialQuery
                .Select(c => new PublicKeyCredentialDescriptor(
                    PublicKeyCredentialType.PublicKey,
                    c.CredentialId,
                    ParseTransports(c.TransportsJson)))
                .ToListAsync();

            // チャレンジ生成
            var challengeResult = await _challengeService.GenerateChallengeAsync(
                new IWebAuthnChallengeService.ChallengeRequest
                {
                    Type = "authentication",
                    UserType = "b2b",
                    Subject = b2bSubject,
                    RpId = rpId,
                    ClientId = client.Id
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
                var challenge = await _challengeService.GetChallengeBySessionIdAsync(request.SessionId);
                if (challenge == null)
                {
                    _logger.LogWarning(
                        "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}",
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
                        "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}",
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
                        "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}",
                        request.ClientId, request.SessionId, "challenge_type_invalid", "Invalid challenge type");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid challenge type"
                    };
                }

                // クレデンシャル取得
                // Fido2.NetLib 4.0.0では Id は Base64URL文字列なのでデコードが必要
                var assertionCredentialIdBytes = WebEncoders.Base64UrlDecode(request.AssertionResponse.Id);
                // EF Coreは byte[] の == 演算子をSQLに変換可能（SequenceEqualは不可）
                var credential = await _context.B2BPasskeyCredentials
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.CredentialId == assertionCredentialIdBytes);

                if (credential == null)
                {
                    _logger.LogWarning(
                        "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason} detail={ErrorDetail}",
                        request.ClientId, request.SessionId, "credential_not_found", "Credential not found");
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Credential not found"
                    };
                }

                // AssertionOptionsを復元
                var options = new AssertionOptions
                {
                    Challenge = WebEncoders.Base64UrlDecode(challenge.Challenge),
                    RpId = challenge.RpId
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
                var result = await dynamicFido2.MakeAssertionAsync(new MakeAssertionParams
                {
                    AssertionResponse = request.AssertionResponse,
                    OriginalOptions = options,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = credential.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = isUserHandleOwner
                });

                // SignCount更新
                credential.SignCount = result.SignCount;
                credential.LastUsedAt = DateTimeOffset.UtcNow;
                await _context.SaveChangesAsync();

                // チャレンジ消費
                await _challengeService.ConsumeChallengeAsync(request.SessionId);

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
                    "Passkey.Verify.Failed: clientId={ClientId} sessionId={SessionId} reason={FailureReason}",
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
