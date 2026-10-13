using System;
using System.ComponentModel.DataAnnotations;
using Abp.Application.Services.Dto;
using Abp.Authorization.Users;
using Abp.AutoMapper;
using Acme.PhoneBook.Authorization.Users;

namespace Acme.PhoneBook.Users.Dto
{
    [AutoMapFrom(typeof(User))]
    public class UserDto : EntityDto<long>
    {
        [Required]
        [StringLength(AbpUserBase.MaxUserNameLength)]
        public string UserName { get; set; }

        public string Name { get; set; }

        public string EmailAddress { get; set; }

        public DateTime CreationTime { get; set; }
    }
}
