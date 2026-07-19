using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Services
{
    public class PasskeyRegistrationTokenServiceTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly PasskeyRegistrationTokenService _service;

        public PasskeyRegistrationTokenServiceTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _service = new PasskeyRegistrationTokenService(_context, Mock.Of<ILogger<PasskeyRegistrationTokenService>>());
        }

        [Fact]
        public async Task Issue_ThenValidate_ReturnsSubject()
        {
            var token = await _service.IssueAsync("subject-1");
            Assert.False(string.IsNullOrEmpty(token));

            var info = await _service.ValidateAsync(token);
            Assert.NotNull(info);
            Assert.Equal("subject-1", info!.Subject);
            // options 未実行なのでセッションは未束縛
            Assert.Null(info.SessionId);
        }

        [Fact]
        public async Task Issue_StoresHashNotPlaintext()
        {
            var token = await _service.IssueAsync("subject-1");
            var stored = await _context.PasskeyRegistrationTokens.SingleAsync();
            Assert.NotEqual(token, stored.TokenHash); // 平文は保存しない
            Assert.Equal("subject-1", stored.Subject);
        }

        [Fact]
        public async Task Validate_UnknownToken_ReturnsNull()
        {
            await _service.IssueAsync("subject-1");
            Assert.Null(await _service.ValidateAsync("some-other-token"));
        }

        [Fact]
        public async Task Consume_MarksUsed_AndSecondValidateFails()
        {
            var token = await _service.IssueAsync("subject-1");
            Assert.True(await _service.BindSessionAsync(token, "session-1"));

            Assert.True(await _service.ConsumeAsync(token, "session-1"));
            // 消費後は検証も消費も失敗する（一回限り）
            Assert.Null(await _service.ValidateAsync(token));
            Assert.False(await _service.ConsumeAsync(token, "session-1"));
        }

        [Fact]
        public async Task Consume_WithUnboundSession_Fails()
        {
            var token = await _service.IssueAsync("subject-1");
            await _service.BindSessionAsync(token, "session-1");

            // 束縛されていない session_id では消費できない
            Assert.False(await _service.ConsumeAsync(token, "session-2"));
            // トークンは未使用のまま（正規セッションでは引き続き消費できる）
            Assert.True(await _service.ConsumeAsync(token, "session-1"));
        }

        [Fact]
        public async Task BindSession_Rebinding_InvalidatesPreviousSession()
        {
            var token = await _service.IssueAsync("subject-1");
            await _service.BindSessionAsync(token, "session-1");
            await _service.BindSessionAsync(token, "session-2");

            var info = await _service.ValidateAsync(token);
            Assert.Equal("session-2", info!.SessionId);
            // 1 トークンにつき verify できるセッションは常に最新の 1 つだけ
            Assert.False(await _service.ConsumeAsync(token, "session-1"));
            Assert.True(await _service.ConsumeAsync(token, "session-2"));
        }

        [Fact]
        public async Task Consume_WithoutBoundSession_Fails()
        {
            var token = await _service.IssueAsync("subject-1");

            // options を経ずに verify へ直行する経路は認めない
            Assert.False(await _service.ConsumeAsync(token, "session-1"));
        }

        [Fact]
        public async Task Validate_ExpiredToken_ReturnsNull()
        {
            var token = await _service.IssueAsync("subject-1");
            // 期限切れに書き換える
            var entity = await _context.PasskeyRegistrationTokens.SingleAsync();
            entity.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await _context.SaveChangesAsync();

            Assert.Null(await _service.ValidateAsync(token));
        }

        public void Dispose() => _context.Dispose();
    }
}
