using Abp.Application.Services;
using Acme.PhoneBook.People.Dto;

namespace Acme.PhoneBook.People
{
    public interface IPersonAppService : IAsyncCrudAppService<PersonDto, int, GetPeopleInput, CreatePersonDto, PersonDto>
    {
    }
}
