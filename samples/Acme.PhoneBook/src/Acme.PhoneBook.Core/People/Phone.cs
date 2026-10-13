using System.ComponentModel.DataAnnotations.Schema;
using Abp.Domain.Entities.Auditing;

namespace Acme.PhoneBook.People
{
    public class Phone : CreationAuditedEntity<long>
    {
        public virtual int PersonId { get; set; }

        [ForeignKey(nameof(PersonId))]
        public virtual Person Person { get; set; }

        public virtual PhoneType Type { get; set; }

        public virtual string Number { get; set; }
    }

    /// <summary>
    /// نوع تلفن (multi-line doc comment)
    /// </summary>
    public enum PhoneType : byte
    {
        Mobile,
        Home,
        Business
    }
}
