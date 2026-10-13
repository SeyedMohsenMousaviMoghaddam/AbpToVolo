using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Abp.Domain.Entities.Auditing;

namespace Acme.PhoneBook.People
{
    public class Person : FullAuditedEntity
    {
        public const int MaxNameLength = 32;

        [Required]
        [MaxLength(MaxNameLength)]
        public virtual string Name { get; set; }

        public virtual string Surname { get; set; }

        public virtual string EmailAddress { get; set; }

        public virtual ICollection<Phone> Phones { get; set; }
    }
}
