using Fido2NetLib;
using Fido2NetLib.Objects;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

namespace IdentityProvider.Test.Services
{
    public class B2BPasskeyServiceTests : IDisposable
    {
        // 発行元識別子（EcAuthDocs#110）。本テストの Client は一貫して "test-client-id"。
        private const string TestIssuerKey = "client:test-client-id";
        private const string TestB2BSubject = "550e8400-e29b-41d4-a716-446655440000";
        private const string TestB2BSubject2 = "550e8400-e29b-41d4-a716-446655440001";

        private readonly EcAuthDbContext _context;
        private readonly Mock<IFido2> _mockFido2;
        private readonly Mock<IWebAuthnChallengeService> _mockChallengeService;
        private readonly Mock<IB2BUserService> _mockUserService;
        private readonly Mock<ILogger<B2BPasskeyService>> _mockLogger;
        private readonly B2BPasskeyService _service;
        private readonly Organization _organization;
        private readonly Client _client;
        private readonly B2BUser _testUser;

        public B2BPasskeyServiceTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockFido2 = new Mock<IFido2>();
            _mockChallengeService = new Mock<IWebAuthnChallengeService>();
            _mockUserService = new Mock<IB2BUserService>();
            _mockLogger = new Mock<ILogger<B2BPasskeyService>>();

            // マルチテナント対応: origin検証のために動的にFido2インスタンスを作成
            // テストではモックを返すファクトリーを注入
            Func<Fido2Configuration, IFido2> fido2Factory = _ => _mockFido2.Object;

            _service = new B2BPasskeyService(
                _context,
                _mockFido2.Object,
                _mockChallengeService.Object,
                _mockUserService.Object,
                _mockLogger.Object,
                fido2Factory);

            // テスト用のテナント・クライアントをセットアップ
            _organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "テスト組織",
                TenantName = "test-tenant"
            };
            _context.Organizations.Add(_organization);

            _client = new Client
            {
                Id = 1,
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AppName = "テストクライアント",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com", "admin.example.com" }
            };
            _context.Clients.Add(_client);

            _testUser = new B2BUser
            {
                Id = 1,
                Subject = TestB2BSubject,
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(_testUser);

            _context.SaveChanges();
        }

        #region CreateRegistrationOptionsAsync Tests

        [Fact]
        public async Task CreateRegistrationOptionsAsync_ValidRequest_ShouldReturnOptions()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                DisplayName = "テスト管理者",
                DeviceName = "MacBook Pro",
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-123",
                Challenge = "dGVzdC1jaGFsbGVuZ2U", // Base64URL of "test-challenge"
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            var credentialCreateOptions = new CredentialCreateOptions
            {
                Challenge = Encoding.UTF8.GetBytes("test-challenge"),
                Rp = new PublicKeyCredentialRpEntity("shop.example.com", "テスト組織"),
                User = new Fido2User
                {
                    Id = Encoding.UTF8.GetBytes(TestB2BSubject),
                    Name = "admin@example.com",
                    DisplayName = "テスト管理者"
                },
                PubKeyCredParams = PubKeyCredParam.Defaults
            };
            _mockFido2.Setup(x => x.RequestNewCredential(It.IsAny<RequestNewCredentialParams>()))
                .Returns(credentialCreateOptions);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("session-123", result.SessionId);
            Assert.NotNull(result.Options);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_WebAuthnUserName_UsesPlaintextExternalId()
        {
            // Arrange: WebAuthn の user.name/displayName は認証器に表示されるため、
            // ハッシュ値ではなくリクエスト由来の平文 external_id（login_id 等）を使う。
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "session-name",
                    Challenge = "dGVzdC1jaGFsbGVuZ2U",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert: name/displayName は平文、かつ identity に保存されるハッシュ値とは一致しない。
            Assert.Equal("admin@example.com", result.Options.User.Name);
            Assert.Equal("admin@example.com", result.Options.User.DisplayName);
            Assert.NotEqual(ExternalIdHasher.Hash("admin@example.com"), result.Options.User.Name);
        }

        [Theory]
        [InlineData(null, "admin")]
        [InlineData("店舗管理者", "店舗管理者")]
        public async Task CreateRegistrationOptionsAsync_UserName_UsesUserNameForWebAuthnUser(string? displayName, string expectedDisplayName)
        {
            // Arrange: EcAuthDocs#110 以降のプラグインは external_id に不変キー（EC-CUBE の member_id）、
            // user_name に人が読める値（login_id）を送る。認証器・パスキー管理画面に表示される
            // user.name は user_name であり、不変キーが表示に漏れないこと。
            // displayName は display_name → user_name の順で決まる。
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "1",
                UserName = "admin",
                DisplayName = displayName
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "session-user-name",
                    Challenge = "dGVzdC1jaGFsbGVuZ2U",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.Equal("admin", result.Options.User.Name);
            Assert.Equal(expectedDisplayName, result.Options.User.DisplayName);
            // identity の補完には external_id（不変キー）だけが使われ、user_name は識別に関与しない
            _mockUserService.Verify(
                x => x.EnsureIdentityAsync(TestB2BSubject, TestIssuerKey, "1", "test-client-id"),
                Times.Once);
            _mockUserService.Verify(
                x => x.EnsureIdentityAsync(It.IsAny<string>(), It.IsAny<string>(), "admin", It.IsAny<string?>()),
                Times.Never);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRegistrationOptionsAsync_UserNameOmitted_FallsBackToExternalId(string? userName)
        {
            // Arrange: user_name を送らない旧プラグイン（external_id に login_id が入る）との互換。
            // 空白のみも未指定として扱う。
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "admin",
                UserName = userName
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "session-user-name-fallback",
                    Challenge = "dGVzdC1jaGFsbGVuZ2U",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.Equal("admin", result.Options.User.Name);
            Assert.Equal("admin", result.Options.User.DisplayName);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_UserNameTooLong_ShouldThrowArgumentException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "1",
                UserName = new string('a', 129)
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("UserName", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRegistrationOptionsAsync_EmptyClientId_ShouldThrowArgumentException(string clientId)
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = clientId,
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "test-admin"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("ClientId", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRegistrationOptionsAsync_EmptyRpId_ShouldThrowArgumentException(string rpId)
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = rpId,
                B2BSubject = TestB2BSubject,
                ExternalId = "test-admin"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("RpId", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRegistrationOptionsAsync_EmptyB2BSubject_ShouldThrowArgumentException(string subject)
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = subject,
                ExternalId = "test-admin"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("B2BSubject", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_NonExistingClient_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "non-existing-client",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "test-admin"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("Client", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_NonExistingUser_ShouldJitProvisionAndReturnOptions()
        {
            // Arrange
            var newSubject = "660e8400-e29b-41d4-a716-446655440099";
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = newSubject,
                DisplayName = "新規管理者",
                ExternalId = "new-admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(newSubject))
                .ReturnsAsync((B2BUser?)null);
            _mockUserService.Setup(x => x.GetByIdentityAsync(TestIssuerKey, "new-admin@example.com"))
                .ReturnsAsync((B2BUser?)null);

            var provisionedUser = new B2BUser
            {
                Id = 99,
                Subject = newSubject,
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _mockUserService.Setup(x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()))
                .ReturnsAsync(new IB2BUserService.CreateUserResult { User = provisionedUser });

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-jit",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("session-jit", result.SessionId);
            Assert.True(result.IsProvisioned);

            // CreateAsync が正しいパラメータで呼ばれたことを確認
            _mockUserService.Verify(x => x.CreateAsync(It.Is<IB2BUserService.CreateUserRequest>(r =>
                r.Subject == newSubject &&
                r.UserType == "admin" &&
                r.OrganizationId == 1 &&
                r.ExternalId == "new-admin@example.com"
            )), Times.Once);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_ExistingUser_ShouldNotProvision()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                DisplayName = "テスト管理者",
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-existing",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.False(result.IsProvisioned);

            // CreateAsync が呼ばれないことを確認
            _mockUserService.Verify(x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()), Times.Never);
        }

        [Theory]
        [InlineData("not-a-uuid")]
        [InlineData("12345")]
        [InlineData("xyz-invalid-format")]
        public async Task CreateRegistrationOptionsAsync_InvalidUuidSubject_ShouldThrowArgumentException(string invalidSubject)
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = invalidSubject,
                ExternalId = "test-admin"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("UUID", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_DisplayNameTooLong_ShouldThrowArgumentException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                DisplayName = new string('a', 129),
                ExternalId = "test-admin"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("DisplayName", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_DeviceNameTooLong_ShouldThrowArgumentException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                DeviceName = new string('a', 129),
                ExternalId = "test-admin"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("DeviceName", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_RpIdNotAllowed_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "unauthorized.example.com", // AllowedRpIdsに含まれない
                B2BSubject = TestB2BSubject,
                ExternalId = "test-admin"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("RpId", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_RpIdCaseInsensitive_ShouldReturnOptions()
        {
            // Arrange: AllowedRpIdsには "shop.example.com" が登録されているが、
            // リクエストでは大文字を含む "Shop.Example.COM" を送信
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "Shop.Example.COM",
                B2BSubject = TestB2BSubject,
                DisplayName = "テスト管理者",
                DeviceName = "MacBook Pro",
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-123",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert: 大文字小文字が異なっても正常に処理される（RFC 4343）
            Assert.NotNull(result);
            Assert.Equal("session-123", result.SessionId);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_MixedCaseRpId_ShouldNormalizeToLowercase()
        {
            // Arrange: Azure Web Appsドメインのように混在ケースのRP IDを送信
            // ブラウザの window.location.hostname は常に小文字を返すため、
            // WebAuthn APIのRP ID検証で不一致になる不具合の再現テスト
            //
            // AllowedRpIdsには "shop.example.com" が登録されているが、
            // リクエストでは "Shop.EXAMPLE.Com" を送信する
            var mixedCaseRpId = "Shop.EXAMPLE.Com";
            var expectedLowercaseRpId = "shop.example.com";

            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = mixedCaseRpId,
                B2BSubject = TestB2BSubject,
                DisplayName = "テスト管理者",
                DeviceName = "E2E Test Device",
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            IWebAuthnChallengeService.ChallengeRequest? capturedChallengeRequest = null;
            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-rp-normalize",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedChallengeRequest = req)
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert: 返却されるRP IDが小文字に正規化されていること
            Assert.NotNull(result);
            Assert.Equal(expectedLowercaseRpId, result.Options.Rp.Id);

            // チャレンジに保存されるRP IDも小文字であること
            Assert.NotNull(capturedChallengeRequest);
            Assert.Equal(expectedLowercaseRpId, capturedChallengeRequest.RpId);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_UserFoundByExternalId_ShouldNotProvisionNewUser()
        {
            // Arrange: Subject では見つからないが、ExternalId で既存ユーザーが見つかるケース
            // （EC-CUBEプラグイン再インストール時の復旧シナリオ）
            var newSubject = "770e8400-e29b-41d4-a716-446655440099";
            var existingSubject = TestB2BSubject;
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = newSubject,
                ExternalId = "admin@example.com",
                DisplayName = "管理者"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(newSubject))
                .ReturnsAsync((B2BUser?)null);
            _mockUserService.Setup(x => x.GetByIdentityAsync(TestIssuerKey, "admin@example.com"))
                .ReturnsAsync(_testUser);

            IWebAuthnChallengeService.ChallengeRequest? capturedChallengeRequest = null;
            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-external-id",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedChallengeRequest = req)
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.False(result.IsProvisioned);

            // CreateAsync は呼ばれないこと
            _mockUserService.Verify(x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()), Times.Never);

            // resolvedSubject（既存ユーザーのSubject）がチャレンジに使われること
            Assert.NotNull(capturedChallengeRequest);
            Assert.Equal(existingSubject, capturedChallengeRequest.Subject);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_SubjectHit_ExternalIdMatches_ReturnsAsRequestedResolution()
        {
            // Arrange: subject 一致 → subject_resolution = "as_requested"。
            // 発行元の identity は insert-if-missing で毎回 Ensure される（変化の有無はサービス側では判定しない）。
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "sess-as-req",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.Equal(TestB2BSubject, result.ResolvedSubject);
            Assert.Equal(IB2BPasskeyService.SubjectResolutions.AsRequested, result.SubjectResolution);
            Assert.False(result.IsProvisioned);
            _mockUserService.Verify(
                x => x.EnsureIdentityAsync(TestB2BSubject, TestIssuerKey, "admin@example.com", "test-client-id"),
                Times.Once);
            _mockUserService.Verify(
                x => x.GetByIdentityAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_SubjectHit_ExternalIdDiffers_EnsuresNewIdentity()
        {
            // Arrange: subject 一致 + external_id が変わった（EC-CUBE の login_id 変更）→ 新しい identity 行が
            // 追加される。旧 identity は残す（EcAuthDocs#110: 差し替えではなく追加）。
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "renamed-admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "sess-sync",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.Equal(TestB2BSubject, result.ResolvedSubject);
            Assert.Equal(IB2BPasskeyService.SubjectResolutions.AsRequested, result.SubjectResolution);
            _mockUserService.Verify(
                x => x.EnsureIdentityAsync(TestB2BSubject, TestIssuerKey, "renamed-admin@example.com", "test-client-id"),
                Times.Once);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_SubjectHit_ExternalIdCollidesWithOtherUser_ThrowsExternalIdConflict()
        {
            // Arrange: subject A に、同一発行元で別ユーザー B が既に保有する external_id を紐づけようとする。
            // 衝突判定は B2BUserService.EnsureIdentityAsync が (issuer_key, external_id) 単位で行い、
            // 409 相当の ExternalIdConflictException を投げる。サービス層はそれをそのまま伝播させる。
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "other-admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);
            var conflict = new ExternalIdConflictException(
                $"ExternalId (hash '{ExternalIdHasher.Hash("other-admin@example.com")}') is already used by another user under issuer '{TestIssuerKey}'.");
            _mockUserService.Setup(x => x.EnsureIdentityAsync(TestB2BSubject, TestIssuerKey, "other-admin@example.com", "test-client-id"))
                .ThrowsAsync(conflict);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ExternalIdConflictException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Same(conflict, ex);
            // チャレンジは発行されない（衝突した subject で登録を進めない）
            _mockChallengeService.Verify(
                x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()), Times.Never);
        }

        /// <summary>
        /// 登録トークン経路は external_id を持たない（subject はトークンで確定し、identity は申込確定時に
        /// 作成済み）。identity の解決・同期は行わず、WebAuthn の user.name には subject を使う。
        /// </summary>
        [Fact]
        public async Task CreateRegistrationOptionsAsync_RegistrationTokenPath_ShouldNotResolveOrSyncIdentity()
        {
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                DisplayName = "owner@example.com",
                ExternalId = null,
                ResolvedByRegistrationToken = true
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "sess-prehashed",
                    Challenge = "dGVzdC1jaGFsbGVuZ2U",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.Equal(TestB2BSubject, result.ResolvedSubject);
            Assert.Equal(IB2BPasskeyService.SubjectResolutions.AsRequested, result.SubjectResolution);
            Assert.Equal(TestB2BSubject, result.Options.User.Name);
            Assert.Equal("owner@example.com", result.Options.User.DisplayName);

            // identity には触らない（解決も同期もしない）
            _mockUserService.Verify(
                x => x.EnsureIdentityAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
            _mockUserService.Verify(
                x => x.GetByIdentityAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _mockUserService.Verify(
                x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()), Times.Never);
        }

        /// <summary>
        /// 登録トークンは既存 B2BUser から発行されるため subject は必ず引ける。
        /// 引けない場合に identity 解決 / JIT へ進むと、external_id を持たないこの経路では
        /// 別人の解決や不正な作成につながるため、明示的に失敗させる。
        /// </summary>
        [Fact]
        public async Task CreateRegistrationOptionsAsync_RegistrationTokenPath_SubjectMissing_ShouldThrow()
        {
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = null,
                ResolvedByRegistrationToken = true
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync((B2BUser?)null);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));

            _mockUserService.Verify(
                x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()), Times.Never);
            _mockUserService.Verify(
                x => x.GetByIdentityAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_EnsureIdentityThrowsTransientError_RethrowsOriginalException()
        {
            // Arrange: identity の書き込みで一意制約違反以外の過渡的障害（タイムアウト / デッドロック等）が
            // 起きた場合、409 に吸収せず元の例外をそのまま伝える（500 相当 / 再試行対象）。
            // race による衝突判定は B2BUserService.EnsureIdentityAsync 側の責務で、ここでは行わない。
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "renamed-admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            var transientError = new DbUpdateException(
                "transient DB error (e.g. timeout / deadlock)",
                new Exception("inner"));
            _mockUserService.Setup(x => x.EnsureIdentityAsync(TestB2BSubject, TestIssuerKey, "renamed-admin@example.com", "test-client-id"))
                .ThrowsAsync(transientError);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Same(transientError, ex);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_SubjectHit_UserBelongsToDifferentOrganization_ThrowsInvalidOperation()
        {
            // Arrange: B2BUser の QueryFilter は TenantName ベースなので、
            // 同一テナント内の別 Organization の subject が GetBySubjectAsync で返り得る。
            // このクロス組織ケースで external_id の自動同期や credential 発行を許してはならない。
            var crossOrgUser = new B2BUser
            {
                Id = 999,
                Subject = TestB2BSubject,
                UserType = "admin",
                OrganizationId = 999, // _client.OrganizationId = 1 とは異なる組織
                Organization = new Organization
                {
                    Id = 999,
                    Code = "other-org",
                    Name = "別組織",
                    TenantName = "test-tenant"
                }
            };

            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(crossOrgUser);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("not associated with this client's organization", ex.Message);

            // identity の同期・解決は一切呼ばれないこと
            _mockUserService.Verify(
                x => x.EnsureIdentityAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
            _mockUserService.Verify(
                x => x.GetByIdentityAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_SubjectMissExternalIdHit_ReturnsFallbackResolution()
        {
            // Arrange: 新 subject UUID だが external_id は既存ユーザーに紐付いている
            var newSubject = "770e8400-e29b-41d4-a716-446655440099";
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = newSubject,
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(newSubject))
                .ReturnsAsync((B2BUser?)null);
            _mockUserService.Setup(x => x.GetByIdentityAsync(TestIssuerKey, "admin@example.com"))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "sess-fallback",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert: resolved_subject はリクエストの新 UUID ではなく既存ユーザーの subject
            Assert.Equal(TestB2BSubject, result.ResolvedSubject);
            Assert.NotEqual(newSubject, result.ResolvedSubject);
            Assert.Equal(IB2BPasskeyService.SubjectResolutions.FallbackByExternalId, result.SubjectResolution);
            Assert.False(result.IsProvisioned);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_SubjectMissExternalIdMiss_ReturnsProvisionedResolution()
        {
            // Arrange: JIT プロビジョニングで新規作成
            var newSubject = "880e8400-e29b-41d4-a716-446655440099";
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = newSubject,
                ExternalId = "brand-new@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(newSubject))
                .ReturnsAsync((B2BUser?)null);
            _mockUserService.Setup(x => x.GetByIdentityAsync(TestIssuerKey, "brand-new@example.com"))
                .ReturnsAsync((B2BUser?)null);

            var newUser = new B2BUser
            {
                Id = 99,
                Subject = newSubject,
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _mockUserService.Setup(x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()))
                .ReturnsAsync(new IB2BUserService.CreateUserResult { User = newUser });

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "sess-provisioned",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.Equal(newSubject, result.ResolvedSubject);
            Assert.Equal(IB2BPasskeyService.SubjectResolutions.Provisioned, result.SubjectResolution);
            Assert.True(result.IsProvisioned);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_JitRaceRefetch_EnsuresIdentityForRequestedExternalId()
        {
            // Arrange: 並行リクエストで先に別の external_id で user が作られ、
            // 今回のリクエストは DbUpdateException → re-fetch で既存 user を取得するシナリオ。
            // re-fetch 後もメインフロー同様、今回の external_id の identity が Ensure されること。
            var contestedSubject = "990e8400-e29b-41d4-a716-446655440099";
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = contestedSubject,
                ExternalId = "winner@example.com"
            };

            var preExistingUser = new B2BUser
            {
                Id = 50,
                Subject = contestedSubject,
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };

            // 1回目: null → JIT パスに入る。2回目(re-fetch): preExistingUser を返す。
            _mockUserService.SetupSequence(x => x.GetBySubjectAsync(contestedSubject))
                .ReturnsAsync((B2BUser?)null)
                .ReturnsAsync(preExistingUser);

            // JIT 前の identity 検索は null（まだ書かれていないタイミング）
            _mockUserService.Setup(x => x.GetByIdentityAsync(TestIssuerKey, "winner@example.com"))
                .ReturnsAsync((B2BUser?)null);

            // CreateAsync は並行リクエストが先に書いたため UNIQUE 違反
            _mockUserService.Setup(x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()))
                .ThrowsAsync(new DbUpdateException("concurrent create", new Exception()));

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "sess-race-sync",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert: race 経路でも identity が Ensure され、AsRequested として返る
            Assert.Equal(contestedSubject, result.ResolvedSubject);
            Assert.Equal(IB2BPasskeyService.SubjectResolutions.AsRequested, result.SubjectResolution);
            Assert.False(result.IsProvisioned);
            _mockUserService.Verify(
                x => x.EnsureIdentityAsync(contestedSubject, TestIssuerKey, "winner@example.com", "test-client-id"),
                Times.Once);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_JitFailsAndRefetchMisses_ThrowsWithOriginalAsInner()
        {
            // Arrange: CreateAsync が一意違反以外の障害（タイムアウト等）で失敗し、再取得でも
            // subject / identity のどちらでも引けないケース。race ではないので元例外を失わず、
            // InvalidOperationException の InnerException として伝える。
            var newSubject = "aa0e8400-e29b-41d4-a716-446655440099";
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = newSubject,
                ExternalId = "unlucky@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(newSubject))
                .ReturnsAsync((B2BUser?)null);
            _mockUserService.Setup(x => x.GetByIdentityAsync(TestIssuerKey, "unlucky@example.com"))
                .ReturnsAsync((B2BUser?)null);

            var transientError = new DbUpdateException("transient DB error", new Exception("inner"));
            _mockUserService.Setup(x => x.CreateAsync(It.IsAny<IB2BUserService.CreateUserRequest>()))
                .ThrowsAsync(transientError);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("Failed to create or retrieve B2BUser", ex.Message);
            Assert.Same(transientError, ex.InnerException);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_MissingExternalId_ShouldThrowArgumentException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = ""
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("ExternalId", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_ExternalIdTooLong_ShouldThrowArgumentException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                ExternalId = new string('a', B2BUserIdentity.ExternalIdMaxLength + 1)
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("ExternalId", ex.Message);
        }

        #endregion

        #region VerifyRegistrationAsync Tests

        [Fact]
        public async Task VerifyRegistrationAsync_ValidRequest_ShouldSaveCredential()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                Id = 1,
                SessionId = "session-123",
                Challenge = "dGVzdC1jaGFsbGVuZ2U", // Base64URL
                Type = "registration",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("session-123"))
                .ReturnsAsync(challenge);

            var credentialIdBytes = Encoding.UTF8.GetBytes("credential-id");
            var attestationResponse = new AuthenticatorAttestationRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                RawId = credentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                {
                    AttestationObject = Encoding.UTF8.GetBytes("attestation"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data")
                }
            };

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "session-123",
                ClientId = "test-client-id",
                AttestationResponse = attestationResponse,
                DeviceName = "MacBook Pro"
            };

            var makeCredentialResult = new RegisteredPublicKeyCredential
            {
                Id = Encoding.UTF8.GetBytes("credential-id"),
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid(),
                AttestationObject = Encoding.UTF8.GetBytes("attestation"),
                AttestationClientDataJson = Encoding.UTF8.GetBytes("client-data")
            };

            _mockFido2.Setup(x => x.MakeNewCredentialAsync(
                It.IsAny<MakeNewCredentialParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(makeCredentialResult);

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync("session-123"))
                .ReturnsAsync(true);

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.CredentialId);

            // DBに保存されていることを確認
            var saved = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == TestB2BSubject);
            Assert.NotNull(saved);
            Assert.Equal("MacBook Pro", saved.DeviceName);
            // 発行元 Client が記録される（EcAuthDocs#110 リリース 2）。
            Assert.Equal(challenge.ClientId, saved.ClientId);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_SecondClientInSameOrganization_ShouldRecordRequestingClientAsIssuer()
        {
            // 同一 Organization に 2 つ目の Client がぶら下がる構成（EcAuthDocs#110 が成立させたい形）で、
            // 発行元が「リクエストを処理した Client」として記録されることを確認する。
            // Organization ではなく Client 単位で記録されないと、リリース 3 の allowCredentials
            // 絞り込み（#110 の問題 4）が成立しない。
            var secondClient = new Client
            {
                Id = 2,
                ClientId = "second-client-id",
                ClientSecret = "second-secret",
                AppName = "同一組織の別アプリ",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com" }
            };
            _context.Clients.Add(secondClient);
            await _context.SaveChangesAsync();

            var challenge = new WebAuthnChallenge
            {
                Id = 2,
                SessionId = "session-second",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                Type = "registration",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = secondClient.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("session-second"))
                .ReturnsAsync(challenge);

            var credentialIdBytes = Encoding.UTF8.GetBytes("second-credential-id");
            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "session-second",
                ClientId = "second-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse
                {
                    Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                    RawId = credentialIdBytes,
                    Type = PublicKeyCredentialType.PublicKey,
                    Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                    {
                        AttestationObject = Encoding.UTF8.GetBytes("attestation"),
                        ClientDataJson = Encoding.UTF8.GetBytes("client-data")
                    }
                },
                DeviceName = "別アプリの端末"
            };

            _mockFido2.Setup(x => x.MakeNewCredentialAsync(
                It.IsAny<MakeNewCredentialParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RegisteredPublicKeyCredential
                {
                    Id = credentialIdBytes,
                    PublicKey = Encoding.UTF8.GetBytes("public-key"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid(),
                    AttestationObject = Encoding.UTF8.GetBytes("attestation"),
                    AttestationClientDataJson = Encoding.UTF8.GetBytes("client-data")
                });

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync("session-second"))
                .ReturnsAsync(true);

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.True(result.Success);

            var saved = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialIdBytes);
            Assert.NotNull(saved);
            // 1 つ目の Client（Id=1）ではなく、リクエストを処理した 2 つ目の Client が入る。
            Assert.Equal(secondClient.Id, saved.ClientId);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_SessionIssuedForAnotherClient_ShouldReturnFailure()
        {
            // 別 Client が発行したセッションを、自分の client_id で verify に持ち込めないことを確認する。
            // 塞がないと、そのセッションの Subject に攻撃者の認証器を登録できてしまう
            // （認証側 VerifyAuthenticationAsync は同じ検証を実施済み）。
            var otherClient = new Client
            {
                Id = 2,
                ClientId = "other-client-id",
                ClientSecret = "other-secret",
                AppName = "セッションを発行した別アプリ",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com" }
            };
            _context.Clients.Add(otherClient);
            await _context.SaveChangesAsync();

            var challenge = new WebAuthnChallenge
            {
                Id = 3,
                SessionId = "session-other-client",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                Type = "registration",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = otherClient.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("session-other-client"))
                .ReturnsAsync(challenge);

            var credentialIdBytes = Encoding.UTF8.GetBytes("hijacked-credential-id");
            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "session-other-client",
                // セッションを発行していない Client（自身の client_secret では認証を通っている想定）
                ClientId = "test-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse
                {
                    Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                    RawId = credentialIdBytes,
                    Type = PublicKeyCredentialType.PublicKey,
                    Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                    {
                        AttestationObject = Encoding.UTF8.GetBytes("attestation"),
                        ClientDataJson = Encoding.UTF8.GetBytes("client-data")
                    }
                },
                DeviceName = "攻撃者の端末"
            };

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Equal("Session does not belong to this client", result.ErrorMessage);

            // WebAuthn 検証まで到達しない
            _mockFido2.Verify(
                x => x.MakeNewCredentialAsync(It.IsAny<MakeNewCredentialParams>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // クレデンシャルは保存されず、チャレンジも消費されない
            Assert.Empty(await _context.B2BPasskeyCredentials.IgnoreQueryFilters().ToListAsync());
            _mockChallengeService.Verify(x => x.ConsumeChallengeAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_NonExistingSession_ShouldReturnFailure()
        {
            // Arrange
            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("non-existing"))
                .ReturnsAsync((WebAuthnChallenge?)null);

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "non-existing",
                ClientId = "test-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse()
            };

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Session", result.ErrorMessage);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_ExpiredChallenge_ShouldReturnFailure()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                SessionId = "expired-session",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                Type = "registration",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) // 期限切れ
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("expired-session"))
                .ReturnsAsync(challenge);

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "expired-session",
                ClientId = "test-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse()
            };

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("expired", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_WrongChallengeType_ShouldReturnFailure()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                SessionId = "session-123",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                Type = "authentication", // 登録ではなく認証
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("session-123"))
                .ReturnsAsync(challenge);

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "session-123",
                ClientId = "test-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse()
            };

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("type", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region CreateAuthenticationOptionsAsync Tests

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_ValidRequest_ShouldReturnOptions()
        {
            // Arrange
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = Encoding.UTF8.GetBytes("credential-id"),
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid(),
                Transports = new[] { "internal" }
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject
            };

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-123",
                Challenge = "YXV0aC1jaGFsbGVuZ2U", // Base64URL of "auth-challenge"
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            var assertionOptions = new AssertionOptions
            {
                Challenge = Encoding.UTF8.GetBytes("auth-challenge"),
                RpId = "shop.example.com"
            };
            _mockFido2.Setup(x => x.GetAssertionOptions(It.IsAny<GetAssertionOptionsParams>()))
                .Returns(assertionOptions);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("auth-session-123", result.SessionId);
            Assert.NotNull(result.Options);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_WithoutSubject_ShouldGetAllCredentialsForRpId()
        {
            // Arrange
            // 複数ユーザーのクレデンシャルを追加
            var user2 = new B2BUser
            {
                Subject = TestB2BSubject2,
                UserType = "staff",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user2);

            var credentials = new[]
            {
                new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes("credential-1"),
                    PublicKey = Encoding.UTF8.GetBytes("public-key-1"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject2,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes("credential-2"),
                    PublicKey = Encoding.UTF8.GetBytes("public-key-2"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                }
            };
            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = null // 全ユーザー
            };

            // Base64URL形式のチャレンジ（"auth-challenge"をエンコード）
            var challengeBytes = Encoding.UTF8.GetBytes("auth-challenge");
            var challengeBase64Url = WebEncoders.Base64UrlEncode(challengeBytes);
            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-456",
                Challenge = challengeBase64Url,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            // 手動構築されたAssertionOptionsの内容を確認
            Assert.Equal(challengeBytes, result.Options.Challenge);
            // Account 以外の client（EC-CUBE プラグイン等の稼働中経路）は従来動作を維持する。
            // 既存クレデンシャルが discoverable とは限らず、空にするとログイン不能を招くため。
            Assert.Equal(2, result.Options.AllowCredentials!.Count);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_WithoutSubject_AccountClient_ShouldNotDiscloseCredentials()
        {
            // Arrange: accounts の管理コンソール（SubjectType.Account）は discoverable credential 前提のため、
            // b2b_subject 未指定でも allowCredentials を返さない（組織内の全クレデンシャル ID を秘匿する）。
            var accountClient = new Client
            {
                Id = 3,
                ClientId = "accounts-console-id",
                ClientSecret = string.Empty,
                AppName = "Accounts コンソール",
                OrganizationId = 1,
                SubjectType = SubjectType.Account,
                AllowedRpIds = new List<string> { "shop.example.com" }
            };
            _context.Clients.Add(accountClient);
            _context.B2BPasskeyCredentials.Add(new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = Encoding.UTF8.GetBytes("credential-account"),
                PublicKey = Encoding.UTF8.GetBytes("public-key-account"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            });
            await _context.SaveChangesAsync();

            var challengeBytes = Encoding.UTF8.GetBytes("auth-challenge");
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "auth-session-account",
                    Challenge = WebEncoders.Base64UrlEncode(challengeBytes),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(
                new IB2BPasskeyService.AuthenticationOptionsRequest
                {
                    ClientId = "accounts-console-id",
                    RpId = "shop.example.com",
                    B2BSubject = null
                });

            // Assert
            Assert.Empty(result.Options.AllowCredentials!);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_WithoutSubject_ShouldPassNullSubjectToChallengeService()
        {
            // Arrange: B2BSubject = null の場合、ChallengeService に Subject = null が渡されることを検証。
            // この経路は Discoverable Credentials 対応のため重要。
            // WebAuthnChallengeService の ValidateRequest が B2B 認証で null Subject を
            // 拒否していた不具合のリグレッション防止。
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = Encoding.UTF8.GetBytes("credential-for-null-subject"),
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = null
            };

            IWebAuthnChallengeService.ChallengeRequest? capturedRequest = null;
            var challengeBytes = Encoding.UTF8.GetBytes("auth-challenge");
            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "null-subject-session",
                Challenge = WebEncoders.Base64UrlEncode(challengeBytes),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedRequest = req)
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert: ChallengeService に渡された Subject が null であること
            Assert.NotNull(capturedRequest);
            Assert.Null(capturedRequest.Subject);
            Assert.Equal("authentication", capturedRequest.Type);
            Assert.Equal("b2b", capturedRequest.UserType);
            Assert.Equal("shop.example.com", capturedRequest.RpId);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_NoCredentials_ShouldReturnEmptyAllowCredentials()
        {
            // Arrange
            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = "user-with-no-passkeys"
            };

            // Base64URL形式のチャレンジ（"auth-challenge"をエンコード）
            var challengeBytes = Encoding.UTF8.GetBytes("auth-challenge");
            var challengeBase64Url = WebEncoders.Base64UrlEncode(challengeBytes);
            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-789",
                Challenge = challengeBase64Url,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            // 手動構築されたAssertionOptionsの内容を確認
            Assert.Equal(challengeBytes, result.Options.Challenge);
            Assert.Empty(result.Options.AllowCredentials!);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_RpIdCaseInsensitive_ShouldReturnOptions()
        {
            // Arrange: AllowedRpIdsには "shop.example.com" が登録されているが、
            // リクエストでは大文字を含む "Shop.Example.COM" を送信
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = Encoding.UTF8.GetBytes("credential-id"),
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid(),
                Transports = new[] { "internal" }
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "Shop.Example.COM",
                B2BSubject = TestB2BSubject
            };

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-123",
                Challenge = "YXV0aC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert: 大文字小文字が異なっても正常に処理される（RFC 4343）
            Assert.NotNull(result);
            Assert.Equal("auth-session-123", result.SessionId);
            Assert.NotNull(result.Options);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_MixedCaseRpId_ShouldNormalizeToLowercase()
        {
            // Arrange: Azure Web Appsドメインのように混在ケースのRP IDを送信
            // ブラウザの window.location.hostname は常に小文字を返すため、
            // WebAuthn APIのRP ID検証で不一致になる不具合の再現テスト
            //
            // AllowedRpIdsには "shop.example.com" が登録されているが、
            // リクエストでは "Shop.EXAMPLE.Com" を送信する
            var mixedCaseRpId = "Shop.EXAMPLE.Com";
            var expectedLowercaseRpId = "shop.example.com";

            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = Encoding.UTF8.GetBytes("credential-for-normalize"),
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid(),
                Transports = new[] { "internal" }
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = mixedCaseRpId,
                B2BSubject = TestB2BSubject
            };

            IWebAuthnChallengeService.ChallengeRequest? capturedChallengeRequest = null;
            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-rp-normalize",
                Challenge = "YXV0aC1jaGFsbGVuZ2U",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedChallengeRequest = req)
                .ReturnsAsync(challengeResult);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert: 返却されるRP IDが小文字に正規化されていること
            Assert.NotNull(result);
            Assert.Equal(expectedLowercaseRpId, result.Options.RpId);

            // チャレンジに保存されるRP IDも小文字であること
            Assert.NotNull(capturedChallengeRequest);
            Assert.Equal(expectedLowercaseRpId, capturedChallengeRequest.RpId);
        }

        /// <summary>
        /// 発行した allowCredentials がチャレンジへ束縛されること（WebAuthn §7.2 Step 5 を
        /// verify 側で実施するための前提）。
        /// </summary>
        [Fact]
        public async Task CreateAuthenticationOptionsAsync_ShouldBindIssuedAllowCredentialsToChallenge()
        {
            // Arrange
            var credentialId1 = Encoding.UTF8.GetBytes("bind-credential-1");
            var credentialId2 = Encoding.UTF8.GetBytes("bind-credential-2");
            _context.B2BPasskeyCredentials.AddRange(
                NewCredential(TestB2BSubject, credentialId1),
                NewCredential(TestB2BSubject, credentialId2));
            await _context.SaveChangesAsync();

            IWebAuthnChallengeService.ChallengeRequest? capturedChallengeRequest = null;
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedChallengeRequest = req)
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "bind-session",
                    Challenge = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("auth-challenge")),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(
                new IB2BPasskeyService.AuthenticationOptionsRequest
                {
                    ClientId = "test-client-id",
                    RpId = "shop.example.com",
                    B2BSubject = TestB2BSubject
                });

            // Assert: 束縛された一覧が、実際に発行した allowCredentials と一致すること
            Assert.NotNull(capturedChallengeRequest);
            Assert.NotNull(capturedChallengeRequest.AllowedCredentialIds);

            var issued = result.Options.AllowCredentials!
                .Select(c => WebEncoders.Base64UrlEncode(c.Id))
                .ToList();
            Assert.Equal(issued, capturedChallengeRequest.AllowedCredentialIds);
            Assert.Equal(
                new[]
                {
                    WebEncoders.Base64UrlEncode(credentialId1),
                    WebEncoders.Base64UrlEncode(credentialId2)
                }.Order(),
                capturedChallengeRequest.AllowedCredentialIds.Order());
        }

        /// <summary>
        /// b2b_subject 指定経路は Client の Organization に属するユーザーのクレデンシャルに
        /// 限定する。この API は無認証で呼べるため、絞らないと他 Organization のユーザーの
        /// クレデンシャル ID 一覧が本人確認前に漏れる。
        /// </summary>
        [Fact]
        public async Task CreateAuthenticationOptionsAsync_SubjectFromAnotherOrganization_ShouldReturnEmptyAllowCredentials()
        {
            // Arrange: 別 Organization のユーザーとそのクレデンシャル
            var otherOrganization = new Organization
            {
                Id = 2,
                Code = "other-org",
                Name = "別組織",
                TenantName = "test-tenant"
            };
            _context.Organizations.Add(otherOrganization);

            _context.B2BUsers.Add(new B2BUser
            {
                Id = 2,
                Subject = TestB2BSubject2,
                UserType = "admin",
                OrganizationId = 2,
                Organization = otherOrganization
            });
            _context.B2BPasskeyCredentials.Add(
                NewCredential(TestB2BSubject2, Encoding.UTF8.GetBytes("other-org-credential")));
            await _context.SaveChangesAsync();

            IWebAuthnChallengeService.ChallengeRequest? capturedChallengeRequest = null;
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedChallengeRequest = req)
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "cross-org-options-session",
                    Challenge = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("auth-challenge")),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act: Organization 1 の Client で、Organization 2 のユーザーの b2b_subject を指定
            var result = await _service.CreateAuthenticationOptionsAsync(
                new IB2BPasskeyService.AuthenticationOptionsRequest
                {
                    ClientId = "test-client-id",
                    RpId = "shop.example.com",
                    B2BSubject = TestB2BSubject2
                });

            // Assert: 他 Organization のクレデンシャルは一切返さない
            Assert.Empty(result.Options.AllowCredentials!);
            Assert.NotNull(capturedChallengeRequest);
            Assert.Empty(capturedChallengeRequest.AllowedCredentialIds!);
        }

        /// <summary>
        /// b2b_subject 指定経路は、発行元が別 Client のクレデンシャルを候補から外す
        /// （EcAuthDocs#110 問題 4 / リリース 3〜4）。同一ドメイン同居では RP ID が一致するため、
        /// 絞らないと別アプリの管理者パスキーがログイン候補に出る。
        /// </summary>
        [Fact]
        public async Task CreateAuthenticationOptionsAsync_WithSubject_ShouldExcludeCredentialsOfAnotherIssuer()
        {
            // Arrange: 同一ユーザーに 2 種類のクレデンシャル（自 Client / 別 Client）
            var otherClient = new Client
            {
                Id = 2,
                ClientId = "other-client-id",
                ClientSecret = "other-secret",
                AppName = "同一組織の別アプリ",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com" }
            };
            _context.Clients.Add(otherClient);

            var ownCredentialId = Encoding.UTF8.GetBytes("own-issuer-credential");
            var otherCredentialId = Encoding.UTF8.GetBytes("other-issuer-credential");
            _context.B2BPasskeyCredentials.AddRange(
                NewCredential(TestB2BSubject, ownCredentialId, clientId: 1),
                NewCredential(TestB2BSubject, otherCredentialId, clientId: otherClient.Id));
            await _context.SaveChangesAsync();

            IWebAuthnChallengeService.ChallengeRequest? capturedChallengeRequest = null;
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedChallengeRequest = req)
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "issuer-filter-session",
                    Challenge = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("auth-challenge")),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(
                new IB2BPasskeyService.AuthenticationOptionsRequest
                {
                    ClientId = "test-client-id",
                    RpId = "shop.example.com",
                    B2BSubject = TestB2BSubject
                });

            // Assert: 自 Client 発行のみが候補に入る
            var issued = result.Options.AllowCredentials!
                .Select(c => WebEncoders.Base64UrlEncode(c.Id))
                .ToList();
            Assert.Equal(new[] { WebEncoders.Base64UrlEncode(ownCredentialId) }, issued);

            // チャレンジへの束縛も同じ一覧であること
            Assert.NotNull(capturedChallengeRequest);
            Assert.Equal(issued, capturedChallengeRequest.AllowedCredentialIds);
        }

        /// <summary>
        /// b2b_subject 未指定経路（Organization 内の全ユーザーを候補にする経路。#110 問題 4 の本丸）
        /// でも、発行元が別 Client のクレデンシャルを候補から外す。
        /// </summary>
        [Fact]
        public async Task CreateAuthenticationOptionsAsync_WithoutSubject_ShouldExcludeCredentialsOfAnotherIssuer()
        {
            // Arrange: 同一 Organization の別ユーザーが、別 Client 発行のクレデンシャルを持つ
            var otherClient = new Client
            {
                Id = 2,
                ClientId = "other-client-id",
                ClientSecret = "other-secret",
                AppName = "同一ドメインに同居する別アプリ",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com" }
            };
            _context.Clients.Add(otherClient);

            _context.B2BUsers.Add(new B2BUser
            {
                Subject = TestB2BSubject2,
                UserType = "staff",
                OrganizationId = 1,
                Organization = _organization
            });

            var ownCredentialId = Encoding.UTF8.GetBytes("eccube-admin-credential");
            var otherCredentialId = Encoding.UTF8.GetBytes("wordpress-admin-credential");
            _context.B2BPasskeyCredentials.AddRange(
                NewCredential(TestB2BSubject, ownCredentialId, clientId: 1),
                NewCredential(TestB2BSubject2, otherCredentialId, clientId: otherClient.Id));
            await _context.SaveChangesAsync();

            IWebAuthnChallengeService.ChallengeRequest? capturedChallengeRequest = null;
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .Callback<IWebAuthnChallengeService.ChallengeRequest>(req => capturedChallengeRequest = req)
                .ReturnsAsync(new IWebAuthnChallengeService.ChallengeResult
                {
                    SessionId = "issuer-filter-session-2",
                    Challenge = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("auth-challenge")),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(
                new IB2BPasskeyService.AuthenticationOptionsRequest
                {
                    ClientId = "test-client-id",
                    RpId = "shop.example.com",
                    B2BSubject = null
                });

            // Assert: 別アプリの管理者パスキーは候補に出ない
            var issued = result.Options.AllowCredentials!
                .Select(c => WebEncoders.Base64UrlEncode(c.Id))
                .ToList();
            Assert.Equal(new[] { WebEncoders.Base64UrlEncode(ownCredentialId) }, issued);
            Assert.NotNull(capturedChallengeRequest);
            Assert.Equal(issued, capturedChallengeRequest.AllowedCredentialIds);
        }

        /// <summary>
        /// Organization 未設定の Client は Organization スコープを判定できないため、
        /// options 発行前に拒否する（登録側 / verify 側と同じ扱い）。
        /// </summary>
        [Fact]
        public async Task CreateAuthenticationOptionsAsync_ClientWithoutOrganization_ShouldThrowInvalidOperationException()
        {
            // Arrange
            _context.Clients.Add(new Client
            {
                Id = 3,
                ClientId = "orphan-client-id",
                ClientSecret = "orphan-secret",
                AppName = "Organization 未設定クライアント",
                OrganizationId = null,
                AllowedRpIds = new List<string> { "shop.example.com" }
            });
            await _context.SaveChangesAsync();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateAuthenticationOptionsAsync(
                    new IB2BPasskeyService.AuthenticationOptionsRequest
                    {
                        ClientId = "orphan-client-id",
                        RpId = "shop.example.com",
                        B2BSubject = null
                    }));
            Assert.Contains("no associated Organization", exception.Message);

            // チャレンジは発行されないこと
            _mockChallengeService.Verify(
                x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()),
                Times.Never);
        }

        #endregion

        #region VerifyAuthenticationAsync Tests

        [Fact]
        public async Task VerifyAuthenticationAsync_ValidRequest_ShouldReturnSuccessAndUpdateSignCount()
        {
            // Arrange
            var credentialIdBytes = Encoding.UTF8.GetBytes("auth-credential-id");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = credentialIdBytes,
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 5,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var challenge = new WebAuthnChallenge
            {
                SessionId = "auth-session",
                Challenge = "YXV0aC1jaGFsbGVuZ2U", // Base64URL of "auth-challenge"
                Type = "authentication",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("auth-session"))
                .ReturnsAsync(challenge);

            var assertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                RawId = credentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                    Signature = Encoding.UTF8.GetBytes("signature")
                }
            };

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "auth-session",
                ClientId = "test-client-id",
                AssertionResponse = assertionResponse
            };

            var verifyResult = new VerifyAssertionResult
            {
                SignCount = 6
            };

            _mockFido2.Setup(x => x.MakeAssertionAsync(
                It.IsAny<MakeAssertionParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(verifyResult);

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync("auth-session"))
                .ReturnsAsync(true);

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.Equal(TestB2BSubject, result.B2BSubject);
            Assert.NotNull(result.CredentialId);

            // SignCountが更新されていることを確認
            var updated = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == TestB2BSubject);
            Assert.NotNull(updated);
            Assert.Equal(6u, updated.SignCount);
            Assert.NotNull(updated.LastUsedAt);
        }

        [Fact]
        public async Task VerifyAuthenticationAsync_NonExistingSession_ShouldReturnFailure()
        {
            // Arrange
            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("non-existing"))
                .ReturnsAsync((WebAuthnChallenge?)null);

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "non-existing",
                ClientId = "test-client-id",
                AssertionResponse = new AuthenticatorAssertionRawResponse()
            };

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Session", result.ErrorMessage);
        }

        [Fact]
        public async Task VerifyAuthenticationAsync_CredentialNotFound_ShouldReturnFailure()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                SessionId = "auth-session",
                Challenge = "YXV0aC1jaGFsbGVuZ2U",
                Type = "authentication",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("auth-session"))
                .ReturnsAsync(challenge);

            var unknownCredentialIdBytes = Encoding.UTF8.GetBytes("unknown-credential");
            var assertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(unknownCredentialIdBytes),
                RawId = unknownCredentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                    Signature = Encoding.UTF8.GetBytes("signature")
                }
            };

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "auth-session",
                ClientId = "test-client-id",
                AssertionResponse = assertionResponse
            };

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Credential", result.ErrorMessage);
        }

        [Fact]
        public async Task VerifyAuthenticationAsync_NullSubjectChallenge_ShouldIdentifyUserByCredentialId()
        {
            // Arrange: Discoverable Credentials フロー
            // authenticate/options で B2BSubject を指定しなかった場合、
            // challenge.Subject は null になる。
            // VerifyAuthenticationAsync は CredentialId からユーザーを特定する。
            var credentialIdBytes = Encoding.UTF8.GetBytes("discoverable-credential-id");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = credentialIdBytes,
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 3,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var challenge = new WebAuthnChallenge
            {
                SessionId = "discoverable-session",
                Challenge = "ZGlzY292ZXJhYmxl", // Base64URL of "discoverable"
                Type = "authentication",
                UserType = "b2b",
                Subject = null, // Discoverable Credentials: Subject なし
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("discoverable-session"))
                .ReturnsAsync(challenge);

            var assertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                RawId = credentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                    Signature = Encoding.UTF8.GetBytes("signature")
                }
            };

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "discoverable-session",
                ClientId = "test-client-id",
                AssertionResponse = assertionResponse
            };

            var verifyResult = new VerifyAssertionResult
            {
                SignCount = 4
            };

            _mockFido2.Setup(x => x.MakeAssertionAsync(
                It.IsAny<MakeAssertionParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(verifyResult);

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync("discoverable-session"))
                .ReturnsAsync(true);

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert: challenge.Subject が null でも CredentialId からユーザーを特定できる
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.Equal(TestB2BSubject, result.B2BSubject);
            Assert.NotNull(result.CredentialId);

            // SignCount が更新されていること
            var updated = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialIdBytes);
            Assert.NotNull(updated);
            Assert.Equal(4u, updated.SignCount);
        }

        [Fact]
        public async Task VerifyAuthenticationAsync_SignCountDecreased_ShouldReturnError()
        {
            // Arrange
            // クローン攻撃シミュレーション: 前回のSignCount(10)より小さい値が返された場合
            var credentialIdBytes = Encoding.UTF8.GetBytes("cloned-credential-id");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = credentialIdBytes,
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 10, // 前回のSignCount
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var challenge = new WebAuthnChallenge
            {
                SessionId = "clone-attack-session",
                Challenge = "Y2xvbmUtY2hhbGxlbmdl", // Base64URL of "clone-challenge"
                Type = "authentication",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("clone-attack-session"))
                .ReturnsAsync(challenge);

            var assertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                RawId = credentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                    Signature = Encoding.UTF8.GetBytes("signature")
                }
            };

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "clone-attack-session",
                ClientId = "test-client-id",
                AssertionResponse = assertionResponse
            };

            // Fido2.NetLibがSignCount異常を検出して例外をスロー
            _mockFido2.Setup(x => x.MakeAssertionAsync(
                It.IsAny<MakeAssertionParams>(),
                It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Fido2VerificationException("Signature counter was not greater than stored value"));

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            // SignCount異常（クローン攻撃の可能性）としてエラーが返されること
            Assert.Contains("Signature counter", result.ErrorMessage);
        }

        #endregion

        #region WebAuthn §7.2 Step 5 / Step 6 検証 Tests

        /// <summary>
        /// WebAuthn Level 3 §7.2 Step 5: allowCredentials が空でない場合、assertion の
        /// credential.id がその一覧に含まれることを検証する。
        /// challenge.Subject を null にして Step 6 と Organization 検証では捕まらない状況を作り、
        /// Step 5 単独の効果を確認する。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_CredentialNotInIssuedAllowCredentials_ShouldReturnFailure()
        {
            // Arrange: 同一ユーザーの 2 本のクレデンシャル。発行した allowCredentials には
            // 1 本目しか入っていないが、assertion は 2 本目で返ってくる。
            var issuedCredentialId = Encoding.UTF8.GetBytes("issued-credential");
            var notIssuedCredentialId = Encoding.UTF8.GetBytes("not-issued-credential");

            _context.B2BPasskeyCredentials.AddRange(
                NewCredential(TestB2BSubject, issuedCredentialId),
                NewCredential(TestB2BSubject, notIssuedCredentialId));
            await _context.SaveChangesAsync();

            var challenge = NewAuthenticationChallenge("step5-session", subject: null);
            challenge.AllowedCredentialIds = new List<string>
            {
                WebEncoders.Base64UrlEncode(issuedCredentialId)
            };
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", notIssuedCredentialId));

            // Assert
            Assert.False(result.Success);
            Assert.Contains("not allowed", result.ErrorMessage);
        }

        /// <summary>
        /// 発行した allowCredentials に含まれるクレデンシャルは Step 5 を通過する。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_CredentialInIssuedAllowCredentials_ShouldSucceed()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("allowed-credential");
            _context.B2BPasskeyCredentials.Add(NewCredential(TestB2BSubject, credentialId));
            await _context.SaveChangesAsync();

            var challenge = NewAuthenticationChallenge("step5-ok-session", subject: null);
            challenge.AllowedCredentialIds = new List<string> { WebEncoders.Base64UrlEncode(credentialId) };
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", credentialId));

            // Assert
            Assert.True(result.Success);
            Assert.Equal(TestB2BSubject, result.B2BSubject);
        }

        /// <summary>
        /// allowCredentials を空で発行したセッション（discoverable credential フロー）では
        /// Step 5 の「空でない場合」という前提を満たさないため照合しない。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_EmptyIssuedAllowCredentials_ShouldSkipStep5()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("discoverable-only-credential");
            _context.B2BPasskeyCredentials.Add(NewCredential(TestB2BSubject, credentialId));
            await _context.SaveChangesAsync();

            var challenge = NewAuthenticationChallenge("step5-empty-session", subject: null);
            challenge.AllowedCredentialIds = new List<string>();
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", credentialId));

            // Assert
            Assert.True(result.Success);
            Assert.Equal(TestB2BSubject, result.B2BSubject);
        }

        /// <summary>
        /// allowed_credential_ids カラム追加前に発行された既存セッション（NULL）では
        /// Step 5 を適用できないため照合しない（マイグレーション直後の 5 分間の互換性）。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_AllowedCredentialIdsNotRecorded_ShouldSkipStep5()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("legacy-session-credential");
            _context.B2BPasskeyCredentials.Add(NewCredential(TestB2BSubject, credentialId));
            await _context.SaveChangesAsync();

            var challenge = NewAuthenticationChallenge("legacy-session", subject: null);
            Assert.Null(challenge.AllowedCredentialIdsJson); // 未記録であることを明示
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", credentialId));

            // Assert
            Assert.True(result.Success);
        }

        /// <summary>
        /// WebAuthn Level 3 §7.2 Step 6: options 発行時にユーザーが確定していた場合
        /// （b2b_subject 指定経路 = challenge.Subject が非 null）、そのユーザーの
        /// クレデンシャルであることを検証する。登録側の ExpectedSubject 突合と対称。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_CredentialOfAnotherSubject_ShouldReturnFailure()
        {
            // Arrange: 同一 Organization の別ユーザーのクレデンシャル
            var otherUser = new B2BUser
            {
                Id = 2,
                Subject = TestB2BSubject2,
                UserType = "staff",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(otherUser);

            var credentialId = Encoding.UTF8.GetBytes("other-subject-credential");
            _context.B2BPasskeyCredentials.Add(NewCredential(TestB2BSubject2, credentialId));
            await _context.SaveChangesAsync();

            // challenge.Subject は TestB2BSubject（別人）。Step 5 は未記録にして Step 6 単独の効果を見る。
            var challenge = NewAuthenticationChallenge("step6-session", subject: TestB2BSubject);
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", credentialId));

            // Assert
            Assert.False(result.Success);
            Assert.Contains("session subject", result.ErrorMessage);
        }

        /// <summary>
        /// 同一 rp_id を共有する別 Organization のクレデンシャルでは認証を通さない。
        ///
        /// 同一ドメインで本番サイトとサンドボックスサイトの両方を申し込んだ場合、両者は
        /// 別 Organization だが allowed_rp_ids が同じ値になりうる。rpIdHash の検証は
        /// challenge.RpId に基づくため assertion 自体は成立してしまい、Organization
        /// 検証がなければ本番 / サンドボックスの分離が破れる。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_CredentialFromAnotherOrganization_ShouldReturnFailure()
        {
            // Arrange: 同一テナント内の別 Organization（サンドボックス相当）
            var sandboxOrganization = new Organization
            {
                Id = 2,
                Code = "test-org-sandbox",
                Name = "テスト組織（サンドボックス）",
                TenantName = "test-tenant"
            };
            _context.Organizations.Add(sandboxOrganization);

            var sandboxUser = new B2BUser
            {
                Id = 2,
                Subject = TestB2BSubject2,
                UserType = "admin",
                OrganizationId = 2,
                Organization = sandboxOrganization
            };
            _context.B2BUsers.Add(sandboxUser);

            var credentialId = Encoding.UTF8.GetBytes("sandbox-credential");
            _context.B2BPasskeyCredentials.Add(NewCredential(TestB2BSubject2, credentialId));
            await _context.SaveChangesAsync();

            // Step 5 / Step 6 では捕まらない状況（未記録 + ユーザー未確定）で Organization 検証を見る
            var challenge = NewAuthenticationChallenge("cross-org-session", subject: null);
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act: 本番 Client（Organization 1）のセッションでサンドボックスのクレデンシャルを提示
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", credentialId));

            // Assert
            Assert.False(result.Success);
            Assert.Contains("organization", result.ErrorMessage);
        }

        /// <summary>
        /// 兄弟 Client が発行したクレデンシャルは、allowCredentials が空のセッション
        /// （discoverable credential フロー）で提示されても認証を通さない
        /// （EcAuthDocs#110 問題 4 / リリース 3）。
        ///
        /// options 側の絞り込みだけでは、絞り込んだ結果が空になった場合に WebAuthn の
        /// 「制限なしフロー」になり、§7.2 Step 5 の照合も空の場合は適用されないため
        /// 素通りする。同一 Organization なので Organization 検証でも捕まらない。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_CredentialIssuedByAnotherClient_ShouldReturnFailure()
        {
            // Arrange: 同一 Organization・同一 RP ID の別 Client（同一ドメインに同居する WordPress 等）
            var siblingClient = new Client
            {
                Id = 2,
                ClientId = "sibling-client-id",
                ClientSecret = "sibling-secret",
                AppName = "同一ドメインに同居する別アプリ",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com" }
            };
            _context.Clients.Add(siblingClient);

            var credentialId = Encoding.UTF8.GetBytes("sibling-issued-credential");
            _context.B2BPasskeyCredentials.Add(
                NewCredential(TestB2BSubject, credentialId, clientId: siblingClient.Id));
            await _context.SaveChangesAsync();

            // Step 5 / Step 6 では捕まらない状況（allowCredentials 空 + ユーザー未確定）を作る
            var challenge = NewAuthenticationChallenge("sibling-issuer-session", subject: null);
            challenge.AllowedCredentialIds = new List<string>();
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act: 自 Client（Id=1）のセッションで、兄弟 Client 発行のクレデンシャルを提示
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", credentialId));

            // Assert
            Assert.False(result.Success);
            Assert.Equal("Credential was not issued for this client", result.ErrorMessage);
        }

        /// <summary>
        /// 別 Client が発行したセッションを自分の client_id で verify に持ち込めない。
        /// コントローラーは request.ClientId で Client を認証するが、セッションが
        /// その Client のものであることは検証していないため、サービス側で突合する。
        /// </summary>
        [Fact]
        public async Task VerifyAuthenticationAsync_SessionIssuedForAnotherClient_ShouldReturnFailure()
        {
            // Arrange: 同一 Organization 内の別 Client が発行したセッション
            var otherClient = new Client
            {
                Id = 2,
                ClientId = "other-client-id",
                ClientSecret = "other-secret",
                AppName = "別クライアント",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com" }
            };
            _context.Clients.Add(otherClient);

            var credentialId = Encoding.UTF8.GetBytes("cross-client-credential");
            _context.B2BPasskeyCredentials.Add(NewCredential(TestB2BSubject, credentialId));
            await _context.SaveChangesAsync();

            var challenge = NewAuthenticationChallenge("cross-client-session", subject: null, clientId: 2);
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act: Client 2 のセッションを Client 1 の client_id で verify する
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "test-client-id", credentialId));

            // Assert
            Assert.False(result.Success);
            Assert.Contains("does not belong to this client", result.ErrorMessage);
        }

        [Fact]
        public async Task VerifyAuthenticationAsync_UnknownClient_ShouldReturnFailure()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("unknown-client-credential");
            _context.B2BPasskeyCredentials.Add(NewCredential(TestB2BSubject, credentialId));
            await _context.SaveChangesAsync();

            var challenge = NewAuthenticationChallenge("unknown-client-session", subject: null);
            SetupChallenge(challenge);
            SetupSuccessfulAssertion(challenge.SessionId, signCount: 1);

            // Act
            var result = await _service.VerifyAuthenticationAsync(
                NewVerifyRequest(challenge.SessionId, "unknown-client-id", credentialId));

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Client not found", result.ErrorMessage);
        }

        #endregion

        #region §7.2 検証テスト用ヘルパー

        private B2BPasskeyCredential NewCredential(string b2bSubject, byte[] credentialId, int clientId = 1) =>
            new B2BPasskeyCredential
            {
                B2BSubject = b2bSubject,
                CredentialId = credentialId,
                ClientId = clientId,
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            };

        private WebAuthnChallenge NewAuthenticationChallenge(string sessionId, string? subject, int clientId = 1) =>
            new WebAuthnChallenge
            {
                SessionId = sessionId,
                Challenge = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("auth-challenge")),
                Type = "authentication",
                UserType = "b2b",
                Subject = subject,
                RpId = "shop.example.com",
                ClientId = clientId,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

        private void SetupChallenge(WebAuthnChallenge challenge) =>
            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync(challenge.SessionId))
                .ReturnsAsync(challenge);

        /// <summary>
        /// Fido2 の assertion 検証自体は成功する状態にする。これにより、テストが失敗した場合の
        /// 原因が §7.2 の追加検証であることを切り分けられる。
        /// </summary>
        private void SetupSuccessfulAssertion(string sessionId, uint signCount)
        {
            _mockFido2.Setup(x => x.MakeAssertionAsync(
                It.IsAny<MakeAssertionParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VerifyAssertionResult { SignCount = signCount });

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync(sessionId))
                .ReturnsAsync(true);
        }

        private static IB2BPasskeyService.AuthenticationVerifyRequest NewVerifyRequest(
            string sessionId, string clientId, byte[] credentialId) =>
            new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = sessionId,
                ClientId = clientId,
                AssertionResponse = new AuthenticatorAssertionRawResponse
                {
                    Id = WebEncoders.Base64UrlEncode(credentialId),
                    RawId = credentialId,
                    Type = PublicKeyCredentialType.PublicKey,
                    Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                    {
                        AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                        ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                        Signature = Encoding.UTF8.GetBytes("signature")
                    }
                }
            };

        #endregion

        #region GetCredentialsBySubjectAsync Tests

        [Fact]
        public async Task GetCredentialsBySubjectAsync_WithCredentials_ShouldReturnList()
        {
            // Arrange
            var credentials = new[]
            {
                new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes("cred-1"),
                    PublicKey = Encoding.UTF8.GetBytes("key-1"),
                    SignCount = 5,
                    DeviceName = "MacBook Pro",
                    AaGuid = Guid.NewGuid(),
                    Transports = new[] { "internal" },
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                    LastUsedAt = DateTimeOffset.UtcNow.AddDays(-1)
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes("cred-2"),
                    PublicKey = Encoding.UTF8.GetBytes("key-2"),
                    SignCount = 3,
                    DeviceName = "iPhone",
                    AaGuid = Guid.NewGuid(),
                    Transports = new[] { "internal", "hybrid" },
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-5)
                }
            };
            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetCredentialsBySubjectAsync(TestB2BSubject);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Contains(result, c => c.DeviceName == "MacBook Pro");
            Assert.Contains(result, c => c.DeviceName == "iPhone");
        }

        [Fact]
        public async Task GetCredentialsBySubjectAsync_NoCredentials_ShouldReturnEmptyList()
        {
            // Act
            var result = await _service.GetCredentialsBySubjectAsync("user-without-credentials");

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetCredentialsBySubjectAsync_InvalidSubject_ShouldReturnEmptyList(string? subject)
        {
            // Act
            var result = await _service.GetCredentialsBySubjectAsync(subject!);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        #endregion

        #region DeleteCredentialAsync Tests

        [Fact]
        public async Task DeleteCredentialAsync_ExistingCredential_ShouldDeleteAndReturnTrue()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("delete-cred");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = credentialId,
                PublicKey = Encoding.UTF8.GetBytes("key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var credentialIdBase64 = WebEncoders.Base64UrlEncode(credentialId);

            // Act
            var result = await _service.DeleteCredentialAsync(TestB2BSubject, credentialIdBase64);

            // Assert
            Assert.True(result);

            var deleted = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialId);
            Assert.Null(deleted);
        }

        [Fact]
        public async Task DeleteCredentialAsync_NonExistingCredential_ShouldReturnFalse()
        {
            // Act
            var result = await _service.DeleteCredentialAsync(TestB2BSubject, "non-existing-cred-id");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task DeleteCredentialAsync_WrongSubject_ShouldReturnFalse()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("other-user-cred");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = "other-user-subject",
                ClientId = 1,
                CredentialId = credentialId,
                PublicKey = Encoding.UTF8.GetBytes("key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var credentialIdBase64 = WebEncoders.Base64UrlEncode(credentialId);

            // Act
            var result = await _service.DeleteCredentialAsync(TestB2BSubject, credentialIdBase64);

            // Assert
            Assert.False(result);

            // 他のユーザーのクレデンシャルは削除されていない
            var notDeleted = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == "other-user-subject");
            Assert.NotNull(notDeleted);
        }

        #endregion

        #region CountCredentialsBySubjectAsync Tests

        [Fact]
        public async Task CountCredentialsBySubjectAsync_WithCredentials_ShouldReturnCount()
        {
            // Arrange
            var credentials = new[]
            {
                new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes("count-cred-1"),
                    PublicKey = Encoding.UTF8.GetBytes("key-1"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes("count-cred-2"),
                    PublicKey = Encoding.UTF8.GetBytes("key-2"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes("count-cred-3"),
                    PublicKey = Encoding.UTF8.GetBytes("key-3"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                }
            };
            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.CountCredentialsBySubjectAsync(TestB2BSubject);

            // Assert
            Assert.Equal(3, result);
        }

        [Fact]
        public async Task CountCredentialsBySubjectAsync_NoCredentials_ShouldReturnZero()
        {
            // Act
            var result = await _service.CountCredentialsBySubjectAsync("user-without-creds");

            // Assert
            Assert.Equal(0, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task CountCredentialsBySubjectAsync_InvalidSubject_ShouldReturnZero(string? subject)
        {
            // Act
            var result = await _service.CountCredentialsBySubjectAsync(subject!);

            // Assert
            Assert.Equal(0, result);
        }

        #endregion

        #region Performance Tests

        [Fact]
        public async Task VerifyAuthenticationAsync_WithManyCredentials_ShouldPerformEfficientQuery()
        {
            // Arrange: 大量のクレデンシャルを作成（100個）
            var targetCredentialId = Encoding.UTF8.GetBytes("target-credential");
            var credentials = new List<B2BPasskeyCredential>();

            // 99個のダミークレデンシャル
            for (int i = 0; i < 99; i++)
            {
                credentials.Add(new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes($"credential-{i}"),
                    PublicKey = Encoding.UTF8.GetBytes($"key-{i}"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                });
            }

            // ターゲットのクレデンシャル
            credentials.Add(new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = targetCredentialId,
                PublicKey = Encoding.UTF8.GetBytes("target-key"),
                SignCount = 10,
                AaGuid = Guid.NewGuid()
            });

            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            var challenge = new WebAuthnChallenge
            {
                SessionId = "perf-session",
                Challenge = "cGVyZi1jaGFsbGVuZ2U",
                Type = "authentication",
                UserType = "b2b",
                Subject = TestB2BSubject,
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("perf-session"))
                .ReturnsAsync(challenge);

            var assertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(targetCredentialId),
                RawId = targetCredentialId,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                    Signature = Encoding.UTF8.GetBytes("signature")
                }
            };

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "perf-session",
                ClientId = "test-client-id",
                AssertionResponse = assertionResponse
            };

            var verifyResult = new VerifyAssertionResult
            {
                SignCount = 11
            };

            _mockFido2.Setup(x => x.MakeAssertionAsync(
                It.IsAny<MakeAssertionParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(verifyResult);

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync("perf-session"))
                .ReturnsAsync(true);

            // Act: パフォーマンステスト（DB側でフィルタリングされることを期待）
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await _service.VerifyAuthenticationAsync(request);
            stopwatch.Stop();

            // Assert
            Assert.True(result.Success);
            Assert.Equal(TestB2BSubject, result.B2BSubject);

            // パフォーマンス検証: 100個のクレデンシャルでも高速に処理できること
            // （DB側でフィルタリングされるため、1秒以内に完了する想定）
            Assert.True(stopwatch.ElapsedMilliseconds < 1000,
                $"Query took {stopwatch.ElapsedMilliseconds}ms, expected < 1000ms");

            // SignCountが正しく更新されていること
            var updated = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == targetCredentialId);
            Assert.NotNull(updated);
            Assert.Equal(11u, updated.SignCount);
        }

        [Fact]
        public async Task DeleteCredentialAsync_WithManyCredentials_ShouldPerformEfficientQuery()
        {
            // Arrange: 大量のクレデンシャルを作成（50個）
            var targetCredentialId = Encoding.UTF8.GetBytes("delete-target");
            var credentials = new List<B2BPasskeyCredential>();

            // 49個のダミークレデンシャル
            for (int i = 0; i < 49; i++)
            {
                credentials.Add(new B2BPasskeyCredential
                {
                    B2BSubject = TestB2BSubject,
                    ClientId = 1,
                    CredentialId = Encoding.UTF8.GetBytes($"delete-cred-{i}"),
                    PublicKey = Encoding.UTF8.GetBytes($"key-{i}"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                });
            }

            // 削除対象のクレデンシャル
            credentials.Add(new B2BPasskeyCredential
            {
                B2BSubject = TestB2BSubject,
                ClientId = 1,
                CredentialId = targetCredentialId,
                PublicKey = Encoding.UTF8.GetBytes("target-key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            });

            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            var credentialIdBase64 = WebEncoders.Base64UrlEncode(targetCredentialId);

            // Act: パフォーマンステスト（DB側でフィルタリングされることを期待）
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await _service.DeleteCredentialAsync(TestB2BSubject, credentialIdBase64);
            stopwatch.Stop();

            // Assert
            Assert.True(result);

            // パフォーマンス検証: 50個のクレデンシャルでも高速に処理できること
            Assert.True(stopwatch.ElapsedMilliseconds < 500,
                $"Query took {stopwatch.ElapsedMilliseconds}ms, expected < 500ms");

            // 削除されていることを確認
            var deleted = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == targetCredentialId);
            Assert.Null(deleted);

            // 他のクレデンシャルは削除されていないことを確認
            var remaining = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .CountAsync(c => c.B2BSubject == TestB2BSubject);
            Assert.Equal(49, remaining);
        }

        #endregion

        #region Multi-Tenant Tests

        [Fact]
        public async Task CreateRegistrationOptionsAsync_DifferentOrganizations_ShouldUseCorrectClient()
        {
            // Arrange: 2つ目のOrganizationとClientを作成
            var org2 = new Organization
            {
                Id = 2,
                Code = "org2-code",
                Name = "第二組織",
                TenantName = "org2-tenant"
            };
            _context.Organizations.Add(org2);

            var client2 = new Client
            {
                Id = 2,
                ClientId = "client2-id",
                ClientSecret = "client2-secret",
                AppName = "第二クライアント",
                OrganizationId = 2,
                Organization = org2,
                AllowedRpIds = new List<string> { "shop2.example.com" }
            };
            _context.Clients.Add(client2);

            var user2 = new B2BUser
            {
                Id = 2,
                Subject = TestB2BSubject2,
                UserType = "admin",
                OrganizationId = 2,
                Organization = org2
            };
            _context.B2BUsers.Add(user2);
            await _context.SaveChangesAsync();

            var request1 = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id", // Organization 1
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                DisplayName = "管理者1",
                ExternalId = "admin@example.com"
            };

            var request2 = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "client2-id", // Organization 2
                RpId = "shop2.example.com",
                B2BSubject = TestB2BSubject2,
                DisplayName = "管理者2",
                ExternalId = "admin2@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);
            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject2))
                .ReturnsAsync(user2);

            var challengeResult1 = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-org1",
                Challenge = "Y2hhbGxlbmdlMQ", // Base64URL of "challenge1"
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            var challengeResult2 = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-org2",
                Challenge = "Y2hhbGxlbmdlMg", // Base64URL of "challenge2"
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.SetupSequence(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult1)
                .ReturnsAsync(challengeResult2);

            // 注: _fido2.RequestNewCredentialは使用しなくなったため、モック設定は不要

            // Act
            var result1 = await _service.CreateRegistrationOptionsAsync(request1);
            var result2 = await _service.CreateRegistrationOptionsAsync(request2);

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);

            // 異なるOrganizationのクライアントで登録オプションが生成されること
            Assert.Equal("session-org1", result1.SessionId);
            Assert.Equal("session-org2", result2.SessionId);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_OrganizationExists_ShouldSucceed()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = TestB2BSubject,
                DisplayName = "テスト管理者",
                ExternalId = "admin@example.com"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync(TestB2BSubject))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-123",
                Challenge = "dGVzdC1jaGFsbGVuZ2U", // Base64URL of "test-challenge"
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            var credentialCreateOptions = new CredentialCreateOptions
            {
                Challenge = Encoding.UTF8.GetBytes("test-challenge"),
                Rp = new PublicKeyCredentialRpEntity("shop.example.com", "テスト組織"),
                User = new Fido2User
                {
                    Id = Encoding.UTF8.GetBytes(TestB2BSubject),
                    Name = "admin@example.com",
                    DisplayName = "テスト管理者"
                },
                PubKeyCredParams = PubKeyCredParam.Defaults
            };
            _mockFido2.Setup(x => x.RequestNewCredential(It.IsAny<RequestNewCredentialParams>()))
                .Returns(credentialCreateOptions);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("session-123", result.SessionId);
            Assert.NotNull(result.Options);
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
