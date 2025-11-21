namespace MockOpenIdProvider.Services
{
    public interface IOrganizationService
    {
        string? TenantName { get; }
        void SetTenantName(string tenantName);
    }
}
