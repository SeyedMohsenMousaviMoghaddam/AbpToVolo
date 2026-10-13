using Abp.EntityFrameworkCore;
using Abp.EntityFrameworkCore.Configuration;
using Abp.Modules;
using Abp.Zero.EntityFrameworkCore;
using Abp.Reflection.Extensions;

namespace Acme.PhoneBook.EntityFrameworkCore
{
    [DependsOn(
        typeof(PhoneBookCoreModule),
        typeof(AbpZeroCoreEntityFrameworkCoreModule))]
    public class PhoneBookEntityFrameworkModule : AbpModule
    {
        /* Used it tests to skip dbcontext registration, in order to use in-memory database of EF Core */
        public bool SkipDbContextRegistration { get; set; }

        public override void PreInitialize()
        {
            if (!SkipDbContextRegistration)
            {
                Configuration.Modules.AbpEfCore().AddDbContext<PhoneBookDbContext>(options =>
                {
                    if (options.ExistingConnection != null)
                    {
                        PhoneBookDbContextConfigurer.Configure(options.DbContextOptions, options.ExistingConnection);
                    }
                    else
                    {
                        PhoneBookDbContextConfigurer.Configure(options.DbContextOptions, options.ConnectionString);
                    }
                });
            }
        }

        public override void Initialize()
        {
            IocManager.RegisterAssemblyByConvention(typeof(PhoneBookEntityFrameworkModule).GetAssembly());
        }
    }
}
