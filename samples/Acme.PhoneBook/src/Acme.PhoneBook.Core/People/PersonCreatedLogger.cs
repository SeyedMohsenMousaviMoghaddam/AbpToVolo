using Abp.Dependency;
using Abp.Events.Bus.Entities;
using Abp.Events.Bus.Handlers;
using Castle.Core.Logging;

namespace Acme.PhoneBook.People
{
    public class PersonCreatedLogger : IEventHandler<EntityCreatedEventData<Person>>, ITransientDependency
    {
        public ILogger Logger { get; set; }

        public PersonCreatedLogger()
        {
            Logger = NullLogger.Instance;
        }

        public void HandleEvent(EntityCreatedEventData<Person> eventData)
        {
            Logger.Info("New person: " + eventData.Entity.Name);
        }
    }
}
