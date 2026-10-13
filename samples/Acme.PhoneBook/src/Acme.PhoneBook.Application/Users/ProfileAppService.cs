using System.Threading.Tasks;
using Abp.Authorization;
using Acme.PhoneBook.Authorization.Users;

namespace Acme.PhoneBook.Users
{
    [AbpAuthorize]
    public class ProfileAppService : PhoneBookAppServiceBase
    {
        private readonly UserProfileManager _userProfileManager;

        public ProfileAppService(UserProfileManager userProfileManager)
        {
            _userProfileManager = userProfileManager;
        }

        public async Task<string> GetMyDisplayNameAsync()
        {
            var user = await GetCurrentUserAsync();
            return _userProfileManager.GetDisplayName(user);
        }

        public async Task SetMyNationalCodeAsync(string nationalCode)
        {
            var user = await GetCurrentUserAsync();
            if (user.NationalCode == nationalCode)
            {
                return;
            }

            await _userProfileManager.SetNationalCodeAsync(user, nationalCode);
        }
    }
}
