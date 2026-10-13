using Abp.Authorization;
using Abp.Localization;
using Abp.MultiTenancy;

namespace Acme.PhoneBook.Authorization
{
    public class PhoneBookAuthorizationProvider : AuthorizationProvider
    {
        public override void SetPermissions(IPermissionDefinitionContext context)
        {
            var people = context.CreatePermission(PermissionNames.Pages_People, L("People"));
            people.CreateChildPermission(PermissionNames.Pages_People_Create, L("CreatingNewPerson"));
            context.CreatePermission(PermissionNames.Pages_Tenants, L("Tenants"), multiTenancySides: MultiTenancySides.Host);
        }

        private static ILocalizableString L(string name)
        {
            return new LocalizableString(name, PhoneBookConsts.LocalizationSourceName);
        }
    }
}
