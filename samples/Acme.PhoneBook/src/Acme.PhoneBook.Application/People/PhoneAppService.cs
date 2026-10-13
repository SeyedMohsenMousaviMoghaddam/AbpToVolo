using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Abp.Application.Services;
using Abp.Application.Services.Dto;
using Abp.Authorization;
using Abp.Domain.Repositories;
using Abp.UI;
using Acme.PhoneBook.People.Dto;

namespace Acme.PhoneBook.People
{
    public interface IPhoneAppService : IApplicationService
    {
        Task<ListResultDto<PhoneDto>> GetPhonesAsync(EntityDto input);

        Task<PhoneDto> AddPhoneAsync(AddPhoneInput input);

        Task<int> GetPhoneCountAsync();

        Task DeletePhoneAsync(EntityDto<long> input);
    }

    [AbpAuthorize]
    public class PhoneAppService : PhoneBookAppServiceBase, IPhoneAppService
    {
        private readonly IRepository<Phone, long> _phoneRepository;
        private readonly IRepository<Person> _personRepository;

        public PhoneAppService(IRepository<Phone, long> phoneRepository, IRepository<Person> personRepository)
        {
            _phoneRepository = phoneRepository;
            _personRepository = personRepository;
        }

        public async Task<ListResultDto<PhoneDto>> GetPhonesAsync(EntityDto input)
        {
            var phones = await _phoneRepository.GetAllListAsync(p => p.PersonId == input.Id);
            return new ListResultDto<PhoneDto>(ObjectMapper.Map<List<PhoneDto>>(phones));
        }

        public async Task<PhoneDto> AddPhoneAsync(AddPhoneInput input)
        {
            var person = await _personRepository.FirstOrDefaultAsync(input.PersonId);
            if (person == null)
            {
                throw new UserFriendlyException(L("PersonNotFound"));
            }

            if (AbpSession.UserId == null)
            {
                throw new UserFriendlyException(L("LoginRequired"));
            }

            var phone = ObjectMapper.Map<Phone>(input);
            phone.PersonId = person.Id;
            var phoneId = await _phoneRepository.InsertAndGetIdAsync(phone);

            var count = await _phoneRepository.CountAsync(p => p.PersonId == person.Id);
            Logger.Info($"Person {person.Id} now has {count} phones");

            return ObjectMapper.Map<PhoneDto>(await _phoneRepository.GetAsync(phoneId));
        }

        public async Task<int> GetPhoneCountAsync()
        {
            var total = await _phoneRepository.CountAsync();
            return (int)total;
        }

        public async Task DeletePhoneAsync(EntityDto<long> input)
        {
            var query = _phoneRepository.GetAll().Where(p => p.Id == input.Id);
            if (!query.Any())
            {
                throw new UserFriendlyException(L("PhoneNotFound", input.Id));
            }

            await _phoneRepository.DeleteAsync(input.Id);
        }
    }
}
