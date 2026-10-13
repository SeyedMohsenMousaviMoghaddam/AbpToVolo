using System;
using System.Threading.Tasks;
using Abp.Application.Services;
using Abp.Runtime.Session;
using Acme.PhoneBook.Authorization.Users;
using Acme.PhoneBook.MultiTenancy;

namespace Acme.PhoneBook
{
    public abstract class PhoneBookAppServiceBase : ApplicationService
    {
        protected PhoneBookAppServiceBase()
        {
            LocalizationSourceName = PhoneBookConsts.LocalizationSourceName;
        }

        public TenantManager TenantManager { get; set; }

        public UserManager UserManager { get; set; }

        protected virtual async Task<User> GetCurrentUserAsync()
        {
            var user = await UserManager.FindByIdAsync(AbpSession.GetUserId().ToString());
            if (user == null)
            {
                throw new Exception("There is no current user!");
            }

            return user;
        }
    }
}
