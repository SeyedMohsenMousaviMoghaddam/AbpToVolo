using Abp.Domain.Entities;
using Abp.EntityFrameworkCore;
using Abp.EntityFrameworkCore.Repositories;

namespace Acme.PhoneBook.EntityFrameworkCore.Repositories
{
    public abstract class PhoneBookRepositoryBase<TEntity, TPrimaryKey> : EfCoreRepositoryBase<PhoneBookDbContext, TEntity, TPrimaryKey>
        where TEntity : class, IEntity<TPrimaryKey>
    {
        protected PhoneBookRepositoryBase(IDbContextProvider<PhoneBookDbContext> dbContextProvider)
            : base(dbContextProvider)
        {
        }
    }

    public abstract class PhoneBookRepositoryBase<TEntity> : PhoneBookRepositoryBase<TEntity, int>
        where TEntity : class, IEntity<int>
    {
        protected PhoneBookRepositoryBase(IDbContextProvider<PhoneBookDbContext> dbContextProvider)
            : base(dbContextProvider)
        {
        }
    }
}
