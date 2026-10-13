using System;
using Abp.Dependency;

namespace Acme.PhoneBook
{
    public class AppTimes : ISingletonDependency
    {
        public DateTime StartupTime { get; set; }
    }
}
