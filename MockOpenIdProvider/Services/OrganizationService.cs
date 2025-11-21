namespace MockOpenIdProvider.Services
{
    public class OrganizationService : IOrganizationService
    {
        public string? TenantName { get; private set; }

        public void SetTenantName(string tenantName)
        {
            TenantName = tenantName;
        }
    }
}
