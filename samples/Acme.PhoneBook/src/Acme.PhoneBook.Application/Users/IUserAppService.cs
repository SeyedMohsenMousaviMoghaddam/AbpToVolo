using Abp.Application.Services;
using Abp.Application.Services.Dto;
using Acme.PhoneBook.Users.Dto;

namespace Acme.PhoneBook.Users
{
    public interface IUserAppService : IAsyncCrudAppService<UserDto, long, PagedResultRequestDto, UserDto, UserDto>
    {
    }
}
