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

            var subject = await _service.ValidateAsync(token);
            Assert.Equal("subject-1", subject);
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

            Assert.True(await _service.ConsumeAsync(token));
            // 消費後は検証も消費も失敗する（一回限り）
            Assert.Null(await _service.ValidateAsync(token));
            Assert.False(await _service.ConsumeAsync(token));
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
