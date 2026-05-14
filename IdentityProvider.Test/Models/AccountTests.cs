using IdentityProvider.Models;

namespace IdentityProvider.Test.Models
{
    public class AccountTests
    {
        [Fact]
        public void Account_DefaultValues_ShouldBeSetCorrectly()
        {
            var account = new Account();

            Assert.Equal(0, account.Id);
            Assert.Equal(string.Empty, account.Subject);
            Assert.Equal(string.Empty, account.Email);
            Assert.Equal(0, account.OrganizationId);
            Assert.Null(account.DisplayName);
            Assert.Null(account.EmailVerifiedAt);
            Assert.True(account.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.True(account.UpdatedAt <= DateTimeOffset.UtcNow);
            Assert.NotNull(account.ManagedOrganizations);
            Assert.Empty(account.ManagedOrganizations);
        }

        [Fact]
        public void Account_SetProperties_ShouldRetainValues()
        {
            var subject = Guid.NewGuid().ToString();
            var verifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

            var account = new Account
            {
                Id = 42,
                Subject = subject,
                Email = "owner@example.jp",
                OrganizationId = 100,
                DisplayName = "Owner Taro",
                EmailVerifiedAt = verifiedAt
            };

            Assert.Equal(42, account.Id);
            Assert.Equal(subject, account.Subject);
            Assert.Equal("owner@example.jp", account.Email);
            Assert.Equal(100, account.OrganizationId);
            Assert.Equal("Owner Taro", account.DisplayName);
            Assert.Equal(verifiedAt, account.EmailVerifiedAt);
        }

        [Fact]
        public void Account_ImplementsISubjectProvider()
        {
            var subject = "subject-uuid";
            ISubjectProvider provider = new Account { Subject = subject };

            Assert.Equal(subject, provider.Subject);
        }
    }
}
