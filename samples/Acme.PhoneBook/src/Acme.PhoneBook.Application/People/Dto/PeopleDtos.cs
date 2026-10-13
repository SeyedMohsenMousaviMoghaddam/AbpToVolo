using System.ComponentModel.DataAnnotations;
using Abp.Application.Services.Dto;
using Abp.AutoMapper;

namespace Acme.PhoneBook.People.Dto
{
    [AutoMapFrom(typeof(Person))]
    public class PersonDto : FullAuditedEntityDto
    {
        public string Name { get; set; }

        public string Surname { get; set; }

        public string EmailAddress { get; set; }
    }

    [AutoMapTo(typeof(Person))]
    public class CreatePersonDto
    {
        [Required]
        [StringLength(Person.MaxNameLength)]
        public string Name { get; set; }

        public string Surname { get; set; }

        public string EmailAddress { get; set; }
    }

    public class GetPeopleInput : PagedAndSortedResultRequestDto
    {
        public string Filter { get; set; }
    }

    [AutoMapFrom(typeof(Phone))]
    public class PhoneDto : EntityDto<long>
    {
        public PhoneType Type { get; set; }

        public string Number { get; set; }
    }

    [AutoMapTo(typeof(Phone))]
    public class AddPhoneInput
    {
        [Range(1, int.MaxValue)]
        public int PersonId { get; set; }

        public PhoneType Type { get; set; }

        [Required]
        public string Number { get; set; }
    }
}
