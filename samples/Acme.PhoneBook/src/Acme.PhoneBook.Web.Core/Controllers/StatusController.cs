using Abp.Authorization;
using Abp.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Acme.PhoneBook.Controllers
{
    [Route("api/[controller]/[action]")]
    public class StatusController : PhoneBookControllerBase
    {
        [HttpGet]
        [DontWrapResult]
        [AbpAllowAnonymous]
        public string Ping() => L("People");
    }
}
