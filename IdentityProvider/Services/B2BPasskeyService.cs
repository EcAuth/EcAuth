using Fido2NetLib;
using Fido2NetLib.Objects;
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

        public B2BPasskeyService(
            EcAuthDbContext context,
            IFido2 fido2,
            IWebAuthnChallengeService challengeService,
            IB2BUserService userService,
            ILogger<B2BPasskeyService> logger)
        {
            _context = context;
            _fido2 = fido2;
            _challengeService = challengeService;
            _userService = userService;
            _logger = logger;
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
            if (string.IsNullOrWhiteSpace(request.B2BSubject))
                throw new ArgumentException("B2BSubject is required", nameof(request));

            // クライアント取得
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.Organization)
                .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

            if (client == null)
                throw new InvalidOperationException($"Client not found: {request.ClientId}");

            // RP ID検証
            if (!client.AllowedRpIds.Contains(request.RpId))
                throw new InvalidOperationException($"RpId is not allowed for this client: {request.RpId}");

            // B2Bユーザー取得
            var user = await _userService.GetBySubjectAsync(request.B2BSubject);
            if (user == null)
                throw new InvalidOperationException($"B2BUser not found: {request.B2BSubject}");

            // 既存のクレデンシャルを取得（除外リスト用）
            var existingCredentials = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .Where(c => c.B2BSubject == request.B2BSubject)
                .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
                .ToListAsync();

            // チャレンジ生成
            var challengeResult = await _challengeService.GenerateChallengeAsync(
                new IWebAuthnChallengeService.ChallengeRequest
                {
                    Type = "registration",
                    UserType = "b2b",
                    Subject = request.B2BSubject,
                    RpId = request.RpId,
                    ClientId = client.Id
                });

            // Fido2ユーザー作成
            var fido2User = new Fido2User
            {
                Id = Encoding.UTF8.GetBytes(request.B2BSubject),
                Name = user.ExternalId ?? request.B2BSubject,
                DisplayName = request.DisplayName ?? user.ExternalId ?? "管理者"
            };

            // 認証器選択オプション
            var authenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred
            };

            // 登録オプション生成（Fido2.NetLib 4.0.0 API）
            var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = fido2User,
                ExcludeCredentials = existingCredentials,
                AuthenticatorSelection = authenticatorSelection,
                AttestationPreference = AttestationConveyancePreference.None
            });

            _logger.LogInformation(
                "Created registration options for B2BUser {Subject}, SessionId: {SessionId}",
                request.B2BSubject,
                challengeResult.SessionId);

            return new IB2BPasskeyService.RegistrationOptionsResult
            {
                SessionId = challengeResult.SessionId,
                Options = options
            };
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
                    return new IB2BPasskeyService.RegistrationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Challenge has expired"
                    };
                }

                // タイプチェック
                if (challenge.Type != "registration")
                {
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

                // 登録検証（Fido2.NetLib 4.0.0 API）
                var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
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
                _logger.LogError(ex, "Failed to verify registration for session {SessionId}", request.SessionId);
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

            // クライアント取得
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

            if (client == null)
                throw new InvalidOperationException($"Client not found: {request.ClientId}");

            // RP ID検証
            if (!client.AllowedRpIds.Contains(request.RpId))
                throw new InvalidOperationException($"RpId is not allowed for this client: {request.RpId}");

            // 許可されるクレデンシャルを取得
            IQueryable<B2BPasskeyCredential> credentialQuery = _context.B2BPasskeyCredentials
                .IgnoreQueryFilters();

            if (!string.IsNullOrWhiteSpace(request.B2BSubject))
            {
                // 特定ユーザーのクレデンシャルのみ
                credentialQuery = credentialQuery.Where(c => c.B2BSubject == request.B2BSubject);
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
                    Subject = request.B2BSubject,
                    RpId = request.RpId,
                    ClientId = client.Id
                });

            // 認証オプション生成（Fido2.NetLib 4.0.0 API）
            var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = allowCredentials,
                UserVerification = UserVerificationRequirement.Preferred
            });

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
                    return new IB2BPasskeyService.AuthenticationVerifyResult
                    {
                        Success = false,
                        ErrorMessage = "Challenge has expired"
                    };
                }

                // タイプチェック
                if (challenge.Type != "authentication")
                {
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
                IsUserHandleOwnerOfCredentialIdAsync isUserHandleOwner = async (args, cancellationToken) =>
                {
                    var cred = await _context.B2BPasskeyCredentials
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(c => c.CredentialId == args.CredentialId, cancellationToken);
                    if (cred == null) return false;

                    var userHandle = Encoding.UTF8.GetString(args.UserHandle);
                    return cred.B2BSubject == userHandle;
                };

                // 認証検証（Fido2.NetLib 4.0.0 API）
                var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
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
                _logger.LogError(ex, "Failed to verify authentication for session {SessionId}", request.SessionId);
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
