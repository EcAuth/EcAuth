using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Test.Models
{
    public class MagicLoginTokenTests : IDisposable
    {
        private readonly EcAuthDbContext _context;

        public MagicLoginTokenTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
        }

        [Fact]
        public void MagicLoginToken_DefaultValues_ShouldBeSetCorrectly()
        {
            var token = new MagicLoginToken();

            Assert.Equal(0, token.Id);
            Assert.Null(token.AccountSubject);
            Assert.Equal(string.Empty, token.RequestedEmailHash);
            Assert.Equal(string.Empty, token.TokenHash);
            Assert.Equal(default, token.ExpiresAt);
            Assert.Null(token.UsedAt);
            Assert.Null(token.RequestedIp);
            Assert.Null(token.RequestedUserAgent);
            Assert.True(token.CreatedAt <= DateTimeOffset.UtcNow);
        }

        [Fact]
        public async Task MagicLoginToken_WithoutAccountSubject_ShouldSave()
        {
            // Email enumeration 対策で Account 不在のリクエストも記録対象とするため
            // AccountSubject = null で保存できることを確認
            var token = new MagicLoginToken
            {
                AccountSubject = null,
                RequestedEmailHash = new string('a', 64),
                TokenHash = new string('b', 128),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                RequestedIp = "203.0.113.1",
                RequestedUserAgent = "Mozilla/5.0"
            };
            _context.MagicLoginTokens.Add(token);
            await _context.SaveChangesAsync();

            var saved = await _context.MagicLoginTokens.FirstOrDefaultAsync();
            Assert.NotNull(saved);
            Assert.Null(saved.AccountSubject);
            Assert.NotEqual(0, saved.Id);
        }

        [Fact]
        public void MagicLoginToken_TokenHash_HasUniqueIndex()
        {
            var entityType = _context.Model.FindEntityType(typeof(MagicLoginToken));
            var index = entityType?.GetIndexes()
                .FirstOrDefault(i => i.Properties.Count == 1
                    && i.Properties[0].Name == nameof(MagicLoginToken.TokenHash));

            Assert.NotNull(index);
            Assert.True(index.IsUnique);
        }

        [Fact]
        public void MagicLoginToken_HasRateLimitIndexes()
        {
            var entityType = _context.Model.FindEntityType(typeof(MagicLoginToken));
            Assert.NotNull(entityType);

            var emailHashIndex = entityType.GetIndexes()
                .FirstOrDefault(i => i.Properties.Count == 2
                    && i.Properties[0].Name == nameof(MagicLoginToken.RequestedEmailHash)
                    && i.Properties[1].Name == nameof(MagicLoginToken.CreatedAt));
            Assert.NotNull(emailHashIndex);

            var ipIndex = entityType.GetIndexes()
                .FirstOrDefault(i => i.Properties.Count == 2
                    && i.Properties[0].Name == nameof(MagicLoginToken.RequestedIp)
                    && i.Properties[1].Name == nameof(MagicLoginToken.CreatedAt));
            Assert.NotNull(ipIndex);
        }

        [Fact]
        public void MagicLoginToken_HasNoQueryFilter_ForCrossTenantRateLimit()
        {
            var entityType = _context.Model.FindEntityType(typeof(MagicLoginToken));
            Assert.NotNull(entityType);
            Assert.Null(entityType.GetQueryFilter());
        }

        [Fact]
        public void MagicLoginToken_RequestedUserAgent_HasMaxLength1000()
        {
            var entityType = _context.Model.FindEntityType(typeof(MagicLoginToken));
            var prop = entityType?.FindProperty(nameof(MagicLoginToken.RequestedUserAgent));

            Assert.NotNull(prop);
            Assert.Equal(1000, prop.GetMaxLength());
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
