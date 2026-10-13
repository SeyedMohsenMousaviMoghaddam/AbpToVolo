using Abp.Authorization.Users;
using Abp.Domain.Repositories;
using Abp.Domain.Uow;
using Acme.PhoneBook.Authorization.Roles;

namespace Acme.PhoneBook.Authorization.Users
{
    public class UserStore : AbpUserStore<Role, User>
    {
        public UserStore(IUnitOfWorkManager unitOfWorkManager, IRepository<User, long> userRepository, IRepository<Role> roleRepository,
            IRepository<UserRole, long> userRoleRepository, IRepository<UserLogin, long> userLoginRepository, IRepository<UserClaim, long> userClaimRepository)
            : base(unitOfWorkManager, userRepository, roleRepository, userRoleRepository, userLoginRepository, userClaimRepository)
        {
        }
    }
}
