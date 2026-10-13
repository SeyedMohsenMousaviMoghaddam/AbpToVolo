using System.Threading.Tasks;
using Abp.Domain.Services;

namespace Acme.PhoneBook.Authorization.Users
{
    public class UserProfileManager : DomainService
    {
        private readonly UserManager _userManager;

        public UserProfileManager(UserManager userManager)
        {
            _userManager = userManager;
        }

        public async Task SetNationalCodeAsync(User user, string nationalCode)
        {
            user.NationalCode = nationalCode;
            await _userManager.UpdateAsync(user);
        }

        public string GetDisplayName(User user)
        {
            return user.Name + " " + user.Surname + " <" + user.EmailAddress + "> " + user.NationalCode;
        }

        public Task<User> FindAsync(string userName)
        {
            return _userManager.FindByNameAsync(userName);
        }
    }
}
