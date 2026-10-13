using System.Linq;
using Abp.Application.Services;
using Abp.Authorization;
using Abp.Domain.Repositories;
using Abp.Extensions;
using Abp.Linq.Extensions;
using Acme.PhoneBook.Authorization;
using Acme.PhoneBook.People.Dto;

namespace Acme.PhoneBook.People
{
    [AbpAuthorize(PermissionNames.Pages_People)]
    public class PersonAppService : AsyncCrudAppService<Person, PersonDto, int, GetPeopleInput, CreatePersonDto, PersonDto>, IPersonAppService
    {
        public PersonAppService(IRepository<Person> repository)
            : base(repository)
        {
            CreatePermissionName = PermissionNames.Pages_People_Create;
            LocalizationSourceName = PhoneBookConsts.LocalizationSourceName;
        }

        protected override IQueryable<Person> CreateFilteredQuery(GetPeopleInput input)
        {
            return Repository.GetAll()
                .WhereIf(!input.Filter.IsNullOrEmpty(), p => p.Name.Contains(input.Filter) || p.Surname.Contains(input.Filter));
        }

        protected override IQueryable<Person> ApplySorting(IQueryable<Person> query, GetPeopleInput input)
        {
            return query.OrderBy(p => p.Name);
        }
    }
}
