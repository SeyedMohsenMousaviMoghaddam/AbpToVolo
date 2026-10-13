using System;
using Abp.AspNetCore;
using Abp.AspNetCore.Configuration;
using Abp.Modules;
using Abp.Reflection.Extensions;
using Acme.PhoneBook.EntityFrameworkCore;

namespace Acme.PhoneBook
{
    [DependsOn(
         typeof(PhoneBookApplicationModule),
         typeof(PhoneBookEntityFrameworkModule),
         typeof(AbpAspNetCoreModule))]
    public class PhoneBookWebCoreModule : AbpModule
    {
        public override void PreInitialize()
        {
            Configuration.DefaultNameOrConnectionString = PhoneBookConsts.ConnectionStringName;

            Configuration.Modules.AbpAspNetCore()
                 .CreateControllersForAppServices(
                     typeof(PhoneBookApplicationModule).GetAssembly()
                 );

            Configuration.Modules.AbpWebCommon().SendAllExceptionsToClients = true;

            Configuration.Caching.ConfigureAll(cache =>
            {
                cache.DefaultSlidingExpireTime = TimeSpan.FromHours(2);
            });
        }

        public override void Initialize()
        {
            IocManager.RegisterAssemblyByConvention(typeof(PhoneBookWebCoreModule).GetAssembly());
        }
    }
}
