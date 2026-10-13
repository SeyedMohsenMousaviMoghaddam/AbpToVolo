using Abp.Modules;
using Abp.Reflection.Extensions;

namespace Acme.PhoneBook.Web.Host.Startup
{
    [DependsOn(
       typeof(PhoneBookWebCoreModule))]
    public class PhoneBookWebHostModule : AbpModule
    {
        public override void Initialize()
        {
            IocManager.RegisterAssemblyByConvention(typeof(PhoneBookWebHostModule).GetAssembly());
        }
    }
}
