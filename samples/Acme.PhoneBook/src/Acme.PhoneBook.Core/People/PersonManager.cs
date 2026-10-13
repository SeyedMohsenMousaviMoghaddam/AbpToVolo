using System.Threading.Tasks;
using Abp.Domain.Repositories;
using Abp.Domain.Services;
using Abp.UI;

namespace Acme.PhoneBook.People
{
    public interface IPersonManager : IDomainService
    {
        Task<Person> CreateAsync(Person person);
    }

    public class PersonManager : DomainService, IPersonManager
    {
        private readonly IRepository<Person> _personRepository;

        public PersonManager(IRepository<Person> personRepository)
        {
            _personRepository = personRepository;
        }

        public async Task<Person> CreateAsync(Person person)
        {
            var count = await _personRepository.CountAsync(p => p.EmailAddress == person.EmailAddress);
            if (count > 0)
            {
                throw new UserFriendlyException("There is already a person with this email address!");
            }

            Logger.Info("Creating person " + person.Name);
            var id = await _personRepository.InsertAndGetIdAsync(person);
            return await _personRepository.GetAsync(id);
        }
    }
}
