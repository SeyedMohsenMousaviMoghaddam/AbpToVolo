using Abp.Application.Services;
using Abp.Application.Services.Dto;
using Abp.Domain.Repositories;
using Acme.PhoneBook.Authorization.Users;
using Acme.PhoneBook.Users.Dto;

namespace Acme.PhoneBook.Users
{
    public class UserAppService : AsyncCrudAppService<User, UserDto, long, PagedResultRequestDto, UserDto, UserDto>, IUserAppService
    {
        private readonly UserManager _userManager;

        public UserAppService(IRepository<User, long> repository, UserManager userManager)
            : base(repository)
        {
            _userManager = userManager;
        }
    }
}
