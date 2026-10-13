using Abp.AspNetCore.Mvc.Controllers;

namespace Acme.PhoneBook.Controllers
{
    public abstract class PhoneBookControllerBase : AbpController
    {
        protected PhoneBookControllerBase()
        {
            LocalizationSourceName = PhoneBookConsts.LocalizationSourceName;
        }
    }
}
