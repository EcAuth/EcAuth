using System.Runtime.CompilerServices;
using IdentityProvider.Controllers;
using IdentityProvider.Data;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Data
{
    /// <summary>
    /// EcAuth#536: <c>EnableRetryOnFailure</c> とユーザー開始トランザクションの整合。
    ///
    /// <para>
    /// 再試行戦略が有効な DbContext では、<c>CreateExecutionStrategy().ExecuteAsync</c> で包まれていない
    /// <c>BeginTransactionAsync</c> が実行時例外になる。InMemory プロバイダーではこれを再現できないため、
    /// <see cref="RetryingSqliteContext"/>（SQLite + <see cref="RetryingTestExecutionStrategy"/>）で
    /// 「トランザクションを張る経路が包まれていること」と「再試行時に追跡状態が正しくリセットされ、
    /// 二重 INSERT にならないこと」を検証する。今後 <c>BeginTransactionAsync</c> を新設したときの
    /// 包み忘れは、ここに同種のテストを足すことで CI が止める。
    /// </para>
    /// </summary>
    public class DatabaseResilienceTests : IDisposable
    {
        private const string Tenant = "accounts";
        private const int AccountsOrgId = 100;
        private const string AccountsClientId = "ecauth-admin-console-test";
        private const string AccountToken = "account-access-token";
        private const string AccountSubject = "account-subject-1";

        private readonly MockTenantService _tenantService;
        private RetryingSqliteContext _db;

        private EcAuthDbContext Context => _db.Context;

        public DatabaseResilienceTests()
        {
            _tenantService = new MockTenantService();
            _tenantService.SetTenant(Tenant);
            _db = new RetryingSqliteContext(_tenantService);
        }

        public void Dispose() => _db.Dispose();

        /// <summary>
        /// コミット成功直後に一過性エラーを報告するインターセプター付きのコンテキストへ差し替える。
        /// 返したインターセプターの <see cref="FailAfterCommitOnceInterceptor.Arm"/> を、シード後・検証対象の
        /// 直前に呼ぶ（シードの SaveChanges が暗黙トランザクションを張ることがあるため）。
        /// </summary>
        private FailAfterCommitOnceInterceptor UseCommitFailingContext()
        {
            _db.Dispose();
            var interceptor = new FailAfterCommitOnceInterceptor();
            _db = new RetryingSqliteContext(_tenantService, interceptor);
            return interceptor;
        }

        // ---- ヘルパー自体の妥当性（テストの検出力を担保する） ----

        [Fact]
        public async Task RetryingStrategy_RejectsUnwrappedUserTransaction()
        {
            // 包まれていないユーザー開始トランザクションは、本番の SqlServerRetryingExecutionStrategy と同じ
            // 例外になる。例外が出るのは BeginTransactionAsync 自体ではなく、トランザクション中に最初の
            // 操作（クエリ / SaveChanges）が ExecutionStrategy.OnFirstExecution を通った時点。
            // これが通らないと、以下の「包まれている」テストは何も検証していないことになる。
            await using var transaction = await Context.Database.BeginTransactionAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Context.Organizations.IgnoreQueryFilters().ToListAsync());

            Assert.Contains("does not support user-initiated transactions", ex.Message);
        }

        // ---- ExecuteInRetryableUnitAsync ----

        [Fact]
        public async Task ExecuteInRetryableUnitAsync_AllowsUserTransaction()
        {
            await Context.ExecuteInRetryableUnitAsync(async ct =>
            {
                await using var transaction = await Context.Database.BeginTransactionAsync(ct);
                Context.Organizations.Add(NewOrganization("wrapped"));
                await Context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return 0;
            });

            Assert.Single(await Context.Organizations.IgnoreQueryFilters().ToListAsync());
        }

        [Fact]
        public async Task ExecuteInRetryableUnitAsync_RetriesTransientFailure_WithEmptyTrackerOnEachAttempt()
        {
            var attempts = 0;
            var trackedAtStart = new List<int>();

            // 呼び出し前に未保存のエンティティを載せておく。各試行の先頭で ChangeTracker が空になることの確認用。
            Context.Organizations.Add(NewOrganization("stale"));

            var result = await Context.ExecuteInRetryableUnitAsync(async ct =>
            {
                attempts++;
                trackedAtStart.Add(Context.ChangeTracker.Entries().Count());

                await using var transaction = await Context.Database.BeginTransactionAsync(ct);
                Context.Organizations.Add(NewOrganization($"attempt-{attempts}"));
                await Context.SaveChangesAsync(ct);
                if (attempts == 1)
                {
                    // 1 回目は SaveChanges 後（コミット前）に一過性の失敗。トランザクションは Dispose でロールバックされる。
                    throw new TransientTestException();
                }
                await transaction.CommitAsync(ct);
                return attempts;
            });

            Assert.Equal(2, result);
            Assert.Equal(2, attempts);
            Assert.All(trackedAtStart, count => Assert.Equal(0, count));

            // 1 回目の INSERT はロールバック済みで、2 回目だけが残る（Added の持ち越しによる二重 INSERT が無い）。
            var codes = await Context.Organizations.IgnoreQueryFilters().Select(o => o.Code).ToListAsync();
            Assert.Equal(new[] { "attempt-2" }, codes);
        }

        [Fact]
        public async Task ExecuteInRetryableUnitAsync_DoesNotRetryNonTransientFailure()
        {
            var attempts = 0;

            await Assert.ThrowsAsync<InvalidOperationException>(() => Context.ExecuteInRetryableUnitAsync<int>(ct =>
            {
                attempts++;
                throw new InvalidOperationException("not transient");
            }));

            Assert.Equal(1, attempts);
        }

        // ---- BeginTransactionAsync を使う実経路が包まれていること ----

        [Fact]
        public async Task SignupService_ConfirmAsync_SucceedsUnderRetryingStrategy_WhenFirstAttemptFailsTransiently()
        {
            await SeedAccountsOrgWithConsoleClientAsync();

            // ProvisionAsync の途中（Organization を SaveChanges した後）で 1 回だけ一過性の失敗を起こす。
            // 再試行で ChangeTracker が空にされないと、ロールバック済みの Organization が Unchanged で残り、
            // 2 回目の SaveChanges が存在しない行への FK で失敗するか、Account が二重追跡になる。
            var protector = CreateProtectorFailingOnce(out var protectCalls);
            var service = CreateSignupService(protector.Object);
            var token = await RequestAndCaptureTokenAsync(service);

            var confirmed = await service.ConfirmAsync(token);

            Assert.Equal(2, protectCalls.Value);
            Assert.NotNull(confirmed.Request.ConfirmedAt);
            // 再試行前に ChangeTracker を空にしたうえで申込行を Attach し直しているため、更新が DB に届く。
            var stored = await Context.SignupRequests.AsNoTracking().SingleAsync();
            Assert.NotNull(stored.ConfirmedAt);
            // 1 回目の試行で作った Organization / Account はロールバックされ、二重化していない。
            Assert.Single(await Context.Organizations.IgnoreQueryFilters().Where(o => o.Code != Tenant).ToListAsync());
            Assert.Single(await Context.Accounts.IgnoreQueryFilters().ToListAsync());
            Assert.Single(await Context.B2BUserIdentities.IgnoreQueryFilters().ToListAsync());
        }

        [Fact]
        public async Task AccountController_CreateOrganization_SucceedsUnderRetryingStrategy_WhenFirstAttemptFailsTransiently()
        {
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            var protector = CreateProtectorFailingOnce(out var protectCalls);
            var controller = CreateController(protector.Object, out var accountServiceMock);
            AuthenticateAsOwnerOf(controller, accountServiceMock);

            var result = await controller.CreateOrganization(new AccountController.CreateOrganizationDto
            {
                SiteUrl = "https://shop.example.jp"
            });

            var created = Assert.IsType<CreatedResult>(result);
            Assert.Equal(2, protectCalls.Value);
            var organizations = await Context.Organizations.IgnoreQueryFilters()
                .Where(o => o.Code == "shop-example-jp").ToListAsync();
            Assert.Single(organizations);
            Assert.Single(await Context.AccountOrganizations.ToListAsync());
        }

        [Fact]
        public async Task AccountController_CreateOrganization_RetriesTransientFailureWrappedInDbUpdateException()
        {
            // SaveChangesAsync 中の一過性の接続断は DbUpdateException に包まれて届く。
            // CreateOrganization の catch (DbUpdateException) がユニーク制約違反以外まで握って 409 に
            // 変換すると、デリゲートが正常終了扱いになり再試行戦略が働かない（Codex / CodeRabbit の指摘）。
            // ExecutionStrategy は DbUpdateException の InnerException で再試行可否を判定するので、
            // 外へ逃がせば再試行される。
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            var protector = CreateProtectorFailingOnce(
                out var protectCalls,
                failure: () => new DbUpdateException("wrapped transient failure", new TransientTestException()));
            var controller = CreateController(protector.Object, out var accountServiceMock);
            AuthenticateAsOwnerOf(controller, accountServiceMock);

            var result = await controller.CreateOrganization(new AccountController.CreateOrganizationDto
            {
                SiteUrl = "https://shop.example.jp"
            });

            Assert.IsType<CreatedResult>(result);
            Assert.Equal(2, protectCalls.Value);
            Assert.Single(await Context.Organizations.IgnoreQueryFilters()
                .Where(o => o.Code == "shop-example-jp").ToListAsync());
        }

        [Fact]
        public async Task AccountController_DeleteOrganization_SucceedsUnderRetryingStrategy_WhenFirstAttemptFailsTransiently()
        {
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            Context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "shop1",
                Name = "shop1",
                TenantName = "shop1"
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            var controller = CreateController(new PlaintextSecretProtector(), out var accountServiceMock);
            var managed = new List<IAccountService.ManagedOrganization>
            {
                new(1, "shop1", "owner")
            };
            // 1 回目（トランザクション前の早期チェック）は成功、2 回目（行ロック後の引き直し）で一過性の失敗、
            // 再試行の 3 回目以降は成功。
            accountServiceMock.SetupSequence(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(managed)
                .ThrowsAsync(new TransientTestException())
                .ReturnsAsync(managed)
                .ReturnsAsync(managed);
            SetBearer(controller);

            var result = await controller.DeleteOrganization(1);

            var ok = Assert.IsType<OkObjectResult>(result);
            accountServiceMock.Verify(x => x.GetManagedOrganizationsAsync(AccountSubject), Times.Exactly(3));
            var organization = await Context.Organizations.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == 1);
            Assert.NotNull(organization.DeletedAt);
        }

        // ---- コミットは成功したが応答が失われ、再試行がやり直しになるケース（idempotency issue） ----

        [Fact]
        public async Task SignupService_ConfirmAsync_ReturnsSuccessWithReissuedToken_WhenCommitSucceedsButConnectionDrops()
        {
            // 1 回目の試行のコミットは DB 上では成功するが、その直後に一過性エラーが報告され、
            // 再試行戦略がデリゲートをやり直す。やり直しは email / 組織コードのユニーク制約で 409 になり、
            // 登録トークンの平文は失われる（Codex の指摘）。再試行時に前回の Account を認識し、
            // トークンを発行し直して成功として返すこと。
            var interceptor = UseCommitFailingContext();
            await SeedAccountsOrgWithConsoleClientAsync();
            var service = CreateSignupService(new PlaintextSecretProtector());
            var token = await RequestAndCaptureTokenAsync(service);
            interceptor.Arm();

            var confirmed = await service.ConfirmAsync(token);

            Assert.Equal(1, interceptor.FailedCommits);
            Assert.False(string.IsNullOrEmpty(confirmed.RegistrationToken));
            Assert.NotNull(confirmed.Request.ConfirmedAt);
            Assert.Single(await Context.Accounts.IgnoreQueryFilters().ToListAsync());
            Assert.Single(await Context.Organizations.IgnoreQueryFilters().Where(o => o.Code != Tenant).ToListAsync());
            // 1 回目のコミットで発行済みのトークンに加えて、再試行で発行し直したトークンが 1 件増える
            var subject = (await Context.Accounts.IgnoreQueryFilters().SingleAsync()).Subject;
            Assert.Equal(2, await Context.PasskeyRegistrationTokens.IgnoreQueryFilters().CountAsync(t => t.Subject == subject));
        }

        [Fact]
        public async Task AccountController_CreateOrganization_ReturnsCreated_WhenCommitSucceedsButConnectionDrops()
        {
            var interceptor = UseCommitFailingContext();
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            // 再試行時に「管理下に作成済みのサイトがある」ことを実 DB から読ませるため、実物の AccountService を使う
            var controller = CreateController(new PlaintextSecretProtector(), out _, useRealAccountService: true);
            SetBearer(controller);
            interceptor.Arm();

            var result = await controller.CreateOrganization(new AccountController.CreateOrganizationDto
            {
                SiteUrl = "https://shop.example.jp"
            });

            Assert.Equal(1, interceptor.FailedCommits);
            var created = Assert.IsType<CreatedResult>(result);
            var organizations = await Context.Organizations.IgnoreQueryFilters()
                .Where(o => o.Code == "shop-example-jp").ToListAsync();
            Assert.Single(organizations);
            Assert.Equal($"/v1/account/organizations/{organizations[0].Id}", created.Location);
        }

        [Fact]
        public async Task AccountController_AddClient_SucceedsUnderRetryingStrategy_WhenFirstAttemptFailsTransiently()
        {
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            await SeedOwnedOrganizationAsync(1, "shop1", isSandbox: false);
            var protector = CreateProtectorFailingOnce(out var protectCalls);
            var controller = CreateController(protector.Object, out _, useRealAccountService: true);
            SetBearer(controller);

            var result = await controller.AddClient(1, new AccountController.AddClientDto
            {
                SiteUrl = "https://wp.example.jp"
            });

            Assert.IsType<CreatedResult>(result);
            Assert.Equal(2, protectCalls.Value);
            // 再試行で二重 INSERT にならない（1 回目は secret 暗号化で落ちて Client は作られていない）。
            Assert.Single(await Context.Clients.IgnoreQueryFilters().Where(c => c.OrganizationId == 1).ToListAsync());
        }

        [Fact]
        public async Task AccountController_AddClient_ReturnsCreated_WhenCommitSucceedsButConnectionDrops()
        {
            var interceptor = UseCommitFailingContext();
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            await SeedOwnedOrganizationAsync(1, "shop1", isSandbox: false);
            var controller = CreateController(new PlaintextSecretProtector(), out _, useRealAccountService: true);
            SetBearer(controller);
            interceptor.Arm();

            var result = await controller.AddClient(1, new AccountController.AddClientDto
            {
                SiteUrl = "https://wp.example.jp",
                EcCubeVersion = "2"
            });

            Assert.Equal(1, interceptor.FailedCommits);
            var created = Assert.IsType<CreatedResult>(result);
            // 再試行は試行間で固定した client_id で前回の成果を見つけて返す。
            // やり直しで同じサイトの Client が 2 件できてはいけない。
            var clients = await Context.Clients.IgnoreQueryFilters().Include(c => c.RedirectUris)
                .Where(c => c.OrganizationId == 1).ToListAsync();
            Assert.Single(clients);
            Assert.Equal("https://wp.example.jp/ecauth/callback.php", clients[0].RedirectUris.Single().Uri);
            Assert.Equal(clients[0].ClientId,
                (string)created.Value!.GetType().GetProperty("client_id")!.GetValue(created.Value)!);
        }

        /// <summary>
        /// 再試行の判定を redirect_uri に頼ると起きる取り違えの再現（EcAuth#551 レビュー指摘）。
        /// 同じ Organization に同じ初期 redirect_uri の Client が既にある状態で、1 回目の試行が
        /// コミット前に一過性エラーで落ちた場合、再試行は既存 Client を「前回の成果」と誤認せず、
        /// 要求された Client を新たに作らなければならない。
        /// </summary>
        [Fact]
        public async Task AccountController_AddClient_CreatesNewClient_WhenSameRedirectUriAlreadyExistsAndFirstAttemptFailsBeforeCommit()
        {
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            await SeedOwnedOrganizationAsync(1, "shop1", isSandbox: false);
            const string existingClientId = "ec-shop1-existing";
            await SeedClientAsync(1, existingClientId, "https://wp.example.jp/ecauth/callback.php");
            var protector = CreateProtectorFailingOnce(out var protectCalls);
            var controller = CreateController(protector.Object, out _, useRealAccountService: true);
            SetBearer(controller);

            var result = await controller.AddClient(1, new AccountController.AddClientDto
            {
                SiteUrl = "https://wp.example.jp",
                EcCubeVersion = "2"
            });

            Assert.Equal(2, protectCalls.Value);
            var created = Assert.IsType<CreatedResult>(result);
            var createdClientId = (string)created.Value!.GetType().GetProperty("client_id")!.GetValue(created.Value)!;
            // 既存 Client を返していない。
            Assert.NotEqual(existingClientId, createdClientId);
            // 既存 1 件 + 今回の 1 件。redirect_uri が同じでも別 Client として増える。
            var clients = await Context.Clients.IgnoreQueryFilters().Include(c => c.RedirectUris)
                .Where(c => c.OrganizationId == 1).OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(2, clients.Count);
            Assert.Equal(existingClientId, clients[0].ClientId);
            Assert.Equal(createdClientId, clients[1].ClientId);
            Assert.All(clients, c => Assert.Equal("https://wp.example.jp/ecauth/callback.php", c.RedirectUris.Single().Uri));
        }

        /// <summary>
        /// コミット後の接続断でも、既存の同一 redirect_uri Client ではなく今回作った Client を返す。
        /// </summary>
        [Fact]
        public async Task AccountController_AddClient_ReturnsNewlyCreatedClient_WhenSameRedirectUriAlreadyExistsAndCommitSucceedsButConnectionDrops()
        {
            var interceptor = UseCommitFailingContext();
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            await SeedOwnedOrganizationAsync(1, "shop1", isSandbox: false);
            const string existingClientId = "ec-shop1-existing";
            await SeedClientAsync(1, existingClientId, "https://wp.example.jp/ecauth/callback.php");
            var controller = CreateController(new PlaintextSecretProtector(), out _, useRealAccountService: true);
            SetBearer(controller);
            interceptor.Arm();

            var result = await controller.AddClient(1, new AccountController.AddClientDto
            {
                SiteUrl = "https://wp.example.jp",
                EcCubeVersion = "2"
            });

            Assert.Equal(1, interceptor.FailedCommits);
            var created = Assert.IsType<CreatedResult>(result);
            var createdClientId = (string)created.Value!.GetType().GetProperty("client_id")!.GetValue(created.Value)!;
            Assert.NotEqual(existingClientId, createdClientId);
            var clients = await Context.Clients.IgnoreQueryFilters()
                .Where(c => c.OrganizationId == 1).OrderBy(c => c.Id).ToListAsync();
            // 既存 1 件 + 今回の 1 件。コミット済みの成果を二重化していない。
            Assert.Equal(2, clients.Count);
            Assert.Equal(createdClientId, clients[1].ClientId);
        }

        [Fact]
        public async Task AccountController_DeleteOrganization_ReturnsOk_WhenCommitSucceedsButConnectionDrops()
        {
            var interceptor = UseCommitFailingContext();
            await SeedAccountsOrgWithConsoleClientAsync();
            await SeedAccountAsync();
            await SeedOwnedOrganizationAsync(1, "shop1", isSandbox: false);
            await SeedOwnedOrganizationAsync(2, "shop1-sandbox", isSandbox: true, parentOrganizationId: 1);
            var controller = CreateController(new PlaintextSecretProtector(), out _, useRealAccountService: true);
            SetBearer(controller);
            interceptor.Arm();

            var result = await controller.DeleteOrganization(1);

            Assert.Equal(1, interceptor.FailedCommits);
            var ok = Assert.IsType<OkObjectResult>(result);
            var deletedIds = (int[])ok.Value!.GetType().GetProperty("deleted_organization_ids")!.GetValue(ok.Value)!;
            Assert.Equal(new[] { 1, 2 }, deletedIds.OrderBy(i => i).ToArray());
            var organizations = await Context.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Id == 1 || o.Id == 2).ToListAsync();
            Assert.All(organizations, o => Assert.NotNull(o.DeletedAt));
        }

        // ---- セットアップ ----

        private static Organization NewOrganization(string code) => new()
        {
            Code = code,
            Name = code,
            TenantName = code
        };

        /// <summary>
        /// 受付テナント Org（Code = TenantName）と管理コンソール Client（SubjectType.Account）を投入する。
        /// SignupService.ConfirmAsync は account_owner の identity 発行元として管理コンソール Client を要求する。
        /// </summary>
        private async Task SeedAccountsOrgWithConsoleClientAsync()
        {
            Context.Organizations.Add(new Organization
            {
                Id = AccountsOrgId,
                Code = Tenant,
                Name = "EcAuth Accounts",
                TenantName = Tenant
            });
            Context.Clients.Add(new Client
            {
                Id = 1,
                ClientId = AccountsClientId,
                ClientSecret = string.Empty,
                AppName = "EcAuth Accounts Console",
                OrganizationId = AccountsOrgId,
                SubjectType = SubjectType.Account
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        /// <summary>
        /// 管理下 Organization に既存の B2B Client を投入する。redirect_uri を指定して
        /// 「同じ初期 redirect_uri の Client が既にある」状態を作る。
        /// </summary>
        private async Task SeedClientAsync(int organizationId, string clientId, string redirectUri)
        {
            var client = new Client
            {
                ClientId = clientId,
                ClientSecret = string.Empty,
                AppName = "existing",
                OrganizationId = organizationId,
                SubjectType = SubjectType.B2B,
                AllowedRpIds = new List<string> { "wp.example.jp" }
            };
            client.RedirectUris!.Add(new RedirectUri { Uri = redirectUri });
            Context.Clients.Add(client);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        private async Task SeedAccountAsync()
        {
            Context.Accounts.Add(new Account
            {
                Id = 1,
                Subject = AccountSubject,
                Email = "owner@example.jp",
                OrganizationId = AccountsOrgId,
                MaxSites = Account.DefaultMaxSites
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        /// <summary>Account が owner として管理する Organization を投入する（実物の AccountService が読む）。</summary>
        private async Task SeedOwnedOrganizationAsync(int id, string code, bool isSandbox, int? parentOrganizationId = null)
        {
            Context.Organizations.Add(new Organization
            {
                Id = id,
                Code = code,
                Name = code,
                TenantName = code,
                IsSandbox = isSandbox,
                ParentOrganizationId = parentOrganizationId
            });
            Context.AccountOrganizations.Add(new AccountOrganization
            {
                AccountSubject = AccountSubject,
                OrganizationId = id,
                Role = "owner"
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        private SignupService CreateSignupService(ISecretProtector protector)
        {
            var disposableMock = new Mock<IDisposableEmailChecker>();
            disposableMock.Setup(x => x.IsDisposable(It.IsAny<string>())).Returns(false);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Signup:ConfirmBaseUrl:{Tenant}"] = "https://ec-auth.io"
                })
                .Build();
            _emailMock = new Mock<IEmailService>();
            return new SignupService(
                Context,
                _tenantService,
                _emailMock.Object,
                disposableMock.Object,
                configuration,
                Mock.Of<ILogger<SignupService>>(),
                new PasskeyRegistrationTokenService(Context, Mock.Of<ILogger<PasskeyRegistrationTokenService>>()),
                new OrganizationProvisioningService(Context, protector));
        }

        private Mock<IEmailService> _emailMock = new();

        /// <summary>申込を受け付け、確認メールに載る生トークンを取り出す（DB にはハッシュしか残らない）。</summary>
        private async Task<string> RequestAndCaptureTokenAsync(SignupService service)
        {
            string? confirmUrl = null;
            _emailMock
                .Setup(x => x.SendSignupConfirmationAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, CancellationToken>((_, _, url, _) => confirmUrl = url)
                .Returns(Task.CompletedTask);

            await service.RequestAsync(new SignupInput
            {
                Email = "owner@example.com",
                OrganizationName = "Example Shop",
                ContactName = "山田 太郎",
                ProductionSiteUrl = "https://shop.example.jp",
                EcCubeVersion = "4"
            });
            return ExtractTokenFromConfirmUrl(confirmUrl!);
        }

        /// <summary>
        /// 1 回目の <c>ProtectAsync</c> だけ一過性の失敗を起こし、以降は平文をそのまま返す ISecretProtector。
        /// ProvisionAsync は Organization を SaveChanges した後に ProtectAsync を呼ぶため、
        /// 「トランザクション途中で一過性の失敗 → 再試行」を再現できる。
        /// </summary>
        private static Mock<ISecretProtector> CreateProtectorFailingOnce(
            out StrongBox<int> calls, Func<Exception>? failure = null)
        {
            var counter = new StrongBox<int>(0);
            var protector = new Mock<ISecretProtector>();
            protector
                .Setup(x => x.ProtectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((plaintext, _) =>
                {
                    counter.Value++;
                    if (counter.Value == 1)
                    {
                        throw failure?.Invoke() ?? new TransientTestException();
                    }
                    return Task.FromResult(plaintext);
                });
            calls = counter;
            return protector;
        }

        /// <param name="useRealAccountService">
        /// true なら実物の <see cref="AccountService"/>（account_organization を実 DB から読む）を使う。
        /// 再試行時に「前回の試行が作った / 消したサイト」を管理下として認識させるテストで必要。
        /// </param>
        private AccountController CreateController(
            ISecretProtector protector, out Mock<IAccountService> accountServiceMock, bool useRealAccountService = false)
        {
            var tokenServiceMock = new Mock<ITokenService>();
            tokenServiceMock
                .Setup(x => x.ValidateAccessTokenWithTypeAsync(AccountToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = AccountSubject,
                    SubjectType = SubjectType.Account
                });
            accountServiceMock = new Mock<IAccountService>();
            IAccountService accountService = useRealAccountService
                ? new AccountService(Context, Mock.Of<ILogger<AccountService>>())
                : accountServiceMock.Object;

            return new AccountController(
                Context,
                tokenServiceMock.Object,
                accountService,
                protector,
                new OrganizationProvisioningService(Context, protector),
                new UsageReportService(Context, new B2BUserService(Context, Mock.Of<ILogger<B2BUserService>>())),
                Mock.Of<ILogger<AccountController>>());
        }

        private static void AuthenticateAsOwnerOf(AccountController controller, Mock<IAccountService> accountServiceMock)
        {
            accountServiceMock
                .Setup(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization>());
            SetBearer(controller);
        }

        private static void SetBearer(AccountController controller)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Authorization"] = $"Bearer {AccountToken}";
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        }

        private static string ExtractTokenFromConfirmUrl(string confirmUrl)
        {
            var query = new Uri(confirmUrl).Query.TrimStart('?');
            var token = query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(kv => kv.Length == 2 && kv[0] == "token")
                .Select(kv => Uri.UnescapeDataString(kv[1]))
                .FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(token));
            return token!;
        }
    }
}
