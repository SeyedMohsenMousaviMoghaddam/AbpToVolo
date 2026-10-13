using System.Threading.Tasks;
using Abp.Authorization;
using Abp.MultiTenancy;
using Acme.PhoneBook.Authorization;
using Acme.PhoneBook.Authorization.Users;
using Acme.PhoneBook.MultiTenancy;
using Microsoft.AspNetCore.Mvc;

namespace Acme.PhoneBook.Controllers
{
    [Route("api/[controller]/[action]")]
    public class TokenAuthController : PhoneBookControllerBase
    {
        private readonly LogInManager _logInManager;

        public TokenAuthController(LogInManager logInManager)
        {
            _logInManager = logInManager;
        }

        [HttpPost]
        public async Task<string> Authenticate(string userName, string password)
        {
            AbpLoginResult<Tenant, User> result = await _logInManager.LoginAsync(userName, password);
            return result.Result.ToString();
        }
    }
}
