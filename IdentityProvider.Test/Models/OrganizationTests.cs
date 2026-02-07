using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Test.Models
{
    public class OrganizationTests
    {
        [Fact]
        public void IsSandbox_DefaultsToFalse()
        {
            var org = new Organization();

            Assert.False(org.IsSandbox);
        }

        [Fact]
        public void IsSandbox_CanBeSetToTrue()
        {
            var org = new Organization { IsSandbox = true };

            Assert.True(org.IsSandbox);
        }

        [Fact]
        public async Task IsSandbox_PersistsToDatabase()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();

            var org = new Organization
            {
                Code = "sandbox-test",
                Name = "サンドボックステスト",
                TenantName = "test-tenant",
                IsSandbox = true
            };
            context.Organizations.Add(org);
            await context.SaveChangesAsync();

            var retrieved = await context.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Code == "sandbox-test");

            Assert.NotNull(retrieved);
            Assert.True(retrieved.IsSandbox);
        }
    }
}
